namespace Callspire.UpdateServer.Models;

public class ArchivedReleaseInfo
{
    public string Id { get; set; } = "";
    public DateTime PublishedAtUtc { get; set; }
    public string Version { get; set; } = "";
    public string? Sha256 { get; set; }
    public string? Notes { get; set; }
    public bool Mandatory { get; set; }
}
