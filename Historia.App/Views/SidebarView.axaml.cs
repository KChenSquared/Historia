namespace Historia.Views;

/// <summary>
/// The sidebar column: a Branches/CMDR toggle above either the ref navigator or the agent queue,
/// bound to a <see cref="SidebarViewModel"/>. Both panes are built here once and disposed with
/// this view; the markup swaps them by visibility.
/// </summary>
public sealed partial class SidebarView : UserControl, IDisposable
{
    private readonly RefNavigatorView _branches;
    private readonly CmdrQueueView _cmdr;

    public SidebarView(IServiceProvider sp)
    {
        InitializeComponent();

        ViewModel = ActivatorUtilities.CreateInstance<SidebarViewModel>(sp);
        DataContext = ViewModel;

        _branches = new RefNavigatorView(sp);
        _cmdr = new CmdrQueueView(sp);
        BranchesHost.Content = _branches;
        CmdrHost.Content = _cmdr;
    }

    internal SidebarViewModel ViewModel { get; }

    /// <summary>The ref navigator shown while Branches is active.</summary>
    internal RefNavigatorView Branches => _branches;

    /// <summary>The agent queue shown while CMDR is active.</summary>
    internal CmdrQueueView Cmdr => _cmdr;

    public void Dispose()
    {
        _branches.Dispose();
        _cmdr.Dispose();
        ViewModel.Dispose();
    }
}
