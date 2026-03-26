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

        await using var stream = File.OpenRead(ArchiveIndexPath);
        var list = await JsonSerializer.DeserializeAsync<List<ArchivedReleaseInfo>>(stream, JsonOptions, ct).ConfigureAwait(false);
        if (list == null)
            return Array.Empty<ArchivedReleaseInfo>();
        return list;
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

    private async Task ArchiveReleaseAsync(UpdateManifest manifest, CancellationToken ct)
    {
        EnsureDataDirectory();

        var id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{SanitizeFileToken(manifest.Version)}";
        var archiveInstallerPath = GetArchivedInstallerPath(id);
        var archiveManifestPath = GetArchivedManifestPath(id);

        File.Copy(InstallerPath, archiveInstallerPath, overwrite: false);
        var archivedJson = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(archiveManifestPath, archivedJson, ct).ConfigureAwait(false);

        var list = (await ReadArchivedReleasesAsync(ct).ConfigureAwait(false)).ToList();
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
