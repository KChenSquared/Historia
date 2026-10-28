namespace Historia.Services.Storage;

/// <inheritdoc cref="IRepoStateCache"/>
public sealed class RepoStateCache(ISettingsService settings) : IRepoStateCache
{
    private const int Version = 2;
    private const int MaxFilesPerKind = 60;

    private static readonly long PruneIntervalMs = (long)TimeSpan.FromHours(1).TotalMilliseconds;

    private static readonly JsonSerializerOptions Json = HistoriaJson.Options;

    // Environment.TickCount64 at which the cache directory is next pruned; 0 means the next write prunes.
    private long _nextPruneTick;

    private bool Enabled => settings.Current.PersistRepoCache;

    // ── Load ──

    public RepoStateSnapshot? TryLoad(string repoPath)
    {
        if (!Enabled)
            return null;

        var refs = ReadGz<RefsFile>(FileFor(repoPath, "refs"));
        var pt = ReadGz<PowerFile>(FileFor(repoPath, "pt"));
        if (refs is null && pt is null)
            return null;

        IReadOnlyList<BranchInfo>? branches = refs?.Branches;
        if (branches is not null && branches.Any(BranchProjection.IsRemoteHead))
        {
            branches = branches.Where(b => !BranchProjection.IsRemoteHead(b)).ToList();
        }

        return new RepoStateSnapshot(
            branches,
            refs?.Tags,
            refs?.Remotes,
            pt?.Stashes,
            pt?.Submodules,
            pt?.Worktrees);
    }

    public RepoStateSnapshot? TryLoadPowerTools(string repoPath)
    {
        if (!Enabled)
            return null;

        var pt = ReadGz<PowerFile>(FileFor(repoPath, "pt"));
        return pt is null ? null : new RepoStateSnapshot(null, null, null, pt.Stashes, pt.Submodules, pt.Worktrees);
    }

    // ── Save ──

    public void SaveRefs(string repoPath, IReadOnlyList<BranchInfo> branches, IReadOnlyList<TagInfo> tags, IReadOnlyList<RemoteInfo> remotes)
    {
        if (!Enabled)
            return;
        WriteGz(FileFor(repoPath, "refs"), new RefsFile
        {
            Version = Version,
            Branches = branches.ToList(),
            Tags = tags.ToList(),
            Remotes = remotes.ToList(),
        });
    }

    public void SavePowerTools(string repoPath, IReadOnlyList<StashEntry>? stashes, IReadOnlyList<SubmoduleInfo>? submodules, IReadOnlyList<WorktreeInfo>? worktrees)
    {
        if (!Enabled)
            return;
        var existing = ReadGz<PowerFile>(FileFor(repoPath, "pt"));
        WriteGz(FileFor(repoPath, "pt"), new PowerFile
        {
            Version = Version,
            Stashes = stashes is not null ? stashes.ToList() : existing?.Stashes,
            Submodules = submodules is not null ? submodules.ToList() : existing?.Submodules,
            Worktrees = worktrees is not null ? worktrees.ToList() : existing?.Worktrees,
        });
    }

    public int WipeAll()
    {
        var n = 0;
        try
        {
            var dir = new DirectoryInfo(AppPaths.CacheDirectory);
            if (!dir.Exists)
                return 0;
            foreach (var f in dir.GetFiles("repo-refs-*.json.gz")
                         .Concat(dir.GetFiles("repo-pt-*.json.gz"))
                         .Concat(dir.GetFiles("repo-*.tmp")))
            {
                try { f.Delete(); n++; } catch { /* locked — skip */ }
            }
        }
        catch { }
        return n;
    }

    // ── gzip JSON helpers ──

    private static T? ReadGz<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var dto = JsonSerializer.Deserialize<T>(gz, Json);
            return dto is IVersioned v && v.Version != Version ? null : dto;
        }
        catch
        {
            return null; // corruption → treat as a miss
        }
    }

    private void WriteGz<T>(string dest, T value)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);
            var tmp = $"{dest}.{Guid.NewGuid():N}.tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gz, value, Json);
            File.Move(tmp, dest, overwrite: true);
            PruneIfDue();
        }
        catch { /* cache write is best-effort */ }
    }

    // Listing the directory costs more than the write, so it runs on the first write of a session
    // and then at most once an hour.
    private void PruneIfDue()
    {
        var now = Environment.TickCount64;
        var due = Volatile.Read(ref _nextPruneTick);
        if (now < due || Interlocked.CompareExchange(ref _nextPruneTick, now + PruneIntervalMs, due) != due)
            return;
        Prune();
    }

    private static void Prune()
    {
        try
        {
            var dir = new DirectoryInfo(AppPaths.CacheDirectory);
            foreach (var pattern in new[] { "repo-refs-*.json.gz", "repo-pt-*.json.gz" })
                foreach (var f in dir.GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxFilesPerKind))
                    f.Delete();
            foreach (var f in dir.GetFiles("repo-*.tmp"))
                if (DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromHours(1))
                    f.Delete();
        }
        catch { }
    }

    private static string FileFor(string repoPath, string kind)
    {
        // Keyed on the resolved directory, so any spelling of the repository root ("." from the
        // command line, a ".." segment) finds the same file.
        string full;
        try { full = Path.GetFullPath(repoPath); }
        catch { full = repoPath; }
        var norm = full.Replace('\\', '/').TrimEnd('/');
        if (OperatingSystem.IsWindows())
            norm = norm.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(norm)));
        return Path.Combine(AppPaths.CacheDirectory, $"repo-{kind}-{hash}.json.gz");
    }

    // ── file shapes ──

    private interface IVersioned { int Version { get; } }

    private sealed class RefsFile : IVersioned
    {
        public int Version { get; set; }
        public List<BranchInfo> Branches { get; set; } = [];
        public List<TagInfo> Tags { get; set; } = [];
        public List<RemoteInfo> Remotes { get; set; } = [];
    }

    private sealed class PowerFile : IVersioned
    {
        public int Version { get; set; }
        public List<StashEntry>? Stashes { get; set; }
        public List<SubmoduleInfo>? Submodules { get; set; }
        public List<WorktreeInfo>? Worktrees { get; set; }
    }
}
