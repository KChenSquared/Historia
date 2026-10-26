namespace Historia.Services.Git;

/// <summary>
/// One per open repository (<see cref="RepositoryContext.RefSnapshots"/>): hands out the current
/// <see cref="RefSnapshot"/>, re-reading refs only when <see cref="RefStateToken"/> says a ref
/// file changed. The scan runs in separate processes, so ref enumeration runs concurrently with
/// other repository reads.
/// <para>Two scans give the same entries. The fast scan lists refs without reading their objects
/// and types them from <c>packed-refs</c> peel lines and the commit-graph, asking
/// <c>cat-file</c> only about the rest (<see cref="ScanFastAsync"/>). It needs a
/// <c>packed-refs</c> file with the <c>fully-peeled</c> trait; a reftable repository, or one
/// without such a file, gets the typed scan: one <c>for-each-ref</c> that reads every ref's
/// object.</para>
/// <para>One scan at a time: callers that miss on the same token share one scan, and a caller
/// whose token differs from the scan in flight waits for it to finish before looking again, so a
/// burst of ref writes (a fetch) never stacks up overlapping scans of every ref. The scan runs on
/// the repository's lifetime token, not a caller's, and each caller waits with its own token — one
/// caller giving up never cancels the scan the others are waiting on.</para>
/// <para>A ref that points at a missing object (a half-deleted branch, a corrupt
/// <c>packed-refs</c>), or an annotated tag whose tagged object is missing, is left out — the
/// full format makes git stop at the first missing object, so the scan then reads refs without
/// their objects and asks <c>cat-file</c> about each object and each tag's peel instead. In a
/// partial clone, an object git would otherwise fetch from the promisor remote counts as missing:
/// the scan never goes to the network. Any other failure is a <see cref="HistoriaGitException"/>.</para>
/// </summary>
public sealed class RefSnapshotProvider
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(60);

    /// <summary>In a partial clone git would fetch a ref's missing object from the promisor
    /// remote on demand (reading refs never touches the network). With lazy fetching off,
    /// such a ref fails the scan like any other missing object and is left out. Only these
    /// reads set it — every object a clone or fetch hands a ref is local, so the normal scan's
    /// output is the same either way. git before 2.45 ignores the variable.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoLazyFetch =
        new Dictionary<string, string> { ["GIT_NO_LAZY_FETCH"] = "1" };

    private readonly IGitCliService _cli;
    private readonly string _gitDir;
    private readonly string _workingDirectory;
    private readonly CancellationToken _lifetime;
    private readonly CommitGraphIndex? _commitGraph;
    private readonly object _sync = new();

    private RefSnapshot? _current;
    private long _currentSeq;
    private (long Token, long Seq, Task<RefSnapshot> Scan)? _inflight;

    // Scans are numbered as they start. A finished scan becomes current only if it started after
    // the current one — an older scan finishing late never steps the cache back.
    private long _nextSeq;
    private int _scanCount;
    private string? _lastScanKind;

    /// <param name="commitGraph">Types refs whose objects the commit-graph lists as commits
    /// without reading them; null types them all through <c>cat-file</c>.</param>
    public RefSnapshotProvider(
        IGitCliService cli, string gitDir, string workingDirectory, CancellationToken lifetime, CommitGraphIndex? commitGraph = null)
    {
        _cli = cli;
        _gitDir = gitDir;
        _workingDirectory = workingDirectory;
        _lifetime = lifetime;
        _commitGraph = commitGraph;
    }

    /// <summary>Scans started since construction — a test seam for single-flight / cache-hit
    /// assertions.</summary>
    internal int ScanCountForTesting => Volatile.Read(ref _scanCount);

    /// <summary>How the last scan read the refs: <c>fast</c>, or <c>typed: &lt;why&gt;</c> — a
    /// test seam.</summary>
    internal string? LastScanKindForTesting => Volatile.Read(ref _lastScanKind);

    /// <summary>The full typed scan, whatever the repository's files would allow — a test seam
    /// for parity with the fast scan.</summary>
    internal Task<List<RefEntry>> ScanTypedForTestingAsync() => ScanTypedAsync();

    /// <summary>The snapshot for the refs on disk right now. A token check costs milliseconds; a
    /// miss runs one <c>for-each-ref</c> (≈0.75 s at 39k refs). Throws only
    /// <see cref="HistoriaGitException"/> (the refs can't be read — never cached) or
    /// <see cref="OperationCanceledException"/>.</summary>
    public async Task<RefSnapshot> GetAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var state = RefStateToken.Compute(_gitDir);
            Task<RefSnapshot> scan;
            var join = true;
            lock (_sync)
            {
                if (_current is { } cur && cur.Token == state.Token)
                    return cur;

                if (_inflight is { } f)
                {
                    scan = f.Scan;
                    join = f.Token == state.Token;
                }
                else
                {
                    var seq = ++_nextSeq;
                    scan = ScanAsync(state, seq);
                    _inflight = (state.Token, seq, scan);
                }
            }

            if (join)
                return await scan.WaitAsync(ct).ConfigureAwait(false);

            // A scan of older refs is running: let it finish rather than start a second one over
            // every ref, then look again — its result may already be what is on disk now.
            try { await scan.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* its failure is its own callers'; this caller rescans */ }
        }
    }

    private async Task<RefSnapshot> ScanAsync(RefStateToken.State state, long seq)
    {
        // GetAsync starts this inside its lock — yield first so none of the scan runs there.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Interlocked.Increment(ref _scanCount);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            List<RefEntry> refs;
            string? typedBecause = null;
            PackedRefs? packed = null;
            if (state.IsReftable)
                typedBecause = "reftable";
            else if ((packed = PackedRefsReader.TryRead(RefStateToken.ResolveCommonDir(_gitDir))) is not { FullyPeeled: true })
                typedBecause = "no fully-peeled packed-refs";

            var fast = packed is { FullyPeeled: true } && typedBecause is null
                ? await ScanFastAsync(packed, sw).ConfigureAwait(false)
                : null;
            if (fast is not null)
            {
                refs = fast;
                Volatile.Write(ref _lastScanKind, "fast");
            }
            else
            {
                typedBecause ??= "the type-free listing failed";
                refs = await ScanTypedAsync().ConfigureAwait(false);
                Volatile.Write(ref _lastScanKind, "typed: " + typedBecause);
                Diagnostics.PerfLog.Mark($"ref snapshot: scanned {refs.Count} refs in {sw.Elapsed.TotalMilliseconds:F0}ms (typed scan: {typedBecause})");
            }

            var (headSymref, headTarget) = state.IsReftable
                ? await ResolveHeadWithCliAsync().ConfigureAwait(false)
                : ResolveHeadFromFile(state, refs);

            var snapshot = new RefSnapshot(refs, state.Token, headSymref, headTarget)
            {
                HistoryShape = RefStateToken.HistoryShape(_gitDir),
            };

            lock (_sync)
            {
                ClearInflight(seq);
                if (seq > _currentSeq)
                {
                    _current = snapshot;
                    _currentSeq = seq;
                }
            }
            return snapshot;
        }
        catch
        {
            lock (_sync)
                ClearInflight(seq);
            throw;
        }
    }

    private Task<GitCliResult> RunAsync(IReadOnlyList<string> arguments, string? stdin = null) =>
        _cli.RunAsync(new GitCliRequest
        {
            Arguments = arguments,
            WorkingDirectory = _workingDirectory,
            StandardInput = stdin,
            Timeout = ScanTimeout,
            RawStandardOutput = true,
            ExtraEnvironment = NoLazyFetch,
        }, _lifetime);

    /// <summary>A failed read of refs: cancelled when the repo is closing, otherwise the
    /// classified git failure.</summary>
    private HistoriaGitException Failure(GitCliResult result, string operation)
    {
        _lifetime.ThrowIfCancellationRequested();
        return HistoriaGitException.FromCli(result, operation);
    }

    /// <summary>Output git produced but this parser can't read is a failure to read refs, never
    /// a partial list.</summary>
    private static T Parse<T>(Func<T> parse)
    {
        try { return parse(); }
        catch (FormatException ex)
        {
            throw new HistoriaGitException(GitErrorKind.Unknown, "Reading refs failed (unreadable output).", ex.Message);
        }
    }

    /// <summary>One <c>for-each-ref</c> with every field, then the missing-object fallback when
    /// git stops on one, then any nested tag a git that peels one level left half-peeled.</summary>
    private async Task<List<RefEntry>> ScanTypedAsync()
    {
        var result = await RunAsync(ForEachRefParser.Arguments).ConfigureAwait(false);
        var refs = result.ExitCode == 0
            ? Parse(() => ForEachRefParser.Parse(result.StandardOutput))
            : await ScanPastMissingObjectsAsync(result).ConfigureAwait(false);
        await PeelNestedTagsAsync(refs).ConfigureAwait(false);
        return refs;
    }

    /// <summary>
    /// The same entries as <see cref="ScanTypedAsync"/>, without reading an object per ref. Refs
    /// are listed with no object reads (<see cref="ForEachRefParser.ListFormat"/>); their types
    /// come from what git already wrote down:
    /// <list type="bullet">
    /// <item>A ref <c>packed-refs</c> holds with the same id (a loose ref shadowing a stale packed
    /// entry doesn't count) and a peel line is an annotated tag peeling to that line — git peels
    /// all the way when it packs. Its peel is a commit when the commit-graph lists it.</item>
    /// <item>Such a ref without a peel line is not a tag (the file says <c>fully-peeled</c>); it is
    /// a commit when the commit-graph lists it.</item>
    /// <item>Everything else — loose refs, objects the graph doesn't list, a peel of unknown type
    /// — goes to one <c>cat-file --batch-check</c>, then one for the peels of objects that turned
    /// out to be tags. A missing object drops its ref, as the typed scan does.</item>
    /// </list>
    /// Tagger and message for every annotated tag come from one <c>cat-file --batch</c>; a tag
    /// object it can't read is missing, and its ref is dropped too. Null when the listing itself
    /// fails: the caller falls back to the typed scan, which classifies the failure.
    /// </summary>
    private async Task<List<RefEntry>?> ScanFastAsync(PackedRefs packed, Stopwatch sw)
    {
        var listing = RunAsync(ForEachRefParser.ListArguments);
        // The packed ids and peels are known before git answers; look them up meanwhile.
        var graphLookup = _commitGraph is { } index
            ? Task.Run(() => index.Lookup([.. packed.Entries.SelectMany(e => e.Peeled is { } p ? [e.Target, p] : new[] { e.Target })]))
            : Task.FromResult<CommitGraphLookup?>(null);
        var listed = await listing.ConfigureAwait(false);
        var graph = await graphLookup.ConfigureAwait(false);
        if (listed.ExitCode != 0)
        {
            if (listed.ErrorKind is GitErrorKind.Cancelled or GitErrorKind.TimedOut)
                throw Failure(listed, "Reading refs");
            return null;
        }
        var list = Parse(() => ForEachRefParser.ParseList(listed.StandardOutput));

        var items = new FastItem[list.Count];
        var unknown = new HashSet<GitObjectId>();
        int fromGraph = 0, fromPeel = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var (name, objectName, symref, upstream) = list[i];
            if (ForEachRefParser.ParseId(objectName) is not { } id)
                return null;
            var item = new FastItem(name, id, symref, upstream);
            if (packed.TryGet(name, out var packedId, out var peel) && packedId == id)
            {
                if (peel is { } p)
                {
                    item.Type = "tag";
                    item.Peeled = p;
                    fromPeel++;
                    if (graph?.Contains(p) == true)
                        item.PeeledType = "commit";
                    else
                        unknown.Add(p);
                }
                else if (graph?.Contains(id) == true)
                {
                    item.Type = "commit";
                    fromGraph++;
                }
            }
            if (item.Type is null)
                unknown.Add(id);
            items[i] = item;
        }

        var unknownList = unknown.ToList();
        var types = await BatchCheckAsync([.. unknownList.Select(o => o.ToString())]).ConfigureAwait(false);
        var typeOf = new Dictionary<GitObjectId, string?>(unknownList.Count);
        for (var i = 0; i < unknownList.Count; i++)
            typeOf[unknownList[i]] = types[i].Type;

        // Tags found by cat-file: peel all the way, as %(*objectname) does.
        var foundTags = items.Where(it => it.Type is null && typeOf[it.Id] == "tag").Select(it => it.Id).Distinct().ToList();
        var peels = await BatchCheckAsync([.. foundTags.Select(o => o + "^{}")]).ConfigureAwait(false);
        var peelOf = new Dictionary<GitObjectId, (GitObjectId? Id, string? Type)>(foundTags.Count);
        for (var i = 0; i < foundTags.Count; i++)
            peelOf[foundTags[i]] = peels[i];

        var dropped = new List<string>();
        foreach (var it in items)
        {
            if (it.Type is null)
            {
                it.Type = typeOf[it.Id];
                if (it.Type == "tag")
                    (it.Peeled, it.PeeledType) = peelOf[it.Id];
            }
            else if (it.Type == "tag" && it.PeeledType is null)
                it.PeeledType = typeOf[it.Peeled!.Value];

            // A missing object, or a tag whose peel is missing, stops the typed scan's listing
            // just the same; both are left out.
            if (it.Type is null || (it.Type == "tag" && it.PeeledType is null))
                it.Dropped = true;
        }

        var tagObjects = items.Where(it => !it.Dropped && it.Type == "tag").Select(it => it.Id).Distinct().ToList();
        var tagData = await ReadTagObjectsAsync([.. tagObjects.Select(o => o.ToString())]).ConfigureAwait(false);

        var refs = new List<RefEntry>(items.Length);
        foreach (var it in items)
        {
            CatFileParser.TagData tag = default;
            if (!it.Dropped && it.Type == "tag" && !tagData.TryGetValue(it.Id, out tag))
                it.Dropped = true;
            if (it.Dropped)
            {
                dropped.Add(it.Name);
                continue;
            }
            var annotated = it.Type == "tag";
            refs.Add(new RefEntry(
                CanonicalName: it.Name,
                Target: it.Id,
                ObjectType: it.Type!,
                Peeled: annotated ? it.Peeled : null,
                PeeledType: annotated ? it.PeeledType : null,
                SymrefTarget: it.Symref,
                TaggerName: annotated ? tag.TaggerName : null,
                TaggerWhen: annotated ? tag.TaggerWhen : null,
                Message: annotated ? tag.Message ?? "" : null)
            {
                Upstream = it.Upstream,
            });
        }
        refs.Sort((a, b) => StringComparer.Ordinal.Compare(a.CanonicalName, b.CanonicalName));

        if (dropped.Count > 0)
            LogDropped(dropped);
        Diagnostics.PerfLog.Mark(
            $"ref snapshot: fast scan {refs.Count} refs (graph {fromGraph}, packed peel {fromPeel}, cat-file {unknownList.Count}) in {sw.Elapsed.TotalMilliseconds:F0} ms{(dropped.Count > 0 ? $", dropped {dropped.Count} refs with missing objects" : "")}");
        return refs;
    }

    private sealed class FastItem(string name, GitObjectId id, string? symref, string? upstream)
    {
        public string Name { get; } = name;
        public GitObjectId Id { get; } = id;
        public string? Symref { get; } = symref;
        public string? Upstream { get; } = upstream;
        public string? Type { get; set; }
        public GitObjectId? Peeled { get; set; }
        public string? PeeledType { get; set; }
        public bool Dropped { get; set; }
    }

    private void LogDropped(List<string> dropped)
    {
        var names = string.Join(", ", dropped.Take(5)) + (dropped.Count > 5 ? $" and {dropped.Count - 5} more" : "");
        AppLog.Write(AppLogLevel.Warn, "git", $"Ignoring {dropped.Count} ref(s) whose objects are missing in {_workingDirectory}: {names}");
    }

    /// <summary>
    /// The full scan failed. If that is because refs point at missing objects — git stops at the
    /// first one (<c>fatal: missing object … for refs/…</c>), whether the ref's own object or the
    /// object an annotated tag peels to — rebuild the same entries without them: list refs
    /// without reading objects, type every object and peel every tag with
    /// <c>cat-file --batch-check</c>, and read tag data with one <c>cat-file --batch</c>. When
    /// nothing is missing, the original failure is unexplained and is thrown as is.
    /// </summary>
    private async Task<List<RefEntry>> ScanPastMissingObjectsAsync(GitCliResult failed)
    {
        if (failed.ErrorKind is GitErrorKind.Cancelled or GitErrorKind.TimedOut)
            throw Failure(failed, "Reading refs");

        var listed = await RunAsync(ForEachRefParser.ListArguments).ConfigureAwait(false);
        if (listed.ExitCode != 0)
            throw Failure(failed, "Reading refs");
        var list = Parse(() => ForEachRefParser.ParseList(listed.StandardOutput));

        var objects = list.Select(r => r.ObjectName).Distinct(StringComparer.Ordinal).ToList();
        var types = await BatchCheckAsync(objects).ConfigureAwait(false);
        var typeOf = new Dictionary<string, (GitObjectId? Id, string? Type)>(StringComparer.Ordinal);
        for (var i = 0; i < objects.Count; i++)
            typeOf[objects[i]] = types[i];

        // Annotated tags: peel all the way (as %(*objectname) does). A tag whose peel is missing —
        // the tag object is there, the object it tags (or an inner tag) is not — stops the full
        // scan just as a missing ref target does, so it counts as broken too.
        var tagObjects = objects.Where(o => typeOf[o].Type == "tag").ToList();
        var peeled = await BatchCheckAsync([.. tagObjects.Select(o => o + "^{}")]).ConfigureAwait(false);
        var peelOf = new Dictionary<string, (GitObjectId? Id, string? Type)>(StringComparer.Ordinal);
        for (var i = 0; i < tagObjects.Count; i++)
            peelOf[tagObjects[i]] = peeled[i];

        bool Broken(string objectName) =>
            typeOf[objectName].Type is not { } type
            || (type == "tag" && peelOf[objectName].Type is null);

        var dropped = list.Where(r => Broken(r.ObjectName)).Select(r => r.Name).ToList();
        if (dropped.Count == 0)
            throw Failure(failed, "Reading refs");

        // Tagger + message for the tags that stay.
        var tagData = await ReadTagObjectsAsync([.. tagObjects.Where(o => !Broken(o))]).ConfigureAwait(false);

        var refs = new List<RefEntry>(list.Count - dropped.Count);
        foreach (var (name, objectName, symref, upstream) in list)
        {
            if (Broken(objectName))
                continue;
            var (id, type) = typeOf[objectName];
            if (id is null || type is null)
                continue;
            var annotated = type == "tag";
            var peel = annotated ? peelOf[objectName] : default;
            var tag = annotated && tagData.TryGetValue(id.Value, out var t) ? t : default;
            refs.Add(new RefEntry(
                CanonicalName: name,
                Target: id,
                ObjectType: type,
                Peeled: peel.Id,
                PeeledType: peel.Type,
                SymrefTarget: symref,
                TaggerName: annotated ? tag.TaggerName : null,
                TaggerWhen: annotated ? tag.TaggerWhen : null,
                Message: annotated ? tag.Message ?? "" : null)
            {
                Upstream = upstream,
            });
        }
        refs.Sort((a, b) => StringComparer.Ordinal.Compare(a.CanonicalName, b.CanonicalName));

        LogDropped(dropped);
        Diagnostics.PerfLog.Mark($"ref snapshot: degraded scan, dropped {dropped.Count} refs with missing objects");
        return refs;
    }

    /// <summary>One <c>cat-file --batch-check</c> over <paramref name="inputs"/>; answers in
    /// input order, a missing object as a null id and type.</summary>
    private async Task<List<(GitObjectId? Id, string? Type)>> BatchCheckAsync(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0)
            return [];
        var result = await RunAsync(
            ["cat-file", "--batch-check=" + CatFileParser.BatchCheckFormat],
            string.Join("\n", inputs) + "\n").ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw Failure(result, "Reading refs");
        return Parse(() => CatFileParser.ParseBatchCheck(result.StandardOutput, inputs.Count));
    }

    private async Task<Dictionary<GitObjectId, CatFileParser.TagData>> ReadTagObjectsAsync(IReadOnlyList<string> tagObjects)
    {
        if (tagObjects.Count == 0)
            return [];
        var result = await RunAsync(["cat-file", "--batch"], string.Join("\n", tagObjects) + "\n").ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw Failure(result, "Reading tags");
        return Parse(() => CatFileParser.ParseTagObjects(result.StandardOutputBytes ?? []));
    }

    /// <summary>
    /// git 2.51's <c>%(*objectname)</c> peels a tag of a tag all the way down (verified against
    /// loose and packed refs); a git that peels only one level leaves <c>PeeledType == "tag"</c>.
    /// Those few entries are peeled in one <c>cat-file --batch-check</c> call rather than one
    /// process per ref.
    /// </summary>
    private async Task PeelNestedTagsAsync(List<RefEntry> refs)
    {
        var nested = new List<int>();
        for (var i = 0; i < refs.Count; i++)
            if (refs[i].PeeledType == "tag" && refs[i].Target is not null)
                nested.Add(i);
        if (nested.Count == 0)
            return;

        var peeled = await BatchCheckAsync([.. nested.Select(i => refs[i].Target!.Value.ToString() + "^{}")]).ConfigureAwait(false);
        for (var k = 0; k < nested.Count; k++)
        {
            var i = nested[k];
            refs[i] = refs[i] with { Peeled = peeled[k].Id, PeeledType = peeled[k].Type };
        }
    }

    /// <summary>Caller holds <see cref="_sync"/>.</summary>
    private void ClearInflight(long seq)
    {
        if (_inflight is { } f && f.Seq == seq)
            _inflight = null;
    }

    /// <summary>Files backend: <c>HEAD</c> is a plain file — a symbolic <c>ref:</c> line resolved
    /// against the snapshot, or a detached SHA.</summary>
    private static (string? Symref, GitObjectId? Target) ResolveHeadFromFile(RefStateToken.State state, List<RefEntry> refs)
    {
        if (state.HeadSha is { } sha && GitObjectId.TryParse(sha, out var hid))
            return (null, hid);
        if (state.HeadSymref is not { } symref)
            return (null, null);

        var entry = refs.Find(r => string.Equals(r.CanonicalName, symref, StringComparison.Ordinal));
        return (symref, entry?.Target);
    }

    /// <summary>Reftable backend: the <c>HEAD</c> file is a placeholder, so ask git.</summary>
    private async Task<(string? Symref, GitObjectId? Target)> ResolveHeadWithCliAsync()
    {
        var symbolic = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["symbolic-ref", "-q", "HEAD"],
            WorkingDirectory = _workingDirectory,
            ExtraEnvironment = NoLazyFetch,
        }, _lifetime).ConfigureAwait(false);
        var target = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["rev-parse", "-q", "--verify", "HEAD"],
            WorkingDirectory = _workingDirectory,
            ExtraEnvironment = NoLazyFetch,
        }, _lifetime).ConfigureAwait(false);

        var symref = symbolic.ExitCode == 0 ? NullIfEmpty(symbolic.StandardOutput.Trim()) : null;
        var sha = target.ExitCode == 0 ? target.StandardOutput.Trim() : "";
        return (symref, (sha.Length is 40 or 64 && GitObjectId.TryParse(sha, out var id)) ? id : null);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
