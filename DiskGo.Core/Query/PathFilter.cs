namespace DiskGo.Core;

/// <summary>
/// Include and exclude rules using <c>*</c> and <c>?</c> wildcards, plus the
/// attribute rules that a name pattern cannot express.
///
/// Patterns are separated by semicolons, and matching ignores case because
/// Windows file names do. Attribute rules apply to every file, whether or not
/// any pattern was given.
/// </summary>
internal sealed class PathFilter
{
    private readonly SearchQuery _include;
    private readonly SearchQuery _exclude;
    private readonly bool _includeHidden;
    private readonly FileAttributes _excludedAttributes;

    private PathFilter(SearchQuery include, SearchQuery exclude, bool includeHidden, FileAttributes excludedAttributes)
    {
        _include = include;
        _exclude = exclude;
        _includeHidden = includeHidden;
        _excludedAttributes = excludedAttributes;
    }

    public static PathFilter Parse(string include, string exclude, bool fullPath)
        => Parse(include, exclude, fullPath, includeHidden: true, excludedAttributes: 0);

    public static PathFilter Parse(string include, string exclude, bool fullPath, bool includeHidden, FileAttributes excludedAttributes)
        => Parse(include, exclude, fullPath, includeHidden, excludedAttributes, regex: false);

    /// <param name="regex">
    /// When set, each of <paramref name="include"/> and <paramref name="exclude"/> is one regular
    /// expression. Otherwise each is a list of <c>*</c> and <c>?</c> patterns separated by semicolons.
    /// </param>
    public static PathFilter Parse(
        string include, string exclude, bool fullPath, bool includeHidden, FileAttributes excludedAttributes, bool regex)
    {
        return new PathFilter(SearchQuery.Parse(include, fullPath, regex), SearchQuery.Parse(exclude, fullPath, regex), includeHidden, excludedAttributes);
    }

    public bool Accepts(FileEntry file)
    {
        if (!_includeHidden && (file.Attributes & FileAttributes.Hidden) != 0)
            return false;

        if (_excludedAttributes != 0 && (file.Attributes & _excludedAttributes) != 0)
            return false;

        if (!_include.IsEmpty && !_include.Matches(file))
            return false;

        return _exclude.IsEmpty || !_exclude.Matches(file);
    }

    /// <summary>
    /// True when the exclude expression names <paramref name="directory"/> and
    /// nothing else, so the walk can leave it unread. A term that also demands
    /// a size or a date is not enough: those are decided per file, and skipping
    /// the directory would drop files the term would have kept.
    /// </summary>
    public bool ExcludesDirectory(string directory)
    {
        if (_exclude.IsEmpty)
            return false;

        string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
        return _exclude.ExcludesDirectory(name, directory);
    }
}
