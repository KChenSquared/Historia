namespace Historia.Views;

public enum ShortcutsBarSide
{
    Left,
    Right,
}

/// <summary>
/// A static shortcut bar positioned above a side pane (left above sidebar, right above detail).
/// Height matches <see cref="CommitGraphControl.HeaderHeight"/> (30 px), forming a continuous
/// chrome strip with the commit graph header band across the window. The buttons are markup bound
/// to a <see cref="ShortcutsBarViewModel"/>; the code here measures the bar so the window can keep
/// its side pane wide enough to show every button.
/// </summary>
internal sealed partial class ShortcutsBarView : UserControl, IDisposable
{
    public const double BarHeight = CommitGraphControl.HeaderHeight;

    private const double GroupGap = 12;

    private readonly Control _container;
    private readonly StackPanel[] _groups;

    private bool _disposed;
    private double _lastReportedNaturalWidth = -1;

    public ShortcutsBarView(IServiceProvider sp, ShortcutsBarSide side)
    {
        InitializeComponent();

        Side = side;
        ViewModel = CreateViewModel(sp);
        DataContext = ViewModel;

        if (side == ShortcutsBarSide.Left)
        {
            Layouts.Children.Remove(RightLayout);
            _container = LeftLayout;
            _groups = [LeadGroup, TrailGroup];
        }
        else
        {
            Layouts.Children.Remove(LeftLayout);
            _container = RightLayout;
            _groups = [RightLayout];
        }

        Bar.Height = BarHeight;
        Height = BarHeight;

        _container.LayoutUpdated += OnPanelLayoutUpdated;

        _lastReportedNaturalWidth = NaturalWidth;
    }

    internal ShortcutsBarViewModel ViewModel { get; }

    public ShortcutsBarSide Side { get; }

    public double NaturalWidth
    {
        get
        {
            double sum = 0;
            foreach (var group in _groups)
            {
                if (!group.IsMeasureValid && VisualRoot == null)
                    group.Measure(new Size(double.PositiveInfinity, BarHeight));

                sum += UnboundedWidth(group);
            }

            if (sum <= 0 && _lastReportedNaturalWidth > 0)
                return _lastReportedNaturalWidth;

            if (_groups.Length > 1)
                sum += GroupGap;

            return sum + Bar.Padding.Left + Bar.Padding.Right;
        }
    }

    public event EventHandler? NaturalWidthChanged;

    // A group's own DesiredSize is clamped to the width its parent offered, so it under-reports
    // when the host is narrower than the buttons. Its children are measured without a width limit
    // inside the horizontal stack, so their desired widths plus the spacing are the natural width.
    private static double UnboundedWidth(StackPanel group)
    {
        double width = 0;
        var visible = 0;
        foreach (var child in group.Children)
        {
            if (!child.IsVisible)
                continue;
            width += child.DesiredSize.Width;
            visible++;
        }

        if (visible > 1)
            width += group.Spacing * (visible - 1);
        return visible > 0 ? width + group.Margin.Left + group.Margin.Right : width;
    }

    private static ShortcutsBarViewModel CreateViewModel(IServiceProvider? sp) => new(
        ViewServices.Dispatcher(sp),
        sp?.GetService<IWorkspaceService>(),
        sp?.GetService<IRepoActionBus>(),
        sp?.GetService<ISettingsService>(),
        sp?.GetService<INotificationService>(),
        sp?.GetService<IStashService>(),
        sp?.GetService<IDialogService>(),
        sp?.GetService<ITerminalSessionService>());

    private void OnPanelLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        var current = NaturalWidth;
        if (Math.Abs(current - _lastReportedNaturalWidth) > 0.5)
        {
            _lastReportedNaturalWidth = current;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                    NaturalWidthChanged?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _container.LayoutUpdated -= OnPanelLayoutUpdated;
        ViewModel.Dispose();
    }
}
