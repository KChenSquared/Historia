namespace Historia.Layout;

/// <summary>
/// Re-runs layout on every open window after the dispatcher swallowed an exception. The layout
/// manager clears its queued flag only on a pass that finishes without throwing; after a throw the
/// flag stays set, no later invalidation queues a pass, and parts of the window keep stale bounds
/// until the OS resizes the window. A direct <see cref="Layoutable.UpdateLayout"/> runs a pass
/// regardless of the flag and clears it.
/// <para>
/// Trap: invalidating the whole tree on every exception loops forever when the thrower is
/// deterministic, so recoveries are capped per time window and a failing pass is only logged.
/// </para>
/// </summary>
internal sealed class LayoutRecovery(
    Func<IReadOnlyList<TopLevel>> topLevels,
    Action<Action> post,
    Func<TimeSpan> now)
{
    internal const int MaxRecoveriesPerWindow = 3;
    internal static readonly TimeSpan CapWindow = TimeSpan.FromSeconds(10);

    private readonly Queue<TimeSpan> _accepted = new();
    private bool _capLogged;

    /// <summary>Call from the UI thread when the dispatcher caught an exception. Schedules a layout
    /// pass on every open window unless the per-window cap is reached.</summary>
    public void OnDispatcherException()
    {
        var at = now();
        while (_accepted.Count > 0 && at - _accepted.Peek() >= CapWindow)
            _accepted.Dequeue();

        if (_accepted.Count >= MaxRecoveriesPerWindow)
        {
            if (!_capLogged)
            {
                _capLogged = true;
                AppLog.Write(AppLogLevel.Warn, "layout",
                    $"Layout recovery skipped: {MaxRecoveriesPerWindow} already ran in the last {CapWindow.TotalSeconds:0} s.");
            }
            return;
        }

        _capLogged = false;
        _accepted.Enqueue(at);
        post(Recover);
    }

    private void Recover()
    {
        foreach (var topLevel in topLevels())
        {
            try
            {
                topLevel.UpdateLayout();
            }
            catch (Exception ex)
            {
                AppLog.Write(AppLogLevel.Error, "layout", $"Layout recovery pass failed for {topLevel.GetType().Name}.", ex);
            }
        }
    }
}
