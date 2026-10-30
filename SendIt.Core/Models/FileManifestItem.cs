using System.Text.Json.Serialization;

namespace SendIt.Core.Models;

public class FileManifestItem
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonIgnore]
    public string LocalFullPath { get; set; } = string.Empty;
}
