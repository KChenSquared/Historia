namespace Historia.ViewModels;

/// <summary>One line of the Changes or Staged list: a file, a conflicted file, or a note.</summary>
public abstract class StagingListItem : ObservableObject
{
}

/// <summary>A line of text shown in place of rows or after them, such as "Nothing staged.".</summary>
public sealed class StagingNoteRowViewModel : StagingListItem
{
    public StagingNoteRowViewModel(string text, string? toolTip = null, bool isFootnote = false)
    {
        Text = text;
        ToolTip = toolTip;
        IsFootnote = isFootnote;
    }

    public string Text { get; }

    /// <summary>Longer explanation shown on hover, or null.</summary>
    public string? ToolTip { get; }

    public bool HasToolTip => ToolTip is not null;

    /// <summary>True for a small note after the rows rather than a stand-in for them.</summary>
    public bool IsFootnote { get; }
}

/// <summary>The "Conflicted" heading above the conflicted files, with the abort button on its right.</summary>
public sealed class ConflictHeaderRowViewModel : StagingListItem
{
    internal ConflictHeaderRowViewModel(IRelayCommand abortCommand, bool isAbortVisible, string abortTip)
    {
        AbortCommand = abortCommand;
        IsAbortVisible = isAbortVisible;
        AbortTip = abortTip;
    }

    public IRelayCommand AbortCommand { get; }

    /// <summary>True while the repository is in an operation that can be aborted.</summary>
    public bool IsAbortVisible { get; }

    public string AbortTip { get; }
}

/// <summary>A file with a path, a context menu to reveal or copy it, and a click action.</summary>
public abstract partial class StagingPathRowViewModel : StagingListItem
{
    private readonly StagingPanelViewModel _owner;

    private protected StagingPathRowViewModel(StagingPanelViewModel owner, string path)
    {
        _owner = owner;
        Path = path;
    }

    /// <summary>Repo-relative path, forward slashes.</summary>
    public string Path { get; }

    /// <summary>What a click on the row does.</summary>
    public abstract IRelayCommand ActivateCommand { get; }

    [RelayCommand]
    private void Reveal() => _owner.RevealPath(Path);

    [RelayCommand]
    private Task CopyPathAsync() => _owner.CopyPathAsync(Path);
}

/// <summary>A conflicted file. It has no checkbox and no discard; a click opens the resolver on it.</summary>
public sealed partial class ConflictRowViewModel : StagingPathRowViewModel
{
    private readonly StagingPanelViewModel _owner;

    internal ConflictRowViewModel(StagingPanelViewModel owner, string path)
        : base(owner, path)
    {
        _owner = owner;
        ActivateCommand = new RelayCommand(() => _owner.OpenConflict(Path));
    }

    public override IRelayCommand ActivateCommand { get; }

    /// <summary>True while the resolver shows this file.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; internal set; }
}

/// <summary>
/// A changed file in the Changes or Staged list. <see cref="IsChecked"/> is the row's checkbox; a
/// click on the row opens the file's diff.
/// </summary>
public sealed partial class FileRowViewModel : StagingPathRowViewModel
{
    private readonly StagingPanelViewModel _owner;
    private bool _syncingChecked;

    internal FileRowViewModel(StagingPanelViewModel owner, FileStatusEntry entry, bool isStaged, bool isChecked)
        : base(owner, entry.Path)
    {
        _owner = owner;
        Entry = entry;
        IsStaged = isStaged;
        SetCheckedFromPanel(isChecked);
        ActivateCommand = new RelayCommand(() => _owner.OpenDiff(this));
        DiscardCommand = new AsyncRelayCommand(() => _owner.DiscardCommand.ExecuteAsync(this));
    }

    private bool CanRunMenuAction => !_owner.IsStagingBusy;

    /// <summary>Stages this file alone. Used from the Changes list.</summary>
    [RelayCommand(CanExecute = nameof(CanRunMenuAction))]
    private Task StageAsync() => _owner.StageOneAsync(this);

    /// <summary>Unstages this file alone. Used from the Staged list.</summary>
    [RelayCommand(CanExecute = nameof(CanRunMenuAction))]
    private Task UnstageAsync() => _owner.UnstageOneAsync(this);

    /// <summary>Stages or unstages every checked file of this row's list, as the arrow does.</summary>
    [RelayCommand(CanExecute = nameof(CanRunMenuAction))]
    private Task MoveSelectedAsync() => _owner.MoveSelectedAsync(IsStaged);

    /// <summary>Adds this file alone to the root <c>.gitignore</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanRunMenuAction))]
    private Task IgnoreAsync() => _owner.IgnoreAsync([Path]);

    /// <summary>Adds every checked file of this row's list to the root <c>.gitignore</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanRunMenuAction))]
    private Task IgnoreSelectedAsync() => _owner.IgnoreCheckedAsync(IsStaged);

    /// <summary>Re-evaluates the menu commands after the panel's busy state changed.</summary>
    internal void NotifyBusyChanged()
    {
        StageCommand.NotifyCanExecuteChanged();
        UnstageCommand.NotifyCanExecuteChanged();
        MoveSelectedCommand.NotifyCanExecuteChanged();
        IgnoreCommand.NotifyCanExecuteChanged();
        IgnoreSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True when the row is checked: the menu then offers the "selected" items too.</summary>
    public bool ShowSelectedItems => IsChecked;

    public FileStatusEntry Entry { get; }

    public FileChangeKind Kind => Entry.Kind;

    /// <summary>True in the Staged list, false in Changes.</summary>
    public bool IsStaged { get; }

    /// <summary>Only a row in Changes can be discarded.</summary>
    public bool CanDiscard => !IsStaged;

    public override IRelayCommand ActivateCommand { get; }

    /// <summary>Discards this file's unstaged changes after the user confirms.</summary>
    public IAsyncRelayCommand DiscardCommand { get; }

    /// <summary>The row's checkbox, which marks the file for the Stage / Unstage arrows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelectedItems))]
    public partial bool IsChecked { get; set; }

    /// <summary>New, or added.</summary>
    public bool IsAdded => Kind is FileChangeKind.Added or FileChangeKind.Untracked;

    public bool IsDeleted => Kind == FileChangeKind.Deleted;

    public bool IsModified => Kind is FileChangeKind.Modified or FileChangeKind.TypeChanged;

    /// <summary>Renamed or copied.</summary>
    public bool IsMoved => Kind is FileChangeKind.Renamed or FileChangeKind.Copied;

    /// <summary>Sets the checkbox without telling the panel, which already knows.</summary>
    internal void SetCheckedFromPanel(bool value)
    {
        _syncingChecked = true;
        try { IsChecked = value; }
        finally { _syncingChecked = false; }
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_syncingChecked)
            _owner.OnRowChecked(this, value);
    }
}
