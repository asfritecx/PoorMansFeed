using System.Text.Json.Serialization;

namespace PoorMansFeed.Core;

public class IpInfoEntry
{
    [JsonPropertyName("network")]
    public string? Network { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("country_code")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("continent")]
    public string? Continent { get; set; }

    [JsonPropertyName("continent_code")]
    public string? ContinentCode { get; set; }

    [JsonPropertyName("asn")]
    public string? Asn { get; set; }

    [JsonPropertyName("as_name")]
    public string? AsName { get; set; }

    [JsonPropertyName("as_domain")]
    public string? AsDomain { get; set; }

    // Derived fields - populated by parser, not from JSON
    [JsonIgnore]
    public byte[]? StartIp { get; set; }

    [JsonIgnore]
    public byte[]? EndIp { get; set; }
}
