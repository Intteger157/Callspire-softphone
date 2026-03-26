using System.Text.Json.Serialization;

namespace Callspire.UpdateServer.Models;

/// <summary>
/// JSON shape for Callspire client (UpdateService / update.json), camelCase.
/// </summary>
public class UpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; }
}
