namespace Historia.ViewModels;

public sealed partial class StagingPanelViewModel
{
    private readonly ICommitDraftStore _drafts;
    private bool _committing;
    private string? _offered;
    private string? _agentNoteText;

    /// <summary>The commit message being written. It is kept per tab in the draft store when the panel goes away.</summary>
    [ObservableProperty]
    public partial string MessageText { get; set; } = "";

    /// <summary>True while the message box cannot be typed in, which a running generation sets.</summary>
    [ObservableProperty]
    public partial bool IsMessageReadOnly { get; set; }

    /// <summary>Amend the previous commit instead of creating a new one.</summary>
    [ObservableProperty]
    public partial bool IsAmend { get; set; }

    /// <summary>A <c>Name &lt;email&gt;</c> override for the commit's author, or empty.</summary>
    [ObservableProperty]
    public partial string AuthorOverride { get; set; } = "";

    /// <summary>The in-box length counter, such as <c>12 / 72</c>.</summary>
    [ObservableProperty]
    public partial string CounterText { get; private set; } = "";

    /// <summary>True from the subject length limit up to the body length limit.</summary>
    [ObservableProperty]
    public partial bool IsCounterWarning { get; private set; }

    /// <summary>True from the body length limit on.</summary>
    [ObservableProperty]
    public partial bool IsCounterDanger { get; private set; }

    /// <summary>True while a message offered by a coding agent waits in the bar above the box.</summary>
    [ObservableProperty]
    public partial bool IsOfferVisible { get; private set; }

    /// <summary>The first 300 characters of the offered message.</summary>
    [ObservableProperty]
    public partial string? OfferTip { get; private set; }

    /// <summary>True while the box holds a message an agent proposed and the user has not edited it.</summary>
    [ObservableProperty]
    public partial bool IsAgentNoteVisible { get; private set; }

    partial void OnMessageTextChanged(string value)
    {
        UpdateCounter();
        if (IsAgentNoteVisible && value != _agentNoteText)
            HideAgentNote();
    }

    private void UpdateCounter()
    {
        var len = (MessageText ?? "").Length;
        string secondPart;
        if (len < 72)
            secondPart = "72";
        else if (len == 72)
            secondPart = "72";
        else if (len < 500)
            secondPart = "500";
        else
            secondPart = "-";

        CounterText = $"{len} / {secondPart}";
        IsCounterWarning = len >= 72 && len < 500;
        IsCounterDanger = len >= 500;
    }

    // ── Commit ──

    /// <summary>Commits the staged changes with the message in the box.</summary>
    [RelayCommand]
    private Task CommitAsync() => CommitCoreAsync(push: false);

    /// <summary>Commits, then asks for a push of the current branch.</summary>
    [RelayCommand]
    private Task CommitAndPushAsync() => CommitCoreAsync(push: true);

    private async Task CommitCoreAsync(bool push)
    {
        // A stage or unstage still running, or its status refresh, decides what is staged; the
        // checks below must see its result.
        if (!_pendingStage.IsCompleted)
        {
            await _pendingStage;
            AdoptCurrentStatus();
        }

        // While a generated message is on its way, Ctrl+Enter must not commit the old draft just
        // before the generated text replaces it.
        if (_committing || _generateCts is not null || _status.Conflicted.Count > 0) return;
        if (string.IsNullOrWhiteSpace(MessageText) || (_status.Staged.Count == 0 && !IsAmend))
        {
            _notifications.Publish(NotificationSeverity.Warning, "Stage changes and enter a message before committing.");
            return;
        }

        // Amending a commit that's already on its upstream (as of the last
        // fetch) rewrites published history — pushing it afterwards needs a force push. An
        // unpushed commit's amend doesn't touch anything else and needs no prompt.
        if (IsAmend)
        {
            var upstream = _ctx.State.CurrentBranch?.UpstreamName;
            if (upstream is not null && _status.AheadBy == 0)
            {
                var headSha = _ctx.State.CurrentBranch?.TipCommitId?.ToString();
                var shortSha = headSha is { Length: >= 7 } ? headSha[..7] : headSha ?? "HEAD";
                try
                {
                    if (!await _dialogs.ConfirmDestructiveAsync("Amend a pushed commit",
                            $"Amend {shortSha}, which is already on {upstream}? This rewrites " +
                            "published history; pushing it afterwards needs a force push.", "Amend"))
                        return;
                }
                catch (Exception ex)
                {
                    _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Commit"), ex.NotificationDetails());
                    return;
                }
            }
        }

        _committing = true;
        // A proposal that arrives while the commit runs is meant for the next commit; only the
        // one already pending now is spent by this commit.
        var pendingAtStart = _ctx.State.PendingCommitMessage;
        try
        {
            var profile = _ctx.State.ActiveProfile;
            var name = profile?.GitUserName ?? "";
            var email = profile?.GitUserEmail ?? "";

            // under Local config (no active profile) the identity comes from git config.
            if (profile is null && (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)))
            {
                var (cfgName, cfgEmail) = await _ctx.Gateway.GetConfiguredIdentityAsync(_ctx.Lifetime);
                if (string.IsNullOrWhiteSpace(cfgName) || string.IsNullOrWhiteSpace(cfgEmail))
                    (cfgName, cfgEmail) = await _ctx.Gateway.GetGlobalIdentityAsync(_ctx.Lifetime);
                name = cfgName ?? name;
                email = cfgEmail ?? email;
            }

            if (TryParseAuthor(AuthorOverride ?? "", out var oN, out var oE)) { name = oN; email = oE; }
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
            {
                _notifications.Publish(NotificationSeverity.Warning,
                    "No author identity. Set user.name / user.email in Git config, choose a profile, or set an author override.");
                return;
            }

            // with no profile, honour whatever git config says about signing.
            var sign = profile?.SignCommits ?? await _ctx.Gateway.GetGpgSignConfiguredAsync(_ctx.Lifetime);

            await _ctx.Gateway.CommitAsync(new CommitRequest
            {
                Message = MessageText!.Trim(),
                AuthorName = name,
                AuthorEmail = email,
                CommitterName = profile?.GitUserName ?? name,
                CommitterEmail = profile?.GitUserEmail ?? email,
                AmendPreviousCommit = IsAmend,
                Sign = sign,
            }, _ctx.Lifetime);
            MessageText = "";
            IsAmend = false;
            _drafts.Set(_ctx.Id, null);
            if (pendingAtStart is not null)
                _ctx.State.ClearPendingCommitMessage(onlyIf: pendingAtStart);
            if (_ctx.State.PendingCommitMessage is null)
                HideOffer();
            HideAgentNote();
            await _ctx.State.RefreshAllAsync(_ctx.Lifetime);
            _notifications.Publish(NotificationSeverity.Success, "Commit created.");

            // The push goes through the network-action runner, which owns its progress,
            // cancellation, busy guard and rejection / host-key / Local-config flows, and
            // reports its own outcome.
            if (push && BuildPushRequest(_ctx) is { } request)
                _actionBus.RequestNetworkAction(request);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Commit"), ex.NotificationDetails());
        }
        finally
        {
            _committing = false;
            if (!IsDisposed && _ctx.State.PendingCommitMessage is { } pending)
                ApplyProposal(pending);
        }
    }

    /// <summary>The push half of Commit &amp; Push: the current branch to <c>origin</c> (else the
    /// first remote), pinned to this panel's repository. Null when there is no remote or no
    /// checked-out branch.</summary>
    public static RepoNetworkRequest? BuildPushRequest(RepositoryContext ctx)
    {
        var remote = ctx.State.Remotes.FirstOrDefault(r => r.Name == "origin")?.Name ?? ctx.State.Remotes.FirstOrDefault()?.Name;
        var branch = ctx.State.CurrentBranch?.Name;
        return remote is null || branch is null
            ? null
            : new RepoNetworkRequest(RepoNetworkAction.Push, remote, branch, Repo: ctx);
    }

    /// <summary>Reads <c>Name &lt;email&gt;</c>; false when either part is missing.</summary>
    public static bool TryParseAuthor(string text, out string name, out string email)
    {
        name = ""; email = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lt = text.IndexOf('<');
        var gt = text.IndexOf('>');
        if (lt < 0 || gt < lt) return false;
        name = text[..lt].Trim();
        email = text[(lt + 1)..gt].Trim();
        return name.Length > 0 && email.Length > 0;
    }

    // ── Messages proposed by an external tool ──

    private void OnCommitMessageProposed(object? sender, CommitMessageProposedEventArgs e)
    {
        if (!IsDisposed)
            ApplyProposal(e.Message);
    }

    /// <summary>Puts the proposal in the box when that cannot lose anything — the box is empty or
    /// already holds the same text — and otherwise offers it in the bar above the box. A proposal
    /// never commits, never touches Amend and never moves focus.</summary>
    private void ApplyProposal(string proposal)
    {
        var current = MessageText ?? "";
        var busy = IsMessageReadOnly || _committing;
        if (!busy && (string.IsNullOrWhiteSpace(current)
                      || string.Equals(current.Trim(), proposal.Trim(), StringComparison.Ordinal)))
        {
            TakeProposal(proposal);
            return;
        }

        _offered = proposal;
        IsOfferVisible = true;
        OfferTip = proposal.Length <= 300 ? proposal : proposal[..300] + "…";
    }

    private void TakeProposal(string proposal)
    {
        MessageText = proposal;
        _ctx.State.ClearPendingCommitMessage();
        HideOffer();
        _agentNoteText = MessageText;
        IsAgentNoteVisible = true;
    }

    /// <summary>Replaces the draft with the offered message, after the user confirms when the draft would be lost.</summary>
    [RelayCommand]
    private async Task UseOfferAsync()
    {
        if (IsDisposed || _offered is null || IsMessageReadOnly || _committing)
            return;

        try
        {
            var offer = _ctx.State.PendingCommitMessage ?? _offered;
            var draft = MessageText ?? "";
            if (!string.IsNullOrWhiteSpace(draft)
                && !string.Equals(draft.Trim(), offer.Trim(), StringComparison.Ordinal)
                && !await ConfirmReplaceDraftAsync("the coding agent's message", draft.Length))
                return;

            if (IsDisposed || IsMessageReadOnly || _committing)
                return;

            TakeProposal(_ctx.State.PendingCommitMessage ?? offer);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "Staging", "Using the offered commit message failed.", ex);
        }
    }

    /// <summary>Forgets the offered message and hides the bar; the draft is untouched.</summary>
    [RelayCommand]
    private void DismissOffer()
    {
        _ctx.State.ClearPendingCommitMessage();
        HideOffer();
    }

    private void HideOffer()
    {
        _offered = null;
        IsOfferVisible = false;
        OfferTip = null;
    }

    private void HideAgentNote()
    {
        _agentNoteText = null;
        IsAgentNoteVisible = false;
    }

    /// <summary>Asks before a typed draft is thrown away for generated or proposed text.</summary>
    private Task<bool> ConfirmReplaceDraftAsync(string replacement, int draftLength) =>
        _dialogs.ConfirmDestructiveAsync("Replace commit message",
            $"Replace your commit message draft ({draftLength} characters) with {replacement}? Your draft is discarded.",
            "Replace");

    /// <summary>Keeps the draft for the next panel of this tab and stops anything still running.</summary>
    protected override void OnDisposed()
    {
        // Cancelled before the draft is saved: the generated text would otherwise land in a dead
        // panel while the store kept the old text.
        _generateCts?.Cancel();
        _modelLoadCts?.Cancel();
        _modelLoadCts?.Dispose();
        _drafts.Set(_ctx.Id, MessageText);
    }
}
