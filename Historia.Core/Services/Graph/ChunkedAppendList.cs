namespace Historia.Services.Graph;

/// <summary>
/// An append-only list backed by fixed-size chunks
/// (16,384 elements each) instead of one contiguous, doubling array. A chunk, once allocated, is
/// never resized or moved — at 16,384 elements a <c>GraphRow</c> chunk is a few hundred KB,
/// comfortably off the large object heap, and growing the list never needs the huge copy
/// <see cref="List{T}"/>'s doubling strategy eventually pays. This is what lets
/// <c>GraphLayoutState.Rows</c> grow to millions of entries across many small Extend calls
/// without <c>DagLayoutEngine.Build</c> having to copy the WHOLE accumulator into a fresh array on
/// every single call (quadratic allocation in the number of extends; ~100 MB
/// per extend at 2M rows, worse at higher <c>ResidentRowCap</c> settings).
/// <para>
/// <b>Concurrency contract:</b> <see cref="Add"/> is single-writer — every caller already holds
/// <c>lock (GraphLayoutState)</c> around the whole Consume/Build pass that appends rows (see
/// <c>GraphLayoutService.ExtendLayoutAsync</c>/<c>ComputeLayoutAsync</c>'s own locking of
/// <c>existing.State</c>). <see cref="Count"/> and the indexer may be called from ANY thread at
/// ANY time with NO lock — the render thread reads a snapshot's rows while a background Extend may
/// be appending to this SAME instance. Safety comes from ordering, not from a lock:
/// </para>
/// <list type="number">
/// <item>A grown chunk directory is published via <c>Volatile.Write</c> strictly BEFORE the new
/// item is written into it.</item>
/// <item><see cref="Count"/>'s backing field is advanced, also via <c>Volatile.Write</c>, only
/// AFTER that item write completes.</item>
/// </list>
/// <para>
/// A reader that observes <c>Count &gt; index</c> (through a <c>Volatile.Read</c> of <see
/// cref="Count"/>) is therefore guaranteed to see both a fully-published chunk directory and a
/// fully-written item
/// at that index: .NET's release semantics on the writer's two <c>Volatile.Write</c> calls and
/// acquire semantics on the reader's <c>Volatile.Read</c> of <see cref="Count"/> chain together —
/// this is the standard "publish the data, then publish the count" idiom other lock-free
/// append-only structures use. A chunk slot, once written, is never written again (<see
/// cref="Add"/> only ever writes the NEXT slot), so there is also no risk of a reader observing a
/// half-updated existing item.
/// </para>
/// </summary>
internal sealed class ChunkedAppendList<T>
{
    internal const int ChunkSize = 16_384;

    private T[][] _chunks = [];
    private int _count;

    /// <summary>Safe to call from any thread, without a lock, concurrently with <see cref="Add"/>.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Safe to call from any thread, without a lock, concurrently with <see cref="Add"/>,
    /// for any <paramref name="index"/> less than a <see cref="Count"/> this same caller already
    /// observed (see the type's concurrency contract above).</summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            var chunks = Volatile.Read(ref _chunks);
            return chunks[index / ChunkSize][index % ChunkSize];
        }
    }

    /// <summary>Single-writer only — the caller must already hold whatever lock serializes every
    /// mutation of the owning <c>GraphLayoutState</c> (see the type's concurrency contract above).</summary>
    public void Add(T item)
    {
        var index = _count;
        var chunkIndex = index / ChunkSize;
        var offset = index % ChunkSize;

        var chunks = _chunks;
        if (chunkIndex >= chunks.Length)
        {
            var grown = new T[chunkIndex + 1][];
            Array.Copy(chunks, grown, chunks.Length);
            for (var i = chunks.Length; i < grown.Length; i++)
                grown[i] = new T[ChunkSize];
            chunks = grown;
            Volatile.Write(ref _chunks, chunks); // publish the grown directory before the item write below
        }

        chunks[chunkIndex][offset] = item;
        Volatile.Write(ref _count, index + 1); // publish the item before any reader can see the new count
    }

    /// <summary>Copies every element into a fresh array. Called once per full Compute, never on
    /// the incremental path: <c>DagLayoutEngine.Build</c> reads the list in place, because a copy
    /// per extend would make extends quadratic.</summary>
    public T[] ToArray()
    {
        var count = Count;
        var result = new T[count];
        for (var i = 0; i < count; i++)
            result[i] = this[i];
        return result;
    }
}
