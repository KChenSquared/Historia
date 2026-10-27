namespace Historia.Services.PowerTools;

/// <inheritdoc cref="IConflictService"/>
public sealed partial class ConflictService(IGitCliService cli, ISettingsService settings) : IConflictService
{
    private readonly UnmergedIndexReader _unmergedReader = new(cli);

    // 5 MB / 50 000 lines: file diff and conflict working copy size budgets.
    private const long MaxWorkingCopyBytes = 5 * 1024 * 1024;
    private const int MaxWorkingCopyLines = 50_000;

    public async Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(RepositoryContext context, CancellationToken ct = default)
    {
        var conflicts = await _unmergedReader.ReadAsync(context.RepositoryPath, null, ct).ConfigureAwait(false);
        var result = new List<ConflictFile>(conflicts.Count);

        static bool IsGitLink(UnmergedPathConflict c) =>
            c.Ancestor?.Mode == "160000" || c.Ours?.Mode == "160000" || c.Theirs?.Mode == "160000";

        // Every stage blob of every non-submodule conflict, classified in a few batched spawns.
        var binaryBlobs = await _unmergedReader.FindBinaryBlobsAsync(
            context.RepositoryPath,
            conflicts.Where(c => !IsGitLink(c))
                .SelectMany(c => new[] { c.Ours, c.Theirs, c.Ancestor })
                .OfType<UnmergedEntry>(),
            ct).ConfigureAwait(false);

        foreach (var conflict in conflicts)
        {
            var isSubmodule = IsGitLink(conflict);
            var isBinary = !isSubmodule
                && new[] { conflict.Ours, conflict.Theirs, conflict.Ancestor }
                    .Any(e => e is not null && binaryBlobs.Contains(e.Id));

            result.Add(new ConflictFile
            {
                FilePath = conflict.Path,
                Status = DeriveStatus(conflict),
                IsBinary = isBinary,
                IsSubmodule = isSubmodule,
            });
        }

        return result;
    }

    public async Task<ConflictWorkingCopy> LoadWorkingCopyAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
    {
        var full = Path.Combine(context.RepositoryPath, filePath);

        // Submodules (directories) are never read as text files.
        if (Directory.Exists(full))
        {
            return new ConflictWorkingCopy
            {
                Path = filePath,
                DiskHash = string.Empty,
                IsBinary = true,
            };
        }

        if (!File.Exists(full))
        {
            return new ConflictWorkingCopy
            {
                Path = filePath,
                DiskHash = string.Empty,
            };
        }

        var info = new FileInfo(full);
        if (info.Length > MaxWorkingCopyBytes)
        {
            var largeHash = await HashFileAsync(full, ct).ConfigureAwait(false);
            return new ConflictWorkingCopy
            {
                Path = filePath,
                DiskHash = largeHash,
                ExceedsBudget = true,
            };
        }

        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        var diskHash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        if (!TextFileFormat.TryDetect(bytes, out var format, out var text))
        {
            var isBinary = bytes.Contains((byte)0);
            return new ConflictWorkingCopy
            {
                Path = filePath,
                DiskHash = diskHash,
                IsBinary = isBinary,
                UnsupportedEncoding = !isBinary,
            };
        }

        if (CountLines(text) > MaxWorkingCopyLines)
        {
            return new ConflictWorkingCopy
            {
                Path = filePath,
                DiskHash = diskHash,
                Format = format,
                ExceedsBudget = true,
            };
        }

        var attrResult = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["check-attr", "conflict-marker-size", "--", filePath],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);
        var markerSize = ParseMarkerSize(attrResult.StandardOutput);

        return new ConflictWorkingCopy
        {
            Path = filePath,
            Text = text,
            Format = format,
            DiskHash = diskHash,
            MarkerSize = markerSize,
        };
    }

    public Task<string> GetDiskHashAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        => HashFileAsync(Path.Combine(context.RepositoryPath, filePath), ct);

    public async Task<ResolutionSaveResult> SaveResolutionAsync(
        RepositoryContext context,
        string filePath,
        string text,
        TextFileFormat format,
        string expectedDiskHash,
        bool stage,
        CancellationToken ct = default)
    {
        if (format.Encoding is null)
        {
            throw new HistoriaGitException(GitErrorKind.Unknown, $"'{filePath}' has no detected text format and cannot be saved as text.");
        }

        using var _ = context.BeginSelfOperation();
        var full = Path.Combine(context.RepositoryPath, filePath);

        if (Directory.Exists(full))
        {
            throw new HistoriaGitException(GitErrorKind.Unknown, $"'{filePath}' is a directory (submodule) and cannot be saved as text.");
        }

        var currentHash = File.Exists(full)
            ? Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false)))
            : string.Empty;

        if (!string.Equals(currentHash, expectedDiskHash, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolutionSaveResult { Saved = false, DiskHash = currentHash };
        }

        var encoded = format.Encode(text);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var newHash = Convert.ToHexStringLower(SHA256.HashData(encoded));

        var replaced = await WriteReplacingAsync(full, encoded, expectedDiskHash, ct).ConfigureAwait(false);
        if (replaced is not null)
            return new ResolutionSaveResult { Saved = false, DiskHash = replaced };

        if (stage)
        {
            var add = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
            {
                Arguments = ["add", "--", filePath],
                WorkingDirectory = context.RepositoryPath,
                // No wall clock: hooks and large tree updates run long, and a kill midway leaves a
                // half-applied tree or sequencer and a stale index.lock.
                // Cancelling stops it only before git starts; once running it is never killed.
                Timeout = Timeout.InfiniteTimeSpan,
            }, innerCt), ct).ConfigureAwait(false);
            if (!add.IsSuccess)
            {
                return new ResolutionSaveResult
                {
                    Saved = true,
                    DiskHash = newHash,
                    StageError = HistoriaGitException.FromCli(add, "Save resolution (stage)")
                };
            }
        }

        return new ResolutionSaveResult { Saved = true, DiskHash = newHash };
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> through a sibling temp file that then replaces the target, so a
    /// crash or a full disk never leaves a truncated working file, and the target keeps its
    /// attributes and ACLs. Re-checks the on-disk hash just before the replace
    /// and returns the differing hash (nothing written) when the file changed meanwhile; null on success.
    /// A symlinked path is written in place so the link survives.
    /// </summary>
    private static async Task<string?> WriteReplacingAsync(string full, byte[] bytes, string expectedDiskHash, CancellationToken ct)
    {
        var existing = new FileInfo(full);
        if (existing.LinkTarget is not null)
        {
            await File.WriteAllBytesAsync(full, bytes, ct).ConfigureAwait(false);
            return null;
        }

        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".historia-save-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows() && existing.Exists)
                File.SetUnixFileMode(temp, File.GetUnixFileMode(full));

            var currentHash = await HashFileAsync(full, ct).ConfigureAwait(false);
            if (!string.Equals(currentHash, expectedDiskHash, StringComparison.OrdinalIgnoreCase))
                return currentHash;

            // Replace, not rename-over: ReplaceFile keeps the destination's attributes and ACLs.
            if (File.Exists(full))
                File.Replace(temp, full, destinationBackupFileName: null);
            else
                File.Move(temp, full);
            return null;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private static async Task<string> HashFileAsync(string fullPath, CancellationToken ct)
    {
        if (Directory.Exists(fullPath))
            return string.Empty;

        try
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            var hashBytes = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return Convert.ToHexStringLower(hashBytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return string.Empty;
        }
    }

    public async Task ResolveWithSideAsync(RepositoryContext context, string filePath, ConflictSide side, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var conflicts = await _unmergedReader.ReadAsync(context.RepositoryPath, filePath, ct).ConfigureAwait(false);
        var conflict = conflicts.FirstOrDefault(c => string.Equals(c.Path, filePath, StringComparison.Ordinal))
            ?? throw new HistoriaGitException(GitErrorKind.RefNotFound, $"'{filePath}' is not a conflicted path.");

        var isSubmodule = (conflict.Ours?.Mode == "160000")
                       || (conflict.Theirs?.Mode == "160000")
                       || (conflict.Ancestor?.Mode == "160000");

        var chosenEntry = side == ConflictSide.Ours ? conflict.Ours : conflict.Theirs;
        var chosenStagePresent = chosenEntry is not null;
        var chosenStageSha = chosenEntry?.Id.ToString() ?? string.Empty;

        if (isSubmodule)
        {
            if (chosenStagePresent && !string.IsNullOrEmpty(chosenStageSha))
            {
                var update = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["update-index", "--cacheinfo", $"160000,{chosenStageSha},{filePath}"],
                    WorkingDirectory = context.RepositoryPath,
                    Timeout = Timeout.InfiniteTimeSpan,
                }, innerCt), ct).ConfigureAwait(false);
                if (!update.IsSuccess)
                    throw HistoriaGitException.FromCli(update, "Resolve submodule side");
            }
            else
            {
                var rm = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["rm", "--cached", "--", filePath],
                    WorkingDirectory = context.RepositoryPath,
                    Timeout = Timeout.InfiniteTimeSpan,
                }, innerCt), ct).ConfigureAwait(false);
                if (!rm.IsSuccess)
                    throw HistoriaGitException.FromCli(rm, "Resolve submodule side (rm)");
            }
            return;
        }

        if (chosenStagePresent)
        {
            var sideArg = side == ConflictSide.Ours ? "--ours" : "--theirs";
            await context.WithWriteLockAsync(async innerCt =>
            {
                var checkout = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["checkout", sideArg, "--", filePath],
                    WorkingDirectory = context.RepositoryPath,
                    Timeout = Timeout.InfiniteTimeSpan,
                }, innerCt).ConfigureAwait(false);
                if (!checkout.IsSuccess)
                    throw HistoriaGitException.FromCli(checkout, $"Resolve ({sideArg})");

                var add = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["add", "--", filePath],
                    WorkingDirectory = context.RepositoryPath,
                    Timeout = Timeout.InfiniteTimeSpan,
                }, innerCt).ConfigureAwait(false);
                if (!add.IsSuccess)
                    throw HistoriaGitException.FromCli(add, "Resolve (stage)");
            }, ct).ConfigureAwait(false);
        }
        else
        {
            var rm = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rm", "-f", "--", filePath],
                WorkingDirectory = context.RepositoryPath,
                Timeout = Timeout.InfiniteTimeSpan,
            }, innerCt), ct).ConfigureAwait(false);
            if (!rm.IsSuccess)
                throw HistoriaGitException.FromCli(rm, "Resolve (delete)");
        }
    }

    public async Task RecreateConflictAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var res = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = ["checkout", "--conflict=zdiff3", "--", filePath],
            WorkingDirectory = context.RepositoryPath,
            Timeout = Timeout.InfiniteTimeSpan,
        }, innerCt), ct).ConfigureAwait(false);

        if (!res.IsSuccess)
            throw HistoriaGitException.FromCli(res, "Recreate conflict");
    }

    public async Task<ConflictSideLabels> GetSideLabelsAsync(RepositoryContext context, CancellationToken ct = default)
    {
        var op = RepoOperation(context);
        var gitDir = context.GitDirectory;

        var headShaResult = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["rev-parse", "--verify", "-q", "HEAD"],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);
        var headSha = headShaResult.IsSuccess ? headShaResult.StandardOutput.Trim() : string.Empty;

        var branchResult = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["symbolic-ref", "--short", "-q", "HEAD"],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);
        var headBranch = branchResult.IsSuccess ? branchResult.StandardOutput.Trim() : "(no branch)";

        var headShort = headSha.Length >= 7 ? headSha[..7] : headSha;
        var oursTitle = headBranch;

        string theirsTitle;
        string opHeadSha = string.Empty;
        string msgNum = string.Empty;
        string ontoSha = string.Empty;

        switch (op)
        {
            case OperationState.Merging:
                {
                    var mergeHeadFile = Path.Combine(gitDir, "MERGE_HEAD");
                    if (File.Exists(mergeHeadFile))
                        opHeadSha = File.ReadAllText(mergeHeadFile).Trim();

                    var mergeMsgFile = Path.Combine(gitDir, "MERGE_MSG");
                    var firstMsgLine = File.Exists(mergeMsgFile)
                        ? (File.ReadLines(mergeMsgFile).FirstOrDefault() ?? string.Empty).Trim()
                        : string.Empty;

                    var shortSha = opHeadSha.Length >= 7 ? opHeadSha[..7] : opHeadSha;
                    theirsTitle = !string.IsNullOrEmpty(firstMsgLine) ? MergeSourceName(firstMsgLine) : (shortSha.Length > 0 ? shortSha : "Incoming merge");
                    break;
                }

            case OperationState.Rebasing:
                {
                    var rebaseMergeDir = Path.Combine(gitDir, "rebase-merge");
                    var rebaseApplyDir = Path.Combine(gitDir, "rebase-apply");
                    var rebaseDir = Directory.Exists(rebaseMergeDir) ? rebaseMergeDir : rebaseApplyDir;

                    var ontoPath = Path.Combine(rebaseDir, "onto");
                    ontoSha = File.Exists(ontoPath) ? File.ReadAllText(ontoPath).Trim() : string.Empty;

                    oursTitle = "the branch you are rebasing onto";

                    var headNamePath = Path.Combine(rebaseDir, "head-name");
                    var headName = File.Exists(headNamePath) ? File.ReadAllText(headNamePath).Trim() : string.Empty;
                    if (headName.StartsWith("refs/heads/", StringComparison.Ordinal))
                        headName = headName[11..];

                    var stoppedShaPath = Path.Combine(rebaseDir, "stopped-sha");
                    var stoppedSha = File.Exists(stoppedShaPath) ? File.ReadAllText(stoppedShaPath).Trim() : string.Empty;
                    if (string.IsNullOrEmpty(stoppedSha))
                    {
                        var rebaseHeadFile = Path.Combine(gitDir, "REBASE_HEAD");
                        if (File.Exists(rebaseHeadFile))
                            stoppedSha = File.ReadAllText(rebaseHeadFile).Trim();
                    }
                    opHeadSha = stoppedSha;

                    theirsTitle = !string.IsNullOrEmpty(headName) ? $"{headName} (replaying)" : "your commit being replayed";

                    var msgNumFile = Path.Combine(rebaseMergeDir, "msgnum");
                    if (File.Exists(msgNumFile))
                        msgNum = File.ReadAllText(msgNumFile).Trim();
                    else
                    {
                        var nextFile = Path.Combine(rebaseApplyDir, "next");
                        if (File.Exists(nextFile))
                            msgNum = File.ReadAllText(nextFile).Trim();
                    }
                    break;
                }

            case OperationState.CherryPicking:
                {
                    var cherryHeadFile = Path.Combine(gitDir, "CHERRY_PICK_HEAD");
                    if (File.Exists(cherryHeadFile))
                        opHeadSha = File.ReadAllText(cherryHeadFile).Trim();

                    var shortSha = opHeadSha.Length >= 7 ? opHeadSha[..7] : opHeadSha;
                    theirsTitle = $"Cherry-pick {shortSha}".Trim();
                    break;
                }

            case OperationState.Reverting:
                {
                    var revertHeadFile = Path.Combine(gitDir, "REVERT_HEAD");
                    if (File.Exists(revertHeadFile))
                        opHeadSha = File.ReadAllText(revertHeadFile).Trim();

                    var shortSha = opHeadSha.Length >= 7 ? opHeadSha[..7] : opHeadSha;
                    theirsTitle = $"Revert {shortSha}".Trim();
                    break;
                }

            default:
                {
                    theirsTitle = "Stashed / applied changes";
                    break;
                }
        }

        var idsToLookup = new List<GitObjectId>();
        if (GitObjectId.TryParse(headSha, out var headId))
            idsToLookup.Add(headId);
        if (!string.IsNullOrEmpty(opHeadSha) && GitObjectId.TryParse(opHeadSha, out var opId))
            idsToLookup.Add(opId);
        if (!string.IsNullOrEmpty(ontoSha) && GitObjectId.TryParse(ontoSha, out var ontoId))
            idsToLookup.Add(ontoId);

        var subjects = new Dictionary<GitObjectId, string>();
        if (idsToLookup.Count > 0)
        {
            try
            {
                var headers = await new CommitHeaderReader(cli, context.RepositoryPath)
                    .ReadAsync(idsToLookup, ct)
                    .ConfigureAwait(false);
                foreach (var h in headers)
                    subjects[h.Id] = h.Subject;
            }
            catch (HistoriaGitException)
            {
            }
        }

        string oursDetail;
        if (op == OperationState.Rebasing)
        {
            var ontoShort = ontoSha.Length >= 7 ? ontoSha[..7] : ontoSha;
            if (GitObjectId.TryParse(ontoSha, out var oid) && subjects.TryGetValue(oid, out var ontoSubj))
                oursDetail = $"{ontoShort} {ontoSubj}".Trim();
            else
                oursDetail = ontoShort;
        }
        else
        {
            if (GitObjectId.TryParse(headSha, out var hid) && subjects.TryGetValue(hid, out var headSubj))
                oursDetail = $"{headShort} {headSubj}".Trim();
            else
                oursDetail = headShort;
        }

        string theirsDetail;
        if (op is OperationState.Merging or OperationState.CherryPicking or OperationState.Reverting)
        {
            var shortSha = opHeadSha.Length >= 7 ? opHeadSha[..7] : opHeadSha;
            if (GitObjectId.TryParse(opHeadSha, out var opOid) && subjects.TryGetValue(opOid, out var opSubj))
                theirsDetail = $"{shortSha} {opSubj}".Trim();
            else
                theirsDetail = shortSha;
        }
        else if (op == OperationState.Rebasing)
        {
            var stoppedShort = opHeadSha.Length >= 7 ? opHeadSha[..7] : opHeadSha;
            if (GitObjectId.TryParse(opHeadSha, out var opOid) && subjects.TryGetValue(opOid, out var opSubj))
                theirsDetail = $"{stoppedShort} {opSubj}".Trim();
            else
                theirsDetail = stoppedShort;
        }
        else
        {
            theirsDetail = "Working tree changes";
        }

        var stopId = $"{headSha}:{opHeadSha}:{msgNum}";

        return new ConflictSideLabels
        {
            OursTitle = oursTitle,
            OursDetail = oursDetail,
            TheirsTitle = theirsTitle,
            TheirsDetail = theirsDetail,
            Operation = op,
            StopId = stopId,
        };
    }

    public async Task<bool> LaunchExternalMergeToolAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
    {
        var tool = settings.Current.ExternalMergeTool;
        if (string.IsNullOrWhiteSpace(tool))
        {
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["mergetool", "--no-prompt", "--", filePath],
                WorkingDirectory = context.RepositoryPath,
                Timeout = TimeSpan.FromMinutes(30),
            }, ct).ConfigureAwait(false);
            return result.ExitCode == 0;
        }

        var temp = Directory.CreateTempSubdirectory("historia-merge-").FullName;
        try
        {
            var ext = Path.GetExtension(filePath);
            var conflicts = await _unmergedReader.ReadAsync(context.RepositoryPath, filePath, ct).ConfigureAwait(false);
            var conflict = conflicts.FirstOrDefault(c => string.Equals(c.Path, filePath, StringComparison.Ordinal));
            var hasStage1 = conflict?.Ancestor is not null;
            var hasStage2 = conflict?.Ours is not null;
            var hasStage3 = conflict?.Theirs is not null;

            var basePath = await CheckoutStageTempAsync(context, filePath, 1, hasStage1, temp, "BASE", ext, ct).ConfigureAwait(false);
            var localPath = await CheckoutStageTempAsync(context, filePath, 2, hasStage2, temp, "LOCAL", ext, ct).ConfigureAwait(false);
            var remotePath = await CheckoutStageTempAsync(context, filePath, 3, hasStage3, temp, "REMOTE", ext, ct).ConfigureAwait(false);
            var mergedPath = Path.Combine(context.RepositoryPath, filePath);

            var tokens = Tokenize(tool);
            if (tokens.Count == 0)
                return false;

            string Expand(string token) => token
                .Replace("$BASE", basePath)
                .Replace("$LOCAL", localPath)
                .Replace("$REMOTE", remotePath)
                .Replace("$MERGED", mergedPath);

            var psi = new ProcessStartInfo(Expand(tokens[0])) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var token in tokens.Skip(1))
                psi.ArgumentList.Add(Expand(token));

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
            if (process is null)
                return false;

            using (process)
                await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (!File.Exists(mergedPath))
                return true;

            var merged = await File.ReadAllTextAsync(mergedPath, ct).ConfigureAwait(false);
            return !merged.Contains("<<<<<<<", StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    private async Task<string> CheckoutStageTempAsync(
        RepositoryContext context,
        string filePath,
        int stage,
        bool stagePresent,
        string tempDir,
        string label,
        string ext,
        CancellationToken ct)
    {
        var targetFile = Path.Combine(tempDir, $"{label}{ext}");
        if (!stagePresent)
        {
            await File.WriteAllBytesAsync(targetFile, [], ct).ConfigureAwait(false);
            return targetFile;
        }

        var stagePrefix = Path.Combine(tempDir, $"stage{stage}") + Path.DirectorySeparatorChar;
        var subDir = Path.GetDirectoryName(Path.Combine(stagePrefix, filePath));
        if (!string.IsNullOrEmpty(subDir))
            Directory.CreateDirectory(subDir);

        var res = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["checkout-index", $"--prefix={stagePrefix}", $"--stage={stage}", "--", filePath],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);

        if (!res.IsSuccess)
            throw HistoriaGitException.FromCli(res, $"Extract stage {stage} for '{filePath}'");

        var extracted = Path.Combine(stagePrefix, filePath);
        if (File.Exists(extracted))
        {
            File.Move(extracted, targetFile, overwrite: true);
        }
        else
        {
            throw new HistoriaGitException(GitErrorKind.Unknown, $"Failed to extract stage {stage} for '{filePath}'.");
        }

        return targetFile;
    }

    public async Task ContinueOperationAsync(RepositoryContext context, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var stillUnmerged = await _unmergedReader.HasUnmergedPathsAsync(context.RepositoryPath, ct).ConfigureAwait(false);
        if (stillUnmerged)
            throw new HistoriaGitException(GitErrorKind.MergeConflict, "Some paths are still unmerged. Resolve every conflicted file first.");

        await RunSequencerAsync(context, "--continue", ct).ConfigureAwait(false);
    }

    public Task SkipCurrentAsync(RepositoryContext context, CancellationToken ct = default)
    {
        var op = RepoOperation(context);
        if (op == OperationState.Merging)
            throw new HistoriaGitException(GitErrorKind.Unknown, "A merge cannot skip a commit — resolve or abort it.");
        return RunSequencerAsync(context, "--skip", ct);
    }

    public Task AbortOperationAsync(RepositoryContext context, CancellationToken ct = default)
        => RunSequencerAsync(context, "--abort", ct);

    // ── internals ──

    private async Task RunSequencerAsync(RepositoryContext context, string action, CancellationToken ct)
    {
        using var _ = context.BeginSelfOperation();

        var verb = RepoOperation(context) switch
        {
            OperationState.Merging => "merge",
            OperationState.Rebasing => "rebase",
            OperationState.CherryPicking => "cherry-pick",
            OperationState.Reverting => "revert",
            _ => throw new HistoriaGitException(GitErrorKind.Unknown, "No operation is in progress."),
        };

        var env = new Dictionary<string, string> { ["GIT_EDITOR"] = "true" }; // never open an editor on --continue
        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = [verb, action],
            WorkingDirectory = context.RepositoryPath,
            ExtraEnvironment = env,
            Timeout = Timeout.InfiniteTimeSpan,
        }, innerCt), ct).ConfigureAwait(false);

        var isConflict = (string.Equals(action, "--continue", StringComparison.Ordinal) || string.Equals(action, "--skip", StringComparison.Ordinal))
            && (result.ErrorKind == GitErrorKind.MergeConflict
                || GitErrorClassifier.Classify(result.ExitCode, $"{result.StandardOutput}\n{result.StandardError}") == GitErrorKind.MergeConflict);

        if (!result.IsSuccess && !isConflict)
            throw HistoriaGitException.FromCli(result, $"{verb} {action}");

        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
    }

    private static OperationState RepoOperation(RepositoryContext context)
        => Historia.Services.State.RepositoryStateService.DetectOperationState(context.GitDirectory);

    private static ConflictStatus DeriveStatus(UnmergedPathConflict c)
    {
        var haveBase = c.Ancestor is not null;
        var haveOurs = c.Ours is not null;
        var haveTheirs = c.Theirs is not null;

        return (haveBase, haveOurs, haveTheirs) switch
        {
            (true, true, true) => ConflictStatus.BothModified,
            (false, true, true) => ConflictStatus.BothAdded,
            (true, false, true) => ConflictStatus.DeletedByUs,
            (true, true, false) => ConflictStatus.DeletedByThem,
            (false, true, false) => ConflictStatus.AddedByUs,
            (false, false, true) => ConflictStatus.AddedByThem,
            _ => ConflictStatus.BothDeletedRenamed,
        };
    }

    private static int CountLines(string text)
    {
        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                count++;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
            }
            else if (text[i] == '\n')
            {
                count++;
            }
        }
        return count;
    }

    private static int ParseMarkerSize(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return 7;
        var parts = output.Trim().Split(':');
        if (parts.Length >= 3 && int.TryParse(parts[^1].Trim(), out var size) && size > 0)
            return size;
        return 7;
    }

    /// <summary>
    /// Splits a user-configured external-tool command line into its executable and argument tokens,
    /// honouring double-quoted segments.
    /// <para>
    /// Tokens are passed directly to <see cref="ProcessStartInfo.ArgumentList"/> with
    /// <see cref="ProcessStartInfo.UseShellExecute"/> set to false, ensuring external tools
    /// are invoked directly as a child process rather than via a shell. Substituted paths
    /// ($BASE, $LOCAL, $REMOTE, $MERGED) are controlled by the repository and temporary directory,
    /// preventing shell-injection vulnerabilities when paths contain spaces or shell metacharacters.
    /// </para>
    /// </summary>
    internal static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
            tokens.Add(current.ToString());

        return tokens;
    }

    /// <summary>
    /// The name a merge message merged — <c>Merge branch 'x' into main</c> → <c>x</c>,
    /// <c>Merge remote-tracking branch 'origin/x'</c> → <c>origin/x</c>, <c>Merge tag 'v1'</c> →
    /// <c>v1</c>; a merged object id is shortened to 7 characters. Any other message (an octopus
    /// merge, a custom message) is returned unchanged.
    /// </summary>
    internal static string MergeSourceName(string firstMsgLine)
    {
        var match = MergeSourceRegex().Match(firstMsgLine);
        if (!match.Success)
        {
            return firstMsgLine;
        }

        var name = match.Groups[1].Value;
        if ((name.Length == 40 || name.Length == 64) && name.All(char.IsAsciiHexDigit))
        {
            return name[..7];
        }

        return name;
    }

    [GeneratedRegex(@"^Merge (?:remote-tracking branch|branch|tag|commit) '([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex MergeSourceRegex();
}
