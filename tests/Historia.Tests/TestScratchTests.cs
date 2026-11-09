namespace Historia.Tests;

[Trait("tier", "unit")]
[Collection("serial")]
public sealed class TestScratchTests
{
    [Fact]
    public void Root_defaults_to_the_bin_scratch_folder()
    {
        var original = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
        try
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", null);
            var root = TestScratch.Root;
            Assert.False(string.IsNullOrWhiteSpace(root));
            Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase);
            Assert.True(root.EndsWith(@"bin\Historia.Tests\scratch", StringComparison.OrdinalIgnoreCase) ||
                        root.EndsWith(@"bin/Historia.Tests/scratch", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", original);
        }
    }

    [Fact]
    public void Root_honours_the_environment_variable()
    {
        var original = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
        var custom = TestScratch.NewPath("custom-scratch-");
        try
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", custom);
            Assert.Equal(custom, TestScratch.Root);

            var dir = TestScratch.NewDirectory("unit-test-");
            Assert.StartsWith(custom, dir, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", original);
            TestScratch.Delete(custom);
        }
    }

    [Fact]
    public void Root_falls_back_to_temp_when_metadata_is_missing()
    {
        var original = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
        try
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", null);
            var root = TestScratch.GetRoot(null);
            Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                         root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", original);
        }
    }

    [Fact]
    public void PruneStale_removes_only_finished_run_folders_older_than_thirty_minutes()
    {
        var original = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
        var custom = TestScratch.NewPath("custom-prune-");
        try
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", custom);
            Directory.CreateDirectory(custom);

            var deadPid = FindUnusedPid();
            var deadOld = BackDated(Path.Combine(custom, $"historia-run-{deadPid}-{Hex()}"), minutes: 31);
            var deadFresh = BackDated(Path.Combine(custom, $"historia-run-{deadPid}-{Hex()}"), minutes: 29);
            var liveOld = BackDated(Path.Combine(custom, $"historia-run-{Environment.ProcessId}-{Hex()}"), minutes: 31);
            var otherOld = BackDated(Path.Combine(custom, $"old-{Hex()}"), minutes: 31);

            var otherFile = Path.Combine(custom, "notes.txt");
            File.WriteAllText(otherFile, "keep");
            File.SetLastWriteTimeUtc(otherFile, DateTime.UtcNow.AddMinutes(-31));

            TestScratch.PruneStale();

            Assert.False(Directory.Exists(deadOld));
            Assert.True(Directory.Exists(deadFresh));
            Assert.True(Directory.Exists(liveOld));
            Assert.True(Directory.Exists(otherOld));
            Assert.True(File.Exists(otherFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", original);
            TestScratch.Delete(custom);
        }
    }

    [Fact]
    public void PruneStale_never_runs_in_the_system_temp_folder()
    {
        var original = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
        try
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", null);
            Assert.False(TestScratch.CanPrune(Path.GetTempPath()));
            Assert.True(TestScratch.CanPrune(TestScratch.Root));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", original);
        }
    }

    [Fact]
    public void Delete_removes_a_tree_containing_read_only_files()
    {
        var dir = TestScratch.NewDirectory("readonly-test-");
        try
        {
            Assert.Equal(0, Git(dir, "init"));
            Assert.Equal(0, Git(dir, "config", "user.name", "Test"));
            Assert.Equal(0, Git(dir, "config", "user.email", "test@example.com"));
            // Trap: a commit can start git's auto-maintenance detached, which creates and removes
            // objects/maintenance.lock while the scan below reads attributes, and can write files
            // after the delete. Turned off so the test sees only read-only objects.
            Assert.Equal(0, Git(dir, "config", "maintenance.auto", "false"));
            Assert.Equal(0, Git(dir, "config", "gc.auto", "0"));
            File.WriteAllText(Path.Combine(dir, "f.txt"), "hello");
            Assert.Equal(0, Git(dir, "add", "."));
            Assert.Equal(0, Git(dir, "commit", "-m", "initial", "--no-gpg-sign"));

            Assert.Contains(
                Directory.EnumerateFiles(Path.Combine(dir, ".git", "objects"), "*", SearchOption.AllDirectories),
                f => (File.GetAttributes(f) & FileAttributes.ReadOnly) != 0);

            TestScratch.Delete(dir);

            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            TestScratch.Delete(dir);
        }
    }

    [Fact]
    public void Delete_removes_a_junction_without_touching_its_target()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var target = TestScratch.NewDirectory("junction-target-");
        var owner = TestScratch.NewDirectory("junction-owner-");
        var kept = Path.Combine(target, "kept.txt");
        try
        {
            File.WriteAllText(kept, "keep");
            File.SetAttributes(kept, FileAttributes.ReadOnly);

            var link = Path.Combine(owner, "link");
            var psi = new ProcessStartInfo("cmd")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
                psi.ArgumentList.Add(arg);
            using (var p = Process.Start(psi)!)
                p.WaitForExit();
            Assert.True((new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0);

            TestScratch.Delete(owner);

            Assert.False(Directory.Exists(owner));
            Assert.True(File.Exists(kept));
            Assert.True((File.GetAttributes(kept) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            TestScratch.Delete(owner);
            TestScratch.Delete(target);
        }
    }

    [Fact]
    public void NewPath_does_not_create_the_path()
    {
        var path = TestScratch.NewPath("path-test-", ".txt");
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(path));
        Assert.EndsWith(".txt", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewDirectory_creates_it_under_the_run_folder()
    {
        var dir = TestScratch.NewDirectory("dir-test-");
        try
        {
            Assert.True(Directory.Exists(dir));
            var parent = Directory.GetParent(dir);
            Assert.NotNull(parent);
            Assert.StartsWith($"historia-run-{Environment.ProcessId}-", parent.Name, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestScratch.Delete(dir);
        }
    }

    [Fact]
    public void Git_does_not_discover_the_enclosing_repository_from_scratch()
    {
        if (!TestScratch.IsInsideGitWorkTree(TestScratch.Root))
            return;

        var dir = TestScratch.NewDirectory("git-ceiling-");
        try
        {
            Assert.NotEqual(0, Git(dir, "rev-parse", "--show-toplevel"));
        }
        finally
        {
            TestScratch.Delete(dir);
        }
    }

    private static int Git(string dir, params string[] args)
    {
        return TestGit.Run(dir, args, testIdentity: false).ExitCode;
    }

    private static string BackDated(string dir, int minutes)
    {
        Directory.CreateDirectory(dir);
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddMinutes(-minutes));
        return dir;
    }

    private static string Hex() => Guid.NewGuid().ToString("N")[..8];

    private static int FindUnusedPid()
    {
        for (var pid = 999_999; pid > 0; pid--)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return pid;
            }
        }
        throw new InvalidOperationException("No unused process id found.");
    }
}
