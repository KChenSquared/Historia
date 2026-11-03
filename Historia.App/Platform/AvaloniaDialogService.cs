namespace Historia.Platform;

/// <summary>
/// App-side <see cref="IDialogService"/>.
/// Folder/file pickers go through Avalonia's <see cref="IStorageProvider"/>; confirm / message /
/// prompt are one small hand-rolled <see cref="Window"/> (Avalonia has no built-in MessageBox,
/// and pulling in a package for it was rejected). Safe to call from any thread.
/// </summary>
public sealed class AvaloniaDialogService : IDialogService
{
    /// <summary>
    /// A live window to own a dialog. Prefers <c>MainWindow</c>, but only while it is still in
    /// the lifetime's window list — during shutdown it can be gone, and
    /// <c>ShowDialog(closedWindow)</c> never returns. Falls back to any other open window, then
    /// to an owner-less dialog.
    /// </summary>
    internal static Window? Owner
    {
        get
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime d)
                return null;
            var main = d.MainWindow;
            if (main is not null && d.Windows.Contains(main))
                return main;
            return d.Windows.LastOrDefault();
        }
    }

    private static Task<T> OnUi<T>(Func<Task<T>> work) =>
        Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work);

    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => OnUi(async () =>
    {
        if (Owner?.StorageProvider is not { CanOpen: true } sp)
            return null;
        var start = await SafeFolder(sp, initialDirectory);
        var picks = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return picks.Count > 0 ? picks[0].TryGetLocalPath() : null;
    });

    public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => OnUi(async () =>
    {
        if (Owner?.StorageProvider is not { CanOpen: true } sp)
            return null;
        var start = await SafeFolder(sp, initialDirectory);
        var picks = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = ParseFilter(filter),
        });
        return picks.Count > 0 ? picks[0].TryGetLocalPath() : null;
    });

    public Task<bool> ConfirmAsync(string title, string message, bool destructive = false,
        string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) =>
        OnUi(async () => (await SimpleDialog.ShowAsync(
            Owner, title, message, prompt: false, confirmLabel, "Cancel", defaultToCancel: defaultToCancel, icon: icon)).Confirmed);

    // The destructive-confirm contract is hard-wired here — the red
    // warning, Cancel as the primary + Enter button (still rightmost), the confirm
    // button left plain. A call site cannot opt out of any of it.
    public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel) =>
        OnUi(async () => (await SimpleDialog.ShowAsync(
            Owner, title, effect, prompt: false, confirmLabel, "Cancel",
            defaultToCancel: true, icon: DialogIcon.Warning)).Confirmed);

    public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options,
        int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) =>
        OnUi(() => SimpleDialog.ShowChoiceAsync(Owner, title, message, options, defaultIndex, destructiveIndex, icon));

    public Task ShowMessageAsync(string title, string message) =>
        OnUi<object?>(async () =>
        {
            await SimpleDialog.ShowAsync(Owner, title, message, prompt: false, "OK", cancelLabel: null);
            return null;
        });

    public Task<string?> PromptAsync(string title, string message, string? defaultValue = null,
        string confirmLabel = "OK", bool primaryConfirm = false) =>
        OnUi(async () =>
        {
            var r = await SimpleDialog.ShowAsync(Owner, title, message, prompt: true, confirmLabel, "Cancel",
                defaultValue, primaryConfirm: primaryConfirm);
            return r.Confirmed ? r.Text : null;
        });

    private static async Task<IStorageFolder?> SafeFolder(IStorageProvider sp, string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return null;
        try { return await sp.TryGetFolderFromPathAsync(dir); }
        catch { return null; }
    }

    private static IReadOnlyList<FilePickerFileType>? ParseFilter(string filter)
    {
        // WPF-style "Label|*.ext;*.ext2|Label2|*.*"
        if (string.IsNullOrWhiteSpace(filter))
            return null;
        var parts = filter.Split('|');
        var types = new List<FilePickerFileType>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
            types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';') });
        return types.Count > 0 ? types : null;
    }
}

/// <summary>Minimal modal dialog: a message, an optional text field, and one or two buttons.</summary>
internal static class SimpleDialog
{
    public readonly record struct Result(bool Confirmed, string? Text);

    /// <summary>The confirm / cancel button row for <see cref="ShowAsync"/>. Cancel is always
    /// the rightmost button (Windows <c>[Action] [Cancel]</c> order); <paramref name="defaultToCancel"/>
    /// Additionally makes Cancel the primary (blue) + Enter button and
    /// leaves the confirm button plain. Factored out so <c>DestructiveConfirmationTests</c> can
    /// assert the contract without showing a window.</summary>
    internal static (StackPanel row, Button confirm, Button? cancel) ConfirmButtonRow(
        string confirmLabel, string? cancelLabel, bool defaultToCancel, bool primaryConfirm)
    {
        var confirm = new Button { Content = confirmLabel, IsDefault = !defaultToCancel, MinWidth = 80 };
        var cancel = cancelLabel is null ? null : new Button { Content = cancelLabel, IsCancel = true, MinWidth = 80 };
        // For a destructive action: Cancel is the highlighted (primary / Enter) button, so a
        // reflexive Enter or click lands on the safe choice.
        if (defaultToCancel && cancel is not null)
        {
            cancel.IsDefault = true;
            cancel.Classes.Add("primary");
        }
        else if (primaryConfirm)
        {
            confirm.Classes.Add("primary");
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
        };
        // Cancel is always the rightmost (first-from-right) button — a reflexive click on the
        // corner hits Cancel. defaultToCancel changes only its styling, never its position.
        row.Children.Add(confirm);
        if (cancel is not null) row.Children.Add(cancel);
        return (row, confirm, cancel);
    }

    /// <summary>The message line, optionally led by the large red <c>warning</c>
    /// (<see cref="DialogIcon.Warning"/>). Shared by every hand-rolled
    /// dialog so the warning treatment is identical everywhere.</summary>
    internal static Control MessageBlock(string message, double maxWidth, DialogIcon icon)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth };
        if (icon != DialogIcon.Warning)
            return text;

        var glyph = Historia.Views.ViewKit.IconGlyphPath("warning", 30, "ColorDanger");
        glyph.VerticalAlignment = VerticalAlignment.Top;
        glyph.Margin = new Thickness(0, 2, 12, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(glyph);
        row.Children.Add(text);
        return row;
    }

    public static async Task<Result> ShowAsync(
        Window? owner, string title, string message, bool prompt,
        string confirmLabel, string? cancelLabel, string? defaultValue = null, bool defaultToCancel = false,
        bool primaryConfirm = false, DialogIcon icon = DialogIcon.None)
    {
        var box = new TextBox { Text = defaultValue ?? string.Empty, Margin = new Thickness(0, 8, 0, 0) };
        var (buttons, confirm, cancel) = ConfirmButtonRow(confirmLabel, cancelLabel, defaultToCancel, primaryConfirm);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(MessageBlock(message, maxWidth: 380, icon));
        if (prompt) panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var tcs = new TaskCompletionSource<Result>();
        // Set the result BEFORE closing: Window.Close() raises Closed synchronously, and that
        // handler's TrySetResult(false, ...) would otherwise win the race against this one,
        // making every confirm-button click resolve as "not confirmed" (ShowChoiceAsync below uses
        // the same order).
        confirm.Click += (_, _) => { tcs.TrySetResult(new Result(true, box.Text)); window.Close(); };
        if (cancel is not null)
            cancel.Click += (_, _) => { tcs.TrySetResult(new Result(false, null)); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(new Result(false, null));
        // A prompt is typed into straight away; a prefilled value is selected so typing replaces it.
        if (prompt)
            window.Opened += (_, _) => { box.Focus(); box.SelectAll(); };

        await ShowOwnedOrFree(window, owner);

        return await tcs.Task;
    }

    /// <summary>One button per option plus a Cancel; returns the option index, or -1.
    /// Labels stay on one line (the window widens to fit rather than trimming);
    /// <paramref name="destructiveIndex"/> gets the red outlined "danger" styling.
    /// <para>When <paramref name="icon"/> is
    /// <see cref="DialogIcon.Warning"/> the red <c>warning</c> leads the message,
    /// <c>Cancel</c> becomes the primary / Enter button, and the <paramref name="defaultIndex"/>
    /// option is left plain (a destructive chooser has no blue "go" button).</para></summary>
    public static async Task<int> ShowChoiceAsync(
        Window? owner, string title, string message, IReadOnlyList<string> options,
        int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
    {
        var tcs = new TaskCompletionSource<int>();
        var warn = icon == DialogIcon.Warning;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(MessageBlock(message, maxWidth: 440, icon));
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        void Pick(int result) { tcs.TrySetResult(result); window.Close(); }

        for (var i = 0; i < options.Count; i++)
        {
            var idx = i;
            var b = new Button
            {
                // A TextBlock (not the raw string) so the Fluent button template can't
                // ellipsis-trim the label — it stays on one line and the window grows to fit.
                Content = new TextBlock
                {
                    Text = options[i],
                    TextAlignment = Avalonia.Media.TextAlignment.Center,
                    TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                },
                MinWidth = 80,
                // In a warning chooser the safe choice (Cancel) is the Enter default,
                // never one of the options.
                IsDefault = !warn && i == defaultIndex,
            };
            if (i == destructiveIndex)
                b.Classes.Add("danger");
            else if (i == defaultIndex && !warn)
                b.Classes.Add("primary");
            b.Click += (_, _) => Pick(idx);
            buttons.Children.Add(b);
        }

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        // Cancel is the primary (blue) + Enter button in a warning chooser; still rightmost.
        if (warn)
        {
            cancel.IsDefault = true;
            cancel.Classes.Add("primary");
        }
        cancel.Click += (_, _) => Pick(-1);
        buttons.Children.Add(cancel); // rightmost

        window.Closed += (_, _) => tcs.TrySetResult(-1);

        await ShowOwnedOrFree(window, owner);

        return await tcs.Task;
    }

    /// <summary>Modal on a live owner; a focused top-level when there is none (e.g. mid-shutdown,
    /// after the main window has closed) — never <c>ShowDialog</c> on a closed window, which
    /// hangs.</summary>
    internal static async Task ShowOwnedOrFree(Window window, Window? owner)
    {
        // Every hand-rolled dialog shares the home panel's surface rather than Fluent's darker
        // default window background. Bound (not set) so it tracks theme changes while open.
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("ColorChrome"));

        window.Icon ??= owner?.Icon ?? AvaloniaDialogService.Owner?.Icon ?? FallbackIcon.Value;

        var themeService = ResolveThemeService();
        void ApplyChrome(EffectiveTheme theme)
            => WindowChrome.Apply(window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, theme);

        // The native handle only exists once shown, so the caption is themed on Opened.
        window.Opened += (_, _) => ApplyChrome(themeService?.Effective ?? CurrentFallbackTheme());

        if (themeService is not null)
        {
            EventHandler<ThemeChangedEventArgs> onThemeChanged =
                (_, e) => Dispatcher.UIThread.Post(() => ApplyChrome(e.NewTheme));
            themeService.ThemeChanged += onThemeChanged;
            window.Closed += (_, _) => themeService.ThemeChanged -= onThemeChanged;
        }

        if (owner is not null)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Show();
            window.Activate();
        }
    }

    /// <summary>App icon for owner-less dialogs, decoded once rather than per dialog.</summary>
    private static readonly Lazy<WindowIcon?> FallbackIcon = new(() =>
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Historia/Assets/Historia.ico"));
            return new WindowIcon(stream);
        }
        catch
        {
            return null; // fall back silently if the icon asset cannot be loaded
        }
    });

    private static IThemeService? ResolveThemeService()
    {
        try
        {
            if (Application.Current is App app)
                return app.Services.GetService<IThemeService>();
        }
        catch
        {
        }
        return null;
    }

    private static EffectiveTheme CurrentFallbackTheme()
    {
        if (Application.Current is { } app)
            return app.ActualThemeVariant == ThemeVariant.Light ? EffectiveTheme.Light : EffectiveTheme.Dark;
        return EffectiveTheme.Dark;
    }
}

/// <summary>Outcome of the clone dialog: <c>null</c> when cancelled / closed.</summary>
/// <param name="ProfileId">the checkout identity chosen in the clone dialog — null means
/// <i>Local config</i>. The caller binds it to the newly-cloned repo.</param>
public sealed record CloneDialogResult(
    string Url,
    string TargetDirectory,
    bool Shallow = false,
    bool Sparse = false,
    Guid? ProfileId = null);

/// <summary>
/// Modal "Clone repository" window — its own top-level, centred on the app window, closable.
/// Splits the destination into a wide <b>Root</b> box and a narrow <b>Folder name</b> box with
/// the platform path separator between them, plus shallow-clone / sparse-checkout toggles and
/// a checkout-identity picker: which profile to clone under and in which network mode.
/// </summary>
internal static class CloneDialog
{
    /// <summary>Whether the folder box's current text is still "owned" by the auto-fill —
    /// i.e. it should be overwritten by the next URL-derived guess. True both when the box is
    /// empty and when its text is exactly what the auto-fill itself last wrote there (so the
    /// auto-fill's own programmatic write is never mistaken for a user edit); false the moment
    /// the user types something else.</summary>
    internal static bool ShouldAutoFillFolder(string currentFolderText, string? lastAutoFilled) =>
        string.IsNullOrEmpty(currentFolderText) || currentFolderText == lastAutoFilled;

    /// <summary>the identity-picker index to pre-select. Index 0 is <i>Local config</i>;
    /// profile <c>i</c> sits at <c>i + 1</c>. Picks the default profile, falling back to the
    /// global profile when the default is unset or no longer exists, and to <i>Local config</i>
    /// only when there are no profiles at all.</summary>
    internal static int InitialProfileIndex(IReadOnlyList<Historia.Models.GitProfile> profiles, Guid? defaultProfileId)
    {
        for (var i = 0; i < profiles.Count; i++)
            if (defaultProfileId is { } id && profiles[i].Id == id)
                return i + 1;
        for (var i = 0; i < profiles.Count; i++)
            if (profiles[i].IsGlobal)
                return i + 1;
        return profiles.Count > 0 ? 1 : 0;
    }

    public static async Task<CloneDialogResult?> ShowAsync(
        IDialogService dialogs, string defaultRoot, Func<string, string> guessFolderName,
        IReadOnlyList<Historia.Models.GitProfile> profiles, Guid? defaultProfileId)
    {
        var sep = System.IO.Path.DirectorySeparatorChar.ToString();

        var url = new TextBox { Watermark = "https://github.com/owner/repo.git", MinWidth = 460 };
        var root = new TextBox { Text = defaultRoot, Watermark = "parent folder", Width = 320 };
        var folder = new TextBox { Watermark = "folder name", Width = 150 };
        var browse = new Button { Content = "Browse…", MinWidth = 80 };
        var shallow = new CheckBox { Content = "Shallow clone (--depth 1)" };
        var sparse = new CheckBox { Content = "Sparse checkout (--sparse)" };
        var urlHint = new TextBlock { FontSize = 11, Margin = new Thickness(0, 3, 0, 0), IsVisible = false }.Dyn(TextBlock.ForegroundProperty, "ColorDanger");

        // Which identity checks the clone out under, and in which network mode:
        // the chosen identity becomes this repo's per-repo selection once the clone
        // finishes; "(Local config)" writes the Local-config marker row.
        var profileLabels = new List<string> { "Local config" };
        profileLabels.AddRange(profiles.Select(p => p.IsGlobal ? "Global (~/.gitconfig)" : p.DisplayName));
        var profileBox = new ComboBox { ItemsSource = profileLabels, Width = 260, SelectedIndex = InitialProfileIndex(profiles, defaultProfileId) };
        Historia.Models.GitProfile? SelectedProfile() => profileBox.SelectedIndex > 0 ? profiles[profileBox.SelectedIndex - 1] : null;

        var confirm = new Button { Content = "Clone", IsDefault = true, MinWidth = 80, IsEnabled = false };
        confirm.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };

        // Folder box auto-fills from the URL until the user types into it (the auto-fill's
        // own write to folder.Text must never be mistaken for a user edit).
        string? lastAutoFilled = null;
        url.TextChanged += (_, _) =>
        {
            if (ShouldAutoFillFolder(folder.Text ?? "", lastAutoFilled))
            {
                lastAutoFilled = guessFolderName(url.Text ?? "");
                folder.Text = lastAutoFilled;
            }
        };

        void Revalidate()
        {
            var urlProblem = CloneValidation.DescribeUrlProblem(url.Text ?? "");
            urlHint.Text = urlProblem;
            urlHint.IsVisible = urlProblem is not null;
            confirm.IsEnabled = urlProblem is null
                && !string.IsNullOrWhiteSpace(root.Text)
                && !string.IsNullOrWhiteSpace(folder.Text);
        }
        url.TextChanged += (_, _) => Revalidate();
        root.TextChanged += (_, _) => Revalidate();
        folder.TextChanged += (_, _) => Revalidate();

        browse.Click += async (_, _) =>
        {
            var picked = await dialogs.PickFolderAsync(
                "Choose the parent folder for the clone",
                string.IsNullOrWhiteSpace(root.Text) ? null : root.Text);
            if (picked is not null)
            {
                root.Text = picked;
                Revalidate();
            }
        };

        static TextBlock Label(string t) => new()
        {
            Text = t,
            FontSize = 11,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Margin = new Thickness(0, 10, 0, 3),
        };

        var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        pathRow.Children.Add(root);
        pathRow.Children.Add(new TextBlock { Text = sep, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        pathRow.Children.Add(folder);
        pathRow.Children.Add(browse);

        var optionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 12, 0, 0) };
        optionsRow.Children.Add(shallow);
        optionsRow.Children.Add(sparse);

        var profileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        profileRow.Children.Add(profileBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0),
        };
        // Cancel is always the rightmost button (matches SimpleDialog).
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label("Remote URL"));
        panel.Children.Add(url);
        panel.Children.Add(urlHint);
        panel.Children.Add(Label("Where to clone to"));
        panel.Children.Add(pathRow);
        panel.Children.Add(optionsRow);
        panel.Children.Add(Label("Checkout identity"));
        panel.Children.Add(profileRow);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "Clone repository",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var tcs = new TaskCompletionSource<CloneDialogResult?>();
        confirm.Click += async (_, _) =>
        {
            // Validated once more here (not just via Revalidate/IsEnabled) because this
            // is the only check that touches disk — it runs on click, not on every keystroke.
            var destProblem = CloneValidation.DescribeDestinationProblem(root.Text ?? "", folder.Text ?? "");
            if (destProblem is not null)
            {
                await dialogs.ShowMessageAsync("Clone repository", destProblem);
                return;
            }

            var target = System.IO.Path.Combine((root.Text ?? "").Trim(), (folder.Text ?? "").Trim());
            tcs.TrySetResult(new CloneDialogResult(
                (url.Text ?? "").Trim(), target, shallow.IsChecked == true, sparse.IsChecked == true,
                SelectedProfile()?.Id));
            window.Close();
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(null);
        window.Opened += (_, _) => url.Focus();

        await SimpleDialog.ShowOwnedOrFree(window, AvaloniaDialogService.Owner);
        return await tcs.Task;
    }
}
