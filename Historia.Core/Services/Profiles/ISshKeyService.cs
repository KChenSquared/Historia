namespace Historia.Services.Profiles;

/// <summary>The private + public key paths for a profile, and the public key text if present.</summary>
public sealed record SshKeyInfo(string PrivatePath, string PublicPath, string? PublicKey)
{
    public bool Exists => PublicKey is not null;
}

/// <summary>
/// Generates and reads the <c>ed25519</c> SSH key Historia can create for a profile. The
/// key is <b>always</b> generated with an empty passphrase — Historia never collects a key
/// passphrase (safety rules); a user who wants one runs <c>ssh-keygen</c> themselves.
/// </summary>
public interface ISshKeyService
{
    /// <summary>Where this profile's Historia-managed key pair lives (may not exist yet).</summary>
    SshKeyInfo PathsFor(Guid profileId);

    /// <summary>Generate <c>id_ed25519</c> (+ <c>.pub</c>) under the profile's key folder,
    /// overwriting any existing Historia-managed pair. Returns the new key info. Throws if
    /// <c>ssh-keygen</c> is not on PATH.</summary>
    Task<SshKeyInfo> GenerateEd25519Async(Guid profileId, string comment, CancellationToken ct = default);

    /// <summary>Read the <c>.pub</c> contents for this profile's key, or null if absent.</summary>
    Task<string?> ReadPublicKeyAsync(Guid profileId, CancellationToken ct = default);
}

/// <inheritdoc cref="ISshKeyService"/>
public sealed class SshKeyService : ISshKeyService
{
    public SshKeyInfo PathsFor(Guid profileId)
    {
        var dir = Path.Combine(AppPaths.KeysDirectory, profileId.ToString("N"));
        var priv = Path.Combine(dir, "id_ed25519");
        var pub = priv + ".pub";
        string? pubText = null;
        try { if (File.Exists(pub)) pubText = File.ReadAllText(pub).Trim(); }
        catch { /* unreadable — treat as absent */ }
        return new SshKeyInfo(priv, pub, pubText);
    }

    public async Task<SshKeyInfo> GenerateEd25519Async(Guid profileId, string comment, CancellationToken ct = default)
    {
        var info = PathsFor(profileId);
        var dir = Path.GetDirectoryName(info.PrivatePath)!;
        Directory.CreateDirectory(dir);

        // Overwrite: ssh-keygen refuses to clobber, so remove first.
        foreach (var f in new[] { info.PrivatePath, info.PublicPath })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }

        var psi = new ProcessStartInfo("ssh-keygen")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("ed25519");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(info.PrivatePath);
        psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(string.IsNullOrWhiteSpace(comment) ? "Historia" : comment);
        psi.ArgumentList.Add("-N"); psi.ArgumentList.Add("");     // empty passphrase (by design)
        psi.ArgumentList.Add("-q");

        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException(); }
        catch
        {
            throw new InvalidOperationException(
                "ssh-keygen was not found. Install OpenSSH (or Git for Windows) and try again, " +
                "or generate the key yourself and point the profile at it.");
        }

        using var owned = proc;
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var err = (await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            throw new InvalidOperationException($"ssh-keygen failed: {(err.Length > 0 ? err : "exit " + proc.ExitCode)}");
        }

        return PathsFor(profileId);
    }

    public Task<string?> ReadPublicKeyAsync(Guid profileId, CancellationToken ct = default)
        => Task.FromResult(PathsFor(profileId).PublicKey);
}
