namespace Historia.Tests;

/// <summary>
/// Offline-first guard: Historia must start and render from
/// what ships in the package, so no UI asset may reference an <c>http(s)</c> URL. This scans
/// every text file under <c>wwwroot/</c> plus every <c>.razor</c>, <c>.css</c> <b>and .cs</b>
/// in the tree and fails on any outbound URL outside a comment. Allowed matches are
/// non-dereferenced XML namespace identifiers (XAML and SVG) and an explicit allowlist of
/// consented URLs — the WebView2 bootstrapper fwlink.
/// </summary>
[Trait("tier", "unit")]
public class OfflineAssetTests
{
    private static readonly string[] AllowedNamespacePrefixes =
    {
        "http://schemas.microsoft.com/",
        "http://www.w3.org/",
        "https://github.com/avaloniaui",
        "http://schemas.openxmlformats.org/",
    };

    /// <summary>Consented URLs — a real allowlist, not a blind spot.</summary>
    private static readonly string[] ConsentedUrls =
    {
        "https://go.microsoft.com/fwlink/p/?LinkId=2124703",       // WebView2 bootstrapper
        "https://developer.microsoft.com/microsoft-edge/webview2/", // its manual-install fallback
        "https://github.com/owner/repo.git",                        // clone-dialog input watermark (not fetched)
        "https://github.com/settings/tokens/new?scopes=repo&description=Historia", // "Sign in with GitHub" — user-initiated browser open
        "https://HistoriaHQ.dev/autolink?issue=$1",                // Issue autolinks hint example (display text, not fetched)
        "https://generativelanguage.googleapis.com/v1beta",         // Gemini template default; requested only when the user generates or fetches models
    };

    private static readonly string[] BinaryExtensions =
    {
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".woff", ".woff2", ".ttf", ".otf", ".eot",
    };

    // Stops at { so an interpolated URL-building template (e.g. $"https://{host}/{path}") — code
    // that constructs a URL, not a package asset fetch — is not a match.
    private static readonly Regex UrlPattern =
        new(@"https?://[^\s""'(){}<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void NoOutboundUrlsInShippedAssets()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var file in ScannedFiles(root))
        {
            var text = StripComments(File.ReadAllText(file), Path.GetExtension(file));
            foreach (Match m in UrlPattern.Matches(text))
            {
                var url = m.Value.TrimEnd('.', ',', ';', ')');
                if (AllowedNamespacePrefixes.Any(p => url.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (ConsentedUrls.Any(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                offenders.Add($"{rel}: {url}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Outbound http(s) URL(s) in shipped assets — offline-first violation:\n  " +
            string.Join("\n  ", offenders));
    }

    private static IEnumerable<string> ScannedFiles(string root)
    {
        var stdout = TestGit.RunChecked(root, ["ls-files", "--cached", "--others", "--exclude-standard"], testIdentity: false);

        var candidateExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".razor", ".css", ".cs", ".axaml"
        };

        var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in lines)
        {
            var normalizedRel = rel.Replace('\\', '/');
            if (IsUnderBuildDir(root, normalizedRel))
                continue;
            if (normalizedRel.Contains("/tests/") || normalizedRel.StartsWith("tests/", StringComparison.Ordinal))
                continue; // the test tree names URLs on purpose

            var ext = Path.GetExtension(normalizedRel).ToLowerInvariant();
            var isWwwroot = normalizedRel.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase);

            if (isWwwroot)
            {
                if (!BinaryExtensions.Contains(ext) && seen.Add(normalizedRel))
                {
                    var full = Path.Combine(root, normalizedRel);
                    if (File.Exists(full))
                        yield return full;
                }
            }
            else if (candidateExtensions.Contains(ext) && seen.Add(normalizedRel))
            {
                var full = Path.Combine(root, normalizedRel);
                if (File.Exists(full))
                    yield return full;
            }
        }
    }

    private static bool IsUnderBuildDir(string root, string file)
    {
        var rel = file.Contains('/') || file.Contains('\\') && !Path.IsPathRooted(file)
            ? file.Replace('\\', '/')
            : Path.GetRelativePath(root, file).Replace('\\', '/');
        return rel.Contains("/bin/") || rel.Contains("/obj/") ||
               rel.StartsWith("bin/") || rel.StartsWith("obj/");
    }

    /// <summary>Removes comments so a URL parked in a comment does not fail the scan.</summary>
    private static string StripComments(string text, string ext)
    {
        // CSS/JS block comments.
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        // HTML / Razor comments.
        text = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"@\*.*?\*@", " ", RegexOptions.Singleline);
        // Line comments for .cs / .js — but not the // inside a :// scheme.
        if (ext.Equals(".js", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            text = Regex.Replace(text, @"(?<!:)//[^\n]*", " ");
        return text;
    }

    private static string RepoRoot()
    {
        // A worktree's top-level .git is a file, not a directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitEntry = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                break;
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
