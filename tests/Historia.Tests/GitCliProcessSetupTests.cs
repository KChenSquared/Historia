namespace Historia.Tests;

public class GitCliProcessSetupTests
{
    [Fact]
    public void ComposeEnvironment_prepends_bundled_paths_when_source_is_bundled()
    {
        var root = TestScratch.NewDirectory("historia-procsetup-bundled-");
        try
        {
            var bundledRoot = Path.Combine(root, "git");
            var ucrtBin = Path.Combine(bundledRoot, "ucrt64", "bin");
            var usrBin = Path.Combine(bundledRoot, "usr", "bin");
            Directory.CreateDirectory(ucrtBin);
            Directory.CreateDirectory(usrBin);

            var psi = new ProcessStartInfo();
            psi.Environment["PATH"] = @"C:\existing\path";

            var bundledInfo = new GitExecutableInfo
            {
                Found = true,
                Source = GitSource.Bundled,
                BundledRoot = bundledRoot,
                Path = Path.Combine(ucrtBin, "git.exe"),
                Version = new Version(2, 56, 0),
            };

            GitCliProcessSetup.ComposeEnvironment(psi, profile: null, extraEnvironment: null, bundledInfo);

            var path = psi.Environment["PATH"];
            Assert.NotNull(path);
            var expectedPrefix = $"{ucrtBin}{Path.PathSeparator}{usrBin}{Path.PathSeparator}";
            Assert.StartsWith(expectedPrefix, path);
            Assert.EndsWith(@"C:\existing\path", path);
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public void ComposeEnvironment_leaves_path_unchanged_when_source_is_installed()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["PATH"] = @"C:\existing\path";

        var installedInfo = new GitExecutableInfo
        {
            Found = true,
            Source = GitSource.Installed,
            Path = @"C:\Program Files\Git\cmd\git.exe",
            Version = new Version(2, 51, 0),
        };

        GitCliProcessSetup.ComposeEnvironment(psi, profile: null, extraEnvironment: null, installedInfo);

        Assert.Equal(@"C:\existing\path", psi.Environment["PATH"]);
    }
}

