namespace DiskGo.Core.Ntfs;

/// <summary>
/// The volume's <c>$Upcase</c> table: 65,536 entries, each the upper-case form
/// of one UTF-16 code unit. NTFS compares file names with this table, which
/// disagrees with <see cref="StringComparer.OrdinalIgnoreCase"/> for a handful
/// of characters. When the table cannot be read, callers keep the ordinal comparer.
/// </summary>
internal sealed class NtfsUpcase : IEqualityComparer<string>
{
    public const int TableBytes = 65536 * 2;

    private readonly char[] _upper;

    private NtfsUpcase(char[] upper) => _upper = upper;

    /// <summary>Builds a table from the raw <c>$Upcase</c> stream. Null when the stream is the wrong size.</summary>
    public static NtfsUpcase? TryFrom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < TableBytes)
            return null;

        var upper = new char[65536];
        for (int i = 0; i < upper.Length; i++)
            upper[i] = (char)BitConverter.ToUInt16(bytes.Slice(i * 2, 2));

        return new NtfsUpcase(upper);
    }

    public bool Equals(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (Fold(left[i]) != Fold(right[i]))
                return false;
        }

        return true;
    }

    public int GetHashCode(string value)
    {
        var hash = new HashCode();
        foreach (char character in value)
            hash.Add(Fold(character));

        return hash.ToHashCode();
    }

    private char Fold(char character) => character < _upper.Length ? _upper[character] : character;
}
