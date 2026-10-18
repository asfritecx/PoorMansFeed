using System.Text.Json.Serialization;

namespace PoorMansFeed.Core;

public class ChecksumResponse
{
    [JsonPropertyName("checksums")]
    public ChecksumData? Checksums { get; set; }
}

public class ChecksumData
{
    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}
