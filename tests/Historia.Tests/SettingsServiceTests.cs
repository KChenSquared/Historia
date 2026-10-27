namespace Historia.Tests;

[Trait("tier", "unit")]

public class SettingsServiceTests : IDisposable
{
    private readonly StateDir _state = new();

    [Fact]
    public void Load_returns_defaults_when_no_file_exists()
    {
        var svc = SettingsService.Load();
        Assert.Equal(ThemeMode.System, svc.Current.AppTheme);
        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.False(svc.Current.ShowAuthorDate);
        Assert.False(svc.Current.EnableMultiLineTabs);
        Assert.Equal(3, svc.Current.MaxTabRows);
        Assert.True(svc.Current.PersistRepoCache);
        Assert.True(svc.Current.GraphMetadataFromDisk); // default on
        Assert.True(svc.Current.AutoFetchEnabled); // default on
        Assert.True(svc.Current.LoggingEnabled); // default on
        Assert.False(svc.Current.DeveloperPanelEnabled); // default off
        Assert.False(svc.Current.EnableGraphSplice); // Default off
        Assert.Equal(DiffViewMode.Unified, svc.Current.DefaultDiffView);
    }

    [Fact]
    public void Logging_and_developer_panel_settings_defaults()
    {
        var settings = new AppSettings();
        Assert.True(settings.LoggingEnabled);
        Assert.False(settings.DeveloperPanelEnabled);
    }

    [Fact]
    public async Task Logging_and_developer_panel_settings_round_trip()
    {
        var svc = SettingsService.Load();
        Assert.True(svc.Current.LoggingEnabled);
        Assert.False(svc.Current.DeveloperPanelEnabled);

        await svc.UpdateAsync(s => s with { LoggingEnabled = false, DeveloperPanelEnabled = true });
        await svc.FlushAsync();

        var reloaded = SettingsService.Load().Current;
        Assert.False(reloaded.LoggingEnabled);
        Assert.True(reloaded.DeveloperPanelEnabled);
    }

    [Fact]
    public void Default_diff_view_is_unified_for_a_fresh_install()
    {
        var svc = SettingsService.Load();
        Assert.Equal(DiffViewMode.Unified, svc.Current.DefaultDiffView);
    }

    [Fact]
    public void Fetch_after_pull_defaults_to_on()
    {
        Assert.True(new AppSettings().FetchAfterPull);
        Assert.True(SettingsService.Load().Current.FetchAfterPull);
    }

    [Fact]
    public void A_settings_file_without_fetchAfterPull_reads_it_as_on()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 24, \"pullWithRebase\": true }");

        var settings = SettingsService.Load().Current;

        Assert.True(settings.PullWithRebase);
        Assert.True(settings.FetchAfterPull);
    }

    [Fact]
    public async Task Fetch_after_pull_round_trips()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { FetchAfterPull = false });
        await svc.FlushAsync();

        Assert.False(SettingsService.Load().Current.FetchAfterPull);

        await svc.UpdateAsync(s => s with { FetchAfterPull = true });
        await svc.FlushAsync();

        Assert.True(SettingsService.Load().Current.FetchAfterPull);
    }

    [Fact]
    public async Task Load_ignores_removed_commitDetailFontSize_key()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 18, \"appTheme\": \"dark\", \"commitDetailFontSize\": 20, \"diffContextLines\": 8 }");

        var svc = SettingsService.Load();

        Assert.Equal(ThemeMode.Dark, svc.Current.AppTheme);
        Assert.Equal(8, svc.Current.DiffContextLines);

        await svc.UpdateAsync(s => s with { DiffContextLines = 6 });
        await svc.FlushAsync();

        var json = File.ReadAllText(AppPaths.SettingsFile);
        Assert.DoesNotContain("commitDetailFontSize", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"diffContextLines\": 6", json);
    }

    // ── v18 → v19 (commit-graph setting removed) ──

    [Fact]
    public void V18_file_drops_the_commit_graph_setting_and_keeps_every_other_value()
    {
        File.WriteAllText(AppPaths.SettingsFile,
            "{ \"schemaVersion\": 18, \"appTheme\": \"dark\", \"writeCommitGraphForLargeRepos\": false, \"diffContextLines\": 6, \"autoFetchEnabled\": false, \"showAuthorDate\": true }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(ThemeMode.Dark, svc.Current.AppTheme);
        Assert.Equal(6, svc.Current.DiffContextLines);
        Assert.False(svc.Current.AutoFetchEnabled);
        Assert.True(svc.Current.ShowAuthorDate);
        var onDisk = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!.AsObject();
        Assert.Equal(24, onDisk["schemaVersion"]!.GetValue<int>());
        Assert.False(onDisk.ContainsKey("writeCommitGraphForLargeRepos"));
        Assert.Equal(6, onDisk["diffContextLines"]!.GetValue<int>());
        Assert.False(onDisk["autoFetchEnabled"]!.GetValue<bool>());
    }

    // ── v19 → v20 (four unread settings removed) ──

    [Fact]
    public void V19_file_drops_the_four_unused_settings_and_keeps_every_other_value()
    {
        File.WriteAllText(AppPaths.SettingsFile,
            "{ \"schemaVersion\": 19, \"appTheme\": \"dark\", \"tabWidth\": 8, \"terminalBellStyle\": \"visual\", \"enableBackgroundPolling\": false, \"terminalDockOpen\": true, \"diffContextLines\": 7, \"autoFetchEnabled\": false, \"showAuthorDate\": true }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(ThemeMode.Dark, svc.Current.AppTheme);
        Assert.Equal(7, svc.Current.DiffContextLines);
        Assert.False(svc.Current.AutoFetchEnabled);
        Assert.True(svc.Current.ShowAuthorDate);
        var onDisk = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!.AsObject();
        Assert.Equal(24, onDisk["schemaVersion"]!.GetValue<int>());
        Assert.False(onDisk.ContainsKey("tabWidth"));
        Assert.False(onDisk.ContainsKey("terminalBellStyle"));
        Assert.False(onDisk.ContainsKey("enableBackgroundPolling"));
        Assert.False(onDisk.ContainsKey("terminalDockOpen"));
        Assert.Equal("dark", onDisk["appTheme"]!.GetValue<string>());
        Assert.Equal(7, onDisk["diffContextLines"]!.GetValue<int>());
        Assert.False(onDisk["autoFetchEnabled"]!.GetValue<bool>());
        Assert.True(onDisk["showAuthorDate"]!.GetValue<bool>());
    }

    // ── v20 → v21 (custom theme becomes an override of Light / Dark / System) ──

    [Theory]
    [InlineData("custom")]
    [InlineData("Custom")]
    public void V20_custom_mode_migrates_to_system_with_the_custom_theme_enabled(string stored)
    {
        File.WriteAllText(AppPaths.SettingsFile,
            $"{{ \"schemaVersion\": 20, \"appTheme\": \"{stored}\", \"customThemeId\": \"theme-1\", \"diffContextLines\": 7, \"showAuthorDate\": true }}");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(ThemeMode.System, svc.Current.AppTheme);
        Assert.True(svc.Current.UseCustomTheme);
        Assert.Equal("theme-1", svc.Current.CustomThemeId);
        Assert.Equal(7, svc.Current.DiffContextLines);
        Assert.True(svc.Current.ShowAuthorDate);
        var onDisk = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!.AsObject();
        Assert.Equal(24, onDisk["schemaVersion"]!.GetValue<int>());
        Assert.Equal("system", onDisk["appTheme"]!.GetValue<string>());
        Assert.True(onDisk["useCustomTheme"]!.GetValue<bool>());
        Assert.Equal("theme-1", onDisk["customThemeId"]!.GetValue<string>());
    }

    [Fact]
    public void V20_dark_mode_migrates_unchanged_with_the_custom_theme_off()
    {
        File.WriteAllText(AppPaths.SettingsFile,
            "{ \"schemaVersion\": 20, \"appTheme\": \"dark\", \"customThemeId\": \"theme-1\", \"diffContextLines\": 7 }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(ThemeMode.Dark, svc.Current.AppTheme);
        Assert.False(svc.Current.UseCustomTheme);
        Assert.Equal("theme-1", svc.Current.CustomThemeId);
        Assert.Equal(7, svc.Current.DiffContextLines);
        var onDisk = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!.AsObject();
        Assert.Equal("dark", onDisk["appTheme"]!.GetValue<string>());
        Assert.False(onDisk["useCustomTheme"]!.GetValue<bool>());
    }

    [Fact]
    public void WithoutDevOnlyOptions_clears_the_custom_theme_flag_and_keeps_the_id()
    {
        var settings = new AppSettings { AppTheme = ThemeMode.Light, UseCustomTheme = true, CustomThemeId = "theme-1", DiffContextLines = 9 };

        var cleared = settings.WithoutDevOnlyOptions();

        Assert.False(cleared.UseCustomTheme);
        Assert.Equal("theme-1", cleared.CustomThemeId);
        Assert.Equal(settings with { UseCustomTheme = false }, cleared);
    }

    // ── v15 → v16 (auto-fetch toggle) ──

    [Fact]
    public void V15_file_loads_with_auto_fetch_enabled_by_default()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 15, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.True(svc.Current.AutoFetchEnabled);
    }

    [Fact]
    public async Task Auto_fetch_toggle_round_trips()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { AutoFetchEnabled = false });
        await svc.FlushAsync();

        Assert.False(SettingsService.Load().Current.AutoFetchEnabled);
    }

    [Fact]
    public void V5_file_without_persist_repo_cache_migrates_to_the_default()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 5, \"appTheme\": \"dark\" }");
        var svc = SettingsService.Load();
        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.True(svc.Current.PersistRepoCache);
    }

    [Fact]
    public async Task UpdateAsync_mutates_persists_and_raises_changed()
    {
        var svc = SettingsService.Load();
        SettingsChangedEventArgs? seen = null;
        svc.Changed += (_, e) => seen = e;

        await svc.UpdateAsync(s => s with { AppTheme = ThemeMode.Light, DiffContextLines = 2 });
        await svc.FlushAsync();

        Assert.Equal(ThemeMode.Light, svc.Current.AppTheme);
        Assert.NotNull(seen);
        Assert.Equal(ThemeMode.Light, seen!.NewSettings.AppTheme);

        var reloaded = SettingsService.Load();
        Assert.Equal(ThemeMode.Light, reloaded.Current.AppTheme);
        Assert.Equal(2, reloaded.Current.DiffContextLines);
    }

    [Fact]
    public async Task Multi_line_tab_settings_round_trip()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { EnableMultiLineTabs = true, MaxTabRows = 4 });
        await svc.FlushAsync();

        var reloaded = SettingsService.Load();
        Assert.True(reloaded.Current.EnableMultiLineTabs);
        Assert.Equal(4, reloaded.Current.MaxTabRows);
    }

    [Fact]
    public void Legacy_file_without_schema_version_is_migrated_to_current()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"appTheme\": \"light\" }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(ThemeMode.Light, svc.Current.AppTheme);
        var onDisk = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!;
        Assert.Equal(24, onDisk["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void V1_file_migrates_forward()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 1, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(ThemeMode.Dark, svc.Current.AppTheme);
    }

    // ── v3 → v4 ──

    [Fact]
    public void V3_to_v4_turns_on_session_restore_and_bumps_recents_when_absent()
    {
        File.WriteAllText(AppPaths.SettingsFile, """
            {
              "schemaVersion": 3,
              "graphColumns": [
                { "kind": "refs", "width": 180, "visible": true },
                { "kind": "graph", "width": 220, "visible": true },
                { "kind": "message", "width": 400, "visible": true, "isFlex": true },
                { "kind": "author", "width": 150, "visible": true },
                { "kind": "date", "width": 110, "visible": true },
                { "kind": "sha", "width": 80, "visible": true }
              ]
            }
            """);

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.True(svc.Current.RestoreSessionTabs);
        Assert.Equal(30, svc.Current.MaxRecentRepositories);
    }

    [Fact]
    public void V3_to_v4_leaves_an_explicit_restore_session_false_alone()
    {
        File.WriteAllText(AppPaths.SettingsFile,
            "{ \"schemaVersion\": 3, \"restoreSessionTabs\": false, \"maxRecentRepositories\": 12 }");

        var svc = SettingsService.Load();

        Assert.False(svc.Current.RestoreSessionTabs);
        Assert.Equal(12, svc.Current.MaxRecentRepositories); // not 20, so left as-is
    }

    [Fact]
    public void Newer_schema_version_is_loaded_read_only_without_rewrite()
    {
        var future = "{ \"schemaVersion\": 999, \"appTheme\": \"light\" }";
        File.WriteAllText(AppPaths.SettingsFile, future);

        var svc = SettingsService.Load();

        Assert.Equal(ThemeMode.Light, svc.Current.AppTheme);
        Assert.Contains("999", File.ReadAllText(AppPaths.SettingsFile)); // untouched
    }

    // ── v8 → v9 (terminal) ──

    [Fact]
    public void V8_file_loads_with_terminal_defaults()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 8, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Null(svc.Current.TerminalShellPath);
        Assert.Empty(svc.Current.TerminalShellArgs);
        Assert.Equal(13, svc.Current.TerminalFontSize);
        Assert.Equal(5000, svc.Current.TerminalScrollbackLines);
        Assert.Equal(TerminalCursorStyle.Block, svc.Current.TerminalCursorStyle);
        Assert.True(svc.Current.TerminalCursorBlink);
        Assert.Equal(260, svc.Current.TerminalDockHeight);
    }

    // ── Kitty keyboard / OSC 52 write toggles ──

    [Fact]
    public void TerminalProtocolSettings_DefaultOn_WhenAbsentFromFile()
    {
        // AppSettings has a parameterless constructor and init setters, so System.Text.Json
        // leaves a missing property at its initializer.
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 19, \"appTheme\": \"dark\", \"terminalFontSize\": 15 }");

        var svc = SettingsService.Load();

        Assert.Equal(15, svc.Current.TerminalFontSize);
        Assert.True(svc.Current.TerminalKittyKeyboard);
        Assert.True(svc.Current.TerminalAllowClipboardWrite);
    }

    [Fact]
    public async Task TerminalProtocolSettings_RoundTrip()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { TerminalKittyKeyboard = false, TerminalAllowClipboardWrite = false });
        await svc.FlushAsync();

        var reloaded = SettingsService.Load().Current;
        Assert.False(reloaded.TerminalKittyKeyboard);
        Assert.False(reloaded.TerminalAllowClipboardWrite);
    }

    [Fact]
    public async Task Schema_v9_terminal_settings_round_trip()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with
        {
            TerminalShellPath = "/usr/bin/fish",
            TerminalShellArgs = ["-i"],
            TerminalFontFamily = "Cascadia Mono",
            TerminalFontSize = 15,
            TerminalScrollbackLines = 12_000,
            TerminalDockHeight = 320,
            TerminalCursorStyle = TerminalCursorStyle.Bar,
            TerminalCursorBlink = false,
        });
        await svc.FlushAsync();

        var reloaded = SettingsService.Load().Current;
        Assert.Equal("/usr/bin/fish", reloaded.TerminalShellPath);
        Assert.Equal(["-i"], reloaded.TerminalShellArgs);
        Assert.Equal("Cascadia Mono", reloaded.TerminalFontFamily);
        Assert.Equal(15, reloaded.TerminalFontSize);
        Assert.Equal(12_000, reloaded.TerminalScrollbackLines);
        Assert.Equal(320, reloaded.TerminalDockHeight);
        Assert.Equal(TerminalCursorStyle.Bar, reloaded.TerminalCursorStyle);
        Assert.False(reloaded.TerminalCursorBlink);
    }

    [Fact]
    public void ClampTerminalScrollback_bounds()
    {
        Assert.Equal(AppSettings.MinTerminalScrollback, AppSettings.ClampTerminalScrollback(0));
        Assert.Equal(AppSettings.MaxTerminalScrollback, AppSettings.ClampTerminalScrollback(int.MaxValue));
        Assert.Equal(9000, AppSettings.ClampTerminalScrollback(9000));
    }

    // ── Tab-width mode ──

    [Fact]
    public void V9_file_loads_with_dynamic_tab_width_default()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 9, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(TabWidthMode.Dynamic, svc.Current.TabWidthMode);
        Assert.Equal(AppSettings.DefaultFixedTabWidth, svc.Current.FixedTabWidth);
    }

    [Fact]
    public async Task Schema_v10_tab_width_settings_round_trip()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { TabWidthMode = TabWidthMode.Fixed, FixedTabWidth = 240 });
        await svc.FlushAsync();

        var reloaded = SettingsService.Load().Current;
        Assert.Equal(TabWidthMode.Fixed, reloaded.TabWidthMode);
        Assert.Equal(240, reloaded.FixedTabWidth);
    }

    [Fact]
    public void ClampFixedTabWidth_bounds()
    {
        Assert.Equal(AppSettings.MinFixedTabWidth, AppSettings.ClampFixedTabWidth(0));
        Assert.Equal(AppSettings.MaxFixedTabWidth, AppSettings.ClampFixedTabWidth(int.MaxValue));
        Assert.Equal(200, AppSettings.ClampFixedTabWidth(200));
    }

    // ── silent-crawl cap ceiling matches the resident-cap ceiling ──

    [Fact]
    public void ClampSilentCrawlRowCap_accepts_values_above_the_old_2M_ceiling()
    {
        Assert.Equal(AppSettings.MinSilentCrawlRowCap, AppSettings.ClampSilentCrawlRowCap(-1));
        Assert.Equal(3_000_000, AppSettings.ClampSilentCrawlRowCap(3_000_000));
        Assert.Equal(20_000_000, AppSettings.ClampSilentCrawlRowCap(20_000_000));
        Assert.Equal(AppSettings.MaxResidentRowCap, AppSettings.MaxSilentCrawlRowCap);
        Assert.Equal(AppSettings.MaxSilentCrawlRowCap, AppSettings.ClampSilentCrawlRowCap(int.MaxValue));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ not valid json ");
        var svc = SettingsService.Load();
        Assert.Equal(ThemeMode.System, svc.Current.AppTheme);
    }

    // ── v16 → v17 (silent crawl cap default 100k), v21 → v22 (500k / 2.5M) ──

    [Fact]
    public void Fresh_settings_default_row_caps_are_500k_and_2_5M()
    {
        var svc = SettingsService.Load();
        Assert.Equal(500_000, svc.Current.SilentCrawlRowCap);
        Assert.Equal(2_500_000, svc.Current.ResidentRowCap);
    }

    [Fact]
    public void Migration_v16_old_300k_silent_crawl_default_chains_to_the_current_default()
    {
        // v16→v17 rewrites 300k to 100k, then v21→v22 rewrites that old default to 500k.
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 16, \"silentCrawlRowCap\": 300000 }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(500_000, svc.Current.SilentCrawlRowCap);
        var onDisk = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!;
        Assert.Equal(24, onDisk["schemaVersion"]!.GetValue<int>());
        Assert.Equal(500_000, onDisk["silentCrawlRowCap"]!.GetValue<int>());
    }

    [Fact]
    public void Migration_v21_rewrites_the_old_row_cap_defaults()
    {
        File.WriteAllText(AppPaths.SettingsFile,
            "{ \"schemaVersion\": 21, \"silentCrawlRowCap\": 100000, \"residentRowCap\": 2000000 }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(500_000, svc.Current.SilentCrawlRowCap);
        Assert.Equal(2_500_000, svc.Current.ResidentRowCap);
        var onDisk = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!;
        Assert.Equal(500_000, onDisk["silentCrawlRowCap"]!.GetValue<int>());
        Assert.Equal(2_500_000, onDisk["residentRowCap"]!.GetValue<int>());
    }

    [Fact]
    public void Migration_v21_keeps_user_chosen_row_caps()
    {
        File.WriteAllText(AppPaths.SettingsFile,
            "{ \"schemaVersion\": 21, \"silentCrawlRowCap\": 0, \"residentRowCap\": 7000000 }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(0, svc.Current.SilentCrawlRowCap);
        Assert.Equal(7_000_000, svc.Current.ResidentRowCap);
    }

    [Theory]
    [InlineData("\"2000000\"")]
    [InlineData("2000000.5")]
    [InlineData("3000000000")]
    public void Migration_v21_malformed_row_cap_resets_to_defaults_instead_of_throwing(string residentRowCap)
    {
        File.WriteAllText(AppPaths.SettingsFile,
            $"{{ \"schemaVersion\": 21, \"appTheme\": \"dark\", \"residentRowCap\": {residentRowCap} }}");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(GraphLimits.MaxResidentRowsDisk, svc.Current.ResidentRowCap);
    }

    [Fact]
    public void Migration_v16_keeps_a_user_chosen_silent_crawl_cap()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 16, \"silentCrawlRowCap\": 250000 }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(250_000, svc.Current.SilentCrawlRowCap);

        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 16, \"silentCrawlRowCap\": 0 }");

        svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(0, svc.Current.SilentCrawlRowCap);
    }

    // ── v17 → v18 (show author date setting) ──

    [Fact]
    public void V17_file_loads_with_commit_date_shown()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 17, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.False(svc.Current.ShowAuthorDate);
    }

    [Fact]
    public async Task Show_author_date_toggle_round_trips()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { ShowAuthorDate = true });
        await svc.FlushAsync();

        Assert.True(SettingsService.Load().Current.ShowAuthorDate);
    }

    [Fact]
    public async Task Racing_updates_leave_the_newest_snapshot_pending()
    {
        const int rounds = 3_000;
        var svc = SettingsService.Load();
        var writer = typeof(SettingsService).GetField("_writer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc)!;
        var pendingOf = typeof(AtomicFileWriter).GetProperty("PendingContentForTesting", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var inversions = 0;
        using var barrier = new Barrier(2);
        for (var r = 0; r < rounds; r++)
        {
            async Task Bump()
            {
                barrier.SignalAndWait();
                await svc.UpdateAsync(s => s with { DiffContextLines = s.DiffContextLines + 1 });
            }
            await Task.WhenAll(Task.Run(Bump), Task.Run(Bump));

            // A debounce that fired between rounds already put a snapshot on disk; nothing to compare.
            if (pendingOf.GetValue(writer) is not string pending)
                continue;
            var queued = JsonSerializer.Deserialize<AppSettings>(pending, HistoriaJson.Options)!;
            if (queued.DiffContextLines != svc.Current.DiffContextLines)
                inversions++;
        }

        Assert.Equal(0, inversions);
    }

    public void Dispose() => _state.Dispose();

    [Fact]
    public async Task A_v22_document_migrates_with_external_control_on()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 22, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();
        await svc.FlushAsync();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.True(svc.Current.ExternalControlEnabled);
        Assert.Equal(ThemeMode.Dark, svc.Current.AppTheme);
    }

    [Fact]
    public async Task A_v23_document_migrates_to_v24_with_the_agent_queue_defaults()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 23, \"appTheme\": \"dark\", \"externalControlEnabled\": false }");

        var svc = SettingsService.Load();
        await svc.FlushAsync();

        Assert.Equal(24, svc.Current.SchemaVersion);
        Assert.Equal(5, svc.Current.CmdrQueueExpiryHours);
        Assert.False(svc.Current.CmdrQueueNewestFirst);
        Assert.False(svc.Current.ExternalControlEnabled);
        Assert.Equal(ThemeMode.Dark, svc.Current.AppTheme);
        var onDisk = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!.AsObject();
        Assert.Equal(24, onDisk["schemaVersion"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(0, AppSettings.MinCmdrQueueExpiryHours)]
    [InlineData(-3, AppSettings.MinCmdrQueueExpiryHours)]
    [InlineData(500, AppSettings.MaxCmdrQueueExpiryHours)]
    [InlineData(12, 12)]
    public void An_out_of_range_queue_expiry_clamps_where_it_is_read(int stored, int expected)
    {
        File.WriteAllText(AppPaths.SettingsFile, $"{{ \"schemaVersion\": 24, \"cmdrQueueExpiryHours\": {stored} }}");

        var svc = SettingsService.Load();

        Assert.Equal(stored, svc.Current.CmdrQueueExpiryHours);
        Assert.Equal(expected, AppSettings.ClampCmdrQueueExpiryHours(svc.Current.CmdrQueueExpiryHours));
    }

    [Fact]
    public async Task Queue_sort_order_round_trips()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { CmdrQueueNewestFirst = true, CmdrQueueExpiryHours = 24 });
        await svc.FlushAsync();

        var reloaded = SettingsService.Load().Current;
        Assert.True(reloaded.CmdrQueueNewestFirst);
        Assert.Equal(24, reloaded.CmdrQueueExpiryHours);
    }

    [Fact]
    public void External_control_off_survives_a_reload()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 23, \"externalControlEnabled\": false }");

        Assert.False(SettingsService.Load().Current.ExternalControlEnabled);
    }

    [Fact]
    public void Lightweight_mode_defaults_off()
    {
        var settings = new AppSettings();
        Assert.False(settings.LightweightMode);

        var svc = SettingsService.Load();
        Assert.False(svc.Current.LightweightMode);
    }

    [Fact]
    public async Task Lightweight_mode_round_trips_and_leaves_stored_row_caps_untouched()
    {
        File.WriteAllText(AppPaths.SettingsFile,
            "{ \"schemaVersion\": 23, \"lightweightMode\": true, \"silentCrawlRowCap\": 123000, \"residentRowCap\": 7000000 }");

        var svc = SettingsService.Load();
        Assert.True(svc.Current.LightweightMode);
        Assert.Equal(123_000, svc.Current.SilentCrawlRowCap);
        Assert.Equal(7_000_000, svc.Current.ResidentRowCap);

        await svc.UpdateAsync(s => s with { LightweightMode = false });
        await svc.FlushAsync();

        Assert.False(svc.Current.LightweightMode);
        Assert.Equal(123_000, svc.Current.SilentCrawlRowCap);
        Assert.Equal(7_000_000, svc.Current.ResidentRowCap);

        var onDisk = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!.AsObject();
        Assert.False(onDisk["lightweightMode"]!.GetValue<bool>());
        Assert.Equal(123_000, onDisk["silentCrawlRowCap"]!.GetValue<int>());
        Assert.Equal(7_000_000, onDisk["residentRowCap"]!.GetValue<int>());
    }
}
