namespace Historia.Diagnostics;

/// <summary>
/// Drives one <c>--frame-bench</c> launch: prepares an isolated data directory before Avalonia
/// starts, then — once the shell is up — pins the window, checks the repository's HEAD, opens it
/// as the only tab, waits for it to load fully and for background work to go quiet, runs the
/// workloads with a settle and a forced GC before each, writes one CSV and one JSON per workload,
/// and shuts the app down with the run's exit code.
/// </summary>
internal static class FrameBenchRunner
{
    /// <summary>The whole launch must finish within this, or the process exits with
    /// <see cref="FrameBenchExitCodes.Watchdog"/>.</summary>
    public static readonly TimeSpan WatchdogTimeout = TimeSpan.FromMinutes(6);

    /// <summary>The window's client size and position on the primary monitor, in device pixels,
    /// so it covers the same physical area at any DPI.</summary>
    public const int WindowXPx = 40;
    public const int WindowYPx = 40;
    public const int WindowWidthPx = 1600;
    public const int WindowHeightPx = 1000;

    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan StartupQuietCap = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan WorkloadQuietCap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(2);

    // Background work counts as settled after this many consecutive one-second windows in which
    // Historia and its child processes together used less than QuietCpuShare of one core.
    private const int QuietWindowsRequired = 3;
    private const double QuietCpuShare = 0.05;

    private static Timer? _watchdog;
    private static string? _logPath;

    // ── Before Avalonia ──

    /// <summary>
    /// Parses the command line and readies the process: takes the single-instance lock, points
    /// every persisted path at <c>--data</c>, turns the opt-in logs off, writes the benchmark's
    /// settings, and arms the watchdog. Returns an exit code to stop with, or null to go on and
    /// start the app.
    /// </summary>
    public static int? PrepareProcess(string[] args)
    {
        var options = FrameBenchOptions.Parse(args, out var error);
        if (options is null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(FrameBenchOptions.Usage);
            return FrameBenchExitCodes.Usage;
        }

        if (!SingleInstance.TryAcquirePrimary())
        {
            Console.Error.WriteLine("frame-bench: another Historia is running; close it first.");
            return FrameBenchExitCodes.AnotherInstance;
        }

        try
        {
            Directory.CreateDirectory(options.OutDirectory);
            Directory.CreateDirectory(options.DataDirectory);

            AppPaths.OverrideProcessForTesting(options.DataDirectory);

            WriteSettings(options);
            foreach (var stale in new[] { AppPaths.WindowPlacementFile, AppPaths.SessionFile })
            {
                if (File.Exists(stale))
                    File.Delete(stale);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"frame-bench: cannot prepare {options.DataDirectory}: {ex.Message}");
            return FrameBenchExitCodes.Failure;
        }

        FrameBench.Activate(options);
        _watchdog = new Timer(_ =>
        {
            var code = FrameBench.FinishedExitCode ?? FrameBenchExitCodes.Watchdog;
            Log(code == FrameBenchExitCodes.Watchdog
                ? $"watchdog: no result within {WatchdogTimeout.TotalMinutes:F0} min"
                : $"watchdog: shutdown did not finish; exiting with {code}");
            Environment.Exit(code);
        }, null, WatchdogTimeout, Timeout.InfiniteTimeSpan);
        return null;
    }

    /// <summary>
    /// The benchmark's own settings, written fresh every launch: nothing that fetches, polls a
    /// host or restores other tabs, no splash, a fixed theme and diff layout, and — for the
    /// terminal workload — a fixed shell.
    /// </summary>
    internal static AppSettings BuildSettings(FrameBenchMode mode)
    {
        var settings = new AppSettings
        {
            LoggingEnabled = false,
            AutoFetchEnabled = false,
            RestoreSessionTabs = false,
            ShowSplashScreen = false,
            AppTheme = ThemeMode.Dark,
            HighContrastOverride = false,
            DefaultDiffView = DiffViewMode.Unified,
            DiffWordWrap = false,
            EnableGraphSplice = false,
        };
        if (mode == FrameBenchMode.Terminal)
        {
            var (shell, shellArgs) = TerminalWorkload.Shell();
            settings = settings with { TerminalShellPath = shell, TerminalShellArgs = shellArgs };
        }
        return settings;
    }

    private static void WriteSettings(FrameBenchOptions options)
    {
        var settings = BuildSettings(options.Mode);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, HistoriaJson.Options));
    }

    // ── After Avalonia ──

    /// <summary>Runs the launch on the UI thread and shuts the app down with its exit code.</summary>
    public static async Task RunAsync(IServiceProvider services, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var options = FrameBench.Options!;
        var avalonia = AvaloniaVersion();
        _logPath = System.IO.Path.Combine(options.OutDirectory,
            $"{options.RepoKey}-{options.ModeName}-{avalonia.Short}-r{options.Run}.log");
        Log($"start: {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");

        int exit;
        try
        {
            exit = await RunCoreAsync(services, desktop, options, avalonia).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log($"failed: {ex}");
            exit = FrameBenchExitCodes.Failure;
        }

        Log($"exit {exit}");
        FrameBench.Finish(exit);
        desktop.Shutdown(exit);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Log("shutdown did not finish within 30 s; exiting");
            Environment.Exit(exit);
        });
    }

    private static async Task<int> RunCoreAsync(
        IServiceProvider services, IClassicDesktopStyleApplicationLifetime desktop, FrameBenchOptions options,
        (string Short, string Full) avalonia)
    {
        var config = FrameBenchConfig.Load(options.ConfigPath, out var configPath);
        if (!config.Repos.TryGetValue(options.RepoKey, out var pin))
        {
            Log($"{configPath} has no pins for '{options.RepoKey}'");
            return FrameBenchExitCodes.HeadMismatch;
        }

        var cli = services.GetRequiredService<IGitCliService>();
        var guard = cli as FrameBenchGitGuard
            ?? throw new InvalidOperationException("The read-only git guard is not registered.");

        var head = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["rev-parse", "HEAD"],
            WorkingDirectory = options.Repo,
        }).ConfigureAwait(true);
        var headSha = head.StandardOutput.Trim();
        if (!head.IsSuccess || !string.Equals(headSha, pin.Head, StringComparison.OrdinalIgnoreCase))
        {
            Log($"HEAD {(head.IsSuccess ? headSha : "unreadable")} does not match the pin {pin.Head}");
            await Console.Error.WriteLineAsync($"frame-bench: {options.Repo} HEAD is not {pin.Head}.").ConfigureAwait(true);
            return FrameBenchExitCodes.HeadMismatch;
        }

        var window = await WaitForMainWindowAsync(desktop).ConfigureAwait(true);
        var environment = PlaceWindow(window);
        Log($"window {environment.WindowWidthPx}x{environment.WindowHeightPx}px at {environment.DpiScale:0.###}x, {environment.RefreshRateHz} Hz{(environment.RefreshRateFallback ? " (fallback)" : "")}");

        var graph = window.GetVisualDescendants().OfType<CommitGraphControl>().FirstOrDefault()
            ?? throw new InvalidOperationException("The commit graph control is not in the window.");

        // ── Open and load ──
        var workspace = services.GetRequiredService<IWorkspaceService>();
        var openWatch = Stopwatch.StartNew();
        var context = await workspace.OpenAsync(options.Repo).ConfigureAwait(true);
        var firstStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(object? s, StatusChangedEventArgs e) => firstStatus.TrySetResult();
        context.State.StatusChanged += OnStatus;

        double? statusMs = null;
        double fullLoadMs, warmedMs;
        try
        {
            await WaitAsync(context.FirstFullLoad, LoadTimeout, "the repository's full load").ConfigureAwait(true);
            fullLoadMs = openWatch.Elapsed.TotalMilliseconds;
            Log($"full load after {fullLoadMs:F0} ms ({graph.RowCountForTesting} rows)");

            await WaitAsync(context.GraphWarmed, LoadTimeout, "the graph's silent crawl").ConfigureAwait(true);
            warmedMs = openWatch.Elapsed.TotalMilliseconds;
            Log($"graph warmed after {warmedMs:F0} ms ({graph.RowCountForTesting} rows)");

            if (await Task.WhenAny(firstStatus.Task, Task.Delay(LoadTimeout)).ConfigureAwait(true) == firstStatus.Task)
            {
                statusMs = openWatch.Elapsed.TotalMilliseconds;
                Log($"first status after {statusMs:F0} ms");
            }
            else
            {
                Log("no status refresh within the load timeout");
            }
        }
        finally
        {
            context.State.StatusChanged -= OnStatus;
        }

        var (settled, settleMs) = await WaitForQuietAsync(StartupQuietCap).ConfigureAwait(true);
        Log($"startup quiet: {settled} after {settleMs:F0} ms ({graph.RowCountForTesting} rows)");

        var startup = new FrameBenchStartup(
            headSha, configPath, fullLoadMs, warmedMs, statusMs, settleMs, settled, graph.RowCountForTesting,
            options.Mode == FrameBenchMode.All ? pin.Diff.Commit : null,
            options.Mode == FrameBenchMode.All ? pin.Diff.Path : null,
            options.Mode == FrameBenchMode.Terminal ? TerminalWorkload.CommandText : null);

        // ── Workloads ──
        var recorder = new FrameRecorder(window, options.Seconds);
        var anyInvalid = false;
        var steps = options.Mode == FrameBenchMode.Terminal
            ? new[] { FrameWorkloadNames.Terminal }
            : new[] { FrameWorkloadNames.GraphScroll, FrameWorkloadNames.GraphJump, FrameWorkloadNames.DiffScroll };

        foreach (var name in steps)
        {
            var startedAt = DateTimeOffset.Now;
            IFrameWorkload? workload = null;
            string? prepareError = null;
            try
            {
                workload = await PrepareAsync(name, services, window, graph, context, pin, options).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                prepareError = ex.Message;
                Log($"{name}: prepare failed: {ex}");
            }

            var (quiet, quietMs) = workload is null ? (false, 0d) : await WaitForQuietAsync(WorkloadQuietCap).ConfigureAwait(true);
            await SettleAsync().ConfigureAwait(true);

            FrameCapture capture;
            WorkloadCoverage coverage;
            if (workload is not null)
            {
                capture = await recorder.RecordAsync(workload).ConfigureAwait(true);
                coverage = workload.Coverage();
            }
            else
            {
                capture = new FrameCapture([], [], [], [], [], new GcDelta(default, default, TimeSpan.Zero), 0, false, null);
                coverage = new WorkloadCoverage(false, prepareError, new Dictionary<string, double>());
            }

            var result = BuildResult(name, options, avalonia, startedAt, environment, startup, capture, coverage,
                quiet, quietMs, prepareError, guard);
            WriteOutputs(options, avalonia.Short, result, capture);
            anyInvalid |= !result.Validity.Valid;
            Log(string.Create(CultureInfo.InvariantCulture,
                $"{name}: {(result.Validity.Valid ? "valid" : "INVALID (" + string.Join("; ", result.Validity.Reasons) + ")")}, "
                + $"{result.Summary.Frames} frames, p99 {result.Summary.IntervalP99Ms:F2} ms, over 22.2 ms {result.Summary.FramesOverCeiling}, "
                + $"render p99 {result.Summary.RenderP99Ms:F2} ms, refused git {guard.RefusedCount}"));
        }

        return anyInvalid ? FrameBenchExitCodes.InvalidRun : FrameBenchExitCodes.Ok;
    }

    // ── Workload preparation ──

    private static async Task<IFrameWorkload> PrepareAsync(
        string name, IServiceProvider services, Window window, CommitGraphControl graph, RepositoryContext context,
        FrameBenchRepoPin pin, FrameBenchOptions options)
    {
        switch (name)
        {
            case FrameWorkloadNames.GraphScroll:
                context.State.SetOpenDiff(null);
                graph.SetVerticalOffsetForBench(0);
                return new GraphScrollWorkload(graph, options.Seconds);

            case FrameWorkloadNames.GraphJump:
                context.State.SetOpenDiff(null);
                graph.SetVerticalOffsetForBench(0);
                return new GraphJumpWorkload(graph, options.Seconds, options.Seed);

            case FrameWorkloadNames.DiffScroll:
                return await PrepareDiffAsync(
                    services.GetRequiredService<ISettingsService>(), window, graph, context, pin, options).ConfigureAwait(true);

            case FrameWorkloadNames.Terminal:
                return await PrepareTerminalAsync(services, window, context, options).ConfigureAwait(true);

            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }

    /// <summary>
    /// Selects the target commit through the graph's go-to-commit path (select it and request
    /// a reveal, which walks the graph toward it if it is not loaded yet), waits for its row, then
    /// opens the target file's diff the way a click on the commit's file list does.
    /// </summary>
    private static async Task<IFrameWorkload> PrepareDiffAsync(
        ISettingsService settings, Window window, CommitGraphControl graph, RepositoryContext context,
        FrameBenchRepoPin pin, FrameBenchOptions options)
    {
        var commit = GitObjectId.Parse(pin.Diff.Commit);
        context.State.SetOpenDiff(null);
        context.State.SetSelectedCommit(commit);
        context.State.RequestRevealCommit(commit);
        if (!await WaitUntilAsync(() => graph.SelectedRowForBench >= 0, PrepareTimeout).ConfigureAwait(true))
            throw new TimeoutException($"commit {pin.Diff.Commit} did not load into the graph");
        Log($"diff-scroll: commit at row {graph.SelectedRowForBench}");

        context.State.SetOpenDiff(new DiffRequest
        {
            Source = DiffSource.Commit,
            Path = pin.Diff.Path,
            CommitId = commit,
            ParentIndex = 1,
            ContextLines = settings.Current.DiffContextLines,
        });

        DiffView? view = null;
        ScrollViewer? viewer = null;
        TextEditor? editor = null;
        string? problem = null;
        var ready = await WaitUntilAsync(() =>
        {
            view ??= window.GetVisualDescendants().OfType<DiffView>().FirstOrDefault();
            if (view?.DiffForTesting is { } diff)
            {
                if (diff.IsTruncated)
                    problem = "the diff is over the viewer's inline size limit and shows no text";
                else if (diff.IsBinary)
                    problem = "the diff is binary";
            }
            if (problem is not null)
                return true;
            editor = view?.PresenterForTesting as TextEditor;
            viewer = editor?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            return viewer is not null && viewer.Extent.Height > viewer.Viewport.Height && viewer.Viewport.Height > 0;
        }, PrepareTimeout).ConfigureAwait(true);

#pragma warning disable S2583 // the wait's lambda sets this
        if (problem is not null)
#pragma warning restore S2583
            throw new InvalidOperationException($"{pin.Diff.Path} at {pin.Diff.Commit}: {problem}");
        if (!ready || viewer is null || editor is null)
            throw new TimeoutException($"the diff of {pin.Diff.Path} did not open");

        viewer.Offset = new Vector(viewer.Offset.X, 0);
        var lines = editor.Document?.LineCount ?? 0;
        Log(string.Create(CultureInfo.InvariantCulture,
            $"diff-scroll: {lines} lines, extent {viewer.Extent.Height:F0} px, viewport {viewer.Viewport.Height:F0} px"));
        return new DiffScrollWorkload(viewer, lines, options.Seconds);
    }

    /// <summary>Opens the repository's terminal in the dock — the shell the benchmark settings
    /// pin — and waits for its prompt before the workload types the command.</summary>
    private static async Task<IFrameWorkload> PrepareTerminalAsync(
        IServiceProvider services, Window window, RepositoryContext context, FrameBenchOptions options)
    {
        var terminals = services.GetRequiredService<ITerminalSessionService>();
        var session = await terminals.OpenOrFocusAsync(RepoTerminalOrigin.For(context), CancellationToken.None).ConfigureAwait(true);
        var ready = await WaitUntilAsync(() =>
            session.State == TerminalSessionState.Running
            && window.GetVisualDescendants().OfType<TerminalDockView>().Any(d => d.IsVisible && d.Bounds.Height > 0)
            && session.ReadBuffer(v =>
            {
                for (var line = 0; line < v.TotalLines; line++)
                    if (v.GetLineText(line).Length > 0)
                        return true;
                return false;
            }), PrepareTimeout).ConfigureAwait(true);
        if (!ready)
            throw new TimeoutException($"the terminal did not show a prompt (state {session.State})");
        Log($"terminal: shell ready, typing '{TerminalWorkload.CommandText}'");
        return new TerminalWorkload(session, options.Seconds);
    }

    // ── Waiting ──

    private static async Task<Window> WaitForMainWindowAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Window? window = null;
        if (!await WaitUntilAsync(() => (window = desktop.MainWindow) is { IsVisible: true, IsLoaded: true }, PrepareTimeout).ConfigureAwait(true))
            throw new TimeoutException("the main window never opened");
        return window!;
    }

    private static async Task WaitAsync(Task task, TimeSpan timeout, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(true) != task)
            throw new TimeoutException($"timed out waiting for {what}");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout)
                return false;
            await Task.Delay(50).ConfigureAwait(true);
        }
        return true;
    }

    /// <summary>
    /// Waits until Historia and its child processes go quiet — the graph's background spill, a
    /// status walk, a diff's tokenizer — so no leftover load work lands inside a measurement.
    /// Returns whether it went quiet within <paramref name="cap"/> and how long it waited.
    /// </summary>
    private static async Task<(bool Settled, double WaitedMs)> WaitForQuietAsync(TimeSpan cap)
    {
        var watch = Stopwatch.StartNew();
        var quietWindows = 0;
        var lastCpu = await Task.Run(FrameBenchPlatform.ProcessTreeCpu).ConfigureAwait(true);
        var lastWall = watch.Elapsed;
        while (watch.Elapsed < cap)
        {
            await Task.Delay(1000).ConfigureAwait(true);
            var cpu = await Task.Run(FrameBenchPlatform.ProcessTreeCpu).ConfigureAwait(true);
            var wall = watch.Elapsed;
            var share = (cpu - lastCpu).TotalMilliseconds / Math.Max(1, (wall - lastWall).TotalMilliseconds);
            lastCpu = cpu;
            lastWall = wall;
            quietWindows = share < QuietCpuShare ? quietWindows + 1 : 0;
            if (quietWindows >= QuietWindowsRequired)
                return (true, watch.Elapsed.TotalMilliseconds);
        }
        return (false, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>The settle before every workload: a pause, then a full blocking collection.</summary>
    private static async Task SettleAsync()
    {
        await Task.Delay(SettleDelay).ConfigureAwait(true);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // ── Window and environment ──

    private static FrameBenchEnvironment PlaceWindow(Window window)
    {
        var scale = window.Screens.Primary?.Scaling ?? window.RenderScaling;
        if (!(scale > 0))
            scale = 1;
        window.WindowState = WindowState.Normal;
        window.Position = new PixelPoint(WindowXPx, WindowYPx);
        window.Width = WindowWidthPx / scale;
        window.Height = WindowHeightPx / scale;
        window.Activate();

        var (hz, fallback) = FrameBenchPlatform.PrimaryRefreshRate();
        return new FrameBenchEnvironment(
            hz, fallback, scale, WindowXPx, WindowYPx, WindowWidthPx, WindowHeightPx,
            window.Width, window.Height,
            RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, Environment.ProcessorCount);
    }

    private static (string Short, string Full) AvaloniaVersion()
    {
        var assembly = typeof(AvaloniaObject).Assembly;
        var full = System.Reflection.CustomAttributeExtensions
                       .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var plus = full.IndexOf('+');
        return (plus > 0 ? full[..plus] : full, full);
    }

    // ── Results ──

    private static FrameBenchResult BuildResult(
        string name, FrameBenchOptions options, (string Short, string Full) avalonia, DateTimeOffset startedAt,
        FrameBenchEnvironment environment, FrameBenchStartup startup, FrameCapture capture, WorkloadCoverage coverage,
        bool settled, double settleMs, string? prepareError, FrameBenchGitGuard guard)
    {
        var summary = capture.Summarize();
        var minimumFrames = (int)Math.Ceiling(0.5 * capture.Seconds * environment.RefreshRateHz);
        var reasons = new List<string>();
        if (prepareError is not null)
            reasons.Add($"preparation failed: {prepareError}");
        if (capture.Error is { } error)
            reasons.Add($"the workload threw {error.GetType().Name}: {error.Message}");
        if (capture.Overflowed)
            reasons.Add("the frame buffer filled before the workload finished");
        if (summary.Frames == 0 || summary.Frames < minimumFrames)
            reasons.Add($"{summary.Frames} frames, below the {minimumFrames} minimum");
        if (!coverage.Covered && prepareError is null)
            reasons.Add($"range not covered: {coverage.Shortfall}");
        if (!settled)
            reasons.Add("background work did not go quiet before recording");

        var validity = new FrameBenchValidity(
            reasons.Count == 0, reasons, summary.Frames, minimumFrames, coverage.Covered, settled, settleMs,
            capture.Overflowed, capture.Error?.ToString() ?? prepareError,
            guard.RefusedCount, guard.Refusals, guard.Subcommands);

        return new FrameBenchResult
        {
            Repo = options.RepoKey,
            Workload = name,
            Mode = options.ModeName,
            Run = options.Run,
            PlannedSeconds = options.Seconds,
            Seed = options.Seed,
            AvaloniaVersion = avalonia.Short,
            AvaloniaInformationalVersion = avalonia.Full,
            StartedAt = startedAt,
            Environment = environment,
            Startup = startup,
            Summary = summary,
            Validity = validity,
            Coverage = coverage.Values,
        };
    }

    private static void WriteOutputs(FrameBenchOptions options, string avalonia, FrameBenchResult result, FrameCapture capture)
    {
        var stem = System.IO.Path.Combine(options.OutDirectory,
            $"{options.RepoKey}-{result.Workload}-{avalonia}-r{options.Run}");

        var csv = new System.Text.StringBuilder("frame,t_ms,interval_ms,render_ms\n");
        for (var i = 0; i < capture.TimesMs.Length; i++)
        {
            csv.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(capture.TimesMs[i].ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
            if (i > 0)
            {
                csv.Append(capture.IntervalsMs[i - 1].ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
                if (capture.RenderCounts[i - 1] > 0)
                    csv.Append(capture.RenderMs[i - 1].ToString("0.###", CultureInfo.InvariantCulture));
            }
            else
            {
                csv.Append(',');
            }
            csv.Append('\n');
        }
        File.WriteAllText(stem + ".csv", csv.ToString());
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(result, HistoriaJson.Options));
    }

    private static void Log(string line)
    {
        if (_logPath is not { } path)
            return;
        try
        {
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // The log is a convenience; a locked file must not fail the run.
        }
    }
}
