namespace Historia.Services.Diff;

/// <summary>
/// Parses the unified-diff text of a single file's patch into the shared
/// <see cref="DiffHunk"/>/<see cref="DiffLine"/> model.
/// </summary>
public static partial class UnifiedDiffParser
{
    public static IReadOnlyList<DiffHunk> Parse(string patchText)
    {
        if (string.IsNullOrEmpty(patchText))
            return [];

        var hunks = new List<DiffHunk>();
        var lines = patchText.Split('\n');

        DiffHunk? pendingHeader = null;
        List<DiffLine>? current = null;
        int oldNo = 0, newNo = 0;
        int oldStart = 0, oldCount = 0, newStart = 0, newCount = 0;
        string header = string.Empty;

        void Flush()
        {
            if (pendingHeader is null || current is null)
                return;
            hunks.Add(pendingHeader with { Lines = current });
            pendingHeader = null;
            current = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;

            var m = HunkHeaderRegex().Match(line);
            if (m.Success)
            {
                Flush();
                oldStart = int.Parse(m.Groups["os"].Value, CultureInfo.InvariantCulture);
                oldCount = m.Groups["oc"].Success ? int.Parse(m.Groups["oc"].Value, CultureInfo.InvariantCulture) : 1;
                newStart = int.Parse(m.Groups["ns"].Value, CultureInfo.InvariantCulture);
                newCount = m.Groups["nc"].Success ? int.Parse(m.Groups["nc"].Value, CultureInfo.InvariantCulture) : 1;
                header = line;
                oldNo = oldStart;
                newNo = newStart;
                current = [];
                pendingHeader = new DiffHunk
                {
                    OldStart = oldStart,
                    OldLineCount = oldCount,
                    NewStart = newStart,
                    NewLineCount = newCount,
                    Header = header,
                    Lines = [],
                };
                continue;
            }

            if (current is null)
                continue; // Preamble (---/+++/index) before the first hunk.

            if (line.Length == 0)
                continue; // Trailing split artifact — git prefixes real context lines with a space.

            switch (line[0])
            {
                case '+':
                    current.Add(new DiffLine
                    {
                        Type = DiffLineType.Addition,
                        Content = line[1..],
                        OldLineNumber = null,
                        NewLineNumber = newNo++,
                    });
                    break;
                case '-':
                    current.Add(new DiffLine
                    {
                        Type = DiffLineType.Deletion,
                        Content = line[1..],
                        OldLineNumber = oldNo++,
                        NewLineNumber = null,
                    });
                    break;
                case '\\': // "\ No newline at end of file"
                    break;
                default: // ' ' context (or any other unified-diff marker)
                    current.Add(new DiffLine
                    {
                        Type = DiffLineType.Context,
                        Content = line[1..],
                        OldLineNumber = oldNo++,
                        NewLineNumber = newNo++,
                    });
                    break;
            }
        }

        Flush();
        return hunks;
    }

    [GeneratedRegex(@"^@@ -(?<os>\d+)(?:,(?<oc>\d+))? \+(?<ns>\d+)(?:,(?<nc>\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeaderRegex();
}
