namespace Historia.Tests;

/// <summary>
/// Guard against tests creating fixtures in the operating system temp directory.
/// All test fixtures must be created under the project scratch root via TestScratch.
/// Allowed uses of temp paths are restricted to an explicit allowlist where nothing is
/// created (path arithmetic or external working directories). Each entry names a file by its
/// path under <c>tests/</c> and the exact number of lines in it that use a temp path, so a new
/// use in an allowed file fails as well.
/// </summary>
[Trait("tier", "unit")]
public class TestTempUsageGuardTests
{
    private static readonly Dictionary<string, (int Lines, string Reason)> TempAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Historia.Tests/TestScratch.cs"] = (3, "Fallback scratch root when assembly metadata is missing, and the never-prune check."),
        ["Historia.Tests/TestScratchTests.cs"] = (3, "Asserts the fallback root and that the system temp folder is never pruned."),
        ["Historia.Tests/ControlInputValidationTests.cs"] = (3, "Path arithmetic only; no files created."),
        ["Historia.Tests/ControlArgsTests.cs"] = (2, "Path arithmetic only; no files created."),
        ["Historia.Tests/GitCliServiceTests.cs"] = (2, "External working directory outside git work trees; no files created."),
        ["Historia.Tests/CommitHeaderReaderTests.cs"] = (2, "External working directory outside git work trees; no files created."),
        ["Historia.Tests/GraphClonePrecacheTests.cs"] = (1, "External working directory outside git work trees; no files created."),
        ["Historia.Tests/RepositoryDiscoveryTests.cs"] = (1, "Discovery root search; no files created."),
    };

    private const string GuardFile = "Historia.Tests/TestTempUsageGuardTests.cs";

    private static readonly Regex TempPattern =
        new(@"(?:GetTemp(?:Path|FileName|Directory)|CreateTempSubdirectory)\s*\(", RegexOptions.Compiled);

    private static readonly Regex DirectoryDeletePattern =
        new(@"Directory\.Delete\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Tests_do_not_create_files_in_the_system_temp_folder()
    {
        var root = RepoRoot();
        var violations = new List<string>();

        foreach (var (rel, lines) in TempUsages(root))
        {
            var allowed = TempAllowlist.TryGetValue(rel, out var entry) ? entry.Lines : 0;
            if (lines.Count > allowed)
            {
                foreach (var (number, text) in lines)
                    violations.Add($"tests/{rel}:{number}: {text}");
                if (allowed > 0)
                    violations.Add($"tests/{rel}: {lines.Count} temp uses, allowlist permits {allowed}");
            }
        }

        foreach (var (rel, number, text) in TempDeletes(root))
            violations.Add($"tests/{rel}:{number}: Directory.Delete on temp path: {text}");

        Assert.True(violations.Count == 0,
            "Direct temporary directory usage detected in tests. Use TestScratch instead:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void Temp_allowlist_has_no_stale_entries()
    {
        var found = TempUsages(RepoRoot()).ToDictionary(u => u.Rel, u => u.Lines.Count, StringComparer.OrdinalIgnoreCase);

        var stale = TempAllowlist
            .Where(e => !found.TryGetValue(e.Key, out var count) || count < e.Value.Lines)
            .Select(e => $"{e.Key}: allowlist permits {e.Value.Lines}, found {found.GetValueOrDefault(e.Key)}")
            .ToList();

        Assert.True(stale.Count == 0,
            "Temp allowlist entries permit more uses than the file has:\n  " +
            string.Join("\n  ", stale));
    }

    private static IEnumerable<(string Rel, List<(int Number, string Text)> Lines)> TempUsages(string root)
    {
        foreach (var (rel, lines) in TestFiles(root))
        {
            var hits = new List<(int, string)>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (TempPattern.IsMatch(StripLineComment(lines[i])))
                    hits.Add((i + 1, lines[i].Trim()));
            }
            if (hits.Count > 0)
                yield return (rel, hits);
        }
    }

    private static IEnumerable<(string Rel, int Number, string Text)> TempDeletes(string root)
    {
        foreach (var (rel, lines) in TestFiles(root))
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var stripped = StripLineComment(lines[i]);
                if (DirectoryDeletePattern.IsMatch(stripped) && stripped.Contains("GetTemp", StringComparison.OrdinalIgnoreCase))
                    yield return (rel, i + 1, lines[i].Trim());
            }
        }
    }

    private static IEnumerable<(string Rel, string[] Lines)> TestFiles(string root)
    {
        var testsDir = Path.Combine(root, "tests");
        foreach (var file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(testsDir, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/") ||
                rel.StartsWith("bin/") || rel.StartsWith("obj/") ||
                string.Equals(rel, GuardFile, StringComparison.OrdinalIgnoreCase))
                continue;

            yield return (rel, File.ReadAllLines(file));
        }
    }

    private static string StripLineComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
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
