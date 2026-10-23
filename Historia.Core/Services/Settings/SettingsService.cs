namespace Historia.Services.Settings;

/// <inheritdoc cref="ISettingsService"/>
public sealed class SettingsService : ISettingsService, IAsyncDisposable
{
    private const int CurrentSchemaVersion = 24;

    private static readonly IJsonMigration[] Migrations =
        [new SettingsV0ToV1(), new SettingsV1ToV2(), new SettingsV2ToV3(), new SettingsV3ToV4(), new SettingsV4ToV5(), new SettingsV5ToV6(), new SettingsV6ToV7(), new SettingsV7ToV8(), new SettingsV8ToV9(), new SettingsV9ToV10(), new SettingsV10ToV11(), new SettingsV11ToV12(), new SettingsV12ToV13(), new SettingsV13ToV14(), new SettingsV14ToV15(), new SettingsV15ToV16(), new SettingsV16ToV17(), new SettingsV17ToV18(), new SettingsV18ToV19(), new SettingsV19ToV20(), new SettingsV20ToV21(), new SettingsV21ToV22(), new SettingsV22ToV23(), new SettingsV23ToV24()];

    private readonly AtomicFileWriter _writer;
    private readonly object _sync = new();
    private AppSettings _current;
    private long _persistSequence;

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    private SettingsService(AppSettings initial, AtomicFileWriter writer)
    {
        _current = initial;
        _writer = writer;
    }

    public AppSettings Current
    {
        get { lock (_sync) return _current; }
    }

    /// <summary>
    /// Synchronous load, called once before the window is shown. <c>settings.json</c>
    /// is small; this is the one permitted blocking read. A missing or corrupt file yields
    /// defaults without throwing.
    /// </summary>
    public static SettingsService Load()
    {
        AppPaths.EnsureCreated();
        var path = AppPaths.SettingsFile;
        var writer = new AtomicFileWriter(path);

        AppSettings settings;
        var needsRewrite = false;

        if (!File.Exists(path))
        {
            settings = new AppSettings();
            needsRewrite = true;
        }
        else
        {
            try
            {
                var raw = File.ReadAllText(path);
                var node = JsonNode.Parse(raw) as JsonObject ?? new JsonObject();

                if (node["schemaVersion"]?.GetValue<int>() > CurrentSchemaVersion)
                {
                    // Newer than this app: keep the on-disk file untouched, run with what we can read.
                    settings = Deserialize(node) ?? new AppSettings();
                }
                else
                {
                    needsRewrite = MigrationRunner.Run(node, CurrentSchemaVersion, Migrations);
                    settings = Deserialize(node) ?? new AppSettings();
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                settings = new AppSettings();
                needsRewrite = true;
            }
        }

        settings = settings with { SchemaVersion = CurrentSchemaVersion };
        var service = new SettingsService(settings, writer);

        if (needsRewrite)
        {
            try { service.PersistNow(); }
            catch { /* non-fatal at startup */ }
        }

        return service;
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        AppSettings oldSettings;
        AppSettings newSettings;
        long sequence;
        lock (_sync)
        {
            oldSettings = _current;
            newSettings = (mutate(oldSettings) with { SchemaVersion = CurrentSchemaVersion });
            if (newSettings == oldSettings)
                return;
            _current = newSettings;
            sequence = ++_persistSequence;
        }

        _writer.QueueWrite(Serialize(newSettings), sequence);
        Changed?.Invoke(this, new SettingsChangedEventArgs(oldSettings, newSettings));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken ct = default) => _writer.FlushAsync(ct);

    private void PersistNow() => _writer.WriteNowAsync(Serialize(Current)).GetAwaiter().GetResult();

    private static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, HistoriaJson.Options);

    private static AppSettings? Deserialize(JsonObject node) =>
        node.Deserialize<AppSettings>(HistoriaJson.Options);

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
