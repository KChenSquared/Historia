namespace Historia.Services.Git;

/// <summary>
/// Parses git status --porcelain=v2 -z output into staged, unstaged, untracked, and conflicted entries,
/// and pairs pure worktree moves by blob hash.
/// </summary>
internal static class GitStatusParser
{
    public const string EmptyBlobHash = "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391";
    public const string EmptyBlobSha256Hash = "473a0f4c3be8a93681a267e3b1e9a7dcda1185436fe141f7749120a303721813";

    private static readonly GitObjectId EmptyBlobSha1Id = GitObjectId.Parse(EmptyBlobHash);
    private static readonly GitObjectId EmptyBlobSha256Id = GitObjectId.Parse(EmptyBlobSha256Hash);

    public static bool IsEmptyBlobHash(string? hash) =>
        string.Equals(hash, EmptyBlobHash, StringComparison.OrdinalIgnoreCase)
        || string.Equals(hash, EmptyBlobSha256Hash, StringComparison.OrdinalIgnoreCase);

    public static bool IsEmptyBlobHash(GitObjectId id) =>
        id == EmptyBlobSha1Id || id == EmptyBlobSha256Id;

    public static GitObjectId? ParseId(string? s) =>
        s is { Length: 40 or 64 } && GitObjectId.TryParse(s, out var id) && !id.IsZero ? id : null;

    public static bool IsZeroOid(string? s) =>
        s is { Length: 40 or 64 } && GitObjectId.TryParse(s, out var id) && id.IsZero;

    public sealed record ParsedStatus
    {
        public required IReadOnlyList<FileStatusEntry> Staged { get; init; }
        public required IReadOnlyList<FileStatusEntry> Unstaged { get; init; }
        public required IReadOnlyList<FileStatusEntry> Untracked { get; init; }
        public required IReadOnlyList<FileStatusEntry> Conflicted { get; init; }
        public required int AheadBy { get; init; }
        public required int BehindBy { get; init; }
        public required IReadOnlyList<WorktreeDeletion> WorktreeDeletions { get; init; }
        public GitObjectId? HeadCommitId { get; init; }
        public bool IsDirty => Staged.Count + Unstaged.Count + Untracked.Count + Conflicted.Count > 0;
    }

    public sealed record WorktreeDeletion(string Path, string Mode, string IndexHash)
    {
        public bool IsRegularFile => Mode is "100644" or "100755";
        public GitObjectId? IndexId => GitStatusParser.ParseId(IndexHash);
    }

    public static ParsedStatus Parse(string porcelainV2Z)
    {
        var staged = new List<FileStatusEntry>();
        var unstaged = new List<FileStatusEntry>();
        var untracked = new List<FileStatusEntry>();
        var conflicted = new List<FileStatusEntry>();
        var worktreeDeletions = new List<WorktreeDeletion>();
        GitObjectId? headCommitId = null;
        var aheadBy = 0;
        var behindBy = 0;

        if (string.IsNullOrEmpty(porcelainV2Z))
        {
            return new ParsedStatus
            {
                Staged = staged,
                Unstaged = unstaged,
                Untracked = untracked,
                Conflicted = conflicted,
                AheadBy = aheadBy,
                BehindBy = behindBy,
                WorktreeDeletions = worktreeDeletions,
                HeadCommitId = null,
            };
        }

        var tokens = porcelainV2Z.Split('\0');
        var i = 0;
        while (i < tokens.Length)
        {
            var token = tokens[i++];
            if (string.IsNullOrEmpty(token))
                continue;

            if (token.StartsWith('#'))
            {
                if (token.StartsWith("# branch.oid ", StringComparison.Ordinal))
                {
                    var oidPart = token["# branch.oid ".Length..].Trim();
                    if (!oidPart.Equals("(initial)", StringComparison.OrdinalIgnoreCase))
                        headCommitId = ParseId(oidPart);
                }
                else if (token.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    ParseAheadBehind(token.AsSpan("# branch.ab ".Length), out aheadBy, out behindBy);
                }
                continue;
            }

            if (token.StartsWith("1 ", StringComparison.Ordinal))
            {
                // Format: 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
                var parts = token.Split(' ', 9);
                if (parts.Length < 9)
                    continue;

                AddEntry(staged, unstaged, worktreeDeletions,
                    xy: parts[1],
                    sub: parts[2],
                    path: parts[8],
                    origPath: null,
                    mI: parts[4],
                    hI: parts[7]);
                continue;
            }

            if (token.StartsWith("2 ", StringComparison.Ordinal))
            {
                // Format: 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>
                // Followed by <origPath> as the next NUL-separated token.
                var parts = token.Split(' ', 10);
                if (parts.Length < 10)
                    continue;

                string? origPath = null;
                if (i < tokens.Length)
                {
                    origPath = tokens[i++];
                }

                AddEntry(staged, unstaged, worktreeDeletions,
                    xy: parts[1],
                    sub: parts[2],
                    path: parts[9],
                    origPath: origPath,
                    mI: parts[4],
                    hI: parts[7]);
                continue;
            }

            if (token.StartsWith("u ", StringComparison.Ordinal))
            {
                // Format: u <xy> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
                var parts = token.Split(' ', 11);
                if (parts.Length < 11)
                    continue;

                var sub = parts[2];
                var path = parts[10];
                var isSubmodule = sub.StartsWith('S');

                conflicted.Add(new FileStatusEntry
                {
                    Path = path,
                    Kind = FileChangeKind.Conflicted,
                    OldPath = null,
                    IsBinary = false,
                    IsSubmodule = isSubmodule,
                    SizeBytes = 0,
                });
                continue;
            }

            if (token.StartsWith("? ", StringComparison.Ordinal))
            {
                var path = token.Substring(2);
                untracked.Add(new FileStatusEntry
                {
                    Path = path,
                    Kind = FileChangeKind.Untracked,
                    OldPath = null,
                    IsBinary = false,
                    IsSubmodule = false,
                    SizeBytes = 0,
                });
                continue;
            }
        }

        return new ParsedStatus
        {
            Staged = staged,
            Unstaged = unstaged,
            Untracked = untracked,
            Conflicted = conflicted,
            AheadBy = aheadBy,
            BehindBy = behindBy,
            WorktreeDeletions = worktreeDeletions,
            HeadCommitId = headCommitId,
        };
    }

    public static bool ShouldHashUntracked(ParsedStatus parsed)
    {
        return parsed.Untracked.Count is >= 1 and <= 1000
            && parsed.WorktreeDeletions.Any(d => d.IsRegularFile);
    }

    public static RepositoryStatus PairExactMoves(
        ParsedStatus parsed,
        IReadOnlyDictionary<string, string> untrackedHashes)
    {
        if (untrackedHashes.Count == 0 || !parsed.WorktreeDeletions.Any(d => d.IsRegularFile))
        {
            return new RepositoryStatus
            {
                Staged = parsed.Staged,
                Unstaged = parsed.Unstaged,
                Untracked = parsed.Untracked,
                Conflicted = parsed.Conflicted,
                IsDirty = parsed.IsDirty,
                AheadBy = parsed.AheadBy,
                BehindBy = parsed.BehindBy,
                ComputedAt = DateTimeOffset.Now,
            };
        }

        var eligibleDeletions = parsed.WorktreeDeletions
            .Where(d => d.IsRegularFile && !IsEmptyBlobHash(d.IndexHash) && !IsZeroOid(d.IndexHash))
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .ToList();

        var candidateUntracked = parsed.Untracked
            .OrderBy(u => u.Path, StringComparer.Ordinal)
            .ToList();

        var pairedUntrackedPaths = new HashSet<string>(StringComparer.Ordinal);
        var pairedMoves = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var del in eligibleDeletions)
        {
            foreach (var untracked in candidateUntracked)
            {
                if (pairedUntrackedPaths.Contains(untracked.Path))
                    continue;

                if (untrackedHashes.TryGetValue(untracked.Path, out var hash)
                    && hash.Equals(del.IndexHash, StringComparison.OrdinalIgnoreCase))
                {
                    pairedUntrackedPaths.Add(untracked.Path);
                    pairedMoves[del.Path] = untracked.Path;
                    break;
                }
            }
        }

        var newUnstaged = new List<FileStatusEntry>(parsed.Unstaged.Count);
        foreach (var entry in parsed.Unstaged)
        {
            if (entry.Kind == FileChangeKind.Deleted && pairedMoves.TryGetValue(entry.Path, out var newPath))
            {
                newUnstaged.Add(new FileStatusEntry
                {
                    Path = newPath,
                    OldPath = entry.Path,
                    Kind = FileChangeKind.Renamed,
                    IsBinary = false,
                    IsSubmodule = false,
                    SizeBytes = 0,
                });
            }
            else
            {
                newUnstaged.Add(entry);
            }
        }

        var newUntracked = parsed.Untracked
            .Where(u => !pairedUntrackedPaths.Contains(u.Path))
            .ToList();

        return new RepositoryStatus
        {
            Staged = parsed.Staged,
            Unstaged = newUnstaged,
            Untracked = newUntracked,
            Conflicted = parsed.Conflicted,
            IsDirty = parsed.Staged.Count + newUnstaged.Count + newUntracked.Count + parsed.Conflicted.Count > 0,
            AheadBy = parsed.AheadBy,
            BehindBy = parsed.BehindBy,
            ComputedAt = DateTimeOffset.Now,
        };
    }

    private static void ParseAheadBehind(ReadOnlySpan<char> span, out int aheadBy, out int behindBy)
    {
        aheadBy = 0;
        behindBy = 0;
        var spaceIndex = span.IndexOf(' ');
        if (spaceIndex < 0)
            return;

        var aheadPart = span[..spaceIndex].Trim();
        var behindPart = span[(spaceIndex + 1)..].Trim();

        if (aheadPart.StartsWith("+", StringComparison.Ordinal) && int.TryParse(aheadPart[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a))
            aheadBy = a;
        if (behindPart.StartsWith("-", StringComparison.Ordinal) && int.TryParse(behindPart[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
            behindBy = b;
    }

    private static FileChangeKind? KindOf(char c) => c switch
    {
        'A' => FileChangeKind.Added,
        'M' => FileChangeKind.Modified,
        'D' => FileChangeKind.Deleted,
        'R' => FileChangeKind.Renamed,
        'C' => FileChangeKind.Copied,
        'T' => FileChangeKind.TypeChanged,
        _ => null,
    };

    private static void AddEntry(
        List<FileStatusEntry> staged,
        List<FileStatusEntry> unstaged,
        List<WorktreeDeletion> worktreeDeletions,
        string xy,
        string sub,
        string path,
        string? origPath,
        string mI,
        string hI)
    {
        var isSubmodule = sub.StartsWith('S');
        var x = xy.Length > 0 ? xy[0] : '.';
        var y = xy.Length > 1 ? xy[1] : '.';

        if (x != '.' && KindOf(x) is { } sk)
        {
            staged.Add(new FileStatusEntry
            {
                Path = path,
                Kind = sk,
                OldPath = (sk is FileChangeKind.Renamed or FileChangeKind.Copied) ? origPath : null,
                IsBinary = false,
                IsSubmodule = isSubmodule,
                SizeBytes = 0,
            });
        }

        if (y != '.')
        {
            if (KindOf(y) is { } uk)
            {
                unstaged.Add(new FileStatusEntry
                {
                    Path = path,
                    Kind = uk,
                    OldPath = (uk is FileChangeKind.Renamed or FileChangeKind.Copied) ? origPath : null,
                    IsBinary = false,
                    IsSubmodule = isSubmodule,
                    SizeBytes = 0,
                });
            }

            if (y == 'D')
            {
                worktreeDeletions.Add(new WorktreeDeletion(path, mI, hI));
            }
        }
    }
}
