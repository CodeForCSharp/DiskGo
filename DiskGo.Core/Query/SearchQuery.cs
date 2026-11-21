using System.Globalization;
using System.Text.RegularExpressions;

namespace DiskGo.Core;

/// <summary>
/// One include or exclude expression, in the form a scan filter is written.
///
/// A term is a name pattern (<c>*.log</c>), a size (<c>&gt;=500m</c>, or
/// <c>a&gt;=500m</c> for the allocated size), a date (<c>&lt;2020/01/01</c>,
/// <c>&gt;=today-7</c>, <c>&gt;now-1h</c>), a length (<c>pathlen&gt;200</c>,
/// <c>namelen=1</c>), or <c>=file</c> and <c>=folder</c>. A space means every
/// term must match. A <c>|</c> means any alternative may. <c>!</c> in front of a
/// term inverts it. A term in quotes is always a name, so <c>"=0"</c> looks for
/// that text instead of a zero size. A <c>\</c> in a name term matches the
/// whole path, whatever the caller asked.
/// </summary>
internal sealed class SearchQuery
{
    private readonly Alternative[] _alternatives;
    private readonly bool _fullPath;

    private SearchQuery(Alternative[] alternatives, bool fullPath)
    {
        _alternatives = alternatives;
        _fullPath = fullPath;
    }

    /// <summary>An expression that matches everything, including a folder.</summary>
    public static SearchQuery MatchAll { get; } = new([], fullPath: false);

    public bool IsEmpty => _alternatives.Length == 0;

    /// <summary>True when any alternative matches <paramref name="file"/>.</summary>
    public bool Matches(FileEntry file) => Matches(file, isFolder: false);

    /// <summary>
    /// True when any alternative matches a directory. Size terms compare against
    /// zero, because a directory is excluded by its path, not by what it holds.
    /// </summary>
    public bool MatchesFolder(string name, string fullPath)
        => Matches(name, fullPath, size: 0, allocated: 0, modified: DateTime.MinValue, isFolder: true);

    /// <summary>
    /// True when <paramref name="fullPath"/> is excluded by a name term alone.
    /// An alternative that also tests a size, a date or a length does not
    /// qualify, because a file inside could still pass it.
    /// </summary>
    public bool ExcludesDirectory(string name, string fullPath)
    {
        foreach (Alternative alternative in _alternatives)
        {
            if (alternative.ExcludesDirectory(name, fullPath, _fullPath))
                return true;
        }

        return false;
    }

    private bool Matches(FileEntry file, bool isFolder)
    {
        if (_alternatives.Length == 0)
            return true;

        string? fullPath = null;
        foreach (Alternative alternative in _alternatives)
        {
            if (alternative.Matches(file, isFolder, _fullPath, ref fullPath))
                return true;
        }

        return false;
    }

    private bool Matches(string name, string fullPath, long size, long allocated, DateTime modified, bool isFolder)
    {
        if (_alternatives.Length == 0)
            return true;

        foreach (Alternative alternative in _alternatives)
        {
            if (alternative.Matches(name, fullPath, size, allocated, modified, isFolder, _fullPath))
                return true;
        }

        return false;
    }

    /// <summary>The terms that appear, identically, on every side of a bar. They are marked so they can be lifted out.</summary>
    private static Term[] SharedTerms(List<Alternative> alternatives)
    {
        Term[] first = alternatives[0].Terms;
        var shared = new List<Term>();
        foreach (Term candidate in first)
        {
            if (alternatives.All(alternative => alternative.Terms.Any(term => Same(term, candidate))))
                shared.Add(candidate);
        }

        return [.. shared];
    }

    private static bool Same(Term left, Term right)
        => left.Kind == right.Kind && left.Inverted == right.Inverted && left.Number == right.Number
            && left.Comparison == right.Comparison && left.When == right.When && left.Tolerance == right.Tolerance
            && left.Pattern?.ToString() == right.Pattern?.ToString();

    /// <summary>Reads one expression. A regular expression is kept whole; everything else is split as described above.</summary>
    public static SearchQuery Parse(string text, bool fullPath, bool regex)
    {
        if (string.IsNullOrWhiteSpace(text))
            return MatchAll;

        if (regex)
        {
            var pattern = new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            var term = new Term(TermKind.Name, false, pattern, 0, 0, default, default);
            return new SearchQuery([new Alternative([term])], fullPath);
        }

        var alternatives = new List<Alternative>();
        List<string> words = SplitAnd(text);
        int bars = words.Count(static word => word.Contains('|'));
        if (bars == 1)
        {
            // "*.bin|*.txt >=1k" is one condition shared by several names, so the
            // bar is expanded before the words are grouped. A bar inside more
            // than one word is left as written.
            int at = words.FindIndex(static word => word.Contains('|'));
            foreach (string option in SplitOr(words[at]))
            {
                var group = new List<string>(words);
                group[at] = option;
                Term[] terms = [.. group.SelectMany(SplitOr).Select(ParseTerm)];
                if (terms.Length > 0)
                    alternatives.Add(new Alternative(terms));
            }
        }
        else
        {
            foreach (string branch in SplitOr(text))
            {
                Term[] terms = [.. SplitAnd(branch).Select(ParseTerm)];
                if (terms.Length > 0)
                    alternatives.Add(new Alternative(terms));
            }
        }

        // A space binds tighter than a bar, so "*.mp3|*.wav dance" is the files
        // that are one of those two types and also contain "dance", not every
        // mp3 plus the wav files that contain it.
        if (alternatives.Count > 1)
        {
            Term[] shared = SharedTerms(alternatives);
            if (shared.Length > 0)
            {
                var branches = new Alternative[alternatives.Count];
                for (int i = 0; i < alternatives.Count; i++)
                    branches[i] = new Alternative(alternatives[i].Terms.Where(term => !shared.Any(kept => Same(kept, term))).ToArray());
                alternatives = [new Alternative([new Term(TermKind.Either, false, null, 0, 0, default, default, default, branches), .. shared])];
            }
        }

        return alternatives.Count == 0 ? MatchAll : new SearchQuery([.. alternatives], fullPath);
    }

    private static Term ParseTerm(string raw)
    {
        string text = raw.Trim();
        bool inverted = false;
        if (text.StartsWith('!'))
        {
            inverted = true;
            text = text[1..].Trim();
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            return NameTerm(text[1..^1], inverted);

        if (text.Equals("=file", StringComparison.OrdinalIgnoreCase))
            return new Term(TermKind.FilesOnly, inverted, null, 0, 0, default, default);
        if (text.Equals("=folder", StringComparison.OrdinalIgnoreCase))
            return new Term(TermKind.FoldersOnly, inverted, null, 0, 0, default, default);

        if (TryLength(text, "pathlen", out Comparison comparison, out long length))
            return new Term(TermKind.PathLength, inverted, null, length, 0, comparison, default);
        if (TryLength(text, "namelen", out comparison, out length))
            return new Term(TermKind.NameLength, inverted, null, length, 0, comparison, default);

        if (TrySize(text, out bool allocated, out comparison, out long bytes))
            return new Term(allocated ? TermKind.Allocated : TermKind.Size, inverted, null, bytes, 0, comparison, default);

        if (TryDate(text, out comparison, out DateTime when, out TimeSpan tolerance))
            return new Term(TermKind.Modified, inverted, null, 0, 0, comparison, when, tolerance);

        return NameTerm(text, inverted);
    }

    private static Term NameTerm(string text, bool inverted)
    {
        // A separator in the pattern is a path, so it is matched against the
        // whole path even when the caller asked for names only. The escaped
        // form is what the pattern is stored as. Without a wildcard the text
        // may occur anywhere in the name, which is how a word is searched for;
        // a wildcard still has to match the whole name.
        bool contains = !text.Contains('*') && !text.Contains('?');
        string body = Regex.Escape(text).Replace(Regex.Escape("*"), ".*", StringComparison.Ordinal).Replace(Regex.Escape("?"), ".", StringComparison.Ordinal);
        string pattern = contains ? body : "^" + body + "$";
        var compiled = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        return new Term(TermKind.Name, inverted, compiled, 0, 0, default, default);
    }

    /// <summary>Splits on <c>|</c> that are not inside quotes. A doubled quote is one quote, not the end of a quoted term.</summary>
    private static List<string> SplitOr(string text)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                    continue;
                }

                quoted = !quoted;
            }

            if (character == '|' && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>Splits one alternative into terms on spaces that are not inside quotes.</summary>
    private static List<string> SplitAnd(string text)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character == '"')
            {
                quoted = !quoted;
                current.Append(character);
                continue;
            }

            if (character == ' ' && !quoted)
            {
                if (current.Length > 0)
                    parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    private static bool TryLength(string text, string name, out Comparison comparison, out long length)
    {
        comparison = default;
        length = 0;
        if (!text.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            return false;

        string rest = text[name.Length..];
        if (rest.Length > 0 && char.IsLetter(rest[0]))
            return false;

        return TryComparison(rest, out comparison, out rest)
            && long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out length);
    }

    private static bool TrySize(string text, out bool allocated, out Comparison comparison, out long bytes)
    {
        allocated = false;
        comparison = default;
        bytes = 0;
        string rest = text;
        if (rest.Length > 0 && (rest[0] == 'a' || rest[0] == 'A'))
        {
            allocated = true;
            rest = rest[1..];
        }

        if (!TryComparison(rest, out comparison, out rest))
            return false;

        return TryQuantity(rest, out bytes);
    }

    /// <summary>A number followed by an optional unit. <c>500m</c> and <c>500mb</c> are both 500 megabytes.</summary>
    private static bool TryQuantity(string text, out long bytes)
    {
        bytes = 0;
        int digits = 0;
        while (digits < text.Length && char.IsDigit(text[digits]))
            digits++;
        if (digits == 0 || !long.TryParse(text[..digits], NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
            return false;

        string unit = text[digits..].Trim();
        long scale = unit.ToLowerInvariant() switch
        {
            "" => 1,
            "k" or "kb" => 1024,
            "m" or "mb" => 1024L * 1024,
            "g" or "gb" => 1024L * 1024 * 1024,
            "t" or "tb" => 1024L * 1024 * 1024 * 1024,
            _ => 0,
        };
        if (scale == 0)
            return false;

        bytes = number * scale;
        return true;
    }

    private static bool TryDate(string text, out Comparison comparison, out DateTime when, out TimeSpan tolerance)
    {
        comparison = default;
        when = default;
        tolerance = default;
        if (!TryComparison(text, out comparison, out string rest))
            return false;

        if (TryClock(rest, out when, out tolerance))
            return true;

        if (rest.StartsWith("today", StringComparison.OrdinalIgnoreCase))
            return TryOffset(rest["today".Length..], DateTime.Today, out when);
        if (rest.StartsWith("now", StringComparison.OrdinalIgnoreCase))
            return TryOffset(rest["now".Length..], DateTime.Now, out when);

        return DateTime.TryParseExact(rest, "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out when)
            || DateTime.TryParseExact(rest, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out when);
    }

    /// <summary>A time of day, such as <c>13:00:00</c> or <c>1:00pm</c>. The date is left blank; only the clock is compared.</summary>
    private static bool TryClock(string text, out DateTime when, out TimeSpan tolerance)
    {
        when = default;
        tolerance = default;
        string[] formats = ["H:mm:ss", "HH:mm:ss", "H:mm", "HH:mm", "h:mmtt", "h:mm:sstt", "htt", "h:mm tt", "h tt"];
        if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime parsed))
            return false;

        when = DateTime.MinValue.Add(parsed.TimeOfDay);
        tolerance = text.Count(static character => character == ':') >= 2 ? TimeSpan.Zero : TimeSpan.FromMinutes(1);
        return true;
    }

    /// <summary>Applies a <c>+5</c>, <c>-7</c>, <c>-5m</c>, <c>-1h</c> or <c>-30s</c> suffix. No suffix leaves the instant unchanged.</summary>
    private static bool TryOffset(string suffix, DateTime origin, out DateTime when)
    {
        when = origin;
        if (suffix.Length == 0)
            return true;

        int sign = suffix[0] == '+' ? 1 : suffix[0] == '-' ? -1 : 0;
        if (sign == 0)
            return false;

        string magnitude = suffix[1..];
        char unit = magnitude.Length > 0 && char.IsLetter(magnitude[^1]) ? char.ToLowerInvariant(magnitude[^1]) : '\0';
        if (unit != '\0')
            magnitude = magnitude[..^1];
        if (!int.TryParse(magnitude, NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
            return false;

        TimeSpan offset = unit switch
        {
            '\0' or 'd' => TimeSpan.FromDays(amount),
            'h' => TimeSpan.FromHours(amount),
            'm' => TimeSpan.FromMinutes(amount),
            's' => TimeSpan.FromSeconds(amount),
            _ => TimeSpan.MinValue,
        };
        if (offset == TimeSpan.MinValue)
            return false;

        when = origin + sign * offset;
        return true;
    }

    private static bool TryComparison(string text, out Comparison comparison, out string rest)
    {
        (Comparison kind, string token)[] operators = [(Comparison.GreaterOrEqual, ">="), (Comparison.LessOrEqual, "<="), (Comparison.NotEqual, "!="), (Comparison.Greater, ">"), (Comparison.Less, "<"), (Comparison.Equal, "=")];
        foreach ((Comparison kind, string token) in operators)
        {
            if (text.StartsWith(token, StringComparison.Ordinal))
            {
                comparison = kind;
                rest = text[token.Length..];
                return true;
            }
        }

        comparison = default;
        rest = text;
        return false;
    }

    private enum TermKind
    {
        Name,
        Size,
        Allocated,
        Modified,
        PathLength,
        NameLength,
        FilesOnly,
        FoldersOnly,
        Either,
    }

    private enum Comparison
    {
        Equal,
        NotEqual,
        Greater,
        GreaterOrEqual,
        Less,
        LessOrEqual,
    }

    private readonly record struct Term(
        TermKind Kind, bool Inverted, Regex? Pattern, long Number, long Unused, Comparison Comparison, DateTime When, TimeSpan Tolerance = default, Alternative[]? Branches = null)
    {
        public bool IsName => Kind == TermKind.Name;

        public bool Matches(FileEntry file, bool isFolder, bool fullPathSetting, ref string? fullPath)
        {
            bool matched = Kind switch
            {
                TermKind.Name => NameMatches(file, fullPathSetting, ref fullPath),
                TermKind.Size => Compare(file.Size, Number),
                TermKind.Allocated => Compare(file.AllocatedSize, Number),
                TermKind.Modified => DateMatches(file.Modified),
                TermKind.PathLength => Compare((fullPath ??= file.FullPath).Length, Number),
                TermKind.NameLength => Compare(file.Name.Length, Number),
                TermKind.FilesOnly => !isFolder,
                TermKind.FoldersOnly => isFolder,
                TermKind.Either => AnyBranchMatches(Branches, file, isFolder, fullPathSetting, ref fullPath),
                _ => false,
            };
            return Inverted ? !matched : matched;
        }

        public bool Matches(string name, string fullPath, long size, long allocated, DateTime modified, bool isFolder, bool fullPathSetting)
        {
            bool matched = Kind switch
            {
                TermKind.Name => NameMatches(name, fullPath, fullPathSetting),
                TermKind.Size => Compare(size, Number),
                TermKind.Allocated => Compare(allocated, Number),
                TermKind.Modified => DateMatches(modified),
                TermKind.PathLength => Compare(fullPath.Length, Number),
                TermKind.NameLength => Compare(name.Length, Number),
                TermKind.FilesOnly => !isFolder,
                TermKind.FoldersOnly => isFolder,
                TermKind.Either => Branches is not null && Branches.Any(branch => branch.Matches(name, fullPath, size, allocated, modified, isFolder, fullPathSetting)),
                _ => false,
            };
            return Inverted ? !matched : matched;
        }

        private static bool AnyBranchMatches(Alternative[]? branches, FileEntry file, bool isFolder, bool fullPathSetting, ref string? fullPath)
        {
            if (branches is null)
                return false;
            foreach (Alternative branch in branches)
            {
                if (branch.Matches(file, isFolder, fullPathSetting, ref fullPath))
                    return true;
            }
            return false;
        }

        private bool NameMatches(FileEntry file, bool fullPathSetting, ref string? fullPath)
        {
            bool useFullPath = fullPathSetting || (Pattern?.ToString().Contains(@"\\", StringComparison.Ordinal) ?? false);
            string subject = useFullPath ? (fullPath ??= file.FullPath) : file.Name;
            return Pattern?.IsMatch(subject) ?? false;
        }

        private bool NameMatches(string name, string fullPath, bool fullPathSetting)
        {
            string subject = fullPathSetting || (Pattern?.ToString().Contains(@"\\", StringComparison.Ordinal) ?? false) ? fullPath : name;
            return Pattern?.IsMatch(subject) ?? false;
        }

        private bool DateMatches(DateTime modified)
        {
            if (Tolerance > TimeSpan.Zero)
            {
                var clock = TimeOnly.FromDateTime(modified);
                var wanted = TimeOnly.FromDateTime(When);
                return Comparison switch
                {
                    Comparison.Equal => clock >= wanted && clock < wanted.Add(Tolerance),
                    Comparison.NotEqual => clock < wanted || clock >= wanted.Add(Tolerance),
                    Comparison.Greater => clock >= wanted.Add(Tolerance),
                    Comparison.GreaterOrEqual => clock >= wanted,
                    Comparison.Less => clock < wanted,
                    Comparison.LessOrEqual => clock < wanted.Add(Tolerance),
                    _ => false,
                };
            }

            int order = DateTime.Compare(modified, When);
            return Comparison switch
            {
                Comparison.Equal => order == 0,
                Comparison.NotEqual => order != 0,
                Comparison.Greater => order > 0,
                Comparison.GreaterOrEqual => order >= 0,
                Comparison.Less => order < 0,
                Comparison.LessOrEqual => order <= 0,
                _ => false,
            };
        }

        private bool Compare(long value, long threshold) => Comparison switch
        {
            Comparison.Equal => value == threshold,
            Comparison.NotEqual => value != threshold,
            Comparison.Greater => value > threshold,
            Comparison.GreaterOrEqual => value >= threshold,
            Comparison.Less => value < threshold,
            Comparison.LessOrEqual => value <= threshold,
            _ => false,
        };
    }

    private readonly record struct Alternative(Term[] Terms)
    {
        public bool Matches(FileEntry file, bool isFolder, bool fullPathSetting, ref string? fullPath)
        {
            foreach (Term term in Terms)
            {
                if (!term.Matches(file, isFolder, fullPathSetting, ref fullPath))
                    return false;
            }

            return true;
        }

        public bool Matches(string name, string fullPath, long size, long allocated, DateTime modified, bool isFolder, bool fullPathSetting)
        {
            foreach (Term term in Terms)
            {
                if (!term.Matches(name, fullPath, size, allocated, modified, isFolder, fullPathSetting))
                    return false;
            }

            return true;
        }

        /// <summary>True when every term is a name term and together they match this directory.</summary>
        public bool ExcludesDirectory(string name, string fullPath, bool fullPathSetting)
        {
            if (Terms.Length == 0)
                return false;

            foreach (Term term in Terms)
            {
                if (!term.IsName)
                    return false;
                if (!term.Matches(name, fullPath, 0, 0, default, true, fullPathSetting))
                    return false;
            }

            return true;
        }
    }
}
