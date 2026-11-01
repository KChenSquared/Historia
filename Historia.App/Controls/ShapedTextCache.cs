namespace Historia.Controls;

/// <summary>
/// A bounded least-recently-used cache of shaped text. Render paths draw the
/// <see cref="TextLayout"/>s it returns and never draw <see cref="FormattedText"/>.
/// <para>
/// Trap: caching <see cref="FormattedText"/> saves the allocation but not the work. Its
/// <c>Draw</c> formats (shapes) every line again on every call, and collapses a trimmed line again,
/// so a cache of them still re-shapes every visible string every frame. A
/// <see cref="TextLayout"/> shapes once, in its constructor, and its <c>Draw</c> only draws the
/// stored lines.
/// </para>
/// <para>
/// The key holds every glyph-affecting input: text, typeface, size, foreground brush (by
/// reference), max width, trimming and max lines. At capacity the least recently used layout is
/// evicted and disposed; the cache is never cleared wholesale on overflow, which would re-shape
/// everything at once. Owners call <see cref="Clear"/> (which disposes) on a palette, theme,
/// font or size change and on detach. Disposing a layout after it has been drawn is safe: the
/// recorded draw list holds its own reference to each glyph run.
/// </para>
/// <para>Not thread-safe: UI thread only.</para>
/// </summary>
public sealed class ShapedTextCache
{
    public const int DefaultCapacity = 2048;

    private readonly record struct Key(
        string Text, Typeface Typeface, double FontSize, IBrush? Foreground,
        double MaxWidth, TextTrimming? Trimming, int MaxLines);

    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(Key a, Key b) =>
            string.Equals(a.Text, b.Text, StringComparison.Ordinal)
            && a.Typeface.Equals(b.Typeface)
#pragma warning disable S1244 // a cache key needs exact identity; a tolerance would break the hash contract
            && a.FontSize.Equals(b.FontSize)
#pragma warning restore S1244
            && ReferenceEquals(a.Foreground, b.Foreground)
#pragma warning disable S1244 // a cache key needs exact identity; a tolerance would break the hash contract
            && a.MaxWidth.Equals(b.MaxWidth)
#pragma warning restore S1244
            && ReferenceEquals(a.Trimming, b.Trimming)
            && a.MaxLines == b.MaxLines;

        public int GetHashCode(Key k) => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(k.Text), k.Typeface, k.FontSize,
            ReferenceEqualityComparer.Instance.GetHashCode(k.Foreground),
            k.MaxWidth, ReferenceEqualityComparer.Instance.GetHashCode(k.Trimming), k.MaxLines);
    }

    private readonly Dictionary<Key, LinkedListNode<(Key Key, TextLayout Layout)>> _map = new(KeyComparer.Instance);
    private readonly LinkedList<(Key Key, TextLayout Layout)> _lru = new();

    public ShapedTextCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => _map.Count;

    /// <summary>Lookups answered from the cache since construction or <see cref="ResetCounters"/>.</summary>
    public int Hits { get; private set; }

    /// <summary>Lookups that shaped a new layout since construction or <see cref="ResetCounters"/>.</summary>
    public int Misses { get; private set; }

    /// <summary>Runs after each layout the cache disposes, on eviction or clear.</summary>
    internal Action<TextLayout>? DisposedForTesting { get; set; }

    /// <summary>
    /// Returns the cached layout for these inputs, shaping and caching one on a miss. The caller
    /// must not dispose it or keep it past the next <see cref="Clear"/>.
    /// </summary>
    public TextLayout Get(
        string text, Typeface typeface, double fontSize, IBrush? foreground,
        double maxWidth = double.PositiveInfinity, TextTrimming? trimming = null, int maxLines = 0)
    {
        var key = new Key(text, typeface, fontSize, foreground, maxWidth, trimming, maxLines);
        if (_map.TryGetValue(key, out var node))
        {
            Hits++;
            if (node != _lru.First)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }
            return node.Value.Layout;
        }

        Misses++;
        var layout = new TextLayout(text, typeface, fontSize, foreground,
            textTrimming: trimming, maxWidth: maxWidth, maxLines: maxLines);

        if (_map.Count >= Capacity)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
            DisposeLayout(last.Value.Layout);
        }

        _map[key] = _lru.AddFirst((key, layout));
        return layout;
    }

    /// <summary>Disposes and drops every cached layout. The counters are kept.</summary>
    public void Clear()
    {
        if (_map.Count == 0)
        {
            return;
        }

        var layouts = new List<TextLayout>(_map.Count);
        foreach (var entry in _lru)
        {
            layouts.Add(entry.Layout);
        }

        _map.Clear();
        _lru.Clear();
        foreach (var layout in layouts)
        {
            DisposeLayout(layout);
        }
    }

    public void ResetCounters()
    {
        Hits = 0;
        Misses = 0;
    }

    private void DisposeLayout(TextLayout layout)
    {
        layout.Dispose();
        DisposedForTesting?.Invoke(layout);
    }
}
