namespace Historia.Services.CommandLine;

/// <inheritdoc cref="ICmdrQueueService"/>
/// <remarks>
/// Backed by <c>cmdr-queue.json</c> in the machine directory. The file is read once, in the
/// constructor, which also removes items older than the expiry setting; nothing expires while
/// Historia runs. A missing file is an empty list; an unreadable, corrupt or newer-schema file is
/// logged and treated as empty, and stays on disk until the next change replaces it.
/// </remarks>
public sealed class CmdrQueueService : ICmdrQueueService, IAsyncDisposable
{
    /// <summary>The most items kept; enqueuing beyond it drops the oldest.</summary>
    public const int MaxItems = 200;

    private const int FileVersion = 1;
    private const string LogCategory = "CmdrQueue";

    private sealed record QueueFile
    {
        public int SchemaVersion { get; init; } = FileVersion;
        public List<CmdrQueueItem> Items { get; init; } = [];
    }

    private readonly IUiDispatcher _dispatcher;
    private readonly IAppLog _log;
    private readonly AtomicFileWriter _writer;
    private readonly object _sync = new();
    private readonly List<CmdrQueueItem> _items;
    private long _persistSequence;

    public event EventHandler? Changed;

    public CmdrQueueService(
        ISettingsService settings,
        IUiDispatcher dispatcher,
        IAppLog log,
        TimeProvider? time = null,
        string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        var path = filePath ?? AppPaths.CmdrQueueFile;
        _writer = new AtomicFileWriter(path);
        _items = Load(path);

        var hours = AppSettings.ClampCmdrQueueExpiryHours(settings.Current.CmdrQueueExpiryHours);
        var cutoff = (time ?? TimeProvider.System).GetUtcNow() - TimeSpan.FromHours(hours);
        var purged = _items.RemoveAll(i => i.CreatedAt < cutoff);
        if (purged > 0)
        {
            _log.Info(LogCategory, $"removed {purged} item(s) older than {hours} h");
            Persist();
        }
    }

    public IReadOnlyList<CmdrQueueItem> Items
    {
        get { lock (_sync) return _items.ToArray(); }
    }

    public int UnseenCount
    {
        get { lock (_sync) return _items.Count(i => !i.Seen); }
    }

    public void Enqueue(CmdrQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_sync)
        {
            _items.Add(item);
            if (_items.Count > MaxItems)
                _items.RemoveRange(0, _items.Count - MaxItems);
        }
        Persist();
        RaiseChanged();
    }

    public void MarkSeen(string id)
    {
        lock (_sync)
        {
            var index = _items.FindIndex(i => i.Id == id);
            if (index < 0 || _items[index].Seen)
                return;
            _items[index] = _items[index] with { Seen = true };
        }
        Persist();
        RaiseChanged();
    }

    public void Remove(string id)
    {
        lock (_sync)
        {
            if (_items.RemoveAll(i => i.Id == id) == 0)
                return;
        }
        Persist();
        RaiseChanged();
    }

    private List<CmdrQueueItem> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node is null)
            {
                _log.Warn(LogCategory, "the queue file is not a JSON object; starting empty");
                return [];
            }
            var version = node["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
            if (version > FileVersion)
            {
                _log.Warn(LogCategory, $"the queue file has schema {version}, newer than {FileVersion}; starting empty");
                return [];
            }
            var file = node.Deserialize<QueueFile>(HistoriaJson.Options);
            // A hand-edited null list would break every reader; an item without an id cannot be
            // marked or removed.
            return file?.Items
                .Where(i => i is not null && !string.IsNullOrEmpty(i.Id))
                .Select(i => i with { Paths = i.Paths ?? [], StagedPaths = i.StagedPaths ?? [] })
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Warn(LogCategory, "the queue file could not be read; starting empty", ex);
            return [];
        }
    }

    /// <summary>Takes the snapshot and its sequence number under the lock, then serializes and
    /// queues the write after releasing it.</summary>
    private void Persist()
    {
        QueueFile file;
        long sequence;
        lock (_sync)
        {
            file = new QueueFile { Items = _items.ToList() };
            sequence = ++_persistSequence;
        }
        _writer.QueueWrite(JsonSerializer.Serialize(file, HistoriaJson.Options), sequence);
    }

    private void RaiseChanged() => _dispatcher.Post(() => Changed?.Invoke(this, EventArgs.Empty));

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
