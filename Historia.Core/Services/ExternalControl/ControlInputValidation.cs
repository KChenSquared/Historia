namespace Historia.Services.ExternalControl;

/// <summary>Validation of everything an external caller supplies, used on both ends of the pipe so
/// a hostile value is refused before it can reach a git argument list. Pure functions, except
/// <see cref="MatchOnDiskCase"/>, which reads a directory listing.</summary>
public static class ControlInputValidation
{
    public const int MaxRevisionLength = 256;
    public const int MaxCommitMessageBytes = 16 * 1024;
    public const int MaxNoteLength = 200;

    /// <summary>A revision is passed to git as a single argument after <c>--end-of-options</c>;
    /// it must still never look like an option, span lines, or be unreasonably long.</summary>
    public static bool TryValidateRevision(string? rev, out string? error)
    {
        if (string.IsNullOrEmpty(rev))
        {
            error = "A revision is required.";
            return false;
        }

        if (rev.Length > MaxRevisionLength)
        {
            error = $"A revision is at most {MaxRevisionLength} characters.";
            return false;
        }

        if (rev[0] == '-')
        {
            error = "A revision cannot start with '-'.";
            return false;
        }

        foreach (var c in rev)
        {
            if (char.IsControl(c))
            {
                error = "A revision cannot contain control characters.";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> (against <paramref name="cwd"/> unless rooted) to a path
    /// relative to <paramref name="repoRoot"/>, with forward slashes. Rejects empty paths, NUL, and
    /// anything that escapes the root. The file need not exist, so deleted files resolve.
    /// </summary>
    public static bool TryResolveRepoRelativePath(string repoRoot, string cwd, string? path, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || string.IsNullOrEmpty(repoRoot))
            return false;

        string full;
        string root;
        try
        {
            root = System.IO.Path.GetFullPath(repoRoot);
            full = System.IO.Path.GetFullPath(path, string.IsNullOrEmpty(cwd) ? root : System.IO.Path.GetFullPath(cwd));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var rootWithSeparator = System.IO.Path.TrimEndingDirectorySeparator(root) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, comparison) || full.Length == rootWithSeparator.Length)
            return false;

        var candidate = full[rootWithSeparator.Length..]
            .Replace(System.IO.Path.DirectorySeparatorChar, '/')
            .Replace(System.IO.Path.AltDirectorySeparatorChar, '/');
        if (candidate.Length == 0)
            return false;

        relative = candidate;
        return true;
    }

    /// <summary>
    /// On a case-insensitive file system, rewrites each segment of the repo-relative
    /// <paramref name="relative"/> that exists under <paramref name="repoRoot"/> to its spelling on
    /// disk, because git matches a path case-sensitively even there: <c>src/foo.cs</c> would find
    /// nothing for a tracked <c>Src/Foo.cs</c>. Segments that are not on disk (a deleted file) are
    /// kept as given. Reads the directory listing only.
    /// </summary>
    public static string MatchOnDiskCase(string repoRoot, string relative)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()))
            return relative;

        var segments = relative.Split('/');
        var dir = repoRoot;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        for (var i = 0; i < segments.Length; i++)
        {
            string? onDisk = null;
            try
            {
                if (!Directory.Exists(dir))
                    break;
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*", options))
                {
                    var name = System.IO.Path.GetFileName(entry);
                    if (string.Equals(name, segments[i], StringComparison.Ordinal))
                    {
                        onDisk = name;
                        break;
                    }
                    if (onDisk is null && string.Equals(name, segments[i], StringComparison.OrdinalIgnoreCase))
                        onDisk = name;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }

            if (onDisk is null)
                break;
            segments[i] = onDisk;
            dir = System.IO.Path.Combine(dir, onDisk);
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Normalizes a proposed commit message: drops one leading BOM, converts CRLF to LF, trims
    /// trailing whitespace on every line and at the end. Fails when the result is empty, holds a
    /// control character other than newline or tab, or exceeds <see cref="MaxCommitMessageBytes"/>.
    /// </summary>
    public static bool NormalizeCommitMessage(string? text, out string normalized, out string? error)
    {
        normalized = string.Empty;
        if (text is null)
        {
            error = "A commit message is required.";
            return false;
        }

        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd();

        var result = string.Join('\n', lines).TrimEnd();
        if (result.Length == 0)
        {
            error = "The commit message is empty.";
            return false;
        }

        foreach (var c in result)
        {
            if (char.IsControl(c) && c is not ('\n' or '\t'))
            {
                error = "The commit message contains a control character.";
                return false;
            }
        }

        if (Encoding.UTF8.GetByteCount(result) > MaxCommitMessageBytes)
        {
            error = $"The commit message is longer than {MaxCommitMessageBytes / 1024} KiB.";
            return false;
        }

        normalized = result;
        error = null;
        return true;
    }

    /// <summary>
    /// Normalizes the short message a caller attaches to a queued item: trims it and collapses every
    /// run of whitespace, line breaks included, to one space. Fails when the result is empty, longer
    /// than <see cref="MaxNoteLength"/> characters, or holds a control character.
    /// </summary>
    public static bool NormalizeNote(string? text, out string? normalized, out string? error)
    {
        normalized = null;
        var sb = new StringBuilder(text?.Length ?? 0);
        var pendingSpace = false;
        foreach (var c in text ?? string.Empty)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (char.IsControl(c))
            {
                error = "The note contains a control character.";
                return false;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }

        if (sb.Length == 0)
        {
            error = "The note is empty.";
            return false;
        }

        if (sb.Length > MaxNoteLength)
        {
            error = $"The note is at most {MaxNoteLength} characters.";
            return false;
        }

        normalized = sb.ToString();
        error = null;
        return true;
    }
}
