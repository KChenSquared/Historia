namespace Historia.Platform;

/// <summary>
/// Window for opening a pull request: editable Source and Target lists, a Push button for an
/// unpushed local source, and the compare link with an Open button. Close is the rightmost button.
/// </summary>
internal sealed partial class CreatePullRequestDialog : Window
{
    private readonly TaskCompletionSource _closed = new();

    internal CreatePullRequestDialog(CreatePullRequestRequest request, IExternalLauncher launcher)
    {
        InitializeComponent();
        ViewModel = new CreatePullRequestDialogViewModel(request, launcher);
        DataContext = ViewModel;
        ViewModel.CloseRequested += (_, _) => Close();
        SourceBox.AddHandler(TextInputEvent, OnBoxTextInput, RoutingStrategies.Bubble, handledEventsToo: true);
        TargetBox.AddHandler(TextInputEvent, OnBoxTextInput, RoutingStrategies.Bubble, handledEventsToo: true);
        Opened += (_, _) => SourceBox.Focus();
        Closed += (_, _) =>
        {
            ViewModel.Dispose();
            _closed.TrySetResult();
        };
    }

    internal CreatePullRequestDialogViewModel ViewModel { get; }

    // Typing shows the ranked list under the box without moving focus out of it.
    private static void OnBoxTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is ComboBox { IsDropDownOpen: false } box)
            box.IsDropDownOpen = true;
    }

    /// <summary>Completes once the window closes.</summary>
    internal Task Completion => _closed.Task;

    /// <summary>Shows the dialog on the UI thread; completes when it closes.</summary>
    public static async Task ShowAsync(CreatePullRequestRequest request, IExternalLauncher launcher)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(request, launcher));
            return;
        }

        var dialog = new CreatePullRequestDialog(request, launcher);
        await SimpleDialog.ShowOwnedOrFree(dialog, AvaloniaDialogService.Owner);
        await dialog.Completion;
    }
}
