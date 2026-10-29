namespace HistoriaApp.Tests;

/// <summary>
/// Headless UI tests verifying the Git section in <see cref="SettingsView"/> renders
/// the active Git executable source, version, path, and notice.
/// </summary>
[Trait("tier", "ui")]
public sealed class SettingsGitSourceTests
{
    private sealed class FixedLocator(GitExecutableInfo info) : IGitExecutableLocator
    {
        public GitExecutableInfo Current => info;

        public Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default)
            => Task.FromResult(info);

        public Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, bool allowBundled, CancellationToken ct = default)
            => Task.FromResult(info);

        public Task<GitExecutableInfo> WhenResolvedAsync(CancellationToken ct = default)
            => Task.FromResult(info);
    }

    [AvaloniaFact]
    public void Git_in_use_shows_bundled_source_and_path()
    {
        var info = new GitExecutableInfo
        {
            Found = true,
            Path = @"C:\App\git\cmd\git.exe",
            Version = new Version(2, 56, 0),
            Source = GitSource.Bundled,
            BundledRoot = @"C:\App\git",
        };
        using var rig = new SettingsViewRig(new AppSettings(), locator: new FixedLocator(info));
        var inUse = rig.View.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == rig.View.ViewModel.GitInUseText);
        Assert.Contains("Git 2.56.0 — bundled with Historia", inUse.Text);
        Assert.Contains(@"C:\App\git\cmd\git.exe", inUse.Text);
    }

    [AvaloniaFact]
    public void Git_in_use_shows_installed_source_and_path()
    {
        var info = new GitExecutableInfo
        {
            Found = true,
            Path = @"C:\Program Files\Git\cmd\git.exe",
            Version = new Version(2, 47, 1),
            Source = GitSource.Installed,
        };
        using var rig = new SettingsViewRig(new AppSettings(), locator: new FixedLocator(info));
        var inUse = rig.View.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == rig.View.ViewModel.GitInUseText);
        Assert.Contains("Git 2.47.1 — installed", inUse.Text);
        Assert.Contains(@"C:\Program Files\Git\cmd\git.exe", inUse.Text);
    }

    [AvaloniaFact]
    public void Git_in_use_shows_user_setting_source_and_path()
    {
        var info = new GitExecutableInfo
        {
            Found = true,
            Path = @"D:\custom\git.exe",
            Version = new Version(2, 48, 0),
            Source = GitSource.UserSetting,
        };
        using var rig = new SettingsViewRig(new AppSettings(), locator: new FixedLocator(info));
        var inUse = rig.View.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == rig.View.ViewModel.GitInUseText);
        Assert.Contains("Git 2.48.0 — set in settings", inUse.Text);
        Assert.Contains(@"D:\custom\git.exe", inUse.Text);
    }

    [AvaloniaFact]
    public void Git_in_use_shows_not_found_when_git_missing()
    {
        var info = GitExecutableInfo.NotFound;
        using var rig = new SettingsViewRig(new AppSettings(), locator: new FixedLocator(info));
        var inUse = rig.View.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == rig.View.ViewModel.GitInUseText);
        Assert.Equal("Git not found", inUse.Text);
    }

    [AvaloniaFact]
    public void Git_notice_shows_when_notice_is_present()
    {
        var info = new GitExecutableInfo
        {
            Found = true,
            Path = @"C:\App\git\cmd\git.exe",
            Version = new Version(2, 56, 0),
            Source = GitSource.Bundled,
            BundledRoot = @"C:\App\git",
            Notice = "Installed Git 2.40.0 is older than 2.46; using the Git bundled with Historia (2.56.0).",
        };
        using var rig = new SettingsViewRig(new AppSettings(), locator: new FixedLocator(info));
        var noticeBlock = rig.View.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == rig.View.ViewModel.GitNoticeText);
        Assert.True(noticeBlock.IsVisible);
        Assert.Equal(info.Notice, noticeBlock.Text);
    }
}

