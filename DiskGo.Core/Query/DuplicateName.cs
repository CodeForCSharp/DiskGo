using System.Globalization;

namespace DiskGo.Core;

/// <summary>
/// The name two files are compared by when looking for duplicates.
///
/// Windows names a copy by appending <c> - Copy</c>, and a further copy by
/// appending <c> (2)</c>. Those decorations are not part of the name, so
/// <c>notes.txt</c>, <c>notes - Copy.txt</c> and <c>notes - copy (2).txt</c>
/// are the same file for this purpose. The extension is kept: only the part
/// before the last dot is rewritten, and only one decoration of each kind is
/// removed.
/// </summary>
public static class DuplicateName
{
    private const string CopySuffix = " - copy";

    public static string Normalize(string name)
    {
        int dot = name.LastIndexOf('.');
        string stem = dot < 0 ? name : name[..dot];
        string extension = dot < 0 ? "" : name[dot..];

        string stripped = StripCopySuffix(StripNumberedSuffix(stem));
        return ReferenceEquals(stripped, stem) ? name : stripped + extension;
    }

    /// <summary>
    /// Removes one <c> (n)</c> from a name that ends in <c>)</c>. The number has
    /// to be a positive integer and the parenthesis has to follow a space, which
    /// is the form Explorer uses. A name such as <c>notes (draft)</c> is left as
    /// it is. The first qualifying parenthesis wins, so <c>notes (1) (2)</c>
    /// becomes <c>notes (1)</c>; a later parenthesis that is not a number, as in
    /// <c>notes (2) (draft)</c>, keeps the whole name.
    /// </summary>
    private static string StripNumberedSuffix(string stem)
    {
        if (stem.Length <= 3 || stem[^1] != ')')
            return stem;

        for (int parenthesis = stem.Length - 2; parenthesis >= 1; parenthesis--)
        {
            if (stem[parenthesis] != '(' || stem[parenthesis - 1] != ' ')
                continue;

            string inside = stem[(parenthesis + 1)..^1];
            if (int.TryParse(inside, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number > 0)
                return stem[..(parenthesis - 1)];
        }

        return stem;
    }

    /// <summary>Removes one trailing <c> - copy</c>, in any capitalisation. A second one is left in place.</summary>
    private static string StripCopySuffix(string stem)
        => stem.Length >= CopySuffix.Length && stem.EndsWith(CopySuffix, StringComparison.OrdinalIgnoreCase)
            ? stem[..^CopySuffix.Length]
            : stem;
}
