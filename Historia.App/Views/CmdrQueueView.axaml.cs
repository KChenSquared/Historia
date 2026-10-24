namespace Historia.Views;

/// <summary>
/// The sidebar's agent queue, bound to a <see cref="CmdrQueueViewModel"/>. The code here turns a
/// press on an item (not on its trash button, which handles its own press) into the open command.
/// </summary>
public sealed partial class CmdrQueueView : UserControl, IDisposable
{
    /// <summary>The height of each row of an item: the header row and each line of its text.</summary>
    public const double RowHeight = 18;

    /// <summary>The padding around an item's rows.</summary>
    public static readonly Thickness ItemPadding = new(8, 5, 4, 5);

    public CmdrQueueView(IServiceProvider sp)
    {
        InitializeComponent();
        ViewModel = ActivatorUtilities.CreateInstance<CmdrQueueViewModel>(sp, ViewServices.Dispatcher(sp));
        DataContext = ViewModel;
    }

    internal CmdrQueueViewModel ViewModel { get; }

    /// <summary>The list of queued items.</summary>
    internal ListBox List => ItemsList;

    public void Dispose() => ViewModel.Dispose();

    private void OnItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || sender is not Control { DataContext: CmdrQueueItemViewModel row })
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        ViewModel.OpenCommand.Execute(row);
    }

    // The arrow keys move the selection; Enter opens the selected item, as a click would.
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
            return;
        if (ItemsList.SelectedItem is not CmdrQueueItemViewModel row)
            return;
        e.Handled = true;
        ViewModel.OpenCommand.Execute(row);
    }
}

/// <summary>
/// A wrapping <see cref="TextBlock"/> that ends its last allowed line with an ellipsis when the
/// text runs past <see cref="TextBlock.MaxLines"/>. A plain text block stops after its last line
/// and trims it only when that one line is too wide on its own, so text cut at a line break shows
/// no sign that it goes on. The full text stays in <see cref="TextBlock.Text"/>; only the layout
/// is shortened.
/// </summary>
public sealed class EllipsisTextBlock : TextBlock
{
    private const string Ellipsis = "…";

    // Styled as a TextBlock, so the text style classes apply.
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override TextLayout CreateTextLayout(string? text)
    {
        var layout = base.CreateTextLayout(text);
        if (string.IsNullOrEmpty(text) || MaxLines <= 0 || TextTrimming == TextTrimming.None || Fits(layout, text.Length))
            return layout;

        // The longest prefix that, with the ellipsis, still fits in the allowed lines.
        var shown = layout.TextLines.Sum(l => l.Length);
        layout.Dispose();
        TextLayout? best = null;
        int lo = 0, hi = Math.Min(shown, text.Length);
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            // Never cut between the halves of a surrogate pair (an emoji, say).
            var cut = mid < text.Length && char.IsLowSurrogate(text[mid]) ? mid - 1 : mid;
            var candidateText = text[..cut].TrimEnd() + Ellipsis;
            var candidate = base.CreateTextLayout(candidateText);
            if (Fits(candidate, candidateText.Length))
            {
                best?.Dispose();
                best = candidate;
                lo = mid;
            }
            else
            {
                candidate.Dispose();
                hi = mid - 1;
            }
        }
        return best ?? base.CreateTextLayout(Ellipsis);
    }

    private static bool Fits(TextLayout layout, int length) => layout.TextLines.Sum(l => l.Length) >= length;
}
