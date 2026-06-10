using System.Security.Cryptography;
using System.Text.Json;
using Callspire.UpdateServer.Models;

namespace Callspire.UpdateServer.Services;

public class UpdateFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public string DataDirectory { get; }
    public string PublicBaseUrl { get; }
    public string InstallerFileName { get; } = "Callspire-setup.exe";

    public string InstallerPath => Path.Combine(DataDirectory, InstallerFileName);
    public string ManifestPath => Path.Combine(DataDirectory, "update.json");
    public string ReleasesDirectory => Path.Combine(DataDirectory, "releases");
    public string ArchiveIndexPath => Path.Combine(ReleasesDirectory, "index.json");
    public string DraftPath => Path.Combine(DataDirectory, "draft.json");

    public UpdateFileStore(string dataDirectory, string publicBaseUrl)
    {
        DataDirectory = dataDirectory;
        PublicBaseUrl = publicBaseUrl.TrimEnd('/');
    }

    public void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ReleasesDirectory);
    }

    public async Task<UpdateManifest?> ReadManifestAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ManifestPath))
            return null;
        await using var stream = File.OpenRead(ManifestPath);
        return await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, ct).ConfigureAwait(false);
    }

    public bool InstallerExists => File.Exists(InstallerPath);

    /// <summary>
    /// Publishes manifest + optionally replaces installer. If <paramref name="newInstallerStream"/> is null, keeps existing installer and prior hash unless <paramref name="replaceSha256"/> is set.
    /// </summary>
    public async Task PublishAsync(
        string version,
        string? notes,
        bool mandatory,
        Stream? newInstallerStream,
        string? replaceSha256,
        CancellationToken ct = default)
    {
        EnsureDataDirectory();

        string sha256;
        if (newInstallerStream != null)
        {
            var tempExe = Path.Combine(DataDirectory, ".upload-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                await using (var fs = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await newInstallerStream.CopyToAsync(fs, ct).ConfigureAwait(false);
                }

                sha256 = await ComputeSha256FileAsync(tempExe, ct).ConfigureAwait(false);

                var finalPath = InstallerPath;
                if (File.Exists(finalPath))
                    File.Delete(finalPath);
                File.Move(tempExe, finalPath);
            }
            finally
            {
                if (File.Exists(tempExe))
                    try { File.Delete(tempExe); } catch { /* ignore */ }
            }
        }
        else
        {
            if (!InstallerExists)
                throw new InvalidOperationException("No installer file on server yet. Upload an .exe first.");

            if (!string.IsNullOrWhiteSpace(replaceSha256))
                sha256 = replaceSha256.Trim();
            else
            {
                var existing = await ReadManifestAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(existing?.Sha256))
                    sha256 = existing!.Sha256!;
                else
                    sha256 = await ComputeSha256FileAsync(InstallerPath, ct).ConfigureAwait(false);
            }
        }

        var publicUrl = $"{PublicBaseUrl}/{InstallerFileName}";
        var manifest = new UpdateManifest
        {
            Version = version.Trim(),
            Url = publicUrl,
            Sha256 = sha256,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            Mandatory = mandatory
        };

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var tempJson = ManifestPath + ".tmp";
        await File.WriteAllTextAsync(tempJson, json, ct).ConfigureAwait(false);
        File.Move(tempJson, ManifestPath, overwrite: true);

        await ArchiveReleaseAsync(manifest, ct).ConfigureAwait(false);
    }

    public void DeleteRelease()
    {
        if (File.Exists(ManifestPath))
            File.Delete(ManifestPath);
        if (File.Exists(InstallerPath))
            File.Delete(InstallerPath);
    }

    public async Task DeleteCurrentAndRollbackAsync(CancellationToken ct = default)
    {
        EnsureDataDirectory();

        // History is newest-first (we insert at 0). If we have a previous entry, restore it.
        var history = (await ReadArchivedReleasesBestEffortAsync(ct).ConfigureAwait(false)).ToList();

        // No history -> behave like old delete (delete current files).
        if (history.Count == 0)
        {
            DeleteRelease();
            return;
        }

        // Determine the "current" archived snapshot: try to match by manifest fields, otherwise assume first item.
        UpdateManifest? currentManifest = null;
        try { currentManifest = await ReadManifestAsync(ct).ConfigureAwait(false); } catch { /* ignore */ }

        var currentIdx = 0;
        if (currentManifest != null)
        {
            var match = history.FindIndex(x =>
                string.Equals(x.Version, currentManifest.Version, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(currentManifest.Sha256) || string.Equals(x.Sha256, currentManifest.Sha256, StringComparison.OrdinalIgnoreCase)));
            if (match >= 0)
                currentIdx = match;
        }

        // Remove the archived snapshot corresponding to the deleted current release from index/history.
        var removed = history[currentIdx];
        history.RemoveAt(currentIdx);

        // If we have something to roll back to, restore its archived installer+manifest as current.
        if (history.Count > 0)
        {
            var prev = history[0];
            var prevInstaller = GetArchivedInstallerPath(prev.Id);
            var prevManifest = GetArchivedManifestPath(prev.Id);

            if (File.Exists(prevManifest))
                File.Copy(prevManifest, ManifestPath, overwrite: true);

            if (File.Exists(prevInstaller))
                File.Copy(prevInstaller, InstallerPath, overwrite: true);
        }
        else
        {
            // Nothing left to roll back to -> remove current.
            DeleteRelease();
        }

        // Persist updated index.json (without the removed entry).
        try
        {
            var indexTemp = ArchiveIndexPath + ".tmp";
            var indexJson = JsonSerializer.Serialize(history, JsonOptions);
            await File.WriteAllTextAsync(indexTemp, indexJson, ct).ConfigureAwait(false);
            File.Move(indexTemp, ArchiveIndexPath, overwrite: true);
        }
        catch
        {
            // If we can't write index, still keep rollback behavior.
        }

        // Best-effort: delete removed snapshot files so "Delete release" actually removes it.
        try
        {
            var removedInstaller = GetArchivedInstallerPath(removed.Id);
            var removedManifest = GetArchivedManifestPath(removed.Id);
            if (File.Exists(removedInstaller))
                File.Delete(removedInstaller);
            if (File.Exists(removedManifest))
                File.Delete(removedManifest);
        }
        catch
        {
            // ignore
        }
    }

    public async Task<ReleaseDraft?> ReadDraftAsync(CancellationToken ct = default)
    {
        EnsureDataDirectory();
        if (!File.Exists(DraftPath))
            return null;

        await using var stream = File.OpenRead(DraftPath);
        return await JsonSerializer.DeserializeAsync<ReleaseDraft>(stream, JsonOptions, ct).ConfigureAwait(false);
    }

    public async Task SaveDraftAsync(ReleaseDraft draft, CancellationToken ct = default)
    {
        EnsureDataDirectory();
        if (draft == null)
            throw new ArgumentNullException(nameof(draft));

        // Нормализуем, чтобы json был аккуратный
        draft.Version = string.IsNullOrWhiteSpace(draft.Version) ? null : draft.Version.Trim();
        draft.Notes = string.IsNullOrWhiteSpace(draft.Notes) ? null : draft.Notes.Trim();

        var json = JsonSerializer.Serialize(draft, JsonOptions);
        var tempPath = DraftPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
        File.Move(tempPath, DraftPath, overwrite: true);
    }

    public Task ClearDraftAsync(CancellationToken ct = default)
    {
        EnsureDataDirectory();
        if (File.Exists(DraftPath))
            File.Delete(DraftPath);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<ArchivedReleaseInfo>> ReadArchivedReleasesAsync(CancellationToken ct = default)
    {
        EnsureDataDirectory();
        if (!File.Exists(ArchiveIndexPath))
            return Array.Empty<ArchivedReleaseInfo>();

        try
        {
            await using var stream = File.OpenRead(ArchiveIndexPath);
            var list = await JsonSerializer.DeserializeAsync<List<ArchivedReleaseInfo>>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (list == null || list.Count == 0)
                return Array.Empty<ArchivedReleaseInfo>();
            return await PruneOrphanedArchivedIndexEntriesAsync(list, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // If index.json is corrupted (e.g. interrupted write / manual edit), recover history from archived manifests.
            var recovered = await RebuildArchiveIndexFromFilesAsync(ct).ConfigureAwait(false);
            return recovered;
        }
    }

    public string GetArchivedInstallerPath(string releaseId) =>
        Path.Combine(ReleasesDirectory, releaseId + "-" + InstallerFileName);

    public string GetArchivedManifestPath(string releaseId) =>
        Path.Combine(ReleasesDirectory, releaseId + "-update.json");

    public async Task UpdateArchivedNotesAsync(string releaseId, string? notes, CancellationToken ct = default)
    {
        EnsureDataDirectory();
        if (string.IsNullOrWhiteSpace(releaseId))
            throw new ArgumentException("Release id is required.", nameof(releaseId));

        var normalizedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        // Update index.json
        var list = (await ReadArchivedReleasesAsync(ct).ConfigureAwait(false)).ToList();
        var item = list.FirstOrDefault(x => x.Id == releaseId);
        if (item == null)
            throw new InvalidOperationException("Archived release not found.");

        item.Notes = normalizedNotes;

        var indexTemp = ArchiveIndexPath + ".tmp";
        var indexJson = JsonSerializer.Serialize(list, JsonOptions);
        await File.WriteAllTextAsync(indexTemp, indexJson, ct).ConfigureAwait(false);
        File.Move(indexTemp, ArchiveIndexPath, overwrite: true);

        // Update archived manifest JSON (so it stays consistent when downloaded)
        var archiveManifestPath = GetArchivedManifestPath(releaseId);
        if (File.Exists(archiveManifestPath))
        {
            await using var stream = File.OpenRead(archiveManifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (manifest != null)
            {
                manifest.Notes = normalizedNotes;
                var json = JsonSerializer.Serialize(manifest, JsonOptions);
                var temp = archiveManifestPath + ".tmp";
                await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
                File.Move(temp, archiveManifestPath, overwrite: true);
            }
        }
    }

    public async Task DeleteArchivedReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        EnsureDataDirectory();
        if (string.IsNullOrWhiteSpace(releaseId))
            throw new ArgumentException("Release id is required.", nameof(releaseId));

        // Update index.json
        var list = (await ReadArchivedReleasesBestEffortAsync(ct).ConfigureAwait(false)).ToList();
        var removed = list.FirstOrDefault(x => x.Id == releaseId);
        if (removed == null)
            throw new InvalidOperationException("Archived release not found.");

        UpdateManifest? currentPublished = null;
        try { currentPublished = await ReadManifestAsync(ct).ConfigureAwait(false); } catch { /* ignore */ }

        var deletingCurrentPublished = currentPublished != null &&
            string.Equals(removed.Version, currentPublished.Version, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(currentPublished.Sha256) ||
             string.Equals(removed.Sha256, currentPublished.Sha256, StringComparison.OrdinalIgnoreCase));

        list.RemoveAll(x => x.Id == releaseId);

        var indexTemp = ArchiveIndexPath + ".tmp";
        var indexJson = JsonSerializer.Serialize(list, JsonOptions);
        await File.WriteAllTextAsync(indexTemp, indexJson, ct).ConfigureAwait(false);
        File.Move(indexTemp, ArchiveIndexPath, overwrite: true);

        // If user deletes the archived row that matches the live update.json, roll back "currently published".
        if (deletingCurrentPublished)
        {
            if (list.Count > 0)
            {
                var prev = list[0];
                var prevInstaller = GetArchivedInstallerPath(prev.Id);
                var prevManifest = GetArchivedManifestPath(prev.Id);
                if (File.Exists(prevManifest))
                    File.Copy(prevManifest, ManifestPath, overwrite: true);
                if (File.Exists(prevInstaller))
                    File.Copy(prevInstaller, InstallerPath, overwrite: true);
            }
            else
            {
                DeleteRelease();
            }
        }

        // Best-effort: delete snapshot files.
        try
        {
            var installer = GetArchivedInstallerPath(releaseId);
            var manifest = GetArchivedManifestPath(releaseId);
            if (File.Exists(installer))
                File.Delete(installer);
            if (File.Exists(manifest))
                File.Delete(manifest);
        }
        catch
        {
            // ignore
        }

        await RepairCurrentReleaseFromHistoryAsync(ct).ConfigureAwait(false);
    }

    private async Task ArchiveReleaseAsync(UpdateManifest manifest, CancellationToken ct)
    {
        EnsureDataDirectory();

        var id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{SanitizeFileToken(manifest.Version)}";
        var archiveInstallerPath = GetArchivedInstallerPath(id);
        var archiveManifestPath = GetArchivedManifestPath(id);

        File.Copy(InstallerPath, archiveInstallerPath, overwrite: false);
        var archivedJson = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(archiveManifestPath, archivedJson, ct).ConfigureAwait(false);

        // Use rebuild fallback when index is missing/corrupted, so we don't "lose" older releases.
        var list = (await ReadArchivedReleasesBestEffortAsync(ct).ConfigureAwait(false)).ToList();
        list.Insert(0, new ArchivedReleaseInfo
        {
            Id = id,
            PublishedAtUtc = DateTime.UtcNow,
            Version = manifest.Version,
            Sha256 = manifest.Sha256,
            Notes = manifest.Notes,
            Mandatory = manifest.Mandatory
        });

        var indexTemp = ArchiveIndexPath + ".tmp";
        var indexJson = JsonSerializer.Serialize(list, JsonOptions);
        await File.WriteAllTextAsync(indexTemp, indexJson, ct).ConfigureAwait(false);
        File.Move(indexTemp, ArchiveIndexPath, overwrite: true);
    }

    private async Task<IReadOnlyList<ArchivedReleaseInfo>> ReadArchivedReleasesBestEffortAsync(CancellationToken ct)
    {
        EnsureDataDirectory();
        if (!File.Exists(ArchiveIndexPath))
            return await RebuildArchiveIndexFromFilesAsync(ct).ConfigureAwait(false);

        try
        {
            await using var stream = File.OpenRead(ArchiveIndexPath);
            var list = await JsonSerializer.DeserializeAsync<List<ArchivedReleaseInfo>>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (list == null)
                return Array.Empty<ArchivedReleaseInfo>();
            return await PruneOrphanedArchivedIndexEntriesAsync(list, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return await RebuildArchiveIndexFromFilesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Keeps UI consistent when admins delete snapshot files manually: removes index entries whose archived files are missing.
    /// </summary>
    private async Task<IReadOnlyList<ArchivedReleaseInfo>> PruneOrphanedArchivedIndexEntriesAsync(List<ArchivedReleaseInfo> list, CancellationToken ct)
    {
        if (list.Count == 0)
            return list;

        var pruned = list.Where(x => ArchivedSnapshotExists(x.Id)).ToList();
        if (pruned.Count == list.Count)
            return pruned;

        try
        {
            var indexTemp = ArchiveIndexPath + ".tmp";
            var indexJson = JsonSerializer.Serialize(pruned, JsonOptions);
            await File.WriteAllTextAsync(indexTemp, indexJson, ct).ConfigureAwait(false);
            File.Move(indexTemp, ArchiveIndexPath, overwrite: true);
        }
        catch
        {
            // If we can't persist, still return pruned in-memory list for this request.
        }

        return pruned;
    }

    private bool ArchivedSnapshotExists(string releaseId)
    {
        if (string.IsNullOrWhiteSpace(releaseId))
            return false;
        var installer = GetArchivedInstallerPath(releaseId);
        var manifest = GetArchivedManifestPath(releaseId);
        return File.Exists(installer) && File.Exists(manifest);
    }

    /// <summary>
    /// If current published files are inconsistent (common after manual deletes), restore from newest complete archived snapshot.
    /// </summary>
    public async Task RepairCurrentReleaseFromHistoryAsync(CancellationToken ct = default)
    {
        EnsureDataDirectory();

        var manifestExists = File.Exists(ManifestPath);
        var installerExists = File.Exists(InstallerPath);

        // Nothing to repair if there is no manifest at all (empty server).
        if (!manifestExists)
            return;

        UpdateManifest? manifest = null;
        try
        {
            manifest = await ReadManifestAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        var shaOk = false;
        try
        {
            if (installerExists && manifest != null && !string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                var actual = await ComputeSha256FileAsync(InstallerPath, ct).ConfigureAwait(false);
                shaOk = string.Equals(actual, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            shaOk = false;
        }

        // Consistent enough: manifest readable and installer matches sha (or sha missing).
        if (installerExists && (string.IsNullOrWhiteSpace(manifest?.Sha256) || shaOk))
            return;

        var history = (await ReadArchivedReleasesBestEffortAsync(ct).ConfigureAwait(false)).ToList();
        foreach (var item in history)
        {
            if (!ArchivedSnapshotExists(item.Id))
                continue;

            var archivedManifestPath = GetArchivedManifestPath(item.Id);
            var archivedInstallerPath = GetArchivedInstallerPath(item.Id);

            UpdateManifest? archivedManifest = null;
            try
            {
                await using var stream = File.OpenRead(archivedManifestPath);
                archivedManifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, ct).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            if (archivedManifest == null)
                continue;

            try
            {
                var actualSha = await ComputeSha256FileAsync(archivedInstallerPath, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(archivedManifest.Sha256) &&
                    !string.Equals(actualSha, archivedManifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            catch
            {
                continue;
            }

            File.Copy(archivedManifestPath, ManifestPath, overwrite: true);
            File.Copy(archivedInstallerPath, InstallerPath, overwrite: true);
            return;
        }
    }

    private async Task<IReadOnlyList<ArchivedReleaseInfo>> RebuildArchiveIndexFromFilesAsync(CancellationToken ct)
    {
        EnsureDataDirectory();

        // Archived manifests follow "<id>-update.json" naming.
        var files = Directory.Exists(ReleasesDirectory)
            ? Directory.EnumerateFiles(ReleasesDirectory, "*-update.json", SearchOption.TopDirectoryOnly)
            : Enumerable.Empty<string>();

        var items = new List<ArchivedReleaseInfo>();
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith("-update.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var id = fileName[..^"-update.json".Length];
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var installerPath = GetArchivedInstallerPath(id);
            if (!File.Exists(installerPath))
                continue;

            UpdateManifest? manifest = null;
            try
            {
                await using var stream = File.OpenRead(path);
                manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Ignore unreadable manifests; keep recovering as much as possible.
            }

            var publishedAt = File.GetLastWriteTimeUtc(path);
            items.Add(new ArchivedReleaseInfo
            {
                Id = id,
                PublishedAtUtc = publishedAt == default ? DateTime.UtcNow : publishedAt,
                Version = manifest?.Version ?? "",
                Sha256 = manifest?.Sha256,
                Notes = manifest?.Notes,
                Mandatory = manifest?.Mandatory ?? false
            });
        }

        // Sort newest first. Prefer timestamp prefix in id when present (yyyyMMdd-HHmmss-...).
        items = items
            .OrderByDescending(x => x.PublishedAtUtc)
            .ThenByDescending(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            // Persist recovered index to make UI stable again.
            var temp = ArchiveIndexPath + ".tmp";
            var json = JsonSerializer.Serialize(items, JsonOptions);
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            File.Move(temp, ArchiveIndexPath, overwrite: true);
        }
        catch
        {
            // If we can't write the index, still return recovered list for rendering.
        }

        // Drop empty-version entries only if we have better ones; otherwise keep everything.
        return items;
    }

    private static string SanitizeFileToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "no-version";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (invalid.Contains(chars[i]) || char.IsWhiteSpace(chars[i]))
                chars[i] = '-';
        }

        return new string(chars);
    }

    private static async Task<string> ComputeSha256FileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
