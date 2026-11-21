using System.Text;

namespace DiskGo.Core;

/// <summary>
/// A drive letter the caller already knows about, because the process that is
/// about to scan cannot see it. An elevated process does not inherit the subst
/// drives and mapped network drives of the user who launched it, so those are
/// handed across as text and applied before the scan starts.
/// </summary>
public readonly record struct ListedDrive(char Letter, string Target);

/// <summary>
/// Collects the subst drives and mapped network drives of the current session,
/// and turns the text form of that collection back into paths a scan can use.
/// </summary>
public static class SessionLists
{
    /// <summary>
    /// Every subst drive visible here, as <c>X:=\??\C:\Real\Path</c>. The
    /// <c>\??\</c> prefix is how the letter is reported, and it is kept so the
    /// text round-trips through <see cref="Parse"/>.
    /// </summary>
    public static string SubstList()
    {
        var lines = new StringBuilder();
        foreach (char letter in Letters())
        {
            string? target = SubstDrives.TargetOf(letter);
            if (target is null)
                continue;

            if (lines.Length > 0)
                lines.Append(';');
            lines.Append(letter).Append(@"=\??\").Append(target.TrimEnd('\\'));
        }

        return lines.ToString();
    }

    /// <summary>
    /// Every mapped network drive visible here, as <c>Z:=\\server\share</c>.
    /// A letter that is only a subst drive is not a network drive and is left out.
    /// </summary>
    public static string NetworkList()
    {
        var lines = new StringBuilder();
        foreach (char letter in Letters())
        {
            string? target = NetworkDrives.TargetOf(letter);
            if (target is null)
                continue;

            if (lines.Length > 0)
                lines.Append(';');
            lines.Append(letter).Append('=').Append(target.TrimEnd('\\'));
        }

        return lines.ToString();
    }

    /// <summary>
    /// Reads one list back. Each entry is <c>letter=target</c>, and entries are
    /// separated by semicolons. A target may itself contain <c>=</c>, which is
    /// why only the first one splits the letter off. Empty text is an empty list.
    /// </summary>
    public static List<ListedDrive> Parse(string text)
    {
        var drives = new List<ListedDrive>();
        if (string.IsNullOrWhiteSpace(text))
            return drives;

        // A subst target is written as X:=\??\C:\Real, and the second '=' is
        // part of the path. Splitting on every '=' would cut it in half, so the
        // letter is taken from the front and the first '=' only.
        foreach (string entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int split = entry.IndexOf('=');
            if (split < 1 || split > 2 || !char.IsAsciiLetter(entry[0]) || split + 1 >= entry.Length)
                throw new ArgumentException($"'{entry}' is not a drive letter followed by a path.");
            if (split == 2 && entry[1] != ':')
                throw new ArgumentException($"'{entry}' is not a drive letter followed by a path.");

            drives.Add(new ListedDrive(char.ToUpperInvariant(entry[0]), entry[(split + 1)..]));
        }

        return drives;
    }

    /// <summary>
    /// Rewrites a path whose drive letter appears in one of the lists. A subst
    /// target is stored with the <c>\??\</c> prefix and comes back as the real
    /// folder. A network target is already a UNC path. A letter in neither list
    /// is left for the live lookup, which still works when this process can see it.
    /// </summary>
    public static string Resolve(string path, IReadOnlyList<ListedDrive> subst, IReadOnlyList<ListedDrive> network)
    {
        char letter = ScanTargets.DriveLetter(path);
        if (letter == '\0')
            return path;

        string? target = Target(letter, subst) ?? Target(letter, network);
        if (target is null)
            return path;

        if (target.StartsWith(@"\??\", StringComparison.Ordinal))
            target = target[4..];
        target = target.TrimEnd('\\');

        string relative = path.Length <= 3 ? "" : path[3..];
        return relative.Length == 0 ? target : Path.Combine(target, relative);
    }

    private static string? Target(char letter, IReadOnlyList<ListedDrive> drives)
    {
        foreach (ListedDrive drive in drives)
        {
            if (drive.Letter == letter)
                return drive.Target;
        }

        return null;
    }

    private static IEnumerable<char> Letters()
    {
        for (char letter = 'A'; letter <= 'Z'; letter++)
            yield return letter;
    }
}
