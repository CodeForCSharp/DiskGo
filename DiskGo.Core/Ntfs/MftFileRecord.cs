using DiskGo.Core;

namespace DiskGo.Core.Ntfs;

/// <summary>
/// One MFT file record, reduced to what a disk-space scan needs.
///
/// A record is either a base record (a real file or directory) or an extension
/// record that only exists because a base record ran out of room. Extension
/// records carry the rest of the attributes and are reached through the base
/// record's <c>$ATTRIBUTE_LIST</c>.
/// </summary>
internal sealed class MftFileRecord
{
    private const int AttributeEnd = -1;
    private const int AttributeList = 0x20;
    private const int AttributeFileName = 0x30;
    private const int AttributeData = 0x80;
    private const int AttributeReparse = 0xC0;
    private const ulong UpcaseRecord = 10;
    private const int FileRecordSignature = 0x454C4946; // "FILE"
    private const ushort InUseFlag = 0x0001;
    private const ushort DirectoryFlag = 0x0002;
    private const ushort CompressedFlag = 0x0001;
    private const ushort SparseFlag = 0x8000;

    private static readonly MftFileRecord Unused = new(0);

    private MftFileRecord(ulong recordNumber) => RecordNumber = recordNumber;

    public ulong RecordNumber { get; }

    public bool InUse { get; private set; }

    /// <summary>Set when this record only holds attributes that spilled out of another record.</summary>
    public bool IsExtension { get; private set; }

    public ulong BaseRecordNumber { get; private set; }

    public bool IsDirectory { get; private set; }

    public FileNameRecord Name { get; private set; }

    public bool HasName { get; private set; }

    private byte _linkState; // 0 = none, 1 = single link in Name, 2 = multiple links in _links
    private List<FileNameRecord>? _links;
    private List<ulong>? _extensionRecords;

    /// <summary>The DOS 8.3 name, when this file has one besides the name it is known by.</summary>
    public string ShortName { get; private set; } = "";

    /// <summary>
    /// Every name this file goes by, apart from a DOS 8.3 alias of the same
    /// file. More than one means the file is hard linked into several directories.
    /// </summary>
    public IReadOnlyList<FileNameRecord> Links => _links ?? (HasName ? [Name] : []);

    public int LinkCount => _linkState == 2 ? _links!.Count : (_linkState == 1 || HasName ? 1 : 0);

    public FileNameRecord GetLink(int index) => _linkState == 2 ? _links![index] : Name;

    /// <summary>Extension records referenced by this record's attribute list. Empty for an ordinary file.</summary>
    public IReadOnlyList<ulong> ExtensionRecords => _extensionRecords ?? (IReadOnlyList<ulong>)[];

    /// <summary>Set when the record carries a reparse point, such as a junction or a symbolic link.</summary>
    public bool HasReparse { get; private set; }

    public ReparseInfo Reparse { get; private set; }

    /// <summary>A <c>$ATTRIBUTE_LIST</c> stored in runs rather than in the record. Null when there is none or it was already read.</summary>
    public ExternalStream? AttributeListStream { get; private set; }

    /// <summary>A reparse point stored in runs. Null when it was resident or absent.</summary>
    public ExternalStream? ReparseStream { get; private set; }

    /// <summary>The <c>$DATA</c> of <c>$Upcase</c> (record 10). Null on every other record.</summary>
    public ExternalStream? Upcase { get; private set; }

    /// <summary>
    /// Sizes taken from the <c>$DATA</c> attributes that live in this record.
    /// For a file with an attribute list this is only the part stored here;
    /// <see cref="Ntfs.RecordResolver"/> adds what the extension records hold.
    /// </summary>
    public RecordSizes Data { get; private set; } = RecordSizes.Unknown;

    public static MftFileRecord Parse(ReadOnlySpan<byte> record, ulong recordNumber, int clusterSize)
    {
        if (record.Length < 48 || BitConverter.ToInt32(record[..4]) != FileRecordSignature)
            return Unused;

        ushort flags = BitConverter.ToUInt16(record[22..24]);
        if ((flags & InUseFlag) == 0)
            return Unused;

        // Fixups overwrite the last two bytes of every sector, so the record
        // has to be copied before it can be read. The caller reuses its buffer.
        byte[]? rented = null;
        Span<byte> owned = record.Length <= 4096
            ? stackalloc byte[record.Length]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(record.Length)).AsSpan(0, record.Length);
        try
        {
            record.CopyTo(owned);
            ushort fixupOffset = BitConverter.ToUInt16(owned[4..6]);
            ushort fixupCount = BitConverter.ToUInt16(owned[6..8]);
            if (!ApplyFixup(owned, fixupOffset, fixupCount))
                return Unused;

            var parsed = new MftFileRecord(recordNumber)
            {
                InUse = true,
                IsDirectory = (flags & DirectoryFlag) != 0,
                BaseRecordNumber = BitConverter.ToUInt64(owned[32..40]) & 0x0000_FFFF_FFFF_FFFFUL,
            };
            parsed.IsExtension = parsed.BaseRecordNumber != 0;

            ushort firstAttribute = BitConverter.ToUInt16(owned[20..22]);
            parsed.ReadAttributes(owned, firstAttribute, clusterSize);
            return parsed;
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void ReadAttributes(ReadOnlySpan<byte> record, int start, int clusterSize)
    {
        int nameRank = -1;
        var sizes = RecordSizes.Unknown;

        for (int offset = start; offset + 8 <= record.Length;)
        {
            int type = BitConverter.ToInt32(record.Slice(offset, 4));
            if (type == AttributeEnd)
                break;

            int length = BitConverter.ToInt32(record.Slice(offset + 4, 4));
            if (length < 24 || offset + length > record.Length)
                break;

            ReadOnlySpan<byte> attribute = record.Slice(offset, length);
            bool resident = attribute[8] == 0;
            int nameLength = attribute[9];
            ReadOnlySpan<byte> value = ValueOf(attribute, resident);

            if (type == AttributeFileName && resident && value.Length > 65
                && FileNameRecord.TryRead(value, out FileNameRecord candidate))
            {
                int rank = NamespaceRank(candidate.Namespace);
                bool updateName = rank > nameRank;

                // A DOS 8.3 alias is the same link under another spelling, and
                // "." / ".." are the directory pointing at itself. Neither is a
                // second place the file occupies.
                if (candidate.Namespace != 2 && candidate.Name is not "." and not "..")
                {
                    if (_linkState == 0)
                    {
                        _linkState = 1;
                        updateName = true;
                    }
                    else if (_linkState == 1)
                    {
                        _links = new List<FileNameRecord>(2) { Name, candidate };
                        _linkState = 2;
                    }
                    else
                    {
                        _links!.Add(candidate);
                    }
                }
                else if (candidate.Namespace == 2 && candidate.Name is not "." and not "..")
                {
                    ShortName = candidate.Name;
                }

                if (updateName)
                {
                    Name = candidate;
                    HasName = true;
                    nameRank = rank;
                    IsDirectory |= candidate.IsDirectory;
                }
            }
            else if (type == AttributeList)
            {
                if (resident)
                    ApplyAttributeList(value);
                else if (TryCapture(attribute, clusterSize, out ExternalStream stream))
                    AttributeListStream = stream;
            }
            else if (type == AttributeReparse)
            {
                if (resident)
                    ApplyReparse(value);
                else if (TryCapture(attribute, clusterSize, out ExternalStream stream))
                    ReparseStream = stream;
            }
            else if (type == AttributeData)
            {
                sizes = sizes.Add(ReadData(attribute, value, resident, clusterSize), unnamed: nameLength == 0);
                if (RecordNumber == UpcaseRecord && nameLength == 0)
                    Upcase = resident
                        ? new ExternalStream { Resident = value.ToArray(), LogicalSize = value.Length }
                        : TryCapture(attribute, clusterSize, out ExternalStream stream) ? stream : null;
            }

            offset += length;
        }

        Data = sizes;
    }

    /// <summary>Folds an <c>$ATTRIBUTE_LIST</c> into <see cref="ExtensionRecords"/>. Used for a list that was read from its runs after parsing.</summary>
    public void ApplyAttributeList(ReadOnlySpan<byte> value)
    {
        foreach (AttributeListEntry entry in AttributeListReader.Read(value))
        {
            if (entry.ExtensionRecord != RecordNumber)
            {
                _extensionRecords ??= new List<ulong>(2);
                _extensionRecords.Add(entry.ExtensionRecord);
            }
        }
    }

    /// <summary>Records where a junction or symbolic link points.</summary>
    public void ApplyReparse(ReadOnlySpan<byte> value)
    {
        if (!ReparsePoint.TryParse(value, out ReparseInfo info))
            return;

        HasReparse = true;
        Reparse = info;
    }

    private static bool TryCapture(ReadOnlySpan<byte> attribute, int clusterSize, out ExternalStream stream)
    {
        stream = null!;
        if (attribute.Length < 64)
            return false;

        long logical = BitConverter.ToInt64(attribute[48..56]);
        int runOffset = BitConverter.ToUInt16(attribute[32..34]);
        if (runOffset < 64 || runOffset > attribute.Length)
            return false;

        stream = new ExternalStream
        {
            Runs = RunList.Decode(attribute[runOffset..], clusterSize),
            LogicalSize = logical,
        };
        return true;
    }

    private static AttributeSize ReadData(ReadOnlySpan<byte> attribute, ReadOnlySpan<byte> value, bool resident, int clusterSize)
    {
        if (resident)
            return new AttributeSize(value.Length, 0, false);

        if (attribute.Length < 64)
            return AttributeSize.None;

        long logical = BitConverter.ToInt64(attribute[48..56]);
        ushort flags = BitConverter.ToUInt16(attribute[12..14]);
        bool compressed = (flags & (CompressedFlag | SparseFlag)) != 0;
        int unitShift = attribute[34];
        int compressionUnit = unitShift == 0 ? 0 : 1 << unitShift;
        int runOffset = BitConverter.ToUInt16(attribute[32..34]);
        if (runOffset < 64 || runOffset > attribute.Length)
            return new AttributeSize(logical, 0, compressed);

        return RunList.Measure(attribute[runOffset..], clusterSize, compressionUnit, logical, compressed);
    }

    private static ReadOnlySpan<byte> ValueOf(ReadOnlySpan<byte> attribute, bool resident)
    {
        if (!resident || attribute.Length < 24)
            return [];

        int valueOffset = BitConverter.ToUInt16(attribute[20..22]);
        int valueLength = BitConverter.ToInt32(attribute[16..20]);
        if (valueOffset < 0 || valueLength < 0 || valueOffset + valueLength > attribute.Length)
            return [];

        return attribute.Slice(valueOffset, valueLength);
    }

    /// <summary>
    /// NTFS replaces the last two bytes of every 512-byte sector with the
    /// update sequence number. Put the original bytes back before reading
    /// anything that might straddle a sector boundary, such as a file name.
    /// </summary>
    private static bool ApplyFixup(Span<byte> record, int offset, int count)
    {
        if (count < 2 || offset < 0 || offset + count * 2 > record.Length)
            return false;

        ushort sequenceNumber = BitConverter.ToUInt16(record.Slice(offset, 2));
        for (int sector = 1; sector < count; sector++)
        {
            int position = sector * 512 - 2;
            if (position + 2 > record.Length || BitConverter.ToUInt16(record.Slice(position, 2)) != sequenceNumber)
                return false;

            record[position] = record[offset + sector * 2];
            record[position + 1] = record[offset + sector * 2 + 1];
        }

        return true;
    }

    private static int NamespaceRank(byte nameSpace) => nameSpace switch
    {
        1 => 3, // Win32
        3 => 3, // Win32 and DOS
        0 => 2, // POSIX
        2 => 1, // DOS 8.3 alias, kept only when nothing better exists
        _ => 0,
    };
}
