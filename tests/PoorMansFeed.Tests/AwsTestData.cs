using System.Text;
using PoorMansFeed.Core.Aws;

namespace PoorMansFeed.Tests;

/// <summary>A small AWS ip-ranges.json and the catalogue read from it, shared by the feed, view model and window tests.</summary>
internal static class AwsTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 16, 8, 5, 0, TimeSpan.Zero);

    public const string SyncToken = "1792136826";

    public const string ETag = "\"2dfa976f58cf72192df853622efbea89\"";

    public const string RangesJson = """
        {"syncToken":"1792136826","createDate":"2026-10-16-07-47-06","prefixes":[
          {"ip_prefix":"13.248.0.0/21","region":"us-east-1","service":"AMAZON","network_border_group":"us-east-1"},
          {"ip_prefix":"3.5.4.0/22","region":"us-east-1","service":"EC2","network_border_group":"us-east-1"},
          {"ip_prefix":"3.5.0.0/22","region":"us-east-1","service":"EC2","network_border_group":"us-east-1"},
          {"ip_prefix":"15.181.0.0/17","region":"us-east-1","service":"EC2","network_border_group":"us-east-1-iah-1"},
          {"ip_prefix":"43.198.0.0/24","region":"ap-east-1","service":"EC2","network_border_group":"ap-east-1"},
          {"ip_prefix":"52.80.1.0/24","region":"cn-north-1","service":"EC2","network_border_group":"cn-north-1"},
          {"ip_prefix":"52.80.0.0/24","region":"cn-north-1","service":"EC2","network_border_group":"cn-north-1"},
          {"ip_prefix":"54.222.0.0/24","region":"cn-northwest-1","service":"EC2","network_border_group":"cn-northwest-1"},
          {"ip_prefix":"54.239.0.0/24","region":"cn-northwest-1","service":"S3","network_border_group":"cn-northwest-1"},
          {"ip_prefix":"1.2.3.0/24","region":"GLOBAL","service":"CLOUDFRONT","network_border_group":"GLOBAL"}
        ],"ipv6_prefixes":[
          {"ipv6_prefix":"2600:1f00::/40","region":"us-east-1","service":"EC2","network_border_group":"us-east-1"},
          {"ipv6_prefix":"2406:da00::/32","region":"cn-north-1","service":"EC2","network_border_group":"cn-north-1"}
        ]}
        """;

    public static byte[] Bytes(string json = RangesJson) => Encoding.UTF8.GetBytes(json);

    public static AwsRangeCatalogue Catalogue(string json = RangesJson, string? eTag = ETag)
    {
        byte[] bytes = Bytes(json);
        ParsedAwsRanges parsed = AwsRangesParser.TryParse(bytes)!;
        var source = new AwsFileSource(parsed.SyncToken, parsed.CreatedUtc, eTag, Now, "abc", bytes.Length);
        return new AwsRangeCatalogue(source, parsed.Services, parsed.SkippedPrefixes);
    }
}
