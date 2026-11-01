namespace Historia.Tests;

public class BundledGitManifestTests
{
    private static readonly Regex Hex64Regex = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    [Fact]
    public void Manifest_Parses_AndMatchesRequiredSchema()
    {
        var repoRoot = FindRepoRoot();
        var manifestPath = Path.Combine(repoRoot, "packaging", "bundled-git.json");
        Assert.True(File.Exists(manifestPath), $"Manifest not found at {manifestPath}");

        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("minimumGitVersion", out var minVerProp), "Manifest must contain minimumGitVersion");
        var minVersionStr = minVerProp.GetString();
        Assert.NotNull(minVersionStr);
        var minVersion = Version.Parse(minVersionStr);
        Assert.Equal(GitExecutableInfo.MinimumVersion, minVersion);

        Assert.True(root.TryGetProperty("mingit", out var mingitProp), "Manifest must contain mingit");
        ValidateComponent(mingitProp, "mingit");

        Assert.True(root.TryGetProperty("gitLfs", out var gitLfsProp), "Manifest must contain gitLfs");
        ValidateComponent(gitLfsProp, "gitLfs");
    }

    [Fact]
    public void Licence_file_names_the_pinned_tags()
    {
        var repoRoot = FindRepoRoot();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "packaging", "bundled-git.json")));
        var licences = File.ReadAllText(Path.Combine(repoRoot, "docs", "third-party-licenses.md"));

        var mingitTag = doc.RootElement.GetProperty("mingit").GetProperty("version").GetString();
        var gitLfsTag = doc.RootElement.GetProperty("gitLfs").GetProperty("version").GetString();

        Assert.Contains($"tree/{mingitTag}", licences);
        Assert.Contains($"pinned release `{gitLfsTag}`", licences);
    }

    [Fact]
    public void Licence_file_says_where_the_bundled_licences_and_sources_are()
    {
        var licences = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "third-party-licenses.md"));

        Assert.Contains(@"git\usr\share\licenses\gcc-libs\COPYING3", licences);
        Assert.Contains(@"git\usr\share\licenses\openssh\LICENCE", licences);
        Assert.Contains(@"git\ucrt64\doc\git-credential-manager\LICENSE", licences);
        Assert.Contains(@"git\etc\package-versions.txt", licences);
        Assert.Contains("repo.msys2.org/msys/sources", licences);
    }

    private static void ValidateComponent(JsonElement element, string componentName)
    {
        Assert.True(element.TryGetProperty("version", out var verProp), $"{componentName} must define version");
        Assert.False(string.IsNullOrWhiteSpace(verProp.GetString()), $"{componentName} version must not be empty");

        Assert.True(element.TryGetProperty("asset", out var assetProp), $"{componentName} must define asset");
        var asset = assetProp.GetString();
        Assert.False(string.IsNullOrWhiteSpace(asset), $"{componentName} asset must not be empty");

        Assert.True(element.TryGetProperty("url", out var urlProp), $"{componentName} must define url");
        var url = urlProp.GetString();
        Assert.NotNull(url);
        Assert.StartsWith("https://github.com/", url);
        Assert.EndsWith(asset!, url);

        Assert.True(element.TryGetProperty("sha256", out var shaProp), $"{componentName} must define sha256");
        var sha = shaProp.GetString();
        Assert.NotNull(sha);
        Assert.Matches(Hex64Regex, sha);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitEntry = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root (.git) could not be located.");
    }
}

