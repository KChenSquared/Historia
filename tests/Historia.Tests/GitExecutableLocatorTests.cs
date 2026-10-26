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
        var path = Path.Combine(root, name);
        File.WriteAllText(path, body + Environment.NewLine);
        return path;
    }
}
