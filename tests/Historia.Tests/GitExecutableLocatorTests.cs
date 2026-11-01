namespace Historia.Tests;

public class GitExecutableLocatorTests
{
    [Fact]
    public void Version_parsing_extracts_major_minor_patch()
    {
        var v1 = GitExecutableLocator.ParseVersion("git version 2.51.0.windows.2");
        Assert.NotNull(v1);
        Assert.Equal(new Version(2, 51, 0), v1);

        var v2 = GitExecutableLocator.ParseVersion("git version 2.30.9");
        Assert.NotNull(v2);
        Assert.Equal(new Version(2, 30, 9), v2);
    }

    [Fact]
    public void Git_version_floor_2_46_rejects_older_and_accepts_newer()
    {
        var older = GitExecutableLocator.ParseVersion("git version 2.45.2");
        var newer = GitExecutableLocator.ParseVersion("git version 2.51.0.windows.2");

        Assert.NotNull(older);
        Assert.NotNull(newer);

        Assert.True(older < GitExecutableInfo.MinimumVersion);
        Assert.True(newer >= GitExecutableInfo.MinimumVersion);

        var olderInfo = new GitExecutableInfo
        {
            Found = false,
            Path = "C:\\Git\\cmd\\git.exe",
            Version = older,
            NotFoundReason = $"Git {older} was found at 'C:\\Git\\cmd\\git.exe', but Git {GitExecutableInfo.MinimumVersion} or newer is required.",
        };
        Assert.False(olderInfo.Found);
        Assert.Contains("2.45.2", olderInfo.NotFoundReason);
        Assert.Contains("2.46", olderInfo.NotFoundReason);

        var newerInfo = new GitExecutableInfo
        {
            Found = true,
            Path = "C:\\Git\\cmd\\git.exe",
            Version = newer,
        };
        Assert.True(newerInfo.Found);
    }

    [Fact]
    public void ParseVersion_2_45_is_below_minimum()
    {
        var version = GitExecutableLocator.ParseVersion("git version 2.45.2");

        Assert.NotNull(version);
        Assert.True(version < GitExecutableInfo.MinimumVersion);
    }

    [Fact]
    public void ParseVersion_2_46_meets_minimum()
    {
        var version = GitExecutableLocator.ParseVersion("git version 2.46.0.windows.1");

        Assert.NotNull(version);
        Assert.True(version >= GitExecutableInfo.MinimumVersion);
    }

    [Fact]
    public void Executable_name_matches_the_platform()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "git.exe" : "git", GitExecutableLocator.ExecutableName);
    }

    [Fact]
    public void FindOnPath_returns_the_first_directory_holding_the_executable()
    {
        var root = TestScratch.NewDirectory("historia-locator-");
        var empty = Path.Combine(root, "empty");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            File.WriteAllText(Path.Combine(first, GitExecutableLocator.ExecutableName), "");
            File.WriteAllText(Path.Combine(second, GitExecutableLocator.ExecutableName), "");
            var pathVar = string.Join(Path.PathSeparator, empty, first, second);

            Assert.Equal(Path.Combine(first, GitExecutableLocator.ExecutableName), GitExecutableLocator.FindOnPath(pathVar));
            Assert.Null(GitExecutableLocator.FindOnPath(empty));
            Assert.Null(GitExecutableLocator.FindOnPath(null));
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("bin")]
    public void PreferDirectGit_swaps_the_launcher_for_the_mingw64_binary_when_present(string launcherDirectory)
    {
        var root = TestScratch.NewDirectory("historia-locator-");
        try
        {
            var launcher = TouchGit(root, launcherDirectory);
            var direct = TouchGit(root, "mingw64", "bin");

            Assert.Equal(direct, GitExecutableLocator.PreferDirectGit(launcher));
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public void PreferDirectGit_prefers_ucrt64_when_present()
    {
        var root = TestScratch.NewDirectory("historia-locator-");
        try
        {
            var launcher = TouchGit(root, "cmd");
            TouchGit(root, "mingw64", "bin");
            TouchGit(root, "clangarm64", "bin");
            var ucrt = TouchGit(root, "ucrt64", "bin");

            Assert.Equal(ucrt, GitExecutableLocator.PreferDirectGit(launcher));
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public void PreferDirectGit_falls_back_to_clangarm64_when_it_is_the_only_one()
    {
        var root = TestScratch.NewDirectory("historia-locator-");
        try
        {
            var launcher = TouchGit(root, "cmd");
            var arm = TouchGit(root, "clangarm64", "bin");

            Assert.Equal(arm, GitExecutableLocator.PreferDirectGit(launcher));
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public void PreferDirectGit_keeps_paths_without_a_sibling_mingw64()
    {
        var root = TestScratch.NewDirectory("historia-locator-");
        try
        {
            var launcher = TouchGit(root, "cmd");
            var elsewhere = TouchGit(root, "tools");
            var direct = TouchGit(root, "mingw64", "bin");

            Assert.Equal(elsewhere, GitExecutableLocator.PreferDirectGit(elsewhere));
            Assert.Equal(direct, GitExecutableLocator.PreferDirectGit(direct));
            Directory.Delete(Path.Combine(root, "mingw64"), recursive: true);
            Assert.Equal(launcher, GitExecutableLocator.PreferDirectGit(launcher));
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task PreferDirectGit_keeps_a_user_override()
    {
        var root = TestScratch.NewDirectory("historia-locator-");
        try
        {
            var launcher = TouchGit(root, "cmd");
            TouchGit(root, "mingw64", "bin");

            var info = await new GitExecutableLocator().ResolveAsync(launcher);

            Assert.Equal(launcher, info.Path);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ChooseGitPath_uses_the_direct_binary_when_exec_path_answers()
    {
        var root = TestScratch.NewDirectory("historia-locator-");
        try
        {
            var launcher = Path.Combine(root, "launcher.cmd");
            var direct = WriteCmd(root, "direct.cmd", "@echo C:\\fake\\libexec\\git-core");

            var chosen = await GitExecutableLocator.ChooseGitPathAsync(launcher, direct, TimeSpan.FromSeconds(30));

            Assert.Equal(direct, chosen);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ChooseGitPath_falls_back_to_the_launcher_when_exec_path_fails()
    {
        var root = TestScratch.NewDirectory("historia-locator-");
        try
        {
            var launcher = Path.Combine(root, "launcher.cmd");
            var failing = WriteCmd(root, "failing.cmd", "@exit /b 3");

            Assert.Equal(launcher, await GitExecutableLocator.ChooseGitPathAsync(launcher, failing, TimeSpan.FromSeconds(30)));
            Assert.Equal(launcher, await GitExecutableLocator.ChooseGitPathAsync(launcher, Path.Combine(root, "missing.exe"), TimeSpan.FromSeconds(30)));
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task CaptureVersion_reads_the_version_a_git_prints()
    {
        var root = TestScratch.NewDirectory("historia-locator-");
        try
        {
            var fake = WriteCmd(root, "git.cmd", "@echo git version 2.51.0.windows.2");

            var version = await GitExecutableLocator.CaptureVersionAsync(fake, TimeSpan.FromSeconds(30));

            Assert.Equal(new Version(2, 51, 0), version);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task CaptureVersion_times_out_on_a_hung_git()
    {
        var root = TestScratch.NewDirectory("historia-locator-");
        try
        {
            var hung = WriteCmd(root, "hung.cmd", "@ping -n 120 127.0.0.1 >nul");

            var started = Stopwatch.StartNew();
            var version = await GitExecutableLocator.CaptureVersionAsync(hung, TimeSpan.FromSeconds(1));

            Assert.Null(version);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(60), $"Probe took {started.Elapsed}.");
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task WhenResolvedAsync_completes_upon_resolution()
    {
        var locator = new GitExecutableLocator();
        var waitTask = locator.WhenResolvedAsync();

        Assert.False(waitTask.IsCompleted);

        var resolved = await locator.ResolveAsync(null);
        var awaited = await waitTask;

        Assert.Equal(resolved.Found, awaited.Found);
        Assert.Equal(resolved.Path, awaited.Path);
    }

    [Fact]
    public async Task WhenResolvedAsync_completes_not_found_when_resolution_fails()
    {
        var locator = new GitExecutableLocator();
        var waitTask = locator.WhenResolvedAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            locator.ResolveAsync(null, new CancellationToken(canceled: true)));

        var awaited = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(awaited.Found);
        Assert.NotNull(awaited.NotFoundReason);
    }

    /// <summary>A locator that searches only <paramref name="pathVar"/> and reads versions from
    /// <paramref name="versions"/> instead of running anything; a path not listed answers no version.</summary>
    private static GitExecutableLocator FakeLocator(string appDir, string pathVar, Dictionary<string, string> versions, List<string>? probed = null) =>
        new(appDir)
        {
            PathEnvironmentOverride = pathVar,
            SkipWellKnownLocations = true,
            SkipRegistry = true,
            VersionProbeOverride = (path, _) =>
            {
                probed?.Add(path);
                return Task.FromResult(versions.TryGetValue(path, out var version) ? Version.Parse(version) : null);
            },
        };

    [Fact]
    public async Task ResolveAsync_no_installed_git_and_bundled_present_resolves_bundled()
    {
        var root = TestScratch.NewDirectory("historia-locator-bundled-");
        try
        {
            var appDir = Path.Combine(root, "app");
            var bundled = TouchGit(appDir, "git", "ucrt64", "bin");
            var locator = FakeLocator(appDir, "", new() { [bundled] = "2.56.0" });

            var info = await locator.ResolveAsync(null);

            Assert.True(info.Found);
            Assert.Equal(GitSource.Bundled, info.Source);
            Assert.Equal(bundled, info.Path);
            Assert.Equal(new Version(2, 56, 0), info.Version);
            Assert.Equal(Path.Combine(appDir, "git"), info.BundledRoot);
            Assert.Null(info.Notice);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ResolveAsync_installed_at_or_above_minimum_prefers_installed_over_bundled()
    {
        var root = TestScratch.NewDirectory("historia-locator-installed-");
        try
        {
            var appDir = Path.Combine(root, "app");
            var bundled = TouchGit(appDir, "git", "ucrt64", "bin");
            var installed = TouchGit(root, "installed", "tools");
            var locator = FakeLocator(appDir, Path.GetDirectoryName(installed)!, new() { [bundled] = "2.56.0", [installed] = "2.51.0" });

            var info = await locator.ResolveAsync(null);

            Assert.True(info.Found);
            Assert.Equal(GitSource.Installed, info.Source);
            Assert.Equal(installed, info.Path);
            Assert.Null(info.BundledRoot);
            Assert.Null(info.Notice);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ResolveAsync_installed_below_minimum_falls_back_to_bundled_with_notice()
    {
        var root = TestScratch.NewDirectory("historia-locator-old-");
        try
        {
            var appDir = Path.Combine(root, "app");
            var bundled = TouchGit(appDir, "git", "ucrt64", "bin");
            var installed = TouchGit(root, "installed", "tools");
            var locator = FakeLocator(appDir, Path.GetDirectoryName(installed)!, new() { [bundled] = "2.56.0", [installed] = "2.40.1" });

            var info = await locator.ResolveAsync(null);

            Assert.True(info.Found);
            Assert.Equal(GitSource.Bundled, info.Source);
            Assert.Equal(bundled, info.Path);
            Assert.NotNull(info.Notice);
            Assert.Contains("2.40.1", info.Notice);
            Assert.Contains("2.46", info.Notice);
            Assert.Contains("2.56.0", info.Notice);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ResolveAsync_an_old_git_on_path_does_not_hide_a_newer_installed_one()
    {
        var root = TestScratch.NewDirectory("historia-locator-shadow-");
        try
        {
            var appDir = Path.Combine(root, "app");
            var bundled = TouchGit(appDir, "git", "ucrt64", "bin");
            var old = TouchGit(root, "old", "tools");
            var newer = TouchGit(root, "newer", "tools");
            var pathVar = string.Join(Path.PathSeparator, Path.GetDirectoryName(old), Path.GetDirectoryName(newer));
            var locator = FakeLocator(appDir, pathVar, new() { [bundled] = "2.56.0", [old] = "2.40.1", [newer] = "2.50.0" });

            var info = await locator.ResolveAsync(null);

            Assert.Equal(GitSource.Installed, info.Source);
            Assert.Equal(newer, info.Path);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ResolveAsync_probes_a_git_listed_twice_on_path_once()
    {
        var root = TestScratch.NewDirectory("historia-locator-dup-");
        try
        {
            var installed = TouchGit(root, "installed", "tools");
            var dir = Path.GetDirectoryName(installed)!;
            var probed = new List<string>();
            var locator = FakeLocator(Path.Combine(root, "app"), string.Join(Path.PathSeparator, dir, dir), [], probed);

            await locator.ResolveAsync(null);

            Assert.Equal([installed], probed);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ResolveAsync_installed_git_with_no_version_falls_back_to_bundled_and_says_why()
    {
        var root = TestScratch.NewDirectory("historia-locator-broken-");
        try
        {
            var appDir = Path.Combine(root, "app");
            var bundled = TouchGit(appDir, "git", "ucrt64", "bin");
            var broken = TouchGit(root, "broken", "tools");
            var locator = FakeLocator(appDir, Path.GetDirectoryName(broken)!, new() { [bundled] = "2.56.0" });

            var info = await locator.ResolveAsync(null);

            Assert.Equal(GitSource.Bundled, info.Source);
            Assert.NotNull(info.Notice);
            Assert.Contains(broken, info.Notice);
            Assert.Contains("did not report a valid version", info.Notice);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ResolveAsync_never_discovers_a_git_cmd()
    {
        var root = TestScratch.NewDirectory("historia-locator-cmd-");
        try
        {
            var cmdDir = Path.Combine(root, "shim");
            var shim = WriteCmd(cmdDir, "git.cmd", "@echo git version 2.56.0");
            var real = TouchGit(root, "real", "tools");
            var pathVar = string.Join(Path.PathSeparator, cmdDir, Path.GetDirectoryName(real));
            var locator = FakeLocator(Path.Combine(root, "app"), pathVar, new() { [shim] = "2.56.0", [real] = "2.50.0" });

            var info = await locator.ResolveAsync(null);

            Assert.Equal(real, info.Path);
            Assert.Null(GitExecutableLocator.FindOnPath(cmdDir));
            Assert.Equal(shim, GitExecutableLocator.PreferDirectGit(shim));
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ResolveAsync_ignores_a_bundled_git_cmd()
    {
        var root = TestScratch.NewDirectory("historia-locator-bundled-cmd-");
        try
        {
            var appDir = Path.Combine(root, "app");
            var shim = WriteCmd(Path.Combine(appDir, "git", "ucrt64", "bin"), "git.cmd", "@echo git version 2.56.0");
            var locator = FakeLocator(appDir, "", new() { [shim] = "2.56.0" });

            Assert.False((await locator.ResolveAsync(null)).Found);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ResolveAsync_disallow_bundled_does_not_fall_back()
    {
        var root = TestScratch.NewDirectory("historia-locator-disallow-");
        try
        {
            var appDir = Path.Combine(root, "app");
            var bundled = TouchGit(appDir, "git", "ucrt64", "bin");
            var locator = FakeLocator(appDir, "", new() { [bundled] = "2.56.0" });

            var info = await locator.ResolveAsync(null, allowBundled: false);

            Assert.False(info.Found);
            Assert.Null(info.Path);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ResolveAsync_user_path_wins_and_is_used_exactly_as_given()
    {
        var root = TestScratch.NewDirectory("historia-locator-user-");
        try
        {
            var appDir = Path.Combine(root, "app");
            var bundled = TouchGit(appDir, "git", "ucrt64", "bin");
            var launcher = TouchGit(root, "user", "cmd");
            TouchGit(root, "user", "mingw64", "bin");
            var locator = FakeLocator(appDir, "", new() { [bundled] = "2.56.0", [launcher] = "2.50.0" });

            var info = await locator.ResolveAsync(launcher);

            Assert.True(info.Found);
            Assert.Equal(GitSource.UserSetting, info.Source);
            Assert.Equal(launcher, info.Path);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ResolveAsync_user_path_below_minimum_is_reported_without_fallback()
    {
        var root = TestScratch.NewDirectory("historia-locator-user-old-");
        try
        {
            var appDir = Path.Combine(root, "app");
            var bundled = TouchGit(appDir, "git", "ucrt64", "bin");
            var userGit = TouchGit(root, "user", "tools");
            var locator = FakeLocator(appDir, "", new() { [bundled] = "2.56.0", [userGit] = "2.40.1" });

            var info = await locator.ResolveAsync(userGit);

            Assert.False(info.Found);
            Assert.Equal(GitSource.UserSetting, info.Source);
            Assert.Contains("2.40.1", info.NotFoundReason);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ResolveAsync_missing_user_path_falls_through_to_discovery_and_says_so()
    {
        var root = TestScratch.NewDirectory("historia-locator-user-missing-");
        try
        {
            var appDir = Path.Combine(root, "app");
            var bundled = TouchGit(appDir, "git", "ucrt64", "bin");
            var missing = Path.Combine(root, "missing", "git.exe");
            var locator = FakeLocator(appDir, "", new() { [bundled] = "2.56.0" });

            var info = await locator.ResolveAsync(missing);

            Assert.True(info.Found);
            Assert.Equal(GitSource.Bundled, info.Source);
            Assert.NotNull(info.Notice);
            Assert.Contains(missing, info.Notice);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public async Task ResolveAsync_missing_user_path_with_nothing_else_reports_the_path()
    {
        var root = TestScratch.NewDirectory("historia-locator-user-none-");
        try
        {
            var missing = Path.Combine(root, "missing", "git.exe");
            var locator = FakeLocator(Path.Combine(root, "app"), "", []);

            var info = await locator.ResolveAsync(missing);

            Assert.False(info.Found);
            Assert.Equal(GitSource.UserSetting, info.Source);
            Assert.Equal(missing, info.Path);
            Assert.Contains(missing, info.NotFoundReason);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    private static string TouchGit(string root, params string[] directories)
    {
        var directory = Path.Combine([root, .. directories]);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "git.exe");
        File.WriteAllText(path, "");
        return path;
    }

    private static string WriteCmd(string root, string name, string body)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        File.WriteAllText(path, body + Environment.NewLine);
        return path;
    }
}
