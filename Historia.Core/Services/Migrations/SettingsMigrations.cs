namespace Historia.Services.Migrations;

/// <summary>
/// v0 → v1 migration for <c>settings.json</c> (<c>Migrations/V0ToV1.cs</c>).
/// v0 is the legacy / first-run shape with no <c>schemaVersion</c> field. There are no field
/// renames yet, so this only stamps the version.
/// </summary>
public sealed class SettingsV0ToV1 : IJsonMigration
{
    public int FromVersion => 0;
    public int ToVersion => 1;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 1;
    }
}

/// <summary>
/// v1 → v2: adds the graph-panel column layout. Additive — an absent
/// <c>graphColumns</c> is left absent so the record default (<c>GraphColumnDefaults.All</c>)
/// applies on deserialize. Structural validation of a hand-edited list happens in
/// <c>SettingsService</c> after deserialize.
/// </summary>
public sealed class SettingsV1ToV2 : IJsonMigration
{
    public int FromVersion => 1;
    public int ToVersion => 2;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 2;
    }
}

/// <summary>v2 → v3: adds issue autolinks and the diff word-wrap flag. Additive —
/// absent keys fall through to the record defaults on deserialize.</summary>
public sealed class SettingsV2ToV3 : IJsonMigration
{
    public int FromVersion => 2;
    public int ToVersion => 3;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 3;
    }
}

/// <summary>
/// v3 → v4. Unlike its predecessors this rewrites three things:
/// <list type="number">
///   <item>sets <c>restoreSessionTabs</c> to <c>true</c> only when the key is absent — a user
///   who deliberately turned it off keeps their choice;</item>
///   <item>sets <c>visible: false</c> on the <c>author</c>, <c>date</c> and <c>sha</c> entries
///   of <c>graphColumns</c> — a one-time event, no "already applied" flag needed;</item>
///   <item>sets <c>maxRecentRepositories</c> to <c>30</c> when it is absent or still exactly
///   <c>20</c> (a default nobody chose).</item>
/// </list>
/// </summary>
public sealed class SettingsV3ToV4 : IJsonMigration
{
    public int FromVersion => 3;
    public int ToVersion => 4;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 4;

        if (document["restoreSessionTabs"] is null)
            document["restoreSessionTabs"] = true;

        var recents = document["maxRecentRepositories"]?.GetValue<int?>();
        if (recents is null or 20)
            document["maxRecentRepositories"] = 30;

        if (document["graphColumns"] is JsonArray columns)
        {
            foreach (var node in columns)
            {
                if (node is not JsonObject col)
                    continue;
                var kind = col["kind"]?.GetValue<string>();
                if (string.Equals(kind, "Author", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kind, "Date", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kind, "Sha", StringComparison.OrdinalIgnoreCase))
                {
                    col["visible"] = false;
                }
            }
        }
    }
}

/// <summary>v4 → v5: adds the multi-line tab-strip flag and its row cap. Additive —
/// absent keys fall through to the record defaults on deserialize, so this only stamps the
/// version.</summary>
public sealed class SettingsV4ToV5 : IJsonMigration
{
    public int FromVersion => 4;
    public int ToVersion => 5;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 5;
    }
}

/// <summary>v5 → v6: adds <c>persistGraphCache</c>. Additive — an absent key
/// falls through to the record default (<c>true</c>) on deserialize, so this only stamps the
/// version.</summary>
public sealed class SettingsV5ToV6 : IJsonMigration
{
    public int FromVersion => 5;
    public int ToVersion => 6;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 6;
    }
}

/// <summary>v6 → v7: adds <c>graphRowHardCap</c>. Additive — an absent key falls through
/// to the record default (200,000) on deserialize, so this only stamps the version.</summary>
public sealed class SettingsV6ToV7 : IJsonMigration
{
    public int FromVersion => 6;
    public int ToVersion => 7;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 7;
    }
}

/// <summary>v7 → v8: adds <c>persistRepoCache</c>. Additive — an absent key falls through to the
/// record default (<c>true</c>) on deserialize, so this only stamps the version.</summary>
public sealed class SettingsV7ToV8 : IJsonMigration
{
    public int FromVersion => 7;
    public int ToVersion => 8;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 8;
    }
}

/// <summary>v8 → v9: adds the terminal settings block. Additive — every new key has a
/// working record default, so this only stamps the version.</summary>
public sealed class SettingsV8ToV9 : IJsonMigration
{
    public int FromVersion => 8;
    public int ToVersion => 9;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 9;
    }
}

/// <summary>v9 → v10: adds <c>tabWidthMode</c> and <c>fixedTabWidth</c>. Additive — an
/// absent key falls through to the record default (<c>Dynamic</c> / 175) on deserialize, so
/// this only stamps the version.</summary>
public sealed class SettingsV9ToV10 : IJsonMigration
{
    public int FromVersion => 9;
    public int ToVersion => 10;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 10;
    }
}

/// <summary>v10 → v11: adds <c>customThemeId</c>. Additive — an absent key falls
/// through to the record default (<c>null</c>) on deserialize, so this only stamps the
/// version.</summary>
public sealed class SettingsV10ToV11 : IJsonMigration
{
    public int FromVersion => 10;
    public int ToVersion => 11;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 11;
    }
}

/// <summary>v11 → v12: adds <c>showSplashScreen</c>. Additive — an absent key falls through to
/// the record default (<c>true</c>) on deserialize, so this only stamps the version.</summary>
public sealed class SettingsV11ToV12 : IJsonMigration
{
    public int FromVersion => 11;
    public int ToVersion => 12;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 12;
    }
}

/// <summary>Generic migration for schema bumps where all new fields are additive and default properly on deserialization.</summary>
public class StampOnlyMigration(int from, int to) : IJsonMigration
{
    public int FromVersion => from;
    public int ToVersion => to;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = to;
    }
}

/// <summary>v12 → v13: adds <c>wheelScrollDirection</c>. Additive — an absent key falls
/// through to the record default (<c>Auto</c>) on deserialize, so this only stamps the
/// version.</summary>
public sealed class SettingsV12ToV13() : StampOnlyMigration(12, 13);

/// <summary>v13 → v14: adds <c>commitDetailFontSize</c> and <c>commitFilesView</c>.
/// Additive — absent keys fall through to the record defaults (14 / <c>Path</c>) on
/// deserialize, so this only stamps the version.</summary>
public sealed class SettingsV13ToV14() : StampOnlyMigration(13, 14);

/// <summary>v14 → v15: v9 profile/network rework — drops <c>networkMode</c> and
/// <c>enablePerRepoProfiles</c>, renames <c>primaryProfileId</c> → <c>defaultProfileId</c>.
/// v9 ships with a data wipe, so no field-level migration is attempted; this only
/// stamps the version for any settings file that survives.</summary>
public sealed class SettingsV14ToV15 : IJsonMigration
{
    public int FromVersion => 14;
    public int ToVersion => 15;

    public void Apply(JsonObject document)
    {
        if (document["primaryProfileId"] is { } pid)
            document["defaultProfileId"] = pid.DeepClone();
        document.Remove("primaryProfileId");
        document.Remove("enablePerRepoProfiles");
        document.Remove("networkMode");
        document["schemaVersion"] = 15;
    }
}

/// <summary>v15 → v16: adds <c>autoFetchEnabled</c>. Additive — an absent key falls
/// through to the record default (<c>true</c>) on deserialize, so this only stamps the
/// version.</summary>
public sealed class SettingsV15ToV16() : StampOnlyMigration(15, 16);

/// <summary>v16 → v17: lowers the default <c>silentCrawlRowCap</c> from 300,000 to 100,000.
/// Rewrites stored <c>300000</c> to <c>100000</c>; preserves any other user-chosen value.</summary>
public sealed class SettingsV16ToV17 : IJsonMigration
{
    public int FromVersion => 16;
    public int ToVersion => 17;

    public void Apply(JsonObject document)
    {
        if (document["silentCrawlRowCap"]?.GetValue<int?>() == 300_000)
            document["silentCrawlRowCap"] = 100_000;
        document["schemaVersion"] = 17;
    }
}

/// <summary>v17 → v18: adds <c>showAuthorDate</c>. Additive — an absent key falls
/// through to the record default (<c>false</c>) on deserialize, so this only stamps the
/// version.</summary>
public sealed class SettingsV17ToV18() : StampOnlyMigration(17, 18);

/// <summary>v18 → v19: removes <c>writeCommitGraphForLargeRepos</c> — every repository's
/// commit-graph is now always kept current.</summary>
public sealed class SettingsV18ToV19 : IJsonMigration
{
    public int FromVersion => 18;
    public int ToVersion => 19;

    public void Apply(JsonObject document)
    {
        document.Remove("writeCommitGraphForLargeRepos");
        document["schemaVersion"] = 19;
    }
}

/// <summary>v19 → v20: removes <c>tabWidth</c>, <c>terminalBellStyle</c>,
/// <c>enableBackgroundPolling</c> and <c>terminalDockOpen</c> — none of them had a reader.</summary>
public sealed class SettingsV19ToV20 : IJsonMigration
{
    public int FromVersion => 19;
    public int ToVersion => 20;

    public void Apply(JsonObject document)
    {
        document.Remove("tabWidth");
        document.Remove("terminalBellStyle");
        document.Remove("enableBackgroundPolling");
        document.Remove("terminalDockOpen");
        document["schemaVersion"] = 20;
    }
}

/// <summary>v20 → v21: a custom theme is an override of Light / Dark / System, switched on by
/// <c>useCustomTheme</c>, not a fourth <c>appTheme</c>. A stored <c>appTheme</c> of
/// <c>custom</c> (any casing) becomes <c>system</c> with <c>useCustomTheme</c> on;
/// <c>customThemeId</c> and every other value are kept.</summary>
public sealed class SettingsV20ToV21 : IJsonMigration
{
    public int FromVersion => 20;
    public int ToVersion => 21;

    public void Apply(JsonObject document)
    {
        if (document["appTheme"] is JsonValue theme
            && theme.TryGetValue<string>(out var name)
            && string.Equals(name, "custom", StringComparison.OrdinalIgnoreCase))
        {
            document["appTheme"] = "system";
            document["useCustomTheme"] = true;
        }
        document["schemaVersion"] = 21;
    }
}

/// <summary>v21 → v22: raises the defaults of <c>silentCrawlRowCap</c> (100,000 → 500,000) and
/// <c>residentRowCap</c> (2,000,000 → 2,500,000). Rewrites a stored old default to the new one;
/// preserves any other user-chosen value.</summary>
public sealed class SettingsV21ToV22 : IJsonMigration
{
    public int FromVersion => 21;
    public int ToVersion => 22;

    public void Apply(JsonObject document)
    {
        if (HasInt(document, "silentCrawlRowCap", 100_000))
            document["silentCrawlRowCap"] = 500_000;
        if (HasInt(document, "residentRowCap", 2_000_000))
            document["residentRowCap"] = 2_500_000;
        document["schemaVersion"] = 22;
    }

    // TryGetValue, not GetValue: a hand-edited string, fraction or out-of-int-range value must
    // fall through to deserialization (which resets to defaults) rather than throw here.
    private static bool HasInt(JsonObject document, string key, int value) =>
        document[key] is JsonValue v && v.TryGetValue<int>(out var n) && n == value;
}

/// <summary>v22 → v23: adds <c>externalControlEnabled</c>. Additive — an absent value keeps the
/// record default (on), so existing settings files gain the setting without an edit.</summary>
public sealed class SettingsV22ToV23 : IJsonMigration
{
    public int FromVersion => 22;
    public int ToVersion => 23;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 23;
    }
}

/// <summary>v23 → v24: adds <c>cmdrQueueExpiryHours</c> and <c>cmdrQueueNewestFirst</c>. Additive —
/// absent values keep the record defaults (5 hours, oldest first).</summary>
public sealed class SettingsV23ToV24 : IJsonMigration
{
    public int FromVersion => 23;
    public int ToVersion => 24;

    public void Apply(JsonObject document)
    {
        document["schemaVersion"] = 24;
    }
}


