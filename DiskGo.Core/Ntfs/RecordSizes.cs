namespace DiskGo.Core.Ntfs;

/// <summary>
/// The size of a file as derived from its <c>$DATA</c> attributes.
///
/// The default, unnamed stream is what Windows reports as the file size, so it
/// is kept separate. Named streams (Zone.Identifier, document metadata and so
/// on) still occupy clusters and are added to <see cref="Allocated"/>, which is
/// the space the file actually costs. A file with no data attribute at all
/// reports <see cref="Found"/> as false and the caller falls back to the size
/// stored in the file name.
/// </summary>
internal readonly record struct RecordSizes(long Logical, long Allocated, bool Found)
{
    public static RecordSizes Unknown { get; } = new(0, 0, false);

    public RecordSizes Add(AttributeSize size, bool unnamed)
    {
        long logical = unnamed ? Logical + size.Logical : Logical;
        return new RecordSizes(logical, Allocated + size.Allocated, true);
    }
}
