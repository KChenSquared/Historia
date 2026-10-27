namespace Historia.Services.UI;

/// <summary>
/// Where UI-thread work sits relative to other queued UI work. <see cref="Normal"/> outranks input
/// and rendering, so it suits small, already-coalesced work whose delay the user would notice (a
/// status change after a stage). <see cref="Background"/> yields to input and rendering, so it
/// suits work that may wait.
/// </summary>
public enum UiPriority
{
    Normal,
    Background,
}

/// <summary>
/// The UI thread, abstracted so <see cref="Historia.Core"/> stays free of any UI framework.
/// The App layer implements it over <c>Avalonia.Threading.Dispatcher.UIThread</c>;
/// tests supply a synchronous inline implementation.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller is already on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Queues <paramref name="action"/> to run on the UI thread.</summary>
    void Post(Action action);

    /// <summary>
    /// Queues <paramref name="action"/> at <paramref name="priority"/>. Implementations that do not
    /// distinguish priorities keep the default, which queues it like <see cref="Post(Action)"/>.
    /// </summary>
    void Post(Action action, UiPriority priority) => Post(action);

    /// <summary>
    /// Creates a repeating timer (initially <b>stopped</b>) whose <paramref name="tick"/> runs
    /// on the UI thread. Used for trailing-edge coalescing, so it must support Stop/Start.
    /// </summary>
    IUiTimer CreateTimer(TimeSpan interval, Action tick);

    /// <summary>
    /// Creates the timer of <see cref="CreateTimer(TimeSpan, Action)"/> with its tick queued at
    /// <paramref name="priority"/>. Implementations that do not distinguish priorities keep the
    /// default, which ignores it.
    /// </summary>
    IUiTimer CreateTimer(TimeSpan interval, Action tick, UiPriority priority) => CreateTimer(interval, tick);
}

/// <summary>A restartable UI-thread timer handed out by <see cref="IUiDispatcher.CreateTimer"/>.</summary>
public interface IUiTimer : IDisposable
{
    void Start();
    void Stop();
}
