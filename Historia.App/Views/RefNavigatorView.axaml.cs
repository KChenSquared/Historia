namespace Historia.Views;

/// <summary>
/// The sidebar ref navigator. Local / remote / tag trees plus stash / submodule / worktree sections,
/// bound to a <see cref="RefNavigatorViewModel"/>. Single click selects and reveals a tip commit;
/// double click checks out. The code here is view mechanics: the branch and remote context menus
/// (built from the row's data when the row gets its view model), focus for the filter box, the gap
/// next to the sidebar's own scrollbar, and turning pointer presses on rows into commands.
/// </summary>
public sealed partial class RefNavigatorView : UserControl, IDisposable
{
    /// <summary>Tallest a section's row list grows before it scrolls on its own.</summary>
    public const double SectionMaxHeight = 240;

    /// <summary>Breathing gap at the right of rows so text and actions do not touch the section scrollbar lane.</summary>
    internal const double RowRightGutter = 4;

    /// <summary>The filled cloud drawn on a remote's root row.</summary>
    public static readonly Geometry CloudGeometry = RemoteCloudGlyph.CreateFilledGeometry(RefNavigatorViewModel.RemoteCloudSize);

    private static readonly Rect CloudDigitBand = RemoteCloudGlyph.DigitBand(RefNavigatorViewModel.RemoteCloudSize);

    public static readonly double CloudDigitBandWidth = CloudDigitBand.Width;
    public static readonly double CloudDigitBandHeight = CloudDigitBand.Height;
    public static readonly Thickness CloudDigitBandMargin = new(CloudDigitBand.X, CloudDigitBand.Y, 0, 0);
    public static readonly double CloudDigitSize = RemoteCloudGlyph.DigitFontSize(1, RefNavigatorViewModel.RemoteCloudSize);
    public static readonly double CloudDigitSizeWide = RemoteCloudGlyph.DigitFontSize(10, RefNavigatorViewModel.RemoteCloudSize);

    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IStashService _stashes;
    private readonly IRepoActionBus _actions;
    private readonly IExternalLauncher _launcher;
    private readonly INavigationBus _nav;
    private readonly PullRequestFlow? _pullRequests;

    private readonly Dictionary<TextBox, (RefSectionViewModel Section, EventHandler Handler)> _filterFocus = [];

    public RefNavigatorView(IServiceProvider sp)
    {
        _dialogs = sp.GetRequiredService<IDialogService>();
        _notifications = sp.GetRequiredService<INotificationService>();
        _stashes = sp.GetRequiredService<IStashService>();
        _actions = sp.GetRequiredService<IRepoActionBus>();
        _launcher = sp.GetRequiredService<IExternalLauncher>();
        _nav = sp.GetRequiredService<INavigationBus>();
        _pullRequests = PullRequestFlow.From(sp);

        InitializeComponent();
        OuterScroll.ScrollChanged += (_, _) => UpdateNestedMargin();
        OuterScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty)
                UpdateNestedMargin();
        };

        ViewModel = ActivatorUtilities.CreateInstance<RefNavigatorViewModel>(sp, ViewServices.Dispatcher(sp));
        DataContext = ViewModel;

        // A click on empty sidebar space returns the detail panel to the repo. Rows that do
        // something set e.Handled, so this only fires for background clicks.
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
                return;
            if (!e.Handled && ViewModel.Repository is not null)
                _nav.RequestNavigate("repo");
        });
    }

    internal RefNavigatorViewModel ViewModel { get; }

    /// <summary>The panel holding every section; it keeps a gap clear of the sidebar's own scrollbar.</summary>
    internal StackPanel Root => RootPanel;

    /// <summary>The sidebar's own scroller, around every section.</summary>
    internal ScrollViewer OuterScroller => OuterScroll;

    public void Dispose()
    {
        foreach (var (section, handler) in _filterFocus.Values)
            section.FocusFilterRequested -= handler;
        _filterFocus.Clear();
        ViewModel.Dispose();
    }

    private void UpdateNestedMargin()
    {
        var needsGap = OuterScroll.Viewport.Height > 0 && OuterScroll.Extent.Height > OuterScroll.Viewport.Height;
        var target = needsGap ? new Thickness(0, 0, ScrollBarMetrics.NestedGap, 0) : default;
        if (RootPanel.Margin != target)
            RootPanel.Margin = target;
    }

    // ── Sections ──

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || sender is not Control { DataContext: RefSectionViewModel section })
            return;
        e.Handled = true;
        section.ToggleCommand.Execute(null);
    }

    // A filter box takes focus when the user opens its filter, not whenever the box becomes visible:
    // re-expanding a section with an open filter must leave the caret where the user was typing.
    // The focus is posted so it lands after the open's bindings and layout have settled.
    private void OnFilterDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBox box)
            return;
        if (_filterFocus.Remove(box, out var previous))
            previous.Section.FocusFilterRequested -= previous.Handler;
        if (box.DataContext is not RefSectionViewModel section)
            return;
        void OnFocusRequested(object? s, EventArgs args) => Dispatcher.UIThread.Post(() => box.Focus());
        section.FocusFilterRequested += OnFocusRequested;
        _filterFocus[box] = (section, OnFocusRequested);
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not Control { DataContext: RefSectionViewModel section })
            return;
        e.Handled = true;
        section.CloseFilterCommand.Execute(null);
    }

    // ── Rows ──

    private void OnLeafPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: RefLeafRowViewModel { CanPress: true } row })
            return;
        e.Handled = true;
        row.Host.SelectCommand.Execute(row);
    }

    private void OnLeafDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: RefLeafRowViewModel row } || row is not (RefBranchRowViewModel or RefSubmoduleRowViewModel))
            return;
        e.Handled = true;
        row.Host.ActivateCommand.Execute(row);
    }

    // Trap: only a left press toggles. The context menu opens on the right button's release, and a
    // toggle replaces every row of the section, so toggling on a right press detaches this row (and
    // moves pointer capture away) before the menu can open.
    private void OnFolderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: RefFolderRowViewModel folder } control
            || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        folder.Host.ToggleFolderCommand.Execute(folder);
    }

    // ── Context menus ──

    // A branch row's menu is rebuilt whenever its container is given another row.
    private void OnLeafDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is not Control row)
            return;
        row.ContextMenu = row.DataContext is RefBranchRowViewModel branch ? BuildBranchMenu(branch) : null;
    }

    private void OnFolderDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is not Control row)
            return;
        row.ContextMenu = row.DataContext is RefFolderRowViewModel { Remote: { } remote } && ViewModel.Repository is { } ctx
            ? new ContextMenu
            {
                ItemsSource = RemoteMenu.Build(remote, ctx, _dialogs, _notifications, _actions, _launcher, CopyToClipboard),
            }
            : null;
    }

    private Task? CopyToClipboard(string text) => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);

    /// <summary>The shared branch context menu on a sidebar branch row.</summary>
    private ContextMenu? BuildBranchMenu(RefBranchRowViewModel row)
    {
        if (ViewModel.Repository is not { } ctx)
            return null;

        var b = row.Branch;
        var isRemote = row.IsRemote;
        var shortLocal = BranchActions.LocalName(b);

        // The counterpart to measure divergence against, and the remote alias that owns
        // it — resolved via BranchInfo.RemoteName (reliable even when the alias itself contains
        // a slash), never by splitting Name/UpstreamName on '/'.
        string? counterpartRef = null, remoteAlias = null;
        if (!isRemote)
        {
            (counterpartRef, remoteAlias) = BranchMenu.UpstreamCounterpart(b.UpstreamName, ctx.State.Branches);
        }
        else if (isRemote)
        {
            var localCounterpart = ctx.State.Branches.FirstOrDefault(x => !x.IsRemote && x.Name == shortLocal);
            if (localCounterpart is not null)
                counterpartRef = shortLocal;
            remoteAlias = b.RemoteName;
        }

        var target = new BranchMenu.Target(
            Name: shortLocal,
            CopyName: b.Name,
            TrackingRef: b.Name,
            IsLocal: !isRemote,
            TipSha: b.TipCommitId?.ToString(),
            CurrentBranch: ctx.State.CurrentBranch?.Name,
            CounterpartRef: counterpartRef,
            RemoteAlias: remoteAlias);

        var baseItems = BranchMenu.Build(target, ctx, _dialogs, _notifications, _stashes, _actions, s => CopyToClipboard(s), _pullRequests);

        var menu = new ContextMenu { ItemsSource = baseItems };
        // The divergence lookup shells out to git — resolve it lazily on Opening so the
        // popup never blocks on a git process, and show the base menu immediately.
        menu.Opening += async (_, _) =>
        {
            var opened = menu;
            IReadOnlyList<Control> divergenceItems;
            try { divergenceItems = await BranchMenu.BuildDivergenceItemsAsync(target, ctx.Gateway, _actions, ctx.Lifetime); }
            catch { return; }
            if (divergenceItems.Count == 0 || !ReferenceEquals(menu, opened) || !menu.IsOpen)
                return;
            var afterCheckout = baseItems.Take(1).ToList();
            afterCheckout.AddRange(divergenceItems);
            afterCheckout.AddRange(baseItems.Skip(1));
            menu.ItemsSource = afterCheckout;
        };
        return menu;
    }
}
