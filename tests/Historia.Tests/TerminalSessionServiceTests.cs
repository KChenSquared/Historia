namespace Historia.Tests;

[Trait("tier", "unit")]

public class TerminalSessionServiceTests
{
    private static TerminalHostEnvironment Host() => new()
    {
        Platform = OSPlatformKind.Linux,
        FileExists = p => p == "/bin/bash",
        DirectoryExists = _ => true,
        GetEnvironmentVariable = _ => null,
        FindOnPath = _ => null,
        GetPasswdShell = () => "/bin/bash",
        GetInheritedEnvironment = () => new Dictionary<string, string> { ["PATH"] = "/usr/bin" },
        HomeDirectory = "/home/me",
        CurrentDirectory = "/tmp",
        ExecutablePath = "/opt/historia/Historia",
        Version = "5.1.0",
    };

    private static (TerminalSessionService svc, FakeTerminalBackend backend) NewService(FakeTerminalBackend? backend = null)
    {
        backend ??= new FakeTerminalBackend();
        var svc = new TerminalSessionService(backend, new FakeSettingsService(), new ImmediateUiDispatcher(), Host());
        return (svc, backend);
    }

    private static TerminalOrigin Repo(string name = "alpha")
        => TerminalOrigin.ForRepository(
            RepoIdentityKey.FromExplicitId(Guid.Parse($"00000000-0000-0000-0000-0000000000{(name == "alpha" ? "01" : "02")}")),
            tabId: name, $"/src/{name}", name);

    [Fact]
    public async Task OpenOrFocus_ReusesSessionForSameOrigin()
    {
        var (svc, backend) = NewService();
        var a = await svc.OpenOrFocusAsync(Repo());
        var b = await svc.OpenOrFocusAsync(Repo());
        Assert.Same(a, b);
        Assert.Single(backend.Starts);
        Assert.Single(svc.Sessions);
    }

    [Fact]
    public async Task OpenOrFocus_IsReentrantSafe()
    {
        var backend = new FakeTerminalBackend { StartDelayMs = 40 };
        var (svc, _) = NewService(backend);

        var t1 = svc.OpenOrFocusAsync(Repo());
        var t2 = svc.OpenOrFocusAsync(Repo());
        var s1 = await t1;
        var s2 = await t2;

        Assert.Same(s1, s2);
        Assert.Single(backend.Starts);
    }

    [Fact]
    public async Task CloseThenReopen_SameOrigin_SpawnsNewSession()
    {
        var (svc, backend) = NewService();
        var origin = Repo();
        var first = await svc.OpenOrFocusAsync(origin);
        await svc.CloseAsync(first.Id);

        var second = await svc.OpenOrFocusAsync(origin);

        Assert.NotSame(first, second);
        Assert.Equal(2, backend.Starts.Count);
        Assert.Single(svc.Sessions);
        Assert.Contains(second, svc.Sessions);
    }

    [Fact]
    public async Task CloseThenReopen_SameOrigin_SpawnsNewSession_WhenSpawnIsAsync()
    {
        var backend = new FakeTerminalBackend { StartDelayMs = 20 };
        var (svc, _) = NewService(backend);
        var origin = Repo();
        var first = await svc.OpenOrFocusAsync(origin);
        await svc.CloseAsync(first.Id);

        var second = await svc.OpenOrFocusAsync(origin);

        Assert.NotSame(first, second);
        Assert.Equal(2, backend.Starts.Count);
        Assert.Single(svc.Sessions);
        Assert.Contains(second, svc.Sessions);
    }

    [Fact]
    public async Task Restart_CloseThenOpenOrFocus_SpawnsNewShell()
    {
        var (svc, backend) = NewService();
        var origin = Repo();
        var first = await svc.OpenOrFocusAsync(origin);
        await svc.CloseAsync(first.Id);

        var restarted = await svc.OpenOrFocusAsync(origin);

        Assert.NotSame(first, restarted);
        Assert.Equal(2, backend.Starts.Count);
        Assert.Single(svc.Sessions);
        Assert.Same(restarted, svc.Sessions[0]);
    }

    [Fact]
    public async Task OriginEquality_IgnoresWorkingDirectory()
    {
        var (svc, backend) = NewService();
        var key = RepoIdentityKey.FromExplicitId(Guid.NewGuid());

        var first = await svc.OpenOrFocusAsync(TerminalOrigin.ForRepository(key, "tab-1", "/src/before-cd", "r"));
        var second = await svc.OpenOrFocusAsync(TerminalOrigin.ForRepository(key, "tab-1", "/somewhere/else", "r"));

        Assert.Same(first, second);
        Assert.Single(backend.Starts);
    }

    [Fact]
    public async Task SameRepoDifferentTabs_GetSeparateSessions()
    {
        var (svc, backend) = NewService();
        // Two workspace tabs open on the same repository (two clones of one upstream): same
        // identity hash, different tab ids — each must get its own shell.
        var key = RepoIdentityKey.FromExplicitId(Guid.NewGuid());

        var a = await svc.OpenOrFocusAsync(TerminalOrigin.ForRepository(key, "tab-a", "/src/clone-a", "docs"));
        var b = await svc.OpenOrFocusAsync(TerminalOrigin.ForRepository(key, "tab-b", "/src/clone-b", "docs"));

        Assert.NotSame(a, b);
        Assert.Equal(2, backend.Starts.Count);
        Assert.Equal(2, svc.Sessions.Count);
    }

    [Fact]
    public async Task HomeAndRepository_AreDistinctOrigins()
    {
        var (svc, backend) = NewService();
        await svc.OpenOrFocusAsync(TerminalOrigin.Home("/home/me"));
        await svc.OpenOrFocusAsync(Repo());
        Assert.Equal(2, backend.Starts.Count);
        Assert.Equal(2, svc.Sessions.Count);
    }

    [Fact]
    public async Task CloseForTab_ClosesOnlyThatTabsSession()
    {
        var (svc, _) = NewService();
        var repoKey = RepoIdentityKey.FromExplicitId(Guid.NewGuid());
        var repoSession = await svc.OpenOrFocusAsync(TerminalOrigin.ForRepository(repoKey, "tab-x", "/src/x", "x"));
        var homeSession = await svc.OpenOrFocusAsync(TerminalOrigin.Home("/home/me"));

        await svc.CloseForTabAsync("tab-x");

        Assert.DoesNotContain(repoSession, svc.Sessions);
        Assert.Contains(homeSession, svc.Sessions);
    }

    [Fact]
    public async Task CloseForTab_LeavesTheOtherTabOnSameRepoAlone()
    {
        var (svc, _) = NewService();
        var repoKey = RepoIdentityKey.FromExplicitId(Guid.NewGuid());
        var tabA = await svc.OpenOrFocusAsync(TerminalOrigin.ForRepository(repoKey, "tab-a", "/src/a", "docs"));
        var tabB = await svc.OpenOrFocusAsync(TerminalOrigin.ForRepository(repoKey, "tab-b", "/src/b", "docs"));

        await svc.CloseForTabAsync("tab-a");

        Assert.DoesNotContain(tabA, svc.Sessions);
        Assert.Contains(tabB, svc.Sessions);
    }

    [Fact]
    public async Task SessionExited_RaisedOnce()
    {
        var (svc, backend) = NewService();
        var count = 0;
        svc.SessionExited += (_, _) => count++;

        await svc.OpenOrFocusAsync(Repo());
        backend.Connections[0].EmitExit(0);
        backend.Connections[0].EmitExit(0); // idempotent

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task StartInfo_CarriesResolvedShellDirAndScrubbedEnv()
    {
        var (svc, backend) = NewService();
        await svc.OpenOrFocusAsync(Repo("alpha"));

        var info = backend.Starts[0];
        Assert.Equal("/bin/bash", info.ShellPath);
        Assert.Equal("/src/alpha", info.WorkingDirectory);
        Assert.Equal("xterm-256color", info.Environment["TERM"]);
        Assert.False(info.Environment.ContainsKey("HISTORIA_LOG"));
    }

    [Fact]
    public async Task Close_KillsAndDisposesThePty()
    {
        var (svc, backend) = NewService();
        var s = await svc.OpenOrFocusAsync(Repo());
        await svc.CloseAsync(s.Id);

        Assert.True(backend.Connections[0].KillCount >= 1);
        Assert.True(backend.Connections[0].Disposed);
    }

    [Fact]
    public async Task DisposeIsClean()
    {
        var (svc, backend) = NewService();
        await svc.OpenOrFocusAsync(Repo("alpha"));
        await svc.OpenOrFocusAsync(Repo("beta"));
        svc.Dispose();

        Assert.All(backend.Connections, c => Assert.True(c.Disposed));
        Assert.Empty(svc.Sessions);
    }

    [Fact]
    public async Task FailedSpawn_DoesNotPoisonTheOriginKey()
    {
        var backend = new FakeTerminalBackend { FailWith = _ => new InvalidOperationException("boom") };
        var (svc, _) = NewService(backend);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.OpenOrFocusAsync(Repo()));
        Assert.Empty(svc.Sessions);

        // A later click must be able to retry rather than get the cached faulted task back.
        backend.FailWith = null;
        var s = await svc.OpenOrFocusAsync(Repo());
        Assert.Single(svc.Sessions);
        Assert.Contains(s, svc.Sessions);
    }

    [Fact]
    public async Task CancelledSpawn_SurfacesAsCancellation_NotTimeout()
    {
        // A spawn that never returns on its own, abandoned by the caller cancelling its token.
        var backend = new FakeTerminalBackend { StartDelayMs = 60_000 };
        var (svc, _) = NewService(backend);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Must come back as OperationCanceledException (silent), never TimeoutException (which
        // the service turns into a "timed out starting a shell" error toast).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.OpenOrFocusAsync(Repo(), cts.Token));
        Assert.Empty(svc.Sessions);

        // The origin key is freed, so a later attempt can still open.
        backend.StartDelayMs = 0;
        var s = await svc.OpenOrFocusAsync(Repo());
        Assert.Contains(s, svc.Sessions);
    }

    [Fact]
    public async Task MissingStartDirectory_StillOpens_WithNotice()
    {
        var backend = new FakeTerminalBackend();
        var host = Host() with { DirectoryExists = p => p == "/home/me" };
        var svc = new TerminalSessionService(backend, new FakeSettingsService(), new ImmediateUiDispatcher(), host);

        var s = await svc.OpenOrFocusAsync(TerminalOrigin.ForRepository(RepoIdentityKey.FromExplicitId(Guid.NewGuid()), "tab-g", "/gone", "g"));

        Assert.Equal("/home/me", backend.Starts[0].WorkingDirectory);
        Assert.NotNull(s.StartupNotice);
    }

    private sealed class FixedGitLocator(GitExecutableInfo info) : IGitExecutableLocator
    {
        public GitExecutableInfo Current => info;
        public Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default) => Task.FromResult(info);
        public Task<GitExecutableInfo> WhenResolvedAsync(CancellationToken ct = default) => Task.FromResult(info);
    }

    [Fact]
    public async Task Built_by_the_container_a_session_gets_the_bundled_git_on_its_PATH()
    {
        var backend = new FakeTerminalBackend();
        var bundledRoot = Path.Combine("historia-app", "git");
        var services = new ServiceCollection();
        services.AddSingleton<ITerminalBackend>(backend);
        services.AddSingleton<ISettingsService>(new FakeSettingsService());
        services.AddSingleton<IUiDispatcher>(new ImmediateUiDispatcher());
        services.AddSingleton<IGitExecutableLocator>(new FixedGitLocator(new GitExecutableInfo
        {
            Found = true,
            Source = GitSource.Bundled,
            BundledRoot = bundledRoot,
            Path = Path.Combine(bundledRoot, "ucrt64", "bin", "git.exe"),
            Version = new Version(2, 56, 0),
        }));
        services.AddSingleton<ITerminalSessionService, TerminalSessionService>();
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<ITerminalSessionService>().OpenOrFocusAsync(Repo());

        var path = backend.Starts[0].Environment
            .First(kv => string.Equals(kv.Key, "PATH", StringComparison.OrdinalIgnoreCase)).Value;
        Assert.StartsWith(Path.Combine(bundledRoot, "cmd") + Path.PathSeparator, path);
    }

    // ── OSC 52 write / Kitty keyboard settings ──

    private const string Osc52Hi = "\x1b]52;c;aGk=\x07";

    [Fact]
    public async Task ClipboardWrite_Forwarded_WhenAllowed()
    {
        var (svc, backend) = NewService();
        var seen = new List<string>();
        svc.ClipboardWriteRequested += (_, e) => seen.Add(e.Text);
        await svc.OpenOrFocusAsync(Repo());

        backend.Connections[0].EmitOutput(Osc52Hi);

        Assert.Equal(["hi"], seen);
    }

    [Fact]
    public async Task ClipboardWrite_Dropped_WhenSettingOff()
    {
        var settings = new FakeSettingsService();
        var backend = new FakeTerminalBackend();
        var svc = new TerminalSessionService(backend, settings, new ImmediateUiDispatcher(), Host());
        var seen = new List<string>();
        svc.ClipboardWriteRequested += (_, e) => seen.Add(e.Text);
        await svc.OpenOrFocusAsync(Repo());

        // Turned off after the session started: the gate is read per write, not per session.
        await settings.UpdateAsync(s => s with { TerminalAllowClipboardWrite = false });
        backend.Connections[0].EmitOutput(Osc52Hi);
        Assert.Empty(seen);

        await settings.UpdateAsync(s => s with { TerminalAllowClipboardWrite = true });
        backend.Connections[0].EmitOutput(Osc52Hi);
        Assert.Equal(["hi"], seen);
    }

    [Fact]
    public async Task ClipboardWrite_NotForwarded_AfterSessionClosed()
    {
        var dispatcher = new RecordingUiDispatcher();
        var backend = new FakeTerminalBackend();
        var svc = new TerminalSessionService(backend, new FakeSettingsService(), dispatcher, Host());
        var seen = new List<string>();
        svc.ClipboardWriteRequested += (_, e) => seen.Add(e.Text);
        var session = await svc.OpenOrFocusAsync(Repo());

        // The write is posted, then the session closes before the dispatcher runs it.
        backend.Connections[0].EmitOutput(Osc52Hi);
        await svc.CloseAsync(session.Id);
        dispatcher.DrainPosted();

        Assert.Empty(seen);
    }

    [Fact]
    public async Task KittySetting_AppliesToNewSessionsOnly()
    {
        var settings = new FakeSettingsService();
        var backend = new FakeTerminalBackend();
        var svc = new TerminalSessionService(backend, settings, new ImmediateUiDispatcher(), Host());
        var first = await svc.OpenOrFocusAsync(Repo("alpha"));

        await settings.UpdateAsync(s => s with { TerminalKittyKeyboard = false });
        var second = await svc.OpenOrFocusAsync(Repo("beta"));

        backend.Connections[0].EmitOutput("\x1b[>1u");
        backend.Connections[1].EmitOutput("\x1b[>1u");

        Assert.True(first.KeyboardProtocol.KittyActive);
        Assert.False(second.KeyboardProtocol.KittyActive);
    }
}
