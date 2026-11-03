namespace HistoriaApp.Tests;

/// <summary>
/// The Create pull request window is markup bound to its view model: it opens on the source box
/// with the defaults chosen, Open is primary and needs a link, and Close is the rightmost button.
/// </summary>
[Trait("tier", "ui")]
public sealed class CreatePullRequestDialogTests
{
    private sealed class FakeLauncher : IExternalLauncher
    {
        public List<string> Opened { get; } = [];

        public void OpenUrl(string url) => Opened.Add(url);

        public void RevealInFileManager(string path)
        {
        }

        public void OpenFolder(string path)
        {
        }
    }

    private static BranchInfo Local(string name, bool current = false, string? upstream = null) => new()
    {
        Name = name,
        CanonicalName = $"refs/heads/{name}",
        IsRemote = false,
        IsCurrent = current,
        UpstreamName = upstream,
    };

    private static BranchInfo Remote(string remote, string name) => new()
    {
        Name = $"{remote}/{name}",
        CanonicalName = $"refs/remotes/{remote}/{name}",
        IsRemote = true,
        IsCurrent = false,
        RemoteName = remote,
    };

    private static CreatePullRequestRequest Request(string url = "https://github.com/acme/widgets.git")
    {
        IReadOnlyList<BranchInfo> branches =
        [
            Local("main", current: true, upstream: "origin/main"),
            Local("feature", upstream: "origin/feature"),
            Remote("origin", "main"),
            Remote("origin", "feature"),
            Remote("origin", "develop"),
        ];
        return new CreatePullRequestRequest(
            branches,
            [new RemoteInfo { Name = "origin", Url = url }],
            "local.feature",
            (_, _, _) => Task.FromResult(true),
            () => branches);
    }

    private static CreatePullRequestDialog Open(CreatePullRequestRequest request, FakeLauncher? launcher = null)
    {
        var dialog = new CreatePullRequestDialog(request, launcher ?? new FakeLauncher());
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        return dialog;
    }

    private static Button ButtonWithContent(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, content));

    [AvaloniaFact]
    public void Opens_with_source_focused_and_defaults_selected()
    {
        var dialog = Open(Request());

        var source = dialog.FindControl<ComboBox>("SourceBox")!;
        var target = dialog.FindControl<ComboBox>("TargetBox")!;
        var focused = TopLevel.GetTopLevel(dialog)!.FocusManager!.GetFocusedElement() as Visual;

        Assert.NotNull(focused);
        Assert.True(focused == source || source.IsVisualAncestorOf(focused));
        Assert.Equal("local.feature", source.SelectedItem);
        Assert.Equal("origin.main", target.SelectedItem);
        Assert.Equal(
            "https://github.com/acme/widgets/compare/main...feature?expand=1",
            dialog.FindControl<TextBox>("UrlBox")!.Text);
    }

    [AvaloniaFact]
    public void Open_button_is_primary_and_disabled_without_url()
    {
        var dialog = Open(Request(url: "https://gitlab.com/acme/widgets.git"));

        var open = ButtonWithContent(dialog, "Open");
        Assert.Contains("primary", open.Classes);
        Assert.False(open.IsEffectivelyEnabled);
        Assert.Equal("", dialog.FindControl<TextBox>("UrlBox")!.Text);

        var note = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("note"));
        Assert.True(note.IsVisible);
        Assert.Equal("Pull request links are built for GitHub remotes only.", note.Text);
    }

    [AvaloniaFact]
    public void Open_button_launches_the_link()
    {
        var launcher = new FakeLauncher();
        var dialog = Open(Request(), launcher);

        var open = ButtonWithContent(dialog, "Open");
        Assert.True(open.IsEffectivelyEnabled);
        open.Command!.Execute(open.CommandParameter);

        Assert.Equal(["https://github.com/acme/widgets/compare/main...feature?expand=1"], launcher.Opened);
    }

    [AvaloniaFact]
    public void Typing_in_a_box_keeps_the_text_and_filters_the_list()
    {
        var dialog = Open(Request());
        var target = dialog.FindControl<ComboBox>("TargetBox")!;

        var editor = target.GetVisualDescendants().OfType<TextBox>().Single();
        editor.Text = "dev";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("dev", editor.Text);
        Assert.Equal("dev", dialog.ViewModel.TargetText);
        Assert.Equal(["origin.develop"], dialog.ViewModel.FilteredTargetItems);
        Assert.Equal("dev", target.Text);

        editor.Text = "origin.develop";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("origin.develop", dialog.ViewModel.SelectedTarget);
        Assert.Equal("https://github.com/acme/widgets/compare/develop...feature?expand=1", dialog.ViewModel.Url);
    }

    [AvaloniaFact]
    public void Typing_opens_the_list_and_keeps_focus_and_caret_in_the_box()
    {
        var dialog = Open(Request());
        var target = dialog.FindControl<ComboBox>("TargetBox")!;
        var editor = target.GetVisualDescendants().OfType<TextBox>().Single();
        editor.Focus();
        editor.SelectAll();

        dialog.KeyTextInput("d");
        dialog.KeyTextInput("e");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("de", editor.Text);
        Assert.Equal(2, editor.CaretIndex);
        Assert.True(target.IsDropDownOpen);
        Assert.Same(editor, TopLevel.GetTopLevel(dialog)!.FocusManager!.GetFocusedElement());
        Assert.Equal(["origin.develop"], dialog.ViewModel.FilteredTargetItems);
    }

    [AvaloniaFact]
    public async Task Close_is_rightmost()
    {
        var dialog = Open(Request());

        var close = ButtonWithContent(dialog, "Close");
        Assert.True(close.IsCancel);

        var buttons = dialog.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Content is string)
            .OrderBy(b => b.TranslatePoint(new Point(0, 0), dialog)!.Value.X + b.Bounds.Width)
            .ToList();
        Assert.Same(close, buttons[^1]);

        close.Command!.Execute(close.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        await dialog.Completion;
        Assert.True(dialog.ViewModel.IsDisposed);
    }
}
