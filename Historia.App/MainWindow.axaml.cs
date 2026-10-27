namespace Historia;

/// <summary>
/// The Avalonia shell window: app band on top, the three body regions
/// (sidebar | commit graph | detail) with two user-driven dividers, status bar on the bottom.
/// The service provider is built by <see cref="App"/> and passed in.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IServiceProvider _services = default!;
    private readonly IThemeService? _theme;
    private readonly IKeyboardCommandRouter? _keyboard;
    private readonly IScrollDirectionProbe? _scrollProbe;
    private readonly AtomicFileWriter? _placementWriter;
    private readonly AppBandView? _appBand;
    private readonly ShortcutsBarView? _leftShortcuts;
    private readonly ShortcutsBarView? _rightShortcuts;
    private readonly CommitGraphControl? _graph;
    private readonly TerminalDockView? _terminalDock;
    private readonly SidebarView? _sidebar;
    private readonly CenterPaneController? _centerPaneController;

    // Parameterless ctor for the XAML previewer only.
    public MainWindow() : this(null!) { }

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();

        _services = services;
        ViewModel = services is null
            ? new MainWindowViewModel()
            : ActivatorUtilities.CreateInstance<MainWindowViewModel>(services);
        DataContext = ViewModel;
        if (services is not null)
        {
            _theme = services.GetService<IThemeService>();
            _keyboard = services.GetService<IKeyboardCommandRouter>();
            _scrollProbe = services.GetService<IScrollDirectionProbe>();
            if (_scrollProbe is not null)
                Activated += (_, _) => _scrollProbe.Refresh();
            _placementWriter = new AtomicFileWriter(AppPaths.WindowPlacementFile);
            if (_theme is not null)
                _theme.ThemeChanged += OnThemeChanged;

            _appBand = new AppBandView();
            AppBandHost.Content = _appBand;
            _appBand.Initialize(services);

            _leftShortcuts = new ShortcutsBarView(services, ShortcutsBarSide.Left);
            LeftShortcutsHost.Content = _leftShortcuts;
            _leftShortcuts.NaturalWidthChanged += (_, _) => OnShortcutsNaturalWidthChanged();

            _rightShortcuts = new ShortcutsBarView(services, ShortcutsBarSide.Right);
            RightShortcutsHost.Content = _rightShortcuts;
            _rightShortcuts.NaturalWidthChanged += (_, _) => OnShortcutsNaturalWidthChanged();

            // Eagerly resolve so its constructor subscribes to IRepoActionBus.NetworkActionRequested
            // right away — Fetch/Pull/Push from the shortcuts bar must keep working regardless of
            // which route the detail pane shows (Home, Settings, …), not just while RepositoryView
            // happens to be the mounted view.
            _ = services.GetRequiredService<IRepoNetworkActionRunner>();

            _graph = new CommitGraphControl();
            _graph.Initialize(services);
            _centerPaneController = new CenterPaneController(CenterHost, _graph, services);

            _sidebar = new SidebarView(services);
            SidebarHost.Content = _sidebar;
            DetailHost.Content = new DetailRouter(services);

            _terminalDock = new TerminalDockView();
            TerminalDockHost.Content = _terminalDock;
            _terminalDock.Initialize(services);
        }

        RestorePlacement();
        InitBodyLayout();
        OnShortcutsNaturalWidthChanged();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        PositionChanged += (_, _) => QueuePlacementSave();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_theme is not null)
            WindowChromeFor(_theme.Effective);
        ApplyBodyLayout();
    }

    internal MainWindowViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.Dispose();
        if (_theme is not null)
            _theme.ThemeChanged -= OnThemeChanged;
        _appBand?.Dispose();
        _leftShortcuts?.Dispose();
        _rightShortcuts?.Dispose();
        _sidebar?.Dispose();
        _terminalDock?.Dispose();
        _centerPaneController?.Dispose();
        _ = _placementWriter?.DisposeAsync();
        base.OnClosed(e);
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
        => Dispatcher.UIThread.Post(() => WindowChromeFor(e.NewTheme));

    private void WindowChromeFor(EffectiveTheme theme)
        => Historia.Platform.WindowChrome.Apply(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, theme);

    // ── Body panel layout ──
    //
    // Each divider resizes only the two panels it sits between: the left divider trades width
    // between Left and Centre, the right divider between Centre and Right. The panel on the far
    // side never moves during a drag, so the adjacent panel responds 1:1 with the pointer and
    // the drag simply stops when the shrinking panel reaches its minimum (CentreMin for the
    // centre). Pane minimums are derived dynamically from the shortcut bars' natural width via
    // BodyColumnLayout.EffectiveMin, and ResolveColumns delegates to BodyColumnLayout.Resolve.

    private const double DividerPx = 4;
    private const double BaseLeftMin = 250;
    // floor the centre panel at the default columns' combined minimum plus padding
    // (Refs 140 + Graph 150 + Message 150 + 40), so a sidebar-divider drag can't squeeze
    // Message past the right edge. Both dividers clamp against it.
    private const double CentreMin = 480;
    // The detail pane's declared floor, set marginally above the width the right shortcut bar's
    // four labelled buttons render at, keeping the pane minimum a deliberate design constraint
    // rather than a consequence of label metrics.
    private const double BaseRightMin = 360;

    private double _leftMin = BaseLeftMin;
    private double _rightMin = BaseRightMin;

    public double LeftMin => _leftMin;
    public double RightMin => _rightMin;

    // Side panes open at their minimum width and the centre gets the rest: an intent of 0 is
    // floored to the effective minimum by BodyColumnLayout.Resolve, so it follows the shortcut
    // bars' measured width until the user drags a divider.
    private double _leftW;
    private double _rightW;

    // Avalonia's XAML name generator does not emit fields for ColumnDefinition; index instead.
    // Only the side columns are pinned; the centre column stays a star so the three columns always
    // span the grid, whatever the side widths round to.
    private ColumnDefinition LeftCol => BodyGrid.ColumnDefinitions[0];
    private ColumnDefinition RightCol => BodyGrid.ColumnDefinitions[4];

    private bool _dividerDragging;
    private double _dragPointerStartX;
    private double _dragStartLeft, _dragStartRight;

    private void OnShortcutsNaturalWidthChanged()
    {
        var leftBarWidth = _leftShortcuts?.NaturalWidth ?? 0;
        var rightBarWidth = _rightShortcuts?.NaturalWidth ?? 0;
        _leftMin = BodyColumnLayout.EffectiveMin(BaseLeftMin, leftBarWidth);
        _rightMin = BodyColumnLayout.EffectiveMin(BaseRightMin, rightBarWidth);

        _leftW = Math.Max(_leftW, _leftMin);
        _rightW = Math.Max(_rightW, _rightMin);

        var newMinWidth = _leftMin + _rightMin + 2 * DividerPx;
        if (Math.Abs(MinWidth - newMinWidth) > 0.5)
            MinWidth = newMinWidth;
        ApplyBodyLayout();
    }

    private void InitBodyLayout()
    {
        BodyGrid.PropertyChanged += (_, ev) =>
        {
            if (ev.Property == BoundsProperty)
                ApplyBodyLayout();
        };
    }

    private double BodyContentWidth => Math.Max(0, BodyGrid.Bounds.Width - 2 * DividerPx);

    private (double Left, double Centre, double Right) ResolveColumns(double total)
        => BodyColumnLayout.Resolve(total, _leftW, _rightW, _leftMin, _rightMin);

    internal void SetIntents(double left, double right)
    {
        _leftW = left;
        _rightW = right;
        ApplyBodyLayout();
    }

    internal void ApplyBodyLayout()
    {
        try
        {
            var total = BodyContentWidth;
            if (total <= 0)
                return;

            var (left, _, right) = ResolveColumns(total);
            var scale = RenderScaling > 0 ? RenderScaling : 1;
            (left, right) = BodyColumnLayout.RoundColumns(total, left, right, scale);

            PinColumn(LeftCol, left);
            PinColumn(RightCol, right);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Error, "layout", "ApplyBodyLayout failed", ex);
        }
    }

    private static void PinColumn(ColumnDefinition column, double width)
    {
        var safe = double.IsFinite(width) && width > 0 ? width : 0;
        if (!column.Width.IsAbsolute || Math.Abs(column.Width.Value - safe) > 0.001)
            column.Width = new GridLength(safe);
    }

    // Divider drags are driven off the raw pointer position in BodyGrid's (stationary)
    // coordinate space — not accumulated DragDelta vectors, which drift once the divider
    // clamps and leave a "stuck, then jump to catch up" dead zone on the way back.
    private void OnDividerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(BodyGrid).Properties.IsLeftButtonPressed)
            return;
        if (!RightCol.Width.IsAbsolute)
            ApplyBodyLayout(); // first drag before any Bounds change — pin the side columns
        if (!RightCol.Width.IsAbsolute)
            return;
        _dividerDragging = true;
        _dragPointerStartX = e.GetPosition(BodyGrid).X;
        _dragStartLeft = LeftCol.Width.Value;
        _dragStartRight = RightCol.Width.Value;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    // Left divider: trade width between Left and Centre only. Right panel is fixed for the drag.
    private void OnLeftDividerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dividerDragging)
            return;
        var dx = e.GetPosition(BodyGrid).X - _dragPointerStartX;
        var maxLeft = Math.Max(_leftMin, BodyContentWidth - _dragStartRight - CentreMin);
        _leftW = Math.Clamp(_dragStartLeft + dx, _leftMin, maxLeft);
        _rightW = _dragStartRight;
        ApplyBodyLayout();
        e.Handled = true;
    }

    // Right divider: trade width between Centre and Right only. Left panel is fixed for the drag.
    private void OnRightDividerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dividerDragging)
            return;
        var dx = e.GetPosition(BodyGrid).X - _dragPointerStartX;
        var maxRight = Math.Max(_rightMin, BodyContentWidth - _dragStartLeft - CentreMin);
        _rightW = Math.Clamp(_dragStartRight - dx, _rightMin, maxRight);
        _leftW = _dragStartLeft;
        ApplyBodyLayout();
        e.Handled = true;
    }

    private void OnDividerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dividerDragging = false;
        e.Pointer.Capture(null);
    }

    private void OnDividerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => _dividerDragging = false;

    // ── Keyboard accelerators ──

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_keyboard is null)
            return;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        // A focused terminal claims the chords it turns into shell input (e.g. Ctrl+B for
        // readline backward-char / tmux prefix); chords the terminal has no use for (Ctrl+,, and
        // Ctrl+Enter unless the child turned the Kitty keyboard protocol on) and Ctrl+` (dock
        // toggle) stay with the application.
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is TerminalControl terminal && terminal.ClaimsChordNow(e.Key, e.KeyModifiers))
            return;

        var parts = new List<string>(4);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");

        var key = e.Key == Key.Return ? "Enter" : e.Key.ToString();
        parts.Add(key.Length == 1 ? key.ToUpperInvariant() : key);

        var chord = string.Join('+', parts);

        // Decide synchronously and mark the key handled *before* dispatching — a handler that
        // yields (e.g. CommitAsync) would otherwise leave e.Handled unset until long after this
        // routed-event walk has finished, letting the keystroke fall through into a focused
        // TextBox (async chord swallow bug).
        if (!_keyboard.IsRegistered(chord))
            return;

        e.Handled = true;
        _ = DispatchAndLogAsync(chord);
    }

    private async Task DispatchAndLogAsync(string chord)
    {
        try
        {
            await _keyboard!.DispatchAsync(chord).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "keyboard", $"chord handler for '{chord}' threw", ex);
        }
    }

    // ── Window geometry ──

    private void QueuePlacementSave()
    {
        if (_placementWriter is null || !IsLoaded)
            return;

        var maximized = WindowState == WindowState.Maximized;
        var placement = new WindowPlacement
        {
            Left = Position.X,
            Top = Position.Y,
            Width = maximized ? Width : Bounds.Width,
            Height = maximized ? Height : Bounds.Height,
            Maximized = maximized,
        };
        if (placement.Width <= 0 || placement.Height <= 0)
            return;

        try { _placementWriter.QueueWrite(JsonSerializer.Serialize(placement, HistoriaJson.Options)); }
        catch { /* best effort */ }
    }

    private void RestorePlacement()
    {
        WindowPlacement? p = null;
        try
        {
            var file = AppPaths.WindowPlacementFile;
            if (File.Exists(file))
                p = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(file), HistoriaJson.Options);
        }
        catch { p = null; }

        if (p is null)
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint((int)p.Left, (int)p.Top);
        Width = Math.Clamp(p.Width, 900, 10000);
        Height = Math.Clamp(p.Height, 600, 10000);
        if (p.Maximized)
            WindowState = WindowState.Maximized;

        // Re-centre if the saved spot is fully off every connected screen.
        Opened += (_, _) =>
        {
            var onScreen = Screens?.All.Any(s => s.Bounds.Intersects(
                new PixelRect(Position, PixelSize.FromSize(new Size(Bounds.Width, 40), 1.0)))) ?? true;
            if (!onScreen)
            {
                var screen = Screens?.Primary ?? Screens?.All.FirstOrDefault();
                if (screen is not null)
                {
                    var workArea = screen.WorkingArea;
                    var x = workArea.X + Math.Max(0, (workArea.Width - (int)Bounds.Width) / 2);
                    var y = workArea.Y + Math.Max(0, (workArea.Height - (int)Bounds.Height) / 2);
                    Position = new PixelPoint(x, y);
                }
                else
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
            }
        };
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        Historia.Diagnostics.PerfLog.Mark("MainWindow.OnClosing start");
        base.OnClosing(e);

        // Cancel is set synchronously: an async void handler returns to the window at its first
        // await, so a cancel set after it would arrive too late.
        var decision = ViewModel.EvaluateClose();
        switch (decision)
        {
            case CloseDecision.CancelOnly:
                e.Cancel = true;
                return;

            case CloseDecision.CancelAndPrompt:
                e.Cancel = true;
                if (await ViewModel.ResolveUnsavedWorkAsync())
                {
                    Close();
                }
                return;

            case CloseDecision.Allow:
            default:
                break;
        }

        using (Historia.Diagnostics.PerfLog.Scope("MainWindow.OnClosing terminalDock.Dispose"))
            _terminalDock?.Dispose();
    }
}
