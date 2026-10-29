namespace Historia.Tests;

[Trait("tier", "unit")]

public class TerminalEnvironmentTests
{
    private const string HistoriaExe = @"C:\Program Files\Historia\Historia.exe";

    private static Dictionary<string, string> Env(params (string, string)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    private static IReadOnlyDictionary<string, string> Build(
        IReadOnlyDictionary<string, string> inherited, string? sshKey = null)
        => TerminalEnvironment.Build(inherited, sshKey, HistoriaExe, "5.1.0");

    [Fact]
    public void AlwaysRemoved_MatchesDocumentedNames()
    {
        Assert.Equal(
        [
            "HISTORIA_ASKPASS", "HISTORIA_ASKPASS_USERNAME", "HISTORIA_ASKPASS_PASSWORD", "HISTORIA_ASKPASS_HOST",
            "HISTORIA_REBASE_DIR", "HISTORIA_REBASE_TOKEN", "HISTORIA_SSH_KEY", "HISTORIA_LOG",
        ], TerminalEnvironment.AlwaysRemoved);
    }

    [Fact]
    public void RemovesAskpassPatAndMarker()
    {
        var e = Build(Env(
            ("HISTORIA_ASKPASS", "1"),
            ("HISTORIA_ASKPASS_USERNAME", "git"),
            ("HISTORIA_ASKPASS_PASSWORD", "ghp_secret"),
            ("HISTORIA_ASKPASS_HOST", "github.com"),
            ("PATH", "/usr/bin")));
        Assert.False(e.ContainsKey("HISTORIA_ASKPASS"));
        Assert.False(e.ContainsKey("HISTORIA_ASKPASS_USERNAME"));
        Assert.False(e.ContainsKey("HISTORIA_ASKPASS_PASSWORD"));
        Assert.False(e.ContainsKey("HISTORIA_ASKPASS_HOST"));
        Assert.Equal("/usr/bin", e["PATH"]);
    }

    [Fact]
    public void RemovesRebaseDirAndToken()
    {
        var e = Build(Env(("HISTORIA_REBASE_DIR", @"C:\t"), ("HISTORIA_REBASE_TOKEN", "abc123")));
        Assert.False(e.ContainsKey("HISTORIA_REBASE_DIR"));
        Assert.False(e.ContainsKey("HISTORIA_REBASE_TOKEN"));
    }

    [Fact]
    public void RemovesHistoriaSshKeyAndLog()
    {
        var e = Build(Env(("HISTORIA_SSH_KEY", @"C:\k"), ("HISTORIA_LOG", "1")));
        Assert.False(e.ContainsKey("HISTORIA_SSH_KEY"));
        Assert.False(e.ContainsKey("HISTORIA_LOG"));
    }

    [Fact]
    public void RemovesGitEditorWhenItPointsAtHistoria()
    {
        var e = Build(Env(("GIT_EDITOR", $"\"{HistoriaExe}\" --rebase-msg")));
        Assert.False(e.ContainsKey("GIT_EDITOR"));
    }

    [Fact]
    public void RemovesGitSequenceEditorWhenItPointsAtHistoria()
    {
        var e = Build(Env(("GIT_SEQUENCE_EDITOR", $"{HistoriaExe} --rebase-seq")));
        Assert.False(e.ContainsKey("GIT_SEQUENCE_EDITOR"));
    }

    [Fact]
    public void PreservesGitEditorWhenUserSet()
    {
        var e = Build(Env(("GIT_EDITOR", "vim")));
        Assert.Equal("vim", e["GIT_EDITOR"]);
    }

    [Fact]
    public void PreservesUserGitAskpass()
    {
        var e = Build(Env(("GIT_ASKPASS", "/usr/bin/ksshaskpass"), ("SSH_ASKPASS", "/usr/bin/ksshaskpass")));
        Assert.Equal("/usr/bin/ksshaskpass", e["GIT_ASKPASS"]);
        Assert.Equal("/usr/bin/ksshaskpass", e["SSH_ASKPASS"]);
    }

    [Fact]
    public void PreservesGitTerminalPrompt()
    {
        var e = Build(Env(("GIT_TERMINAL_PROMPT", "0")));
        Assert.Equal("0", e["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public void InjectsSshKeyAndCommandTogether()
    {
        var e = Build(Env(("PATH", "/usr/bin")), sshKey: @"C:\Users\me\.ssh\id_ed25519");
        Assert.Equal(@"C:\Users\me\.ssh\id_ed25519", e["HISTORIA_SSH_KEY"]);
        Assert.Equal("ssh -i \"$HISTORIA_SSH_KEY\" -o IdentitiesOnly=yes", e["GIT_SSH_COMMAND"]);
    }

    [Fact]
    public void NoSshKey_InjectsNeither()
    {
        var e = Build(Env(("PATH", "/usr/bin")));
        Assert.False(e.ContainsKey("HISTORIA_SSH_KEY"));
        Assert.False(e.ContainsKey("GIT_SSH_COMMAND"));
    }

    [Fact]
    public void SetsTermVariables()
    {
        var e = Build(Env());
        Assert.Equal("xterm-256color", e["TERM"]);
        Assert.Equal("truecolor", e["COLORTERM"]);
        Assert.Equal("Historia", e["TERM_PROGRAM"]);
        Assert.Equal("5.1.0", e["TERM_PROGRAM_VERSION"]);
    }

    [Fact]
    public void TerminalHostEnvironment_prepends_cmd_to_path_only_when_bundled()
    {
        var bundledRoot = @"C:\app\git";
        var bundledInfo = new GitExecutableInfo
        {
            Found = true,
            Source = GitSource.Bundled,
            BundledRoot = bundledRoot,
            Path = @"C:\app\git\ucrt64\bin\git.exe",
            Version = new Version(2, 56, 0),
        };

        var envBundled = TerminalHostEnvironment.SnapshotEnvironment(bundledInfo);
        Assert.True(envBundled.ContainsKey("PATH"));
        Assert.StartsWith(Path.Combine(bundledRoot, "cmd") + Path.PathSeparator, envBundled["PATH"]);

        var installedInfo = new GitExecutableInfo
        {
            Found = true,
            Source = GitSource.Installed,
            Path = @"C:\Program Files\Git\cmd\git.exe",
            Version = new Version(2, 51, 0),
        };

        var envInstalled = TerminalHostEnvironment.SnapshotEnvironment(installedInfo);
        if (envInstalled.TryGetValue("PATH", out var path))
        {
            Assert.False(path.StartsWith(Path.Combine(bundledRoot, "cmd")));
        }
    }
}
