namespace Historia.Services.Git;

/// <summary>
/// Marks the current async flow as a repository write. While a scope is active every git process
/// the flow starts is treated as <see cref="GitCliCancelPolicy.NeverAfterStart"/>: a cancellation
/// or timeout that arrives after the process has started does not kill it, because a killed writer
/// leaves <c>index.lock</c> behind and a half-applied change. The flag lives in an
/// <see cref="AsyncLocal{T}"/>, so work that escapes with <c>Task.Run</c> inside the scope inherits it.
/// </summary>
public static class GitWriteScope
{
    private static readonly AsyncLocal<bool> Active = new();

    /// <summary>True while a scope entered by this flow is open.</summary>
    public static bool IsActive => Active.Value;

    /// <summary>Opens a scope; dispose it to restore the previous state.</summary>
    public static IDisposable Enter()
    {
        var previous = Active.Value;
        Active.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Active.Value = previous;
        }
    }
}
