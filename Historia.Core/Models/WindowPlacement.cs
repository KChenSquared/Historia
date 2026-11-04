namespace Historia.Models;

/// <summary>
/// Main-window geometry, persisted to <c>window.json</c> in <c>AppPaths.MachineDirectory</c>.
/// Deliberately <b>not</b> a field on <see cref="AppSettings"/> — monitor coordinates
/// must not follow the user to another machine. <see cref="Left"/> and <see cref="Top"/> are the
/// restored (normal) position in physical pixels and <see cref="Width"/> and <see cref="Height"/> the
/// restored size in device-independent pixels; a maximized or minimized window keeps its last normal rectangle here.
/// </summary>
public sealed record WindowPlacement
{
    public required double Left { get; init; }
    public required double Top { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public bool Maximized { get; init; }
}
