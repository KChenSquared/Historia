namespace Historia.Services.PowerTools;

/// <summary>
/// Appends ignore entries to the repository-root <c>.gitignore</c>. Each entry anchors one
/// repo-relative path at the root, so it ignores exactly that file and nothing with a similar name.
/// </summary>
public static class GitIgnoreWriter
{
    private const string FileName = ".gitignore";

    /// <summary>False for a path that cannot be written as a single ignore pattern: a line break
    /// ends the pattern, so such a path is skipped rather than written wrongly.</summary>
    public static bool CanWrite(string repoRelativePath)
        => !string.IsNullOrEmpty(repoRelativePath)
            && repoRelativePath.AsSpan().IndexOfAny('\r', '\n') < 0;

    /// <summary>
    /// The pattern line that ignores <paramref name="repoRelativePath"/>: a leading <c>/</c> and the
    /// path with the characters gitignore treats specially escaped. A <c>#</c> or <c>!</c> opening
    /// the path is escaped too, so the line never reads as a comment or negation if the anchor is
    /// ever dropped.
    /// </summary>
    /// <exception cref="ArgumentException">The path cannot be written (see <see cref="CanWrite"/>).</exception>
    public static string EntryFor(string repoRelativePath)
    {
        if (!CanWrite(repoRelativePath))
            throw new ArgumentException("The path cannot be written as a single ignore pattern.", nameof(repoRelativePath));

        var sb = new StringBuilder(repoRelativePath.Length + 8);
        sb.Append('/');
        for (var i = 0; i < repoRelativePath.Length; i++)
        {
            var c = repoRelativePath[i];
            var escape = c switch
            {
                '*' or '?' or '[' or '\\' => true,
                '#' or '!' => i == 0,
                // Git strips unescaped trailing spaces; escaping the last one keeps all of them.
                ' ' => i == repoRelativePath.Length - 1,
                _ => false,
            };
            if (escape)
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Adds an entry for each path to <c>.gitignore</c> at the repository root, creating the file
    /// when it is missing. Entries already present as a whole line, duplicates and paths that cannot
    /// be written as one pattern are skipped. The file's line ending is kept (LF for a new file) and
    /// a missing final newline is added first. Runs inside the repository write lock.
    /// </summary>
    /// <returns>The number of lines added.</returns>
    /// <exception cref="InvalidOperationException">The repository is bare and has no working tree, or
    /// the existing <c>.gitignore</c> is UTF-16.</exception>
    public static async Task<int> AppendAsync(RepositoryContext ctx, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (ctx.IsBare)
            throw new InvalidOperationException("A bare repository has no working tree to ignore files in.");

        var entries = paths.Where(CanWrite).Select(EntryFor).Distinct(StringComparer.Ordinal).ToList();
        if (entries.Count == 0)
            return 0;

        var target = Path.Combine(ctx.RepositoryPath, FileName);
        using var op = ctx.BeginSelfOperation();
        var added = await ctx.WithWriteLockAsync(innerCt => AppendCoreAsync(target, entries, innerCt), ct, label: "Ignore")
            .ConfigureAwait(false);
        AppLog.Write(AppLogLevel.Debug, "gitignore", $"Appended {added} entr{(added == 1 ? "y" : "ies")}");
        return added;
    }

    // Only the new lines are written, in append mode: the existing bytes (a BOM, bytes that are not
    // UTF-8, an edit saved by another program) stay exactly as they are, and a failed write can
    // only leave part of the new lines.
    private static async Task<int> AppendCoreAsync(string target, List<string> entries, CancellationToken ct)
    {
        var existing = File.Exists(target) ? await File.ReadAllBytesAsync(target, ct).ConfigureAwait(false) : [];
        if (existing.Length >= 2 && ((existing[0] == 0xFF && existing[1] == 0xFE) || (existing[0] == 0xFE && existing[1] == 0xFF)))
            throw new InvalidOperationException(".gitignore is saved as UTF-16, which git does not read. Save it as UTF-8 and try again.");

        // The text is only compared against, so a byte that is not UTF-8 cannot change the file.
        var text = Encoding.UTF8.GetString(existing).TrimStart('﻿');
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var present = text
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .ToHashSet(StringComparer.Ordinal);
        var toAdd = entries.Where(e => !present.Contains(e)).ToList();
        if (toAdd.Count == 0)
            return 0;

        var sb = new StringBuilder();
        if (existing.Length > 0 && existing[^1] != (byte)'\n')
            sb.Append(newline);
        foreach (var entry in toAdd)
            sb.Append(entry).Append(newline);

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(sb.ToString());
        await using var stream = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        return toAdd.Count;
    }
}
