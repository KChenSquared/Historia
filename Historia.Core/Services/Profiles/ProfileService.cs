namespace Historia.Services.Profiles;

/// <inheritdoc cref="IProfileService"/>
public sealed class ProfileService : IProfileService, IAsyncDisposable
{
    private sealed class ProfilesFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<GitProfile> Profiles { get; set; } = [];
        public List<ProfileUsageState> Usage { get; set; } = [];
    }

    private sealed class BindingsFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, BindingDto> Bindings { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record BindingDto(Guid? ProfileId, string LastKnownPath, DateTimeOffset BoundAt, bool LocalConfig = false);

    private sealed record ExportBundle
    {
        public int SchemaVersion { get; init; } = 1;
        public bool IncludesTokens { get; init; }
        public List<ExportProfile> Profiles { get; init; } = [];
    }

    private sealed record ExportProfile
    {
        public required GitProfile Profile { get; init; }
        public string? GitHubToken { get; init; }
        public string? GitLabToken { get; init; }
        public string? BitbucketToken { get; init; }
    }

    private readonly ISettingsService _settings;
    private readonly ICredentialVaultService _vault;
    private readonly IGitCliService _cli;

    private readonly AtomicFileWriter _profilesWriter;
    private readonly AtomicFileWriter _bindingsWriter;
    private readonly object _sync = new();

    // Serializes Create/Update/Delete for their whole duration, vault calls included: each of them
    // reads the profile list, awaits the vault, and only then writes back, so two overlapping ones
    // validated against the same stale list and could leave a token for a profile that no longer
    // exists. Not reentrant: Import calls Create and does not take it itself.
    private readonly SemaphoreSlim _mutation = new(1, 1);

    // Numbers the snapshots handed to the writers, taken under _sync with the snapshot itself, so
    // the writer can drop one that arrives after a newer one.
    private long _profilesSequence;
    private long _bindingsSequence;

    private readonly List<GitProfile> _profiles = [];
    private readonly List<ProfileUsageState> _usage = [];
    private readonly Dictionary<string, BindingDto> _bindings = new(StringComparer.Ordinal);

    public event EventHandler? ProfilesChanged;

    public ProfileService(ISettingsService settings, ICredentialVaultService vault, IGitCliService cli)
    {
        _settings = settings;
        _vault = vault;
        _cli = cli;
        _profilesWriter = new AtomicFileWriter(AppPaths.ProfilesFile);
        _bindingsWriter = new AtomicFileWriter(AppPaths.RepoBindingsFile);
        LoadProfiles();
        LoadBindings();
        EnsureGlobalProfile();
    }

    // ── Global profile ──

    /// <summary>The single reserved global profile — always present after construction.</summary>
    public GitProfile GetGlobalProfile()
    {
        lock (_sync) return _profiles.First(p => p.IsGlobal);
    }

    private void EnsureGlobalProfile()
    {
        bool added = false;
        lock (_sync)
        {
            if (!_profiles.Any(p => p.IsGlobal))
            {
                var id = Guid.NewGuid();
                _profiles.Insert(0, new GitProfile
                {
                    Id = id,
                    IsGlobal = true,
                    DisplayName = "Global",
                    GitUserName = "",
                    GitUserEmail = "",
                    CreatedAt = DateTimeOffset.MinValue,
                });
                _usage.Add(new ProfileUsageState { ProfileId = id, LastUsedAt = DateTimeOffset.Now, UseCount = 0 });
                added = true;
            }
        }
        if (added)
            PersistProfiles();
    }

    /// <summary>refresh the global profile's identity from <c>git config --global</c>.
    /// Call once at startup; a no-op when nothing changed or git is unavailable.</summary>
    public async Task SyncGlobalIdentityAsync(CancellationToken ct = default)
    {
        if (!_cli.IsGitAvailable)
            return;

        var name = await ReadGlobalConfigAsync("user.name", ct).ConfigureAwait(false) ?? "";
        var email = await ReadGlobalConfigAsync("user.email", ct).ConfigureAwait(false) ?? "";

        bool changed = false;
        lock (_sync)
        {
            var idx = _profiles.FindIndex(p => p.IsGlobal);
            if (idx < 0)
                return;
            var g = _profiles[idx];
            if (!string.Equals(g.GitUserName, name, StringComparison.Ordinal)
                || !string.Equals(g.GitUserEmail, email, StringComparison.Ordinal))
            {
                _profiles[idx] = g with { GitUserName = name, GitUserEmail = email };
                changed = true;
            }
        }

        if (changed)
        {
            PersistProfiles();
            RaiseProfilesChanged();
        }
    }

    private async Task<string?> ReadGlobalConfigAsync(string key, CancellationToken ct)
    {
        var result = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "--global", "--get", key],
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Timeout = TimeSpan.FromSeconds(10),
        }, ct).ConfigureAwait(false);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardOutput.Trim() : null;
    }

    // ── CRUD ──

    public IReadOnlyList<GitProfile> GetAllProfiles()
    {
        // the global profile always sorts first, then the rest by creation order.
        lock (_sync) return _profiles.OrderByDescending(p => p.IsGlobal).ThenBy(p => p.CreatedAt).ToList();
    }

    public GitProfile? GetProfileById(Guid id)
    {
        lock (_sync) return _profiles.FirstOrDefault(p => p.Id == id);
    }

    public ProfileUsageState? GetUsage(Guid id)
    {
        lock (_sync) return _usage.FirstOrDefault(u => u.ProfileId == id);
    }

    public async Task<GitProfile> CreateProfileAsync(GitProfileCreateRequest request, CancellationToken ct = default)
    {
        GitProfile profile;
        await _mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            profile = await CreateCoreAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutation.Release();
        }

        RaiseProfilesChanged();
        return profile;
    }

    private async Task<GitProfile> CreateCoreAsync(GitProfileCreateRequest request, CancellationToken ct)
    {
        List<GitProfile> snapshot;
        lock (_sync) snapshot = _profiles.ToList();

        ProfileValidation.ValidateNew(
            request.DisplayName, request.GitUserName, request.GitUserEmail,
            request.SshKeyPath, request.GpgSigningKeyId, snapshot);

        var id = Guid.NewGuid();
        var profile = new GitProfile
        {
            Id = id,
            DisplayName = request.DisplayName.Trim(),
            GitUserName = request.GitUserName.Trim(),
            GitUserEmail = request.GitUserEmail.Trim(),
            AvatarPath = request.AvatarPath,
            SshKeyPath = request.SshKeyPath,
            GpgSigningKeyId = request.GpgSigningKeyId,
            SignCommits = request.SignCommits,
            GitHubTokenRef = await StoreTokenIfPresentAsync(id, RemoteHost.GitHub, request.GitHubToken, ct).ConfigureAwait(false),
            GitLabTokenRef = await StoreTokenIfPresentAsync(id, RemoteHost.GitLab, request.GitLabToken, ct).ConfigureAwait(false),
            BitbucketTokenRef = await StoreTokenIfPresentAsync(id, RemoteHost.Bitbucket, request.BitbucketToken, ct).ConfigureAwait(false),
            CreatedAt = DateTimeOffset.Now,
        };

        bool isFirst;
        lock (_sync)
        {
            _profiles.Add(profile);
            _usage.Add(new ProfileUsageState { ProfileId = id, LastUsedAt = DateTimeOffset.Now, UseCount = 0 });
            isFirst = _profiles.Count == 1;
        }

        PersistProfiles();

        // the default stays the global profile (null) unless the user explicitly changes
        // it — creating a profile never makes it the default.
        _ = isFirst;

        return profile;
    }

    public async Task<GitProfile> UpdateProfileAsync(Guid id, GitProfileUpdateRequest request, CancellationToken ct = default)
    {
        GitProfile updated;
        await _mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            updated = await UpdateCoreAsync(id, request, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutation.Release();
        }

        RaiseProfilesChanged();
        return updated;
    }

    private async Task<GitProfile> UpdateCoreAsync(Guid id, GitProfileUpdateRequest request, CancellationToken ct)
    {
        GitProfile current;
        List<GitProfile> snapshot;
        lock (_sync)
        {
            current = _profiles.FirstOrDefault(p => p.Id == id)
                      ?? throw new ProfileValidationException("Profile not found.");
            snapshot = _profiles.ToList();
        }

        var updated = current with
        {
            DisplayName = (request.DisplayName ?? current.DisplayName).Trim(),
            GitUserName = (request.GitUserName ?? current.GitUserName).Trim(),
            GitUserEmail = (request.GitUserEmail ?? current.GitUserEmail).Trim(),
            AvatarPath = request.AvatarPath ?? current.AvatarPath,
            SshKeyPath = request.SshKeyPath ?? current.SshKeyPath,
            GpgSigningKeyId = request.GpgSigningKeyId ?? current.GpgSigningKeyId,
            SignCommits = request.SignCommits ?? current.SignCommits,
        };

        ProfileValidation.ValidateUpdate(
            id, updated.DisplayName, updated.GitUserName, updated.GitUserEmail,
            updated.SshKeyPath, updated.GpgSigningKeyId, snapshot);

        updated = updated with
        {
            GitHubTokenRef = await ApplyTokenChangeAsync(id, RemoteHost.GitHub, request.GitHubToken, current.GitHubTokenRef, ct).ConfigureAwait(false),
            GitLabTokenRef = await ApplyTokenChangeAsync(id, RemoteHost.GitLab, request.GitLabToken, current.GitLabTokenRef, ct).ConfigureAwait(false),
            BitbucketTokenRef = await ApplyTokenChangeAsync(id, RemoteHost.Bitbucket, request.BitbucketToken, current.BitbucketTokenRef, ct).ConfigureAwait(false),
        };

        bool stillThere;
        lock (_sync)
        {
            var idx = _profiles.FindIndex(p => p.Id == id);
            stillThere = idx >= 0;
            if (stillThere)
                _profiles[idx] = updated;
        }

        if (!stillThere)
        {
            // Gone while the vault was being written: remove what this call stored so no token
            // outlives its profile, and report the failure rather than a profile that isn't there.
            await DeleteAllTokensAsync(updated, CancellationToken.None).ConfigureAwait(false);
            throw new ProfileValidationException("Profile not found.");
        }

        PersistProfiles();
        return updated;
    }

    public async Task<ProfileDeleteResult> DeleteProfileAsync(Guid id, CancellationToken ct = default)
    {
        ProfileDeleteResult result;
        await _mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await DeleteCoreAsync(id, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutation.Release();
        }

        if (result is ProfileDeleteResult.Deleted or ProfileDeleteResult.DeletedClearedDefault)
            RaiseProfilesChanged();
        return result;
    }

    private async Task<ProfileDeleteResult> DeleteCoreAsync(Guid id, CancellationToken ct)
    {
        GitProfile? profile;
        bool wasDefault;

        lock (_sync)
        {
            profile = _profiles.FirstOrDefault(p => p.Id == id);
            if (profile is null)
                return ProfileDeleteResult.NotFound;
            if (profile.IsGlobal)
                return ProfileDeleteResult.CannotDeleteGlobal; // the global profile is undeletable.

            _profiles.Remove(profile);
            _usage.RemoveAll(u => u.ProfileId == id);

            // Drop every per-repo selection row that named it. Local-config marker rows
            // (ProfileId == null) are untouched.
            var orphanedKeys = _bindings.Where(kv => kv.Value.ProfileId == id).Select(kv => kv.Key).ToList();
            foreach (var key in orphanedKeys)
                _bindings.Remove(key);

            wasDefault = _settings.Current.DefaultProfileId == id;
        }

        // Delete associated tokens from the vault.
        await DeleteAllTokensAsync(profile, ct).ConfigureAwait(false);

        PersistProfiles();
        PersistBindings();

        // deleting the current default clears it — global becomes the automatic default
        // again. No other profile is auto-promoted.
        if (wasDefault)
            await _settings.UpdateAsync(s => s with { DefaultProfileId = null }, ct).ConfigureAwait(false);

        return wasDefault ? ProfileDeleteResult.DeletedClearedDefault : ProfileDeleteResult.Deleted;
    }

    // ── Default profile ──

    /// <summary>The default profile: the one named by <c>DefaultProfileId</c>, else the global
    /// profile. Never null after construction.</summary>
    public GitProfile? GetDefaultProfile()
    {
        var defaultId = _settings.Current.DefaultProfileId;
        lock (_sync)
        {
            if (defaultId is { } id)
            {
                var match = _profiles.FirstOrDefault(p => p.Id == id);
                if (match is not null) return match;
            }
            return _profiles.FirstOrDefault(p => p.IsGlobal);
        }
    }

    public Task SetDefaultProfileAsync(Guid? profileId, CancellationToken ct = default)
    {
        // the global profile as default is stored as null (the automatic case).
        Guid? store = profileId;
        lock (_sync)
        {
            if (profileId is { } id && _profiles.FirstOrDefault(p => p.Id == id)?.IsGlobal == true)
                store = null;
        }
        return _settings.UpdateAsync(s => s with { DefaultProfileId = store }, ct);
    }

    // ── Per-repo selection ──

    public GitProfile? GetProfileForRepository(RepoIdentityKey repoKey)
    {
        lock (_sync)
        {
            if (_bindings.TryGetValue(repoKey.Value, out var binding) && !binding.LocalConfig && binding.ProfileId is { } pid)
                return _profiles.FirstOrDefault(p => p.Id == pid);
            return null;
        }
    }

    public bool IsLocalConfigSelected(RepoIdentityKey repoKey)
    {
        lock (_sync)
            return _bindings.TryGetValue(repoKey.Value, out var b) && b.LocalConfig;
    }

    public Task BindProfileToRepositoryAsync(RepoIdentityKey repoKey, string displayPath, Guid profileId, CancellationToken ct = default)
    {
        lock (_sync)
            _bindings[repoKey.Value] = new BindingDto(profileId, displayPath, DateTimeOffset.Now);
        PersistBindings();
        RaiseProfilesChanged();
        return Task.CompletedTask;
    }

    /// <summary>pin this repo to <i>Local config</i> — an explicit marker row, distinct
    /// from the absence of a row.</summary>
    public Task SetLocalConfigForRepositoryAsync(RepoIdentityKey repoKey, string displayPath, CancellationToken ct = default)
    {
        lock (_sync)
            _bindings[repoKey.Value] = new BindingDto(null, displayPath, DateTimeOffset.Now, LocalConfig: true);
        PersistBindings();
        RaiseProfilesChanged();
        return Task.CompletedTask;
    }

    public Task UnbindRepositoryAsync(RepoIdentityKey repoKey, CancellationToken ct = default)
    {
        lock (_sync)
            _bindings.Remove(repoKey.Value);
        PersistBindings();
        RaiseProfilesChanged();
        return Task.CompletedTask;
    }

    public IReadOnlyDictionary<RepoIdentityKey, RepoProfileBinding> GetAllRepoBindings()
    {
        lock (_sync)
        {
            return _bindings
                .Where(kv => kv.Value.ProfileId is { })
                .ToDictionary(
                    kv => new RepoIdentityKey(kv.Key),
                    kv => new RepoProfileBinding
                    {
                        ProfileId = kv.Value.ProfileId!.Value,
                        LastKnownPath = kv.Value.LastKnownPath,
                        BoundAt = kv.Value.BoundAt,
                    });
        }
    }

    public IReadOnlyList<RepoProfileBinding> GetStaleBindings()
    {
        lock (_sync)
        {
            return _bindings.Values
                .Where(b => b.ProfileId is { } && !SafeDirExists(b.LastKnownPath))
                .Select(b => new RepoProfileBinding
                {
                    ProfileId = b.ProfileId!.Value,
                    LastKnownPath = b.LastKnownPath,
                    BoundAt = b.BoundAt,
                })
                .ToList();
        }
    }

    public Task<int> CleanStaleBindingsAsync(CancellationToken ct = default)
    {
        int removed;
        lock (_sync)
        {
            var stale = _bindings.Where(kv => !SafeDirExists(kv.Value.LastKnownPath)).Select(kv => kv.Key).ToList();
            foreach (var key in stale)
                _bindings.Remove(key);
            removed = stale.Count;
        }

        if (removed > 0)
        {
            PersistBindings();
            RaiseProfilesChanged();
        }

        return Task.FromResult(removed);
    }

    // ── Resolution cascade ──

    /// <summary>
    /// (1) an explicit <c>repo-profiles.json</c> row — a profile, or the <i>Local config</i>
    /// marker (→ null); a row naming a missing profile is ignored. (2) no row + a non-global
    /// default → that default. (3) otherwise → null (<i>Local config</i>).
    /// </summary>
    public GitProfile? ResolveActiveProfile(RepoIdentityKey? repoKey = null)
    {
        if (repoKey is { } key)
        {
            lock (_sync)
            {
                if (_bindings.TryGetValue(key.Value, out var binding))
                {
                    if (binding.LocalConfig)
                        return null;
                    if (binding.ProfileId is { } pid)
                    {
                        var bound = _profiles.FirstOrDefault(p => p.Id == pid);
                        if (bound is not null)
                            return bound;
                        // row names a missing profile → fall through to the default
                    }
                }
            }
        }

        var def = GetDefaultProfile();
        return def is { IsGlobal: false } ? def : null;
    }

    // ── Application ──

    public async Task ApplyProfileToGitConfigAsync(GitProfile profile, string repoPath, GitConfigScope scope, CancellationToken ct = default)
    {
        var scopeArg = scope switch
        {
            GitConfigScope.Global => "--global",
            GitConfigScope.System => "--system",
            _ => "--local",
        };

        await SetConfigAsync(repoPath, scopeArg, "user.name", profile.GitUserName, ct).ConfigureAwait(false);
        await SetConfigAsync(repoPath, scopeArg, "user.email", profile.GitUserEmail, ct).ConfigureAwait(false);

        if (profile.SignCommits && !string.IsNullOrWhiteSpace(profile.GpgSigningKeyId))
        {
            await SetConfigAsync(repoPath, scopeArg, "user.signingkey", profile.GpgSigningKeyId, ct).ConfigureAwait(false);
            await SetConfigAsync(repoPath, scopeArg, "commit.gpgsign", "true", ct).ConfigureAwait(false);
        }
        else
        {
            await SetConfigAsync(repoPath, scopeArg, "commit.gpgsign", "false", ct).ConfigureAwait(false);
        }
    }

    public Task TouchProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var usage = _usage.FirstOrDefault(u => u.ProfileId == profileId);
            if (usage is null)
            {
                usage = new ProfileUsageState { ProfileId = profileId, LastUsedAt = DateTimeOffset.Now, UseCount = 0 };
                _usage.Add(usage);
            }

            usage.LastUsedAt = DateTimeOffset.Now;
            usage.UseCount++;
        }

        PersistProfiles();
        return Task.CompletedTask;
    }

    public async Task<string?> GetTokenAsync(Guid profileId, RemoteHost host, CancellationToken ct = default)
    {
        var profile = GetProfileById(profileId);
        var tokenRef = profile is null ? null : TokenRefFor(profile, host);
        return tokenRef is null ? null : await _vault.RetrieveTokenAsync(tokenRef, ct).ConfigureAwait(false);
    }

    // ── Export / import ──

    public async Task ExportProfilesAsync(string exportFilePath, bool includeTokens, string? password, CancellationToken ct = default)
    {
        List<GitProfile> profiles;
        lock (_sync) profiles = _profiles.ToList();

        if (!includeTokens)
        {
            var stripped = profiles
                .Select(p => p with { GitHubTokenRef = null, GitLabTokenRef = null, BitbucketTokenRef = null })
                .ToList();
            var json = JsonSerializer.Serialize(
                new ExportBundle { IncludesTokens = false, Profiles = stripped.Select(p => new ExportProfile { Profile = p }).ToList() },
                HistoriaJson.Options);
            await File.WriteAllTextAsync(exportFilePath, json, ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A password is required to export profiles with tokens.", nameof(password));

        var withTokens = new List<ExportProfile>();
        foreach (var p in profiles)
        {
            withTokens.Add(new ExportProfile
            {
                Profile = p,
                GitHubToken = await GetTokenAsync(p.Id, RemoteHost.GitHub, ct).ConfigureAwait(false),
                GitLabToken = await GetTokenAsync(p.Id, RemoteHost.GitLab, ct).ConfigureAwait(false),
                BitbucketToken = await GetTokenAsync(p.Id, RemoteHost.Bitbucket, ct).ConfigureAwait(false),
            });
        }

        var plaintext = JsonSerializer.Serialize(
            new ExportBundle { IncludesTokens = true, Profiles = withTokens }, HistoriaJson.Options);
        var blob = ProfileExportCrypto.Encrypt(plaintext, password);
        await File.WriteAllBytesAsync(exportFilePath, blob, ct).ConfigureAwait(false);
    }

    public async Task ImportProfilesAsync(string importFilePath, string? password, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(importFilePath, ct).ConfigureAwait(false);
        string json;

        if (LooksEncrypted(bytes))
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("This export is encrypted; a password is required.", nameof(password));
            json = ProfileExportCrypto.Decrypt(bytes, password);
        }
        else
        {
            json = System.Text.Encoding.UTF8.GetString(bytes);
        }

        var bundle = JsonSerializer.Deserialize<ExportBundle>(json, HistoriaJson.Options)
                     ?? throw new InvalidDataException("Export file could not be read.");

        foreach (var entry in bundle.Profiles)
        {
            var name = UniqueName(entry.Profile.DisplayName);
            await CreateProfileAsync(new GitProfileCreateRequest
            {
                DisplayName = name,
                GitUserName = entry.Profile.GitUserName,
                GitUserEmail = entry.Profile.GitUserEmail,
                AvatarPath = entry.Profile.AvatarPath,
                SshKeyPath = entry.Profile.SshKeyPath,
                GpgSigningKeyId = entry.Profile.GpgSigningKeyId,
                SignCommits = entry.Profile.SignCommits,
                GitHubToken = entry.GitHubToken,
                GitLabToken = entry.GitLabToken,
                BitbucketToken = entry.BitbucketToken,
            }, ct).ConfigureAwait(false);
        }
    }

    // ── Internals ──

    private async Task SetConfigAsync(string repoPath, string scopeArg, string key, string value, CancellationToken ct)
    {
        if (!_cli.IsGitAvailable)
            return;

        var result = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", scopeArg, key, value],
            WorkingDirectory = repoPath,
            Timeout = TimeSpan.FromSeconds(15),
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, $"Write git config {key}");
    }

    private async Task<string?> StoreTokenIfPresentAsync(Guid id, RemoteHost host, string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        var key = VaultKey(id, host);
        await _vault.StoreTokenAsync(key, token, ct).ConfigureAwait(false);
        return key;
    }

    private async Task<string?> ApplyTokenChangeAsync(Guid id, RemoteHost host, string? incoming, string? existingRef, CancellationToken ct)
    {
        if (incoming is null)
            return existingRef; // Not supplied → unchanged.

        var key = VaultKey(id, host);
        if (incoming.Length == 0)
        {
            await _vault.DeleteTokenAsync(key, ct).ConfigureAwait(false);
            return null; // Cleared.
        }

        await _vault.StoreTokenAsync(key, incoming, ct).ConfigureAwait(false);
        return key;
    }

    private async Task DeleteAllTokensAsync(GitProfile profile, CancellationToken ct)
    {
        foreach (var host in Enum.GetValues<RemoteHost>())
        {
            var tokenRef = TokenRefFor(profile, host);
            if (tokenRef is not null)
                await _vault.DeleteTokenAsync(tokenRef, ct).ConfigureAwait(false);
        }
    }

    private static string VaultKey(Guid id, RemoteHost host) => $"{host.ToString().ToLowerInvariant()}/profile-{id:N}";

    private static string? TokenRefFor(GitProfile profile, RemoteHost host) => host switch
    {
        RemoteHost.GitHub => profile.GitHubTokenRef,
        RemoteHost.GitLab => profile.GitLabTokenRef,
        RemoteHost.Bitbucket => profile.BitbucketTokenRef,
        _ => null,
    };

    private string UniqueName(string desired)
    {
        lock (_sync)
        {
            if (_profiles.All(p => !string.Equals(p.DisplayName, desired, StringComparison.OrdinalIgnoreCase)))
                return desired;
            for (var i = 2; ; i++)
            {
                var candidate = $"{desired} ({i})";
                if (_profiles.All(p => !string.Equals(p.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
                    return candidate;
            }
        }
    }

    private void LoadProfiles()
    {
        try
        {
            if (!File.Exists(AppPaths.ProfilesFile))
                return;
            var file = JsonSerializer.Deserialize<ProfilesFile>(File.ReadAllText(AppPaths.ProfilesFile), HistoriaJson.Options);
            if (file is null)
                return;
            _profiles.AddRange(file.Profiles);
            _usage.AddRange(file.Usage);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Start empty on a corrupt file.
        }
    }

    private void LoadBindings()
    {
        try
        {
            if (!File.Exists(AppPaths.RepoBindingsFile))
                return;
            var file = JsonSerializer.Deserialize<BindingsFile>(File.ReadAllText(AppPaths.RepoBindingsFile), HistoriaJson.Options);
            if (file is null)
                return;
            foreach (var (k, v) in file.Bindings)
                _bindings[k] = v;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Start empty on a corrupt file.
        }
    }

    private void PersistProfiles()
    {
        ProfilesFile file;
        long sequence;
        lock (_sync)
        {
            file = new ProfilesFile { Profiles = _profiles.ToList(), Usage = _usage.ToList() };
            sequence = ++_profilesSequence;
        }
        _profilesWriter.QueueWrite(JsonSerializer.Serialize(file, HistoriaJson.Options), sequence);
    }

    private void PersistBindings()
    {
        BindingsFile file;
        long sequence;
        lock (_sync)
        {
            file = new BindingsFile { Bindings = new Dictionary<string, BindingDto>(_bindings, StringComparer.Ordinal) };
            sequence = ++_bindingsSequence;
        }
        _bindingsWriter.QueueWrite(JsonSerializer.Serialize(file, HistoriaJson.Options), sequence);
    }

    private void RaiseProfilesChanged() => ProfilesChanged?.Invoke(this, EventArgs.Empty);

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private static bool LooksEncrypted(byte[] bytes)
        => bytes.Length >= 10 && bytes[0] == (byte)'P' && bytes[1] == (byte)'O' && bytes[2] == (byte)'T'
           && bytes[3] == (byte)'A' && bytes[4] == (byte)'T' && bytes[5] == (byte)'O';

    public async ValueTask DisposeAsync()
    {
        Historia.Diagnostics.PerfLog.Mark("ProfileService.DisposeAsync start");
        await _profilesWriter.DisposeAsync().ConfigureAwait(false);
        await _bindingsWriter.DisposeAsync().ConfigureAwait(false);
        Historia.Diagnostics.PerfLog.Mark("ProfileService.DisposeAsync done");
    }
}
