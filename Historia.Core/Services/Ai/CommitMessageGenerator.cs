namespace Historia.Services.Ai;

/// <inheritdoc cref="ICommitMessageGenerator"/>
public sealed class CommitMessageGenerator(
    IAiProfileService profiles,
    IAiClientFactory clients,
    IAiModelCatalog? catalog = null) : ICommitMessageGenerator
{
    public Task<CommitMessageResult> GenerateAsync(RepositoryContext ctx, bool amend, CancellationToken ct)
    {
        var state = ctx.State;
        var gateway = ctx.Gateway;
        return Task.Run(
            () => GenerateAsync(gateway, state.Status, state.CurrentBranch?.Name ?? "HEAD", amend, ct),
            ct);
    }

    internal async Task<CommitMessageResult> GenerateAsync(
        IGitGateway gateway, RepositoryStatus status, string branch, bool amend, CancellationToken ct)
    {
        var profile = profiles.ActiveProfile
            ?? throw new AiException(AiErrorKind.NotConfigured, "No AI integration is active. Choose one in AI Integrations.");

        if (string.IsNullOrWhiteSpace(profile.Server))
            throw new AiException(AiErrorKind.NotConfigured, $"{profiles.DisplayLabel(profile)} has no Server address. Set one in AI Integrations.");

        var key = profiles.ResolveApiKey(profile);
        var keyless = profile.Kind == AiServiceKind.Custom && !profile.ReadApiKeyFromEnv && string.IsNullOrWhiteSpace(profile.ApiKey);
        if (key is null && !keyless)
            throw new AiException(AiErrorKind.MissingApiKey, $"{profiles.DisplayLabel(profile)} has no API key. Add one in AI Integrations.");

        if (profile.Kind == AiServiceKind.Custom && string.IsNullOrWhiteSpace(profile.Model))
            throw new AiException(AiErrorKind.NoModel, "Choose a model in AI Integrations.");

        var client = clients.For(profile.Kind);
        string? replaced = null;
        string? picked = null;
        var model = profile.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            model = picked = await SwitchModelAsync(profile, client, key, "", ct).ConfigureAwait(false);
        }

        // With a size limit, git's output is read only as far as the prompt can use.
        var cap = AiDiffCap.ToBytes(profile.DiffCapTokens);
        var patches = await gateway.GetStagedPatchesAsync(amend, cap, ct).ConfigureAwait(false);
        if (patches.Count == 0)
            throw new AiException(AiErrorKind.NothingStaged, "Stage changes before generating a commit message.");

        var files = amend || status.Staged.Count == 0 ? FilesFromPatches(patches) : FilesFromStatus(status.Staged);
        var prompt = CommitMessagePrompt.Build(branch, files, patches, cap, profile.AdditionalPrompt);

        using var op = AppLog.Operation("ai",
            $"commit message  profile={profile.Id} kind={profile.Kind} model={model} files={files.Count} promptBytes={Encoding.UTF8.GetByteCount(prompt)}");
        try
        {
            string reply;
            try
            {
                reply = await client.CompleteAsync(Request(profile, key, model, prompt), ct).ConfigureAwait(false);
            }
            catch (AiException ex) when (ex.Kind == AiErrorKind.ModelNotFound && profile.Kind == AiServiceKind.Gemini && picked is null)
            {
                replaced = model;
                picked = await SwitchModelAsync(profile, client, key, model, ct).ConfigureAwait(false);
                model = picked;
                try
                {
                    reply = await client.CompleteAsync(Request(profile, key, model, prompt), ct).ConfigureAwait(false);
                }
                catch (AiException retry) when (retry.Kind == AiErrorKind.ModelNotFound)
                {
                    throw Unavailable(replaced, retry.Details);
                }
            }
            catch (AiException ex) when (ex.Kind == AiErrorKind.ModelNotFound && picked is not null)
            {
                throw Unavailable(picked, ex.Details);
            }

            var message = CommitMessagePrompt.Clean(reply);
            return new CommitMessageResult(message, replaced, picked);
        }
        catch (AiException ex)
        {
            op.Outcome($"FAILED: {ex.Kind}");
            throw;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>Lists the service's models, picks the replacement and saves it to the profile
    /// before the caller retries, so the switch persists even if the retry fails. With no model
    /// set, the list is the first request to the service, so its failure (a bad Server, key or
    /// connection) is reported as it is rather than as a missing model.</summary>
    private async Task<string> SwitchModelAsync(
        AiServiceProfile profile, IAiChatClient client, string? key, string oldModel, CancellationToken ct)
    {
        IReadOnlyList<string> ids;
        try
        {
            ids = await client.ListModelsAsync(Request(profile, key, oldModel, ""), ct).ConfigureAwait(false);
        }
        catch (AiException ex) when (!string.IsNullOrWhiteSpace(oldModel))
        {
            throw Unavailable(oldModel, ex.Details ?? ex.Message);
        }

        catalog?.Remember(profile, ids);

        var picked = GeminiModelSelector.PickReplacement(ids, string.IsNullOrWhiteSpace(oldModel) ? null : oldModel);
        if (picked is null)
            throw Unavailable(oldModel, null);

        await profiles.UpdateAsync(profile.Id, p => p with { Model = picked }, ct).ConfigureAwait(false);
        return picked;
    }

    private static AiException Unavailable(string oldModel, string? details) =>
        new(AiErrorKind.ModelUnavailable,
            string.IsNullOrWhiteSpace(oldModel)
                ? "No model is set and no available model was found. Choose one in AI Integrations."
                : $"'{oldModel}' is no longer available and no replacement model was found. Choose one in AI Integrations.",
            details);

    private static AiChatRequest Request(AiServiceProfile profile, string? key, string model, string prompt) =>
        new(profile.Server, key, model, profile.ReasoningEffort, profile.ExtraHeaders, prompt);

    private static List<(string Status, string Path, string? OldPath)> FilesFromStatus(IReadOnlyList<FileStatusEntry> staged) =>
        staged.Select(f => (Letter(f.Kind), f.Path, f.OldPath)).ToList();

    private static string Letter(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => "A",
        FileChangeKind.Deleted => "D",
        FileChangeKind.Renamed => "R",
        FileChangeKind.Copied => "C",
        FileChangeKind.TypeChanged => "T",
        _ => "M",
    };

    /// <summary>The change list recovered from each patch's extended header, for an amend (the
    /// status list doesn't cover the amended commit's own changes). A file past the size limit has
    /// no patch to read and is listed as modified.</summary>
    private static List<(string Status, string Path, string? OldPath)> FilesFromPatches(IReadOnlyList<(string Path, string? Patch)> patches)
    {
        var files = new List<(string, string, string?)>(patches.Count);
        foreach (var (path, patch) in patches)
        {
            if (patch is null)
            {
                files.Add(("M", path, null));
                continue;
            }
            var header = patch.Length > 2048 ? patch[..2048] : patch;
            string? oldPath = null;
            string status;
            if (header.Contains("\nnew file mode", StringComparison.Ordinal))
                status = "A";
            else if (header.Contains("\ndeleted file mode", StringComparison.Ordinal))
                status = "D";
            else if (Header(header, "\nrename from ") is { } renamed)
                (status, oldPath) = ("R", renamed);
            else if (Header(header, "\ncopy from ") is { } copied)
                (status, oldPath) = ("C", copied);
            else if (header.Contains("\nold mode", StringComparison.Ordinal) && !header.Contains("\n@@", StringComparison.Ordinal))
                status = "T";
            else
                status = "M";
            files.Add((status, path, oldPath));
        }
        return files;
    }

    private static string? Header(string header, string prefix)
    {
        var start = header.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += prefix.Length;
        var end = header.IndexOf('\n', start);
        return (end < 0 ? header[start..] : header[start..end]).Trim();
    }
}
