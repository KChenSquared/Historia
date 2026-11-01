namespace Historia.Services.Terminal;

/// <summary>
/// The OS-facing seams <see cref="TerminalSessionService"/> uses to resolve a shell, a start
/// directory, and the child environment. Injected so every part of session start-up is unit
/// testable from any platform.
/// </summary>
public sealed record TerminalHostEnvironment
{
    public required OSPlatformKind Platform { get; init; }
    public required Func<string, bool> FileExists { get; init; }
    public required Func<string, bool> DirectoryExists { get; init; }
    public required Func<string, string?> GetEnvironmentVariable { get; init; }
    public required Func<string, string?> FindOnPath { get; init; }
    public required Func<string?> GetPasswdShell { get; init; }
    public required Func<IReadOnlyDictionary<string, string>> GetInheritedEnvironment { get; init; }
    public required string HomeDirectory { get; init; }
    public required string CurrentDirectory { get; init; }
    public required string ExecutablePath { get; init; }
    public required string Version { get; init; }

    public static TerminalHostEnvironment CreateSystem(Func<GitExecutableInfo?>? gitInfoProvider = null) => new()
    {
        Platform = OperatingSystem.IsWindows() ? OSPlatformKind.Windows
                 : OperatingSystem.IsMacOS() ? OSPlatformKind.MacOS
                 : OSPlatformKind.Linux,
        FileExists = File.Exists,
        DirectoryExists = Directory.Exists,
        GetEnvironmentVariable = Environment.GetEnvironmentVariable,
        FindOnPath = FindExecutableOnPath,
        GetPasswdShell = () => null,
        GetInheritedEnvironment = () => SnapshotEnvironment(gitInfoProvider?.Invoke()),
        HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        CurrentDirectory = Environment.CurrentDirectory,
        ExecutablePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "Historia",
        Version = AppVersion.Current,
    };

    public static TerminalHostEnvironment System { get; } = CreateSystem();

    internal static IReadOnlyDictionary<string, string> SnapshotEnvironment(GitExecutableInfo? gitInfo = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new Dictionary<string, string>(comparer);
        foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            if (kv.Key is string k && kv.Value is string v)
                result[k] = v;
        }

        if (gitInfo is { Found: true, Source: GitSource.Bundled, BundledRoot: not null })
        {
            var cmdDir = Path.Combine(gitInfo.BundledRoot, "cmd");
            var existingPath = result.TryGetValue("PATH", out var p) ? p : "";
            result["PATH"] = string.IsNullOrEmpty(existingPath) ? cmdDir : $"{cmdDir}{Path.PathSeparator}{existingPath}";
        }

        return result;
    }

    private static string? FindExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // a malformed PATH entry — skip it
            }
        }
        return null;
    }
}
