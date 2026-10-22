namespace PoorMansFeed.Core;

public sealed record CoreOptions
{
    public string DownloadUrl { get; init; } = "https://ipinfo.io/data/ipinfo_lite.json.gz";
    public string ChecksumUrl { get; init; } = "https://ipinfo.io/data/ipinfo_lite.json.gz/checksums";
    public int BatchSize { get; init; } = 10_000;
    public int MaxRetries { get; init; } = 3;
    public int HttpTimeoutMinutes { get; init; } = 5;
    public string UserAgent { get; init; } = "curl/8.7.1";
    public string GoogleCloudRangesUrl { get; init; } = "https://www.gstatic.com/ipranges/cloud.json";

    /// <summary>The download page that names the current Azure service tags file. The file's own address changes weekly.</summary>
    public string AzureServiceTagsPageUrl { get; init; } = "https://www.microsoft.com/en-us/download/details.aspx?id=56519";

    public int AzurePageTimeoutSeconds { get; init; } = 30;
    public int AzureMaxPageBytes { get; init; } = 2 * 1024 * 1024;
    public int AzureMaxFileBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>The AWS ip-ranges.json. One fixed address; an unchanged file is answered with a 304.</summary>
    public string AwsIpRangesUrl { get; init; } = "https://ip-ranges.amazonaws.com/ip-ranges.json";

    public int AwsRequestTimeoutSeconds { get; init; } = 60;
    public int AwsMaxFileBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>The AbuseIPDB check endpoint, with no query. The key goes in a header, never in this address.</summary>
    public string AbuseIpDbCheckUrl { get; init; } = "https://api.abuseipdb.com/api/v2/check";

    public int AbuseIpDbRequestTimeoutSeconds { get; init; } = 60;
    public int AbuseIpDbMaxCheckBytes { get; init; } = 256 * 1024;

    /// <summary>The AbuseIPDB blacklist endpoint, sent with no query, or with <c>ipVersion=4</c> while IPv6 is off. The key goes in a header, never in this address.</summary>
    public string AbuseIpDbBlacklistUrl { get; init; } = "https://api.abuseipdb.com/api/v2/blacklist";

    /// <summary>The largest blacklist answer read; a larger one is refused as a bad answer.</summary>
    public int AbuseIpDbMaxBlacklistBytes { get; init; } = 16 * 1024 * 1024;
}
