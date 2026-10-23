namespace Historia.Controls;

/// <summary>
/// Native commit graph + history list. Fully virtualized: only the visible row range is
/// drawn each frame. Selection flows through <see cref="IRepositoryStateService"/>.
/// </summary>
public sealed partial class CommitGraphControl : Control
{
    private const double MinRowHeight = 20;
    private const double MaxRowHeight = 44;
    private const double DefaultRowHeight = 26;
    private const double WashRowGap = 2; // px of background between adjacent rows' lane washes
    private const double MinLaneWidth = 15;
    private const double MaxLaneWidth = 60;
    private const double DefaultLaneWidth = 18;
    /// <summary>commit node radius.</summary>
    private const double NodeRadius = 5;
    private const double RowTextSize = 14;
    private const double HeaderTextSize = 12;
    private const double BadgeLabelSize = 14;
    internal const double HeaderHeight = 30;
    private const double QueryBarHeight = 32;
    private const double BoundaryGrab = 4;
    private const double TickWidth = 4;
    /// <summary>the lane-line stroke width, shared by <see cref="LanePen"/> and the
    /// HEAD badge→node connector.</summary>
    private const double LaneStrokeWidth = 2.0;

    private double _laneWidth = DefaultLaneWidth;
    private double LaneWidth => _laneWidth;

    private IServiceProvider _services = default!;
    private IWorkspaceService _workspace = default!;
    private IGraphLayoutService _layoutService = default!;
    private IBackgroundWorkGate? _backgroundWorkGate;
    private IThemeService _theme = default!;
    private IDialogService _dialogs = default!;
    private INotificationService _notifications = default!;
    private Historia.Services.PowerTools.ICherryPickService _cherryPick = default!;
    private Historia.Services.PowerTools.IStashService _stashes = default!;
    private INavigationBus _nav = default!;
    private Historia.Services.Settings.ISettingsService _settings = default!;
    private Historia.Services.UI.IRepoActionBus _actionBus = default!;
    private Historia.Services.UI.IScrollDirectionProbe _scrollProbe = default!;
    private Historia.Services.Settings.IRepoLayoutService _repoLayout = default!;

    // takes effect on the next launch, and GraphLayoutService snapshots it at construction.
    // Snapshot it here too so the control's resident-row ceiling always matches the service's —
    // a mid-session toggle must not make the control cap at the configurable disk-mode ceiling
    // (AppSettings.ResidentRowCap, floored at 500k) while metadata is still fully resident
    // (the 500k memory limit exists precisely to bound that allocation).
    private bool _graphMetaFromDisk;
    private bool _lightweightMode;
    private bool _showAuthorDate;
    private int _residentRowCapSetting;
    private int ResidentRowCap => GraphLimits.GetMaxResidentRows(_graphMetaFromDisk, _residentRowCapSetting, _lightweightMode);

    // the silent background crawler pre-loads only this many rows into the
    // resident layout after first paint, advancing in discrete threshold steps as the user
    // scrolls. Deeper history reaches disk in full via the background spill
    // regardless of this cap. Crawling all the way to ResidentRowCap (2,500,000 on a big
    // repo) built a multi-GB GraphRow[] and consumed CPU/memory for minutes right after first paint.
    // Configurable (AppSettings.SilentCrawlRowCap, default 500,000); 0 means uncapped, i.e. the
    // crawler runs to ResidentRowCap same as an explicit scroll would. Lightweight mode fixes it
    // at 10,000. Snapshotted at construction like _graphMetaFromDisk — a mid-session change takes
    // effect on next launch.
    private int _silentCrawlRowCap;
    private GraphCrawlThreshold _crawl = new(0, 0);
    private int _crawlTarget;
    private int _crawlRunning;

    // A crawl step that adds no rows waits before asking again, doubling from the first value to
    // the cap; any growth or a new layout resets it. The loop never exits on "no rows": most
    // causes clear up by themselves, and the loop is what a recompute's new layout is picked up by.
    private const int CrawlIdleBackoffFirstMs = 40;
    private const int CrawlIdleBackoffMaxMs = 500;

    private IReadOnlyList<GraphColumn> _columns = GraphColumnDefaults.All;
    private bool _graphWidthPinned;

    // ── Reveal-toward-an-unrendered-commit ──
    private GitObjectId? _revealTarget;         // commit the pump is walking toward; null = idle
    private GraphDecoration? _revealDeco;    // stand-in badge drawn while walking
    private bool _revealUserTookOver;        // user scrolled/zoomed since the click → stop driving
    private bool _revealPumpRunning;

    /// <summary>While true the graph body is blanked and only the reveal placeholder is drawn.</summary>
    private bool RevealBlanking => _revealTarget is not null && !_revealUserTookOver;
    private readonly HashSet<GraphColumnKind> _manuallySized = [];
    private double _refsBaseWidth = GraphColumnDefaults.RefsStaticFloor;
    private ResolvedColumn[] _resolvedColumns = [];
    private double _bandWidth;
    private double _graphColLeft;
    private double _graphColWidth;

    private int _resizeBoundary = -1;
    private bool _resizing;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private GraphColumnKind _resizeKind;
    private bool _resizeInverted;
    private bool _zoomOverridesPin;

    private GraphColumnKind? _reorderCandidate;
    private GraphColumnKind? _reorderKind;
    private double _reorderGhostX;
    private int _reorderCaret = -1;

    private readonly ScrollBar _scrollBar;
    private readonly ScrollBar _hScrollBar;
    private bool _scrollBarUserActive;
    // Last pointer position over the vertical bar, in the bar's own coordinates (drag log only).
    private Point? _scrollBarPointer;

    private RepositoryContext? _context;
    private IRepositoryStateService? _state;
    private bool _refsRecomputeInFlight;
    private bool _refsPendingRecompute;
    private bool _detachedSinceAttach;
    internal int RefsRecomputeCount => _refsRecomputeCount;
    private int _refsRecomputeCount;
    internal Task? ActiveRefsRecomputeTask { get; private set; }
    internal RepositoryContext? ContextForTesting
    {
        get => _context;
        set => _context = value;
    }
    internal Func<RepositoryContext, Task>? RecomputeOverrideForTesting
    {
        get => _recomputeOverrideForTesting;
        set => _recomputeOverrideForTesting = value;
    }
    internal GraphLayoutResult? LayoutForTesting => _layout;
    internal bool ExtendPumpActiveForTesting => _extendChannel is not null;
    internal int ResolveRowForTesting(GitObjectId id) => ResolveRow(id);
    internal bool ShowAuthorDateForTesting => _showAuthorDate;
    internal EffectiveTheme ThemeModeForTesting => _themeMode;
    internal TextBox SearchBoxForTesting => _searchBox;
    internal void StepMatchForTesting(int direction) => StepMatch(direction);
    internal int CurrentMatchRowForTesting => _matches.CurrentRow;
    private Func<RepositoryContext, Task>? _recomputeOverrideForTesting;
    private GraphLayoutResult? _layout;
    // The commit store behind _layout. A disk-backed store holds an open file handle that
    // GraphLayoutService will close as soon as a RefsChanged re-walk drops its own reference —
    // which can happen mid-Render and would kill the process (ObjectDisposedException off the
    // render thread). We hold an explicit lease for as long as _layout can be rendered from.
    private ICommitStore? _leasedStore;
    // Consecutive Render() failures caught by the transient-fault handler. Reset on any frame
    // that paints cleanly; capped so a *deterministic* fault can't turn "drop the frame and
    // repaint" into an unbounded Background-priority repaint loop.
    private int _renderFailStreak;
    private const int MaxRenderRetries = 8;
    private CancellationTokenSource? _layoutCts;
    private sealed record ExtendRequest(
        RepositoryContext Context,
        int TargetRow,
        int BatchSize,
        TaskCompletionSource? Completion,
        CancellationToken Token,
        bool Background = false); // silent crawler (background); scroll → foreground

    private Channel<ExtendRequest>? _extendChannel;
    private int _cappedNotified;

    // ── First-paint timing diagnostics ──
    private long _firstPaintStartTimestamp;
    private string? _firstPaintKind;
    private bool _firstPaintQueued;
    private bool _firstPaintRenderLogged;

    private double _rowHeight = DefaultRowHeight;
    private double _scrollOffset;
    private double _hScrollOffset;

    // in-graph lane pan: scrolls the lanes INSIDE the Graph column without moving any
    // other column, so any lane is reachable while Message stays on screen. In DIP, lane space.
    // Per repo for the session only (reset to lane 0 at launch) — saved on repo switch, restored
    // on Attach, never persisted.
    private double _graphPanOffset;
    private readonly Dictionary<string, double> _panByRepo = new(StringComparer.Ordinal);

    /// <summary>Widest the lanes can be panned: the natural graph width minus what the column
    /// shows. Zero when every lane already fits.</summary>
    private double MaxPan =>
        Math.Max(0, NaturalGraphWidth(_layout?.Layout.TotalLaneCount ?? 0) - _graphColWidth);

    private void ClampPan() => _graphPanOffset = Math.Clamp(_graphPanOffset, 0, MaxPan);

    private int _hoverRow = -1;
    private int _selectedRow = -1;
    private readonly HashSet<int> _multiSelect = [];
    private readonly List<(Rect Rect, int Row, GraphDecoration Deco)> _pillRects = [];
    private readonly List<(Rect Rect, string Tip)> _iconRects = [];
    private readonly GraphHoverDwell _hoverDwell = new();
    private readonly DispatcherTimer _hoverDwellTimer;
    private CancellationTokenSource? _hoverFetchCts;

    // LRU cache of full, raw commit messages, bounded with oldest-25% eviction.
    private readonly Dictionary<GitObjectId, string> _msgCache = new();
    private readonly LinkedList<GitObjectId> _msgLru = new();
    private const int MsgCacheCapacity = 512;

    private bool TryGetCachedMessage(GitObjectId id, out string message)
    {
        if (_msgCache.TryGetValue(id, out message!))
        {
            _msgLru.Remove(id);
            _msgLru.AddLast(id);
            return true;
        }
        return false;
    }

    private void CacheMessage(GitObjectId id, string message)
    {
        if (_msgCache.TryGetValue(id, out _))
        {
            _msgLru.Remove(id);
            _msgLru.AddLast(id);
            _msgCache[id] = message;
            return;
        }

        if (_msgCache.Count >= MsgCacheCapacity)
        {
            var toEvict = MsgCacheCapacity / 4;
            for (var i = 0; i < toEvict && _msgLru.First is { } node; i++)
            {
                _msgCache.Remove(node.Value);
                _msgLru.RemoveFirst();
            }
        }

        _msgCache[id] = message;
        _msgLru.AddLast(id);
    }

    private GitObjectId? _hoverMsgId;
    private GitObjectId? _msgFetchInFlight;
    private string? _msgTipText;

    private readonly Popup _refList = new()
    {
        Placement = PlacementMode.Pointer,
        IsLightDismissEnabled = false,
    };
    private GraphDecoration? _refListFor;
    private DispatcherTimer? _refListCloseTimer;

    private const double HeaderIconSize = 14;
    private const double HeaderIconInset = 6;
    private readonly Geometry _icoFilter;
    private readonly Geometry _icoSearch;
    private readonly Geometry _icoSettings;
    private readonly Geometry _icoReset;

    private readonly Popup _refSearch = new()
    {
        Placement = PlacementMode.BottomEdgeAlignedRight,
        IsLightDismissEnabled = true,
    };
    private readonly TextBox _refSearchBox = new();
    private readonly StackPanel _refSearchItems = new();
    private readonly List<(RefCandidate Candidate, Border Row)> _refSearchRows = [];
    private int _refSearchSelectedIndex = -1;
    private (GraphColumnKind Kind, int Slot)? _hoverHeaderIcon;

    /// <summary>The Graph column header's two right-aligned action icons: slot 0 (right-most)
    /// is the cog, slot 1 is the reset. Passed to <see cref="GraphColumnInteraction.HeaderIconAt"/>
    /// alongside the single-slot Refs/Message search icons.</summary>
    private static readonly (GraphColumnKind Kind, int Slot)[] HeaderIconCandidates =
    [
        (GraphColumnKind.Refs, 0),
        (GraphColumnKind.Message, 0),
        (GraphColumnKind.Graph, 0),
        (GraphColumnKind.Graph, 1),
    ];

    private readonly record struct RefCandidate(string Label, string Icon, GitObjectId? CommitId);

    internal Popup RefSearchPopup => _refSearch;
    internal TextBox RefSearchBox => _refSearchBox;
    internal StackPanel RefSearchItems => _refSearchItems;
    internal Border QueryBarControl => _queryBar;
    internal double ScrollOffset => _scrollOffset;
    internal double RowHeightValue => _rowHeight;
    internal IReadOnlyList<ResolvedColumn> ResolvedColumnsForTesting => _resolvedColumns;
    internal double HScrollOffsetForTesting => _hScrollOffset;
    internal double GraphPanOffsetForTesting => _graphPanOffset;
    internal double RowHeightForTesting => _rowHeight;
    internal double LaneWidthForTesting => _laneWidth;
    internal bool GraphWidthPinnedForTesting => _graphWidthPinned;
    internal int RowCountForTesting => _layout?.Layout.RowCount ?? 0;
    internal IReadOnlyList<GraphColumn> ColumnsForTesting => _columns;
    internal ContextMenu? OpenMenuForTesting => _openMenu;
    internal bool HoverDwellTimerRunningForTesting => _hoverDwellTimer.IsEnabled;
    internal HoverTarget? PendingHoverTargetForTesting => _hoverDwell.Pending;
    internal HoverTarget? ShownHoverTargetForTesting => _hoverDwell.Shown;
    internal bool MsgFetchInFlightForTesting => _msgFetchInFlight is not null;
    internal int MsgCacheCountForTesting => _msgCache.Count;
    internal string? CachedMessageForTesting(int row) =>
        _layout is { } lay && row >= 0 && row < lay.Layout.Commits.Count
            && _msgCache.TryGetValue(lay.Layout.Commits[row].Id, out var m) ? m : null;
    internal void FireHoverDwellTimerForTesting() => HandleHoverDwellElapsed();
    internal int LaneRunsDrawnForTesting => _laneRunsDrawn;
    internal int LanePiecesSeenForTesting => _lanePiecesSeen;
    internal int LaneRunGrowthsForTesting => _laneRuns.Growths;
    internal void ResetLaneRunCountersForTesting() => _laneRuns.ResetCounters();
    internal int PoolMissesForTesting => _poolMisses;
    internal int TextMissesForTesting => _textCache.Misses;
    internal int TextCacheCountForTesting => _textCache.Count;
    internal IReadOnlyList<(Rect Rect, string Tip)> IconRectsForTesting => _iconRects;
    internal IReadOnlyList<(Rect Rect, int Row, GraphDecoration Deco)> PillRectsForTesting => _pillRects;
    internal IReadOnlyList<(Rect Rect, int Row, List<GraphDecoration> Hidden)> OverflowChipsForTesting => _overflowChips;
    internal IReadOnlyDictionary<int, double> BadgeRightEdgesForTesting => _badgeRightEdge;
    internal RemoteNumbering RemoteNumberingForTesting => _remoteNumbering;
    internal void ResetMissCountersForTesting()
    {
        _poolMisses = 0;
        _textCache.ResetCounters();
    }
    internal ScrollBar VerticalScrollBarForTesting => _scrollBar;
    internal double ScrollOffsetForTesting => _scrollOffset;
    /// <summary>Test-only: replaces the layout's row count when sizing the vertical scroll
    /// range, so a test can make the bar scrollable (and grow it) without a repository.</summary>
    internal int? ContentRowCountOverrideForTesting
    {
        get => _contentRowCountOverrideForTesting;
        set
        {
            _contentRowCountOverrideForTesting = value;
            InvalidateArrange();
        }
    }
    private int? _contentRowCountOverrideForTesting;
    /// <summary>Test-only: receives the <c>graph-scroll</c> lines in place of <see cref="AppLog"/>.</summary>
    internal IAppLog? ScrollLogForTesting { get; set; }

    /// <summary>
    /// Set to <see langword="true"/> and rebuild to write <c>[graph-scroll]</c> Debug lines
    /// (one per vertical thumb move, scroll-range changes mid-drag, and drag end) to the developer
    /// log, which also must be enabled.
    /// </summary>
    internal static readonly bool TraceScrollDrags = false;

    private bool ScrollLogOn => ScrollLogForTesting is not null || (TraceScrollDrags && AppLog.Enabled);
    private void LogScroll(string message) => (ScrollLogForTesting ?? AppLog.Current).Debug("graph-scroll", message);

    /// <summary>Test-only: simulates a horizontal scroll/pan without driving the real
    /// <see cref="ScrollBar"/> or lane-pan gestures.</summary>
    internal void SetScrollOffsetsForTesting(double hScrollOffset, double graphPanOffset)
    {
        _hScrollOffset = hScrollOffset;
        _graphPanOffset = graphPanOffset;
        _hScrollBar.Value = hScrollOffset;
        InvalidateVisual();
    }

    // The graph builds a fresh ContextMenu per right-click (content depends on hit
    // position — column header / overflow chip / pill / row), so there's no single control-owned
    // ContextMenu for Avalonia to auto-close when a new one opens. Track the live one and close it
    // explicitly, or a second right-click (or an overflow/pill menu opened from within another)
    // leaves the previous popup stuck open behind the new one.
    private ContextMenu? _openMenu;

    private void OpenMenu(ContextMenu menu)
    {
        CancelHoverDwell();
        if (_openMenu is { } prev)
        {
            _openMenu = null;
            try { prev.Close(); } catch { /* ignore */ }
        }

        _openMenu = menu;
        EventHandler<RoutedEventArgs>? onClosed = null;
        onClosed = (_, _) =>
        {
            menu.Closed -= onClosed;
            if (ReferenceEquals(_openMenu, menu)) _openMenu = null;
        };
        menu.Closed += onClosed;
        menu.Open(this);
    }

    private Geometry _icoLocal = new PathGeometry();
    private Geometry _icoRemote = new PathGeometry();
    private Geometry _icoRemoteFilled = new PathGeometry();
    private Geometry _icoTag = new PathGeometry();

    // Numbers for the configured remotes; with two or more, a branch badge draws one numbered
    // cloud per remote instead of a single plain cloud. Rebuilt when the remote names change.
    private RemoteNumbering _remoteNumbering = RemoteNumbering.Empty;
    private string[] _remoteNumberingNames = [];
    // A badge's joined remote names for the single plain cloud's tooltip, built once per
    // decoration array rather than every frame.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<string[], string> _joinedRemoteTips = new();
    private static readonly string[] NumberStrings =
        Enumerable.Range(0, 256).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
    private readonly Typeface _digitTypeface = new(FontFamily.Default, weight: FontWeight.SemiBold);

    private Point _dragStart;
    private int _dragRow = -1;
    private bool _dragging;
    private Point _lastPointerPos;
    private IPointer? _capturedPointer;

    private EffectiveTheme _themeMode = EffectiveTheme.Dark;
    private readonly Dictionary<int, Pen> _lanePens = [];
    private readonly Dictionary<int, SolidColorBrush> _laneBrushes = [];
    private readonly Dictionary<int, Pen> _emphPens = [];
    private readonly Dictionary<int, Pen> _pillBorders = [];
    // faint 1 DIP pen per lane colour for the non-HEAD badge→node connector.
    private readonly Dictionary<int, Pen> _faintConnectorPens = [];
    // per-frame row -> right edge (screen x) of the primary drawn badge.
    private readonly Dictionary<int, double> _badgeRightEdge = [];
    private readonly Dictionary<int, SolidColorBrush> _laneFills = [];
    private readonly Dictionary<int, SolidColorBrush> _laneTicks = [];
    // Every string the render path draws, shaped once. Keyed by brush reference, so a palette
    // swap can never reuse a stale-coloured layout; cleared (and disposed) on a theme change.
    private readonly ShapedTextCache _textCache = new(TextCacheCapacity);
    // Builds by any Pooled(...) pool, the elbow geometry cache or the joined remote-tooltip
    // cache since the last reset: zero on a repeat frame.
    private int _poolMisses;
    // Reused every frame to merge vertical lane pieces into runs (counts its growths).
    private readonly LaneRunBuilder _laneRuns = new();
    private int _lanePiecesSeen;
    private int _laneRunsDrawn;

    private double CornerRadius => Math.Min(LaneWidth, _rowHeight) * 0.75;

    private SolidColorBrush _bg = new(Colors.Black);
    private SolidColorBrush _chrome = new(Colors.Black);
    private Pen _borderPen = new(new SolidColorBrush(Colors.Gray), 1);
    private SolidColorBrush _nodeFill = new(Colors.White);
    private SolidColorBrush _text = new(Colors.Gray);
    private SolidColorBrush _textMuted = new(Colors.DimGray);
    private SolidColorBrush _pillText = new(Colors.White);
    private SolidColorBrush _headerText = new(Colors.DimGray);
    private SolidColorBrush _selectionFill = new(Colors.SlateBlue);
    private SolidColorBrush _selectionBar = new(Colors.SlateBlue);
    private SolidColorBrush _hoverFill = new(Colors.DimGray);
    private Pen _hoverPen = new(new SolidColorBrush(Colors.White), 1);
    private SolidColorBrush _matchFill = new(Colors.Transparent);
    private Typeface _typeface = new(FontFamily.Default);

    private GraphQuery _query = new() { RefScope = GraphRefScope.AllRefs };
    // The query _layout was computed for. _query changes before the recompute lands (and a
    // dropped ApplyLayout keeps the old layout), so the two can briefly disagree.
    private GraphQuery? _layoutQuery;
    private readonly Border _queryBar;
    private readonly TextBox _searchBox;
    private readonly TextBox _pathBox;
    private readonly ComboBox _scopeBox;
    private readonly TextBlock _matchLabel;
    private readonly GraphMatchCursor _matches = new();

    private double RowsTop => HeaderHeight + (_queryBar.IsVisible ? QueryBarHeight : 0);

    // Avalonia's Track.ApplyThumbDrag divides the drag vector by the value change; a drag move
    // with no motion along the track (a sideways wiggle) divides by zero and leaves a NaN drag
    // record. If the Track re-arranges before the next move — rows streaming in change Maximum —
    // that NaN is folded into the Thumb's grab point, every later move computes NaN, Track.Value
    // goes NaN and the thumb sticks at the bottom until something rewrites Value. Such a move
    // changes nothing, so it is marked handled here; class handlers run before the Track's own
    // DragDelta handler on the Thumb. Registered once for every Track in the app.
    static CommitGraphControl()
    {
        Thumb.DragDeltaEvent.AddClassHandler<Thumb>((thumb, e) =>
        {
            if (thumb.Parent is Track track
                && (track.Orientation == Orientation.Vertical ? e.Vector.Y : e.Vector.X) == 0)
                e.Handled = true;
        });
    }

    public CommitGraphControl()
    {
        UseLayoutRounding = true;
        Focusable = true;
        ClipToBounds = true;

        _refListCloseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _refListCloseTimer.Tick += (_, _) =>
        {
            if (_refList.IsOpen && !RefListHovered && !RefListBadgeHovered())
                CloseRefList();
        };

        _hoverDwellTimer = new DispatcherTimer { Interval = GraphHoverDwell.Delay };
        _hoverDwellTimer.Tick += (_, _) => HandleHoverDwellElapsed();
        ToolTip.SetPlacement(this, PlacementMode.Custom);
        ToolTip.SetCustomPopupPlacementCallback(this, PlaceTip);

        _scrollBar = new ScrollBar { Orientation = Orientation.Vertical, Width = ScrollBarMetrics.Lane, IsVisible = false, [Grid.ColumnProperty] = 0 };
        // While the user is dragging the thumb, background row-loading keeps triggering
        // ArrangeOverride passes (more rows stream in). Those must not reassert Maximum/Value
        // from our own stale `_scrollOffset`, which fights the ScrollBar's internal Track drag
        // math and desyncs the thumb from the pointer. `_scrollBarUserActive` gates that — and
        // it is driven by the ScrollBar's OWN Scroll event type, not pointer capture: a
        // ThumbTrack means a drag is in progress, and EVERY drag ends with EndScroll regardless
        // of where the pointer is released.
        //
        // Avalonia's Track adds each DragDelta to Value, and a DragDelta is the pointer's offset
        // from the grab point measured against the thumb's last-arranged position. When the UI
        // thread is starved (rows loading) several moves arrive before one layout pass, each
        // carrying the whole offset since that arrange, so they are double-counted and the thumb
        // races to the bottom and stays offset from the pointer. Re-arranging the bar after every
        // drag move keeps one move per arrange. Wheel, keys and track clicks don't need it.
        _scrollBar.Scroll += (_, e) =>
        {
            _scrollBarUserActive = e.ScrollEventType == ScrollEventType.ThumbTrack;
            _scrollOffset = _scrollBar.Value;
            if (ScrollLogOn)
                LogScrollEvent(e.ScrollEventType);
            OnScrolled();
            if (_scrollBarUserActive)
                _scrollBar.UpdateLayout();
        };
        // Belt-and-braces: any of these unlatch too, and can never leave it stuck.
        _scrollBar.AddHandler(PointerReleasedEvent, (_, _) => _scrollBarUserActive = false, RoutingStrategies.Tunnel);
        _scrollBar.PointerCaptureLost += (_, _) => _scrollBarUserActive = false;
        _scrollBar.AddHandler(PointerMovedEvent, (_, e) => { if (ScrollLogOn) _scrollBarPointer = e.GetPosition(_scrollBar); }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _hScrollBar = new ScrollBar { Orientation = Orientation.Horizontal, Height = ScrollBarMetrics.Lane, IsVisible = false };
        _hScrollBar.Scroll += (_, _) => { _hScrollOffset = _hScrollBar.Value; InvalidateVisual(); };

        _searchBox = new TextBox { Width = 200, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_searchBox, "Find in message / author / SHA (Ctrl+F). Enter / Shift+Enter to step.");
        _searchBox.TextChanged += (_, _) => RecomputeMatches();
        _searchBox.AddHandler(KeyDownEvent, OnSearchBoxKeyDown, RoutingStrategies.Tunnel);
        _matchLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        Dyn(_matchLabel, TextBlock.ForegroundProperty, "ColorTextMuted");

        _pathBox = new TextBox { Width = 160, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_pathBox, "Path filter — limits the walk to commits touching this path");
        _pathBox.LostFocus += (_, _) => ApplyQueryFromBar();
        _pathBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyQueryFromBar(); };

        _scopeBox = new ComboBox { Width = 130, VerticalContentAlignment = VerticalAlignment.Center };
        _scopeBox.Items.Add(new ComboBoxItem { Content = "All refs", Tag = GraphRefScope.AllRefs });
        _scopeBox.Items.Add(new ComboBoxItem { Content = "Current branch", Tag = GraphRefScope.CurrentBranch });
        _scopeBox.SelectedIndex = 0;
        _scopeBox.SelectionChanged += (_, _) => ApplyQueryFromBar();

        var prev = new Button { Content = "◂", Width = 22 };
        ToolTip.SetTip(prev, "Previous match (Shift+Enter / Shift+F3)");
        prev.Click += (_, _) => StepMatch(-1);
        var next = new Button { Content = "▸", Width = 22 };
        ToolTip.SetTip(next, "Next match (Enter / F3)");
        next.Click += (_, _) => StepMatch(1);

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        TextElement.SetFontSize(bar, 13);
        foreach (var el in new Control[]
        {
            new TextBlock { Text = "Find", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,6,0) },
            _searchBox, _matchLabel, prev, next,
            new Border { Width = 12 },
            new TextBlock { Text = "Path", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,6,0) },
            _pathBox,
            new Border { Width = 12 }, _scopeBox,
        })
        {
            bar.Children.Add(el);
        }
        _queryBar = new Border
        {
            Child = bar,
            Padding = new Thickness(8, 3, 8, 3),
            BorderThickness = new Thickness(0, 0, 0, 1),
            IsVisible = false,
        };
        Dyn(_queryBar, Border.BackgroundProperty, "ColorChrome");
        Dyn(_queryBar, Border.BorderBrushProperty, "ColorBorder");

        VisualChildren.Add(_scrollBar);
        VisualChildren.Add(_hScrollBar);
        VisualChildren.Add(_queryBar);
        LogicalChildren.Add(_scrollBar);
        LogicalChildren.Add(_hScrollBar);
        LogicalChildren.Add(_queryBar);
        LogicalChildren.Add(_refList);

        _refSearchBox.Watermark = "Filter branches & tags…";
        _refSearchBox.FontSize = 12;
        _refSearchBox.Height = 24;
        _refSearchBox.Padding = new Thickness(6, 2);
        _refSearchBox.Margin = new Thickness(6, 6, 6, 4);
        _refSearchBox.TextChanged += (_, _) => UpdateRefSearchMatches();
        _refSearchBox.KeyDown += OnRefSearchBoxKeyDown;

        var refSearchContainer = new StackPanel { Width = 260 };
        refSearchContainer.Children.Add(_refSearchBox);
        refSearchContainer.Children.Add(_refSearchItems);

        var refSearchBorder = new Border
        {
            Child = refSearchContainer,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0, 0, 0, 4),
        };
        Dyn(refSearchBorder, Border.BackgroundProperty, "ColorChrome");
        Dyn(refSearchBorder, Border.BorderBrushProperty, "ColorBorder");

        _refSearch.Child = refSearchBorder;
        _refSearch.PlacementTarget = this;
        LogicalChildren.Add(_refSearch);

        _icoLocal = ParseBadgeIcon(IconPaths.Get("hard_drive"));
        _icoRemote = ParseBadgeIcon(IconPaths.Get("cloud"));
        _icoRemoteFilled = RemoteCloudGlyph.CreateFilledGeometry(NumberedCloudBox);
        _icoTag = ParseBadgeIcon(IconPaths.Get("sell"));
        _icoFilter = ParseHeaderIcon(IconPaths.Get("filter_alt"));
        _icoSearch = ParseHeaderIcon(IconPaths.Get("search"));
        _icoSettings = ParseHeaderIcon(IconPaths.Get("settings"));
        _icoReset = ParseHeaderIcon(IconPaths.Get("refresh"));
    }

    private static void Dyn(Control c, AvaloniaProperty p, string key)
        => c.Bind(p, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key));

    private static AvColor ToColor(Rgb c) => c.ToAvalonia();

    private static Geometry ParseBadgeIcon(string pathData)
    {
        var g = Geometry.Parse(pathData);
        g.Transform = new MatrixTransform(
            Matrix.CreateTranslation(0, 960) * Matrix.CreateScale(12.0 / 960.0, 12.0 / 960.0));
        return g;
    }

    private static Geometry ParseHeaderIcon(string pathData)
    {
        var g = Geometry.Parse(pathData);
        g.Transform = new MatrixTransform(
            Matrix.CreateTranslation(0, 960) * Matrix.CreateScale(HeaderIconSize / 960.0, HeaderIconSize / 960.0));
        return g;
    }

    public void Initialize(IServiceProvider services)
    {
        _services = services;
        _workspace = services.GetRequiredService<IWorkspaceService>();
        _layoutService = services.GetRequiredService<IGraphLayoutService>();
        _backgroundWorkGate = services.GetService<IBackgroundWorkGate>();
        _theme = services.GetRequiredService<IThemeService>();
        _dialogs = services.GetRequiredService<IDialogService>();
        _notifications = services.GetRequiredService<INotificationService>();
        _cherryPick = services.GetRequiredService<Historia.Services.PowerTools.ICherryPickService>();
        _stashes = services.GetRequiredService<Historia.Services.PowerTools.IStashService>();
        _nav = services.GetRequiredService<INavigationBus>();
        _settings = services.GetRequiredService<Historia.Services.Settings.ISettingsService>();
        _actionBus = services.GetRequiredService<Historia.Services.UI.IRepoActionBus>();
        _scrollProbe = services.GetRequiredService<Historia.Services.UI.IScrollDirectionProbe>();
        _graphMetaFromDisk = _settings.Current.GraphMetadataFromDisk;
        _lightweightMode = _settings.Current.LightweightMode;
        _showAuthorDate = _settings.Current.ShowAuthorDate;
        _residentRowCapSetting = _settings.Current.ResidentRowCap;
        _silentCrawlRowCap = GraphLimits.GetSilentCrawlRowCap(_settings.Current.SilentCrawlRowCap, _lightweightMode);
        _crawl = new GraphCrawlThreshold(_silentCrawlRowCap, ResidentRowCap);
        _crawlTarget = _crawl.Target;
        Volatile.Write(ref _crawlRunning, 0);
        _repoLayout = services.GetRequiredService<Historia.Services.Settings.IRepoLayoutService>();

        _repoLayout.Changed += OnRepoLayoutChanged;
        _settings.Changed += OnSettingsChanged;

        _themeMode = _theme.Effective;
        RebuildThemeResources();

        _workspace.FocusedRepositoryChanged += OnFocusedRepositoryChanged;
        _theme.ThemeChanged += OnThemeChanged;
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    private void Attach()
    {
        _workspace.FocusedRepositoryChanged -= OnFocusedRepositoryChanged;
        _workspace.FocusedRepositoryChanged += OnFocusedRepositoryChanged;

        _theme.ThemeChanged -= OnThemeChanged;
        _theme.ThemeChanged += OnThemeChanged;

        if (_repoLayout is not null)
        {
            _repoLayout.Changed -= OnRepoLayoutChanged;
            _repoLayout.Changed += OnRepoLayoutChanged;
        }

        if (_settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
            _settings.Changed += OnSettingsChanged;
        }

        // Re-take the commit-store lease Detach() dropped, if the previous layout survived the
        // round-trip off the visual tree. AddRef returns null when the store closed while we
        // were detached (a RefsChanged re-walk dropped the layout service's lease and ours was
        // the last) — that layout can't be rendered any more, so drop it and let the rebind /
        // recompute below repopulate rather than paint stub rows.
        if (_leasedStore is null && _layout?.Layout.Commits is { } resident)
        {
            _leasedStore = resident.AddRef();
            if (_leasedStore is null)
                _layout = null;
        }

        if (_detachedSinceAttach)
        {
            _detachedSinceAttach = false;
            _themeMode = _theme.Effective;
            RebuildThemeResources();
            InvalidateVisual();
        }

        var focused = _workspace.Focused;
        if (!ReferenceEquals(focused, _context))
        {
            Bind(focused);
        }
        else if (_state is not null)
        {
            _state.RefsChanged -= OnRefsChanged;
            _state.RefsChanged += OnRefsChanged;
            _state.SelectedCommitChanged -= OnSelectedCommitChanged;
            _state.SelectedCommitChanged += OnSelectedCommitChanged;
            _state.RevealCommitRequested -= OnRevealCommitRequested;
            _state.RevealCommitRequested += OnRevealCommitRequested;

            UpdateRemoteNumbering(_state.Remotes);

            if (_settings is not null)
                _showAuthorDate = _settings.Current.ShowAuthorDate;

            SyncColumnsFromLayout();

            if (_layout is null)
            {
                if (_context is { } stale)
                    _ = ComputeLayoutAsync(stale);
            }
            else if (_context is { } ctx)
            {
                RequestRefsRecompute(ctx);
            }

            InvalidateMeasure();
            InvalidateArrange();
            InvalidateVisual();
        }
    }

    private void OnFocusedRepositoryChanged(object? sender, FocusedRepositoryChangedEventArgs e)
        => Dispatcher.UIThread.Post(() => Bind(e.New));

    private void Bind(RepositoryContext? context)
    {
        if (ReferenceEquals(context, _context))
            return;
        Historia.Diagnostics.PerfLog.Mark($"graph Bind -> {(context?.RepositoryPath ?? "<none>")}");

        _firstPaintStartTimestamp = context is not null ? Stopwatch.GetTimestamp() : 0;
        _firstPaintKind = null;
        _firstPaintQueued = false;
        _firstPaintRenderLogged = false;

        if (_state is not null)
        {
            _state.RefsChanged -= OnRefsChanged;
            _state.SelectedCommitChanged -= OnSelectedCommitChanged;
            _state.RevealCommitRequested -= OnRevealCommitRequested;
        }

        // stash this repo's lane-pan offset for the session before switching away.
        if (_context is { } leaving)
            _panByRepo[Historia.Services.Settings.IRepoLayoutService.KeyFor(leaving.RepositoryPath)] = _graphPanOffset;

        CancelHoverDwell();
        CancelAndDisposeLayoutCts();
        SetLayout(null);               // release the commit-store lease before switching repos
        ClearReveal();                 // a running pump exits when it sees _revealTarget == null
        _revealUserTookOver = false;
        _selectedRow = -1;
        _hoverRow = -1;
        _scrollOffset = 0;
        _refsRecomputeInFlight = false;
        _refsPendingRecompute = false;
        _context = context;
        _state = context?.State;
        UpdateRemoteNumbering(_state?.Remotes ?? []);

        if (context is not null)
        {
            var layout = _repoLayout.Get(context.RepositoryPath);
            _columns = layout.Columns;
            _graphWidthPinned = layout.GraphWidthPinned;
            _manuallySized.Clear();
            _zoomOverridesPin = false;
            _graphPanOffset = _panByRepo.GetValueOrDefault(
                Historia.Services.Settings.IRepoLayoutService.KeyFor(context.RepositoryPath));
        }
        else
        {
            _columns = GraphColumnDefaults.All;
            _graphWidthPinned = false;
            _graphPanOffset = 0;
        }

        if (_state is not null)
        {
            _state.RefsChanged += OnRefsChanged;
            _state.SelectedCommitChanged += OnSelectedCommitChanged;
            _state.RevealCommitRequested += OnRevealCommitRequested;
        }

        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
        if (context is not null)
            _ = ComputeLayoutAsync(context);
    }

    private void Detach()
    {
        _detachedSinceAttach = true;
        _firstPaintStartTimestamp = 0;
        _firstPaintKind = null;
        _firstPaintQueued = false;
        _firstPaintRenderLogged = false;

        if (_state is not null)
        {
            _state.RefsChanged -= OnRefsChanged;
            _state.SelectedCommitChanged -= OnSelectedCommitChanged;
            _state.RevealCommitRequested -= OnRevealCommitRequested;
        }

        _workspace.FocusedRepositoryChanged -= OnFocusedRepositoryChanged;
        _theme.ThemeChanged -= OnThemeChanged;
        if (_repoLayout is not null)
            _repoLayout.Changed -= OnRepoLayoutChanged;
        if (_settings is not null)
            _settings.Changed -= OnSettingsChanged;
        CloseRefList();
        CloseRefSearch();
        CancelAndDisposeLayoutCts();

        // Off the visual tree no Render can run, so drop the commit-store lease now rather than
        // hold a disk-backed store's file handle open after the panel is torn down for good.
        // Attach() re-takes it if _layout is still around (a transient detach/reattach). _layout
        // itself is kept so that round-trip doesn't blank the graph.
        _leasedStore?.Dispose();
        _leasedStore = null;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.NewSettings.ShowAuthorDate != _showAuthorDate)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _showAuthorDate = e.NewSettings.ShowAuthorDate;
                InvalidateVisual();
            });
        }
    }

    // Detach can run more than once across a control's attach/detach cycles, and several
    // async continuations touch _layoutCts on the UI thread. Swap the field out first, then
    // cancel/dispose — and tolerate a source that a prior pass already disposed.
    private void CancelAndDisposeLayoutCts()
    {
        _extendChannel?.Writer.TryComplete();
        _extendChannel = null;
        var cts = _layoutCts;
        _layoutCts = null;
        if (cts is null)
            return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    private void OnRepoLayoutChanged(object? sender, Historia.Services.Settings.RepoLayoutChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_context is null)
                return;
            // accept this repo's own change and any application-wide column change.
            var mine = string.Equals(e.RepoPath,
                Historia.Services.Settings.IRepoLayoutService.KeyFor(_context.RepositoryPath), StringComparison.Ordinal);
            var global = string.Equals(e.RepoPath,
                Historia.Services.Settings.IRepoLayoutService.GlobalKey, StringComparison.Ordinal);
            if (!mine && !global)
                return;

            SyncColumnsFromLayout();
        });

    private void SyncColumnsFromLayout()
    {
        if (_context is null || _repoLayout is null)
            return;

        // Re-read the merged view (global order + visibility with this repo's session
        // widths) rather than trusting e.Layout, whose widths are defaults for a global change.
        var layout = _repoLayout.Get(_context.RepositoryPath);
        if (ReferenceEquals(layout.Columns, _columns))
            return;
        _columns = layout.Columns;
        _graphWidthPinned = layout.GraphWidthPinned;
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
    }

    internal void OnRefsChanged(object? sender, RefsChangedEventArgs e)
    {
        if (_context is not { } ctx)
            return;

        if (UpdateRemoteNumbering(e.Remotes))
            InvalidateVisual();

        RequestRefsRecompute(ctx);
    }

    private void RequestRefsRecompute(RepositoryContext ctx)
    {
        if (_refsRecomputeInFlight)
        {
            _refsPendingRecompute = true;
            return;
        }

        ActiveRefsRecomputeTask = RunCoalescedRefsRecomputeAsync(ctx);
    }

    /// <summary>Renumbers the remotes when their names changed; true when it did.</summary>
    private bool UpdateRemoteNumbering(IReadOnlyList<RemoteInfo> remotes)
    {
        var same = remotes.Count == _remoteNumberingNames.Length;
        for (var i = 0; same && i < remotes.Count; i++)
            same = string.Equals(remotes[i].Name, _remoteNumberingNames[i], StringComparison.Ordinal);
        if (same)
            return false;

        _remoteNumberingNames = remotes.Select(r => r.Name).ToArray();
        _remoteNumbering = RemoteNumbering.From(_remoteNumberingNames);
        return true;
    }

    private async Task RunCoalescedRefsRecomputeAsync(RepositoryContext context)
    {
        _refsRecomputeInFlight = true;
        try
        {
            while (ReferenceEquals(context, _context))
            {
                _refsPendingRecompute = false;
                await RecomputeAsync(context).ConfigureAwait(true);
                if (!_refsPendingRecompute || !ReferenceEquals(context, _context))
                    break;
            }
        }
        finally
        {
            _refsRecomputeInFlight = false;
        }
    }

    private void OnSelectedCommitChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedRow = ResolveRow(e.CommitId);
        AutoPanToSelectedRow();
        InvalidateVisual();
    }

    /// <summary>pan the Graph column so the selected commit's lane is visible. Runs from
    /// the one place the selected row changes (row click, clamped-node click, keyboard nav,
    /// Ctrl+F step, reveal) — never from hover.</summary>
    private void AutoPanToSelectedRow()
    {
        if (_selectedRow >= 0 && _layout is { } l && _selectedRow < l.Layout.RowCount)
            EnsureLaneInView(l.Layout.Rows[_selectedRow].LaneIndex);
    }

    private void OnRevealCommitRequested(object? sender, RevealCommitEventArgs e)
    {
        var row = ResolveRow(e.CommitId);
        if (row >= 0)
        {
            // Already loaded — jump straight there and drop any pending reveal.
            ClearReveal();
            _selectedRow = row;
            AutoPanToSelectedRow();
            CenterRow(row);
            InvalidateVisual();
            return;
        }

        // Not loaded yet: blank the graph, show the target name centred, and (if one isn't
        // already running) start the background pump that walks toward it. A fresh explicit
        // click re-takes the viewport.
        _revealTarget = e.CommitId;
        _revealDeco = BuildRevealDecoration(e.CommitId);
        _revealUserTookOver = false;
        InvalidateArrange();
        InvalidateVisual();

        if (!_revealPumpRunning)
            _ = RunRevealPumpAsync();
    }

    private async Task RunRevealPumpAsync()
    {
        _revealPumpRunning = true;
        try
        {
            var ct = _layoutCts?.Token ?? CancellationToken.None;
            while (_revealTarget is { } target && !_revealUserTookOver && !ct.IsCancellationRequested)
            {
                var row = ResolveRow(target);
                if (row >= 0)
                {
                    _selectedRow = row;
                    AutoPanToSelectedRow();
                    if (!_revealUserTookOver)
                        CenterRow(row);
                    break;
                }

                InvalidateVisual(); // refresh the "… N rows" count on the placeholder

                var before = _layout?.Layout.RowCount ?? 0;
                await EnsureExtendedAsync(int.MaxValue).ConfigureAwait(true);
                var after = _layout?.Layout.RowCount ?? 0;

                if (after != before)
                {
                    await Task.Delay(16, ct).ConfigureAwait(true);
                    continue;
                }

                if (_layout is { Layout.IsComplete: true })
                {
                    _notifications.Publish(NotificationSeverity.Info,
                        "That commit isn't in this graph.",
                        "It may be on a branch outside the current graph scope. Widen the scope, or use Find (Ctrl+F).");
                    break;
                }

                // Extension stalled before reaching the target and the walk isn't complete —
                // the resident-row ceiling blocks loading more. A bare break here would leave
                // the user on the blanked placeholder with no explanation.
                _notifications.Publish(NotificationSeverity.Info,
                    "That commit is beyond the loaded graph.",
                    ResidentCapNotice(ResidentRowCap, _lightweightMode));
                break;
            }
        }
        catch { /* reveal is best-effort — never surface as a fatal error */ }
        finally
        {
            _revealPumpRunning = false;
            ClearReveal();
            InvalidateArrange(); // un-blank: bring the graph body (and scrollbars) back
            InvalidateVisual();
        }
    }

    internal static string ResidentCapNotice(int cap, bool lightweight) =>
        lightweight
            ? $"History is capped at {cap:N0} rows by Lightweight mode. Turn it off in Settings → Advanced to load more, or use Find (Ctrl+F) to jump straight to it."
            : $"History is capped at {cap:N0} rows to preserve memory. Use Find (Ctrl+F) to jump straight to it.";

    internal static string ResidentCapReachedNotice(int rowCount, bool lightweight) =>
        lightweight
            ? $"Loaded {rowCount:N0} commits. Lightweight mode caps history; turn it off in Settings → Advanced to load more."
            : $"Loaded {rowCount:N0} commits. Further history is capped to preserve system memory.";

    private void ClearReveal()
    {
        _revealTarget = null;
        _revealDeco = null;
    }

    /// <summary>A stand-in badge for a commit we haven't walked to yet — a branch or tag that
    /// points at it (rendered exactly like a real one), else its short SHA. The colour is seeded
    /// from the name the same way the layout engine seeds lane colour, so it matches once the
    /// row loads.</summary>
    private GraphDecoration BuildRevealDecoration(GitObjectId id)
    {
        var branch = _state?.Branches.FirstOrDefault(b => Equals(b.TipCommitId, id));
        var tag = branch is null ? _state?.Tags.FirstOrDefault(t => Equals(t.TargetCommitId, id)) : null;

        DecorationType type;
        string label;
        var isLocal = false;
        string[]? remotes = null;

        if (branch is not null && branch.IsRemote)
        {
            type = DecorationType.RemoteBranch;
            var rn = branch.RemoteName;
            remotes = [rn ?? "origin"];
            label = rn is not null && branch.Name.StartsWith(rn + "/", StringComparison.Ordinal)
                ? branch.Name[(rn.Length + 1)..]
                : branch.Name;
        }
        else if (branch is not null)
        {
            type = DecorationType.LocalBranch;
            isLocal = true;
            label = branch.Name;
        }
        else if (tag is not null)
        {
            type = DecorationType.Tag;
            label = tag.Name;
        }
        else
        {
            type = DecorationType.LocalBranch;
            label = id.ToShortString(7);
        }

        return new GraphDecoration
        {
            RowIndex = 0,
            Type = type,
            Label = label,
            ColorIndex = (int)(Historia.Services.Graph.StableHash.Fnv1a(label) % (uint)LanePalette.Size),
            IsLocal = isLocal,
            RemotesOrNull = remotes,
        };
    }

    private void CenterRow(int row)
    {
        if (_layout is not { } l)
            return;
        var contentHeight = l.Layout.RowCount * _rowHeight;
        var viewH = Math.Max(0, Bounds.Height - RowsTop - (_hScrollBar.IsVisible ? ScrollBarMetrics.Lane : 0));
        var offset = GraphScroll.CenterOffset(row, _rowHeight, viewH, contentHeight);
        if (offset is null)
            return;
        _scrollOffset = offset.Value;
        _scrollBar.Value = _scrollOffset;
        _ = EnsureExtendedAsync((int)((_scrollOffset + viewH) / _rowHeight) + 1);
        NoteViewportBottom();
    }

    private int ResolveRow(GitObjectId? id)
        // RowByCommitId is shared with the layout service's still-growing accumulator
        // (not copied per snapshot), so an older layout must ignore rows beyond its own RowCount.
        => id is { } nonNull && _layout is { } l && l.Layout.RowByCommitId.TryGetValue(nonNull, out var row)
            && row < l.Layout.RowCount ? row : -1;

    /// <summary>rows laid out in the first, fast phase before the configured
    /// "Initial rows loaded" batch, so a repo open / switch paints almost immediately.</summary>
    private const int QuickInitialRows = 200;

    private async Task ComputeLayoutAsync(RepositoryContext context)
    {
        CancelAndDisposeLayoutCts();
        _layoutCts = new CancellationTokenSource();
        var ct = _layoutCts.Token;
        InitializeExtendPump(ct);
        _crawl = new GraphCrawlThreshold(_silentCrawlRowCap, ResidentRowCap);
        _crawlTarget = _crawl.Target;
        Volatile.Write(ref _crawlRunning, 0);

        if (_firstPaintStartTimestamp == 0)
        {
            _firstPaintStartTimestamp = Stopwatch.GetTimestamp();
            _firstPaintKind = null;
            _firstPaintQueued = false;
            _firstPaintRenderLogged = false;
        }
        var startTimestamp = _firstPaintStartTimestamp;

        try
        {
            var query = _query;
            Historia.Diagnostics.PerfLog.Mark($"graph ComputeLayoutAsync start ({context.RepositoryPath})");
            const int full = 2_500;
            var quick = Math.Min(QuickInitialRows, full);

            // Paint an optimistic snapshot from the on-disk walk cache before the real
            // quick phase below even starts — that phase still has to pay for a full ref
            // enumeration (ComputeFingerprint) before it can so much as check the cache, and on
            // a large repo that alone can take a while. This shortcut skips straight to a plain
            // file read, so something is on screen sooner; it is unconditionally replaced by the
            // fingerprint-verified quick-phase result a few lines down; if the repo hasn't
            // changed since the file was written, that replacement paints identical rows anyway.
            if (_layout is null)
            {
                var optimistic = await Task.Run(
                    () => _layoutService.TryOptimisticSnapshot(context, query, quick), ct).ConfigureAwait(true);
                if (optimistic is not null && !ct.IsCancellationRequested)
                {
                    ApplyLayout(optimistic, query);
                    if (!_firstPaintQueued)
                    {
                        _firstPaintQueued = true;
                        _firstPaintKind = "optimistic";
                        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                        Historia.Diagnostics.PerfLog.Mark(
                            $"graph first paint queued: optimistic ({optimistic.Layout.RowCount} rows, {elapsedMs:F1}ms total)");
                    }
                    else
                    {
                        Historia.Diagnostics.PerfLog.Mark($"graph optimistic paint ({optimistic.Layout.RowCount} rows)");
                    }
                }
            }

            // Immediate first paint (<15ms), then extend smoothly.
            if (quick < full)
            {
                var fast = await Task.Run(
                    () => _layoutService.ComputeLayoutAsync(context, query, quick, ct), ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                    return;
                ApplyLayout(fast, query);
                if (!_firstPaintQueued)
                {
                    _firstPaintQueued = true;
                    _firstPaintKind = "quick";
                    var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    Historia.Diagnostics.PerfLog.Mark(
                        $"graph first paint queued: quick ({fast.Layout.RowCount} rows, {elapsedMs:F1}ms total)");
                }
                else
                {
                    Historia.Diagnostics.PerfLog.Mark($"graph quick paint ({fast.Layout.RowCount} rows)");
                }

                var rest = await Task.Run(
                    () => _layoutService.ExtendLayoutAsync(context, fast, full - fast.Layout.RowCount, ct), ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                    return;
                ApplyLayout(rest, query);
                Historia.Diagnostics.PerfLog.Mark($"graph initial batch extend queued ({rest.Layout.RowCount} rows)");
                _ = StartBackgroundSilentLoadAsync(context, ct);
                return;
            }

            var result = await Task.Run(
                () => _layoutService.ComputeLayoutAsync(context, query, full, ct), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
                return;
            ApplyLayout(result, query);
            if (!_firstPaintQueued)
            {
                _firstPaintQueued = true;
                _firstPaintKind = "full";
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                Historia.Diagnostics.PerfLog.Mark(
                    $"graph first paint queued: full ({result.Layout.RowCount} rows, {elapsedMs:F1}ms total)");
            }
            else
            {
                Historia.Diagnostics.PerfLog.Mark($"graph first paint queued ({result.Layout.RowCount} rows)");
            }
            _ = StartBackgroundSilentLoadAsync(context, ct);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Layout failure leaves the graph blank rather than crashing the shell.
        }
        finally
        {
            // Whatever happened above -- painted, cancelled, or failed -- this context's first
            // graph-paint attempt is done. WorkspaceService.InitializeContextAsync is waiting on
            // this (bounded by its own timeout) before it starts touching the repo itself.
            context.MarkFirstGraphPaintDone();

            // The background-write gate's other half of "done loading" -- this context's
            // first-paint attempt FOR THE CURRENT FOCUS, whether it painted, was cancelled, or
            // failed.
            _backgroundWorkGate?.ReportGraphSettled(context);
        }
    }

    private void ApplyLayout(GraphLayoutResult result, GraphQuery query)
    {
        if (!SetLayout(result))
        {
            // The freshly computed store was closed by a concurrent re-walk before we could
            // lease it. Keep the previous layout; the re-walk that closed it queues its own
            // RefsChanged -> ComputeLayoutAsync, which will hand us a live store.
            Historia.Diagnostics.PerfLog.Mark("graph ApplyLayout dropped: store closed pre-lease");
            return;
        }
        _layoutQuery = query;
        _selectedRow = ResolveRow(_state?.SelectedCommitId);
        RecomputeMatches();
        InvalidateMeasure();
        // TotalLaneCount drives the auto Graph width — re-arrange, not just re-paint.
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>The single choke point for replacing <see cref="_layout"/>. Transfers the commit-
    /// store lease: takes one on the new layout's store and releases the one held for the old,
    /// so a disk-backed store's file handle stays open exactly as long as this control can still
    /// render from it — even after <c>GraphLayoutService</c> drops its own reference on a
    /// RefsChanged re-walk. Idempotent when the store instance is unchanged (an in-place
    /// Extend reuses it). Must run on the UI thread.
    /// <para>Returns false when the new layout's store was already closed by a concurrent final
    /// Dispose (<see cref="ICommitStore.AddRef"/> returned null) — the layout is dead on arrival
    /// and neither it nor the lease is adopted; the caller keeps whatever it had.</para></summary>
    private bool SetLayout(GraphLayoutResult? result)
    {
        var next = result?.Layout.Commits;
        if (ReferenceEquals(next, _leasedStore))
        {
            _layout = result;
            return true;
        }

        if (next is not null && next.AddRef() is null)
            return false;

        _leasedStore?.Dispose();
        _leasedStore = next;
        _layout = result;
        return true;
    }

    private async Task RecomputeAsync(RepositoryContext context)
    {
        _refsRecomputeCount++;
        if (_recomputeOverrideForTesting is not null)
        {
            await _recomputeOverrideForTesting(context).ConfigureAwait(true);
            return;
        }

        if (_layout is not { } previous)
        {
            await ComputeLayoutAsync(context).ConfigureAwait(true);
            return;
        }

        CancelAndDisposeLayoutCts();
        _layoutCts = new CancellationTokenSource();
        var ct = _layoutCts.Token;
        InitializeExtendPump(ct);

        try
        {
            var query = _query;
            const int initial = 2_500;
            var result = await Task.Run(() =>
                _layoutService.RecomputeAsync(context, query, previous, InvalidationReason.RefsChanged, initial, ct), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
                return;
            // Unchanged refs hand back the painted layout; re-applying it would reset the find
            // cursor and scroll to the first match.
            if (!ReferenceEquals(result, previous))
                ApplyLayout(result, query);
            _ = StartBackgroundSilentLoadAsync(context, ct);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void InitializeExtendPump(CancellationToken ct)
    {
        Interlocked.Exchange(ref _cappedNotified, 0);
        _extendChannel = Channel.CreateUnbounded<ExtendRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _ = RunExtendPumpAsync(_extendChannel.Reader, ct);
    }

    // Every path that stops extending because the resident-row ceiling was reached routes
    // through here: EnsureExtendedAsync and StartBackgroundSilentLoadAsync both bail before
    // ever writing to the channel, so the pump alone could not be relied on to surface it.
    // Fires the toast at most once per layout (reset by InitializeExtendPump).
    private void NotifyResidentCapReached(int rowCount)
    {
        if (Interlocked.Exchange(ref _cappedNotified, 1) != 0)
            return;
        _notifications.Publish(NotificationSeverity.Info,
            "Graph capped at resident limit.",
            ResidentCapReachedNotice(rowCount, _lightweightMode));
    }

    private async Task RunExtendPumpAsync(ChannelReader<ExtendRequest> reader, CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var req))
                {
                    if (ct.IsCancellationRequested || !ReferenceEquals(req.Context, _context))
                    {
                        req.Completion?.TrySetCanceled(ct);
                        continue;
                    }

                    if (_layout is not { } current || current.Layout.IsComplete)
                    {
                        req.Completion?.TrySetResult();
                        continue;
                    }

                    // For scroll requests, skip if already satisfied
                    if (req.TargetRow >= 0 && req.TargetRow < current.Layout.RowCount - 500 && req.TargetRow != int.MaxValue)
                    {
                        req.Completion?.TrySetResult();
                        continue;
                    }

                    // The silent crawler stops at _crawlTarget (by design, not a memory
                    // limit — no toast); scroll-driven requests run to the true resident cap.
                    var cap = req.Background ? Math.Min(Volatile.Read(ref _crawlTarget), ResidentRowCap) : ResidentRowCap;
                    if (current.Layout.RowCount >= cap)
                    {
                        if (!req.Background)
                            NotifyResidentCapReached(current.Layout.RowCount);
                        req.Completion?.TrySetResult();
                        break;
                    }

                    var batch = Math.Min(req.BatchSize, cap - current.Layout.RowCount);
                    if (batch <= 0)
                    {
                        req.Completion?.TrySetResult();
                        continue;
                    }

                    try
                    {
                        var extended = await _layoutService.ExtendLayoutAsync(req.Context, current, batch, ct, req.Background).ConfigureAwait(false);
                        if (extended.Layout.RowCount > current.Layout.RowCount && ReferenceEquals(req.Context, _context))
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                if (ct.IsCancellationRequested || !ReferenceEquals(req.Context, _context))
                                    return;
                                // A recompute applied a new layout while this extend ran: the
                                // extension belongs to the layout it replaced — drop it.
                                if (!ReferenceEquals(_layout?.State, current.State))
                                    return;

                                SetLayout(extended);
                                InvalidateMeasure();
                                InvalidateArrange(); // lane count can grow -> auto Graph width
                                InvalidateVisual();
                            });
                        }
                        req.Completion?.TrySetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        req.Completion?.TrySetCanceled(ct);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write(AppLogLevel.Warn, "graph", $"Background graph extend failed for {req.Context.DisplayName}", ex);
                        // The waiter may already have given up (a tab switch cancels its wait), and
                        // a faulted task nobody reads is rethrown by the finalizer as an unobserved
                        // exception — an "unexpected error" for the user. Reading Exception marks it
                        // observed; a waiter still waiting gets the fault as before.
                        if (req.Completion is { } completion && completion.TrySetException(ex))
                            _ = completion.Task.Exception;
                    }

                    // Yield briefly after background batch to keep system and UI fully responsive
                    if (req.TargetRow < 0)
                    {
                        await Task.Delay(20, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task EnsureExtendedAsync(int neededRow)
    {
        if (_context is not { } ctx || _layout is not { } current || _extendChannel is not { } channel)
            return;
        if (current.Layout.IsComplete)
            return;
        if (neededRow < current.Layout.RowCount - 500 && neededRow != int.MaxValue)
            return;

        if (current.Layout.RowCount >= ResidentRowCap)
        {
            NotifyResidentCapReached(current.Layout.RowCount);
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ct = _layoutCts?.Token ?? CancellationToken.None;
        const int batch = 5_000;
        if (channel.Writer.TryWrite(new ExtendRequest(ctx, neededRow, batch, tcs, ct)))
        {
            try
            {
                await tcs.Task.WaitAsync(ct).ConfigureAwait(true);
            }
            catch { }
        }
    }

    /// <summary>
    /// Stuck-guard for the headless/no-signal path, not a pacing timeout — the intended exit is
    /// <see cref="RepositoryContext.FirstFullLoad"/> completing. Mirrors GraphFirstPaintCeiling.
    /// </summary>
    private static readonly TimeSpan CrawlStuckGuardCeiling = TimeSpan.FromSeconds(120);

    internal static TimeSpan StuckGuardCeiling => CrawlStuckGuardCeiling;

    /// <summary>Rows the silent crawl loads before <see cref="RepositoryContext.MarkGraphWarmed"/>
    /// lets a large working tree's first <c>git status</c> start.</summary>
    internal const int GraphWarmedRows = 100_000;

    internal static async Task<bool> WaitForCrawlGuardAsync(RepositoryContext context, TimeSpan ceiling, CancellationToken ct)
    {
        var ceilingTask = Task.Delay(ceiling, ct);
        var completed = await Task.WhenAny(context.FirstFullLoad, ceilingTask).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var firedCeiling = completed == ceilingTask && !context.FirstFullLoad.IsCompleted;
        if (firedCeiling && Historia.Diagnostics.PerfLog.Enabled)
            Historia.Diagnostics.PerfLog.Mark($"crawl stuck-guard ceiling fired after {ceiling.TotalSeconds:F0}s without FirstFullLoad ({context.DisplayName})");
        return !firedCeiling;
    }

    private async Task StartBackgroundSilentLoadAsync(RepositoryContext context, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _crawlRunning, 1, 0) != 0)
            return;

        try
        {
            await Task.Yield();

            // don't pre-load history until this tab has fully loaded (first paint +
            // its own identity resolution + first RefreshAllAsync). Otherwise the crawl competes
            // with ~1 request/second and the tab's own status/branch
            // work is starved (observed: a 47 s footer freeze mid-crawl on linux). Bounded
            // guard for a headless/no-signal path. Mirrors MaybeStartBackgroundSpill.
            await WaitForCrawlGuardAsync(context, CrawlStuckGuardCeiling, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || !ReferenceEquals(context, _context))
                return;

            var idleDelayMs = 0;
            while (!ct.IsCancellationRequested && ReferenceEquals(context, _context))
            {
                var cap = Volatile.Read(ref _crawlTarget);
                if (_layout is not { } current || current.Layout.IsComplete)
                    break;
                if (current.Layout.RowCount >= GraphWarmedRows)
                    context.MarkGraphWarmed();
                if (current.Layout.RowCount >= cap)
                    break; // reached the silent pre-load ceiling — deeper history loads on scroll

                if (_extendChannel is not { } channel)
                    break;

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                const int batch = 5_000;
                if (!channel.Writer.TryWrite(new ExtendRequest(context, -1, batch, tcs, ct, Background: true)))
                    break;

                await tcs.Task.WaitAsync(ct).ConfigureAwait(false);

                // The pump applies an extension before completing the request, so an unchanged
                // layout here means the step added nothing: a walk that failed or was superseded
                // (until a recompute replaces the layout), a layout its walk no longer owns, or a
                // transient miss such as a short disk read. Back off rather than ask every 20 ms.
                if (_layout is { } after && ReferenceEquals(after.State, current.State)
                    && after.Layout.RowCount <= current.Layout.RowCount)
                {
                    idleDelayMs = idleDelayMs == 0
                        ? CrawlIdleBackoffFirstMs
                        : Math.Min(idleDelayMs * 2, CrawlIdleBackoffMaxMs);
                    await Task.Delay(idleDelayMs, ct).ConfigureAwait(false);
                }
                else
                    idleDelayMs = 0;
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _crawlRunning, 0);
            // However the crawl ended, the status it was keeping off the disk may run now.
            context.MarkGraphWarmed();
        }
    }

    private void NoteViewportBottom()
    {
        var row = (int)((_scrollOffset + Bounds.Height - RowsTop) / _rowHeight) + 1;
        if (_crawl.Advance(row))
        {
            var target = _crawl.Target;
            Volatile.Write(ref _crawlTarget, target);
            Historia.Diagnostics.PerfLog.Mark($"graph crawl target → {target} (viewport row {row})");
            if (Volatile.Read(ref _crawlRunning) == 0 && _context is { } ctx && _layoutCts is { } cts)
                _ = StartBackgroundSilentLoadAsync(ctx, cts.Token);
        }
    }

    // ── Theme ──

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            _themeMode = e.NewTheme;
            RebuildThemeResources(); // recomputes _refsBaseWidth, an input to Resolve
            InvalidateArrange();
            InvalidateVisual();
        });

    private void RebuildThemeResources()
    {
        _lanePens.Clear();
        _laneBrushes.Clear();
        _emphPens.Clear();
        _faintConnectorPens.Clear();
        _pillBorders.Clear();
        _laneFills.Clear();
        _laneTicks.Clear();
        _textCache.Clear();
        _elbowGeometryCache.Clear();

        var palette = _theme.Palette;
        var bg = ToColor(palette["bg"]);
        var text = ToColor(palette["text"]);
        var muted = ToColor(palette["text-muted"]);
        var primary = ToColor(palette["primary"]);

        _bg = new SolidColorBrush(bg);
        _chrome = new SolidColorBrush(ToColor(palette["chrome"]));
        _borderPen = new Pen(new SolidColorBrush(ToColor(palette["border"])), 1);
        _nodeFill = new SolidColorBrush(bg);
        _text = new SolidColorBrush(text);
        _textMuted = new SolidColorBrush(muted);
        _pillText = new SolidColorBrush(ToColor(palette["ref-label"]));
        _headerText = new SolidColorBrush(muted);
        _selectionFill = new SolidColorBrush(ToColor(palette["list-active"]));
        _hoverFill = new SolidColorBrush(ToColor(palette["list-hover"]));
        _selectionBar = new SolidColorBrush(primary);
        _hoverPen = new Pen(new SolidColorBrush(primary), 2.0);
        var warn = palette["warning"];
        _matchFill = new SolidColorBrush(AvColor.FromArgb(0x30, warn.R, warn.G, warn.B));

        RecomputeRefsBaseWidth();
    }

    private void RecomputeRefsBaseWidth()
    {
        var probe = new FormattedText("abcdefghijkl…", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface, BadgeLabelSize, Brushes.Black);
        var w = 8 + 6 + probe.Width + 4 + 12 + 2 + 12 + 6 + 8;
        _refsBaseWidth = Math.Clamp(w, GraphColumnDefaults.RefsStaticFloor, 240);
    }

    /// <summary>the resolved lane colour for an index, read from the theme service
    /// (built-in rainbow or the active custom overlay). The maths lives in
    /// <see cref="GraphLanePalette.LaneRgb"/> so it is unit-testable; this is the control-state
    /// binding of it.</summary>
    private Rgb LaneRgb(int colorIndex)
        => GraphLanePalette.LaneRgb(colorIndex, _theme.LaneColors, _themeMode);

    // every per-lane-colour Pen/Brush is memoised in its own dictionary (all cleared
    // together by RebuildThemeResources on a theme change). One shape: look it up, else build
    // it once and cache. The build lambda runs only on a miss, which is counted. Builders are
    // static lambdas handed the control, so a hit allocates no delegate.
    private T Pooled<T>(Dictionary<int, T> pool, int colorIndex, Func<CommitGraphControl, int, T> build)
    {
        if (pool.TryGetValue(colorIndex, out var v))
            return v;
        _poolMisses++;
        return pool[colorIndex] = build(this, colorIndex);
    }

    // Lane and emphasis pens use a square cap: each piece overhangs its ends by half the pen
    // width, so butted runs, elbows and colour changes overlap instead of meeting at a
    // fractional y, where anti-aliasing would leave a faint seam. The overhang at a line's
    // real end sits under its commit dot or is clipped by the row area.
    private Pen LanePen(int colorIndex) => Pooled(_lanePens, colorIndex,
        static (g, i) => new Pen(new SolidColorBrush(ToColor(g.LaneRgb(i))), LaneStrokeWidth, lineCap: PenLineCap.Square));

    /// <summary>a thin, faint version of the lane colour for a non-HEAD badge→node
    /// connector. 1 DIP at 45% alpha — the crisp, stable equivalent of "thin and faint" (a
    /// sub-pixel stroke rounds away under layout rounding).</summary>
    private Pen FaintConnectorPen(int colorIndex) => Pooled(_faintConnectorPens, colorIndex, static (g, i) =>
    {
        var c = g.LaneRgb(i);
        return new Pen(new SolidColorBrush(AvColor.FromArgb(0x73, c.R, c.G, c.B)), 1.0);
    });

    private SolidColorBrush LaneBrush(int colorIndex) => Pooled(_laneBrushes, colorIndex,
        static (g, i) => new SolidColorBrush(ToColor(g.LaneRgb(i))));

    private Pen EmphPen(int colorIndex) => Pooled(_emphPens, colorIndex,
        static (g, i) => new Pen(new SolidColorBrush(ToColor(g.LaneRgb(i))), 3.0, lineCap: PenLineCap.Square));

    private Pen PillBorder(int colorIndex) => Pooled(_pillBorders, colorIndex,
        static (g, i) => new Pen(new SolidColorBrush(ToColor(g.LaneRgb(i))), 1.5));

    private SolidColorBrush LaneFill(int colorIndex) => Pooled(_laneFills, colorIndex, static (g, i) =>
    {
        var c = g.LaneRgb(i);
        return new SolidColorBrush(AvColor.FromArgb(GraphLanePalette.WashAlpha(g._themeMode), c.R, c.G, c.B));
    });

    private SolidColorBrush LaneTick(int colorIndex) => Pooled(_laneTicks, colorIndex,
        static (g, i) => new SolidColorBrush(ToColor(g.LaneRgb(i))));

    // ── Layout / arrange ──

    protected override Size MeasureOverride(Size availableSize)
    {
        _scrollBar.Measure(availableSize);
        _hScrollBar.Measure(availableSize);
        _queryBar.Measure(new Size(double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width, QueryBarHeight));
        return new Size(
            double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height);
    }

    /// <summary>resolve the column band into <see cref="_resolvedColumns"/>,
    /// <see cref="_bandWidth"/>, <see cref="_graphColLeft"/>, <see cref="_graphColWidth"/> and
    /// clamp the lane pan. Runs in <see cref="ArrangeOverride"/> (so the scrollbar logic there
    /// sees this frame's band width, not last frame's) and again at the top of
    /// <see cref="Render"/>. Pure geometry — safe to call more than once per frame.</summary>
    private void ResolveColumnBand(double width)
    {
        var totalLanes = _layout?.Layout.TotalLaneCount ?? 0;
        var res = GraphColumnLayout.Resolve(_columns, width, LaneWidth, totalLanes,
            _graphWidthPinned && !_zoomOverridesPin, _refsBaseWidth, _manuallySized);
        _resolvedColumns = res.Columns as ResolvedColumn[] ?? [.. res.Columns];
        _bandWidth = res.BandWidth;
        _graphColLeft = 0;
        _graphColWidth = 0;
        foreach (var c in _resolvedColumns)
            if (c.Kind == GraphColumnKind.Graph) { _graphColLeft = c.Left; _graphColWidth = c.Width; }
        ClampPan();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _queryBar.Arrange(new Rect(0, HeaderHeight, finalSize.Width, QueryBarHeight));

        // Resolve the band before the scrollbar logic below reads _bandWidth — during an
        // interactive sidebar drag the arrange pass would otherwise see last frame's width.
        ResolveColumnBand(finalSize.Width);

        var contentHeight = (_contentRowCountOverrideForTesting ?? _layout?.Layout.RowCount ?? 0) * _rowHeight;
        var rowArea = finalSize.Height - RowsTop;
        var showV = contentHeight > rowArea;
        _scrollBar.IsVisible = showV && !RevealBlanking;

        var showH = _bandWidth > finalSize.Width - (showV ? ScrollBarMetrics.Lane : 0) + 0.5;
        _hScrollBar.IsVisible = showH && !RevealBlanking;

        var viewport = Math.Max(0, rowArea - (showH ? ScrollBarMetrics.Lane : 0));
        if (showV)
        {
            _scrollBar.Arrange(new Rect(finalSize.Width - ScrollBarMetrics.Lane, RowsTop, ScrollBarMetrics.Lane, viewport));
            _scrollBar.Minimum = 0;
            var maximum = Math.Max(0, contentHeight - viewport);
            if (_scrollBarUserActive && maximum != _scrollBar.Maximum && ScrollLogOn)
                LogScroll(string.Create(CultureInfo.InvariantCulture, $"maximum {_scrollBar.Maximum:F1} -> {maximum:F1} mid-drag"));
            _scrollBar.Maximum = maximum;
            _scrollBar.ViewportSize = viewport;
            _scrollBar.LargeChange = viewport;
            _scrollBar.SmallChange = _rowHeight;
            // Always keep our mirror inside the (possibly just-grown or just-shrunk) range —
            // an out-of-range `_scrollOffset` is what left the bar unusable when Maximum moved
            // under a drag. Only push the value back into the ScrollBar when the user is NOT
            // mid-drag, so we never fight its Track math.
            _scrollOffset = Math.Clamp(_scrollOffset, 0, _scrollBar.Maximum);
            if (!_scrollBarUserActive)
                _scrollBar.Value = _scrollOffset;
        }
        else
        {
            _scrollOffset = 0;
        }

        if (_hScrollBar.IsVisible)
        {
            _hScrollBar.Arrange(new Rect(0, finalSize.Height - ScrollBarMetrics.Lane, Math.Max(0, finalSize.Width - (showV ? ScrollBarMetrics.Lane : 0)), ScrollBarMetrics.Lane));
            _hScrollBar.Minimum = 0;
            _hScrollBar.Maximum = Math.Max(0, _bandWidth - (finalSize.Width - (showV ? ScrollBarMetrics.Lane : 0)));
            _hScrollBar.ViewportSize = finalSize.Width;
            _hScrollBar.LargeChange = finalSize.Width * 0.9;
            _hScrollBar.SmallChange = 40;
            _hScrollOffset = Math.Min(_hScrollOffset, _hScrollBar.Maximum);
            _hScrollBar.Value = _hScrollOffset;
        }
        else
        {
            _hScrollOffset = 0;
        }

        return finalSize;
    }
}
