using System.Text;
using PoorMansFeed.Core.Aws;

namespace PoorMansFeed.Tests;

public sealed class AwsRangesParserTests
{
    [Fact]
    public void Reads_The_Sync_Token_And_The_Create_Date_As_UTC()
    {
        ParsedAwsRanges? parsed = AwsRangesParser.TryParse(AwsTestData.Bytes());

        Assert.NotNull(parsed);
        Assert.Equal("1792136826", parsed.SyncToken);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 7, 47, 6, TimeSpan.Zero), parsed.CreatedUtc);
        Assert.Equal(0, parsed.SkippedPrefixes);
    }

    [Fact]
    public void Groups_By_Service_And_Region_Sorted_By_Name()
    {
        ParsedAwsRanges parsed = AwsRangesParser.TryParse(AwsTestData.Bytes())!;

        Assert.Equal(["AMAZON", "CLOUDFRONT", "EC2", "S3"], parsed.Services.Select(service => service.Name).ToArray());
        AwsService ec2 = parsed.Services.Single(service => service.Name == "EC2");
        Assert.Equal(["ap-east-1", "cn-north-1", "cn-northwest-1", "us-east-1"], ec2.Regions.Select(region => region.Region).ToArray());
        Assert.Equal(["GLOBAL"], parsed.Services.Single(service => service.Name == "CLOUDFRONT").Regions.Select(region => region.Region).ToArray());
    }

    [Fact]
    public void A_Regions_Prefixes_List_IPv4_First_By_Address_And_Count_Each_Family()
    {
        AwsService ec2 = AwsRangesParser.TryParse(AwsTestData.Bytes())!.Services.Single(service => service.Name == "EC2");

        AwsRegionRanges usEast = ec2.Regions.Single(region => region.Region == "us-east-1");

        Assert.Equal(
            ["3.5.0.0/22", "3.5.4.0/22", "15.181.0.0/17", "2600:1f00::/40"],
            usEast.Prefixes.Select(prefix => prefix.Block.Text).ToArray());
        Assert.Equal(3, usEast.Ipv4Count);
        Assert.Equal(1, usEast.Ipv6Count);
        Assert.Equal(4, usEast.CountFor(includeIPv6: true));
        Assert.Equal(3, usEast.CountFor(includeIPv6: false));
    }

    [Fact]
    public void Keeps_The_Network_Border_Group_Of_Each_Prefix()
    {
        AwsService ec2 = AwsRangesParser.TryParse(AwsTestData.Bytes())!.Services.Single(service => service.Name == "EC2");

        AwsRegionRanges usEast = ec2.Regions.Single(region => region.Region == "us-east-1");

        Assert.Equal(
            ["us-east-1", "us-east-1", "us-east-1-iah-1", "us-east-1"],
            usEast.Prefixes.Select(prefix => prefix.BorderGroup).ToArray());
    }

    [Fact]
    public void A_Missing_Border_Group_Falls_Back_To_The_Region()
    {
        ParsedAwsRanges? parsed = AwsRangesParser.TryParse(Bytes(Doc(
            "{\"ip_prefix\":\"10.1.0.0/16\",\"region\":\"eu-west-1\",\"service\":\"EC2\"}")));

        AwsPrefix prefix = Assert.Single(parsed!.Services[0].Regions[0].Prefixes);
        Assert.Equal("eu-west-1", prefix.BorderGroup);
        Assert.Equal(0, parsed.SkippedPrefixes);
    }

    [Fact]
    public void All_Regions_Is_The_Union_Without_Repeats_IPv4_First_Then_By_Address()
    {
        ParsedAwsRanges? parsed = AwsRangesParser.TryParse(Bytes(Doc(
            Prefix("10.9.0.0/16", "us-east-1", "EC2"),
            Prefix("10.9.0.0/16", "eu-west-1", "EC2"),
            Prefix("10.2.0.0/16", "eu-west-1", "EC2"),
            Prefix("10.9.0.0/16", "us-east-1", "EC2", "us-east-1-iah-1"),
            V6Prefix("2001:db8::/32", "us-east-1", "EC2"))));

        AwsService ec2 = Assert.Single(parsed!.Services);
        Assert.Equal(["10.2.0.0/16", "10.9.0.0/16", "2001:db8::/32"], ec2.AllRegionsPrefixes.Select(prefix => prefix.Block.Text).ToArray());
        Assert.Equal(["eu-west-1", "us-east-1"], ec2.Regions.Select(region => region.Region).ToArray());
        Assert.Equal(2, ec2.Regions.Single(region => region.Region == "us-east-1").Prefixes.Count);
        Assert.Equal(2, ec2.Ipv4Count);
        Assert.Equal(1, ec2.Ipv6Count);
        Assert.Equal(3, ec2.CountFor(includeIPv6: true));
        Assert.Equal(2, ec2.CountFor(includeIPv6: false));
    }

    [Fact]
    public void Find_Looks_Services_And_Regions_Up_By_Exact_Name()
    {
        AwsRangeCatalogue catalogue = AwsTestData.Catalogue();

        AwsService? ec2 = catalogue.Find("EC2");
        Assert.NotNull(ec2);
        Assert.NotNull(ec2.Find("cn-north-1"));
        Assert.Null(ec2.Find("CN-NORTH-1"));
        Assert.Null(ec2.Find("mars-1"));
        Assert.Null(catalogue.Find("ec2"));
        Assert.Null(catalogue.Find(null));
    }

    [Fact]
    public void Skips_And_Counts_Prefixes_That_Cannot_Be_Used()
    {
        ParsedAwsRanges? parsed = AwsRangesParser.TryParse(Bytes(Doc(
            Prefix("10.1.0.0/16", "eu-west-1", "EC2"),
            Prefix("not a cidr", "eu-west-1", "EC2"),
            Prefix("10.2.0.0", "eu-west-1", "EC2"),
            Prefix("10.3.0.0/33", "eu-west-1", "EC2"),
            Prefix("10.4.0.0/16", "eu-west-1", "ec2"),
            Prefix("10.5.0.0/16", "eu-west-1", "EC2 X"),
            Prefix("10.6.0.0/16", "EU West", "EC2"),
            Prefix("10.7.0.0/16", "eu/west", "EC2"),
            Prefix("10.8.0.0/16", "eu-west-1", "EC2", "bad group"),
            Prefix("10.9.0.0/16", "eu-west-1", "EC2", "a/b"),
            Prefix("10.10.0.0/16", "eu-west-1", "E" + new string('C', 70)),
            Prefix("10.11.0.0/16", new string('a', 33), "EC2"),
            "{\"region\":\"eu-west-1\",\"service\":\"EC2\"}",
            "{\"ip_prefix\":\"10.12.0.0/16\",\"service\":\"EC2\"}",
            "{\"ip_prefix\":\"10.13.0.0/16\",\"region\":\"eu-west-1\"}",
            "{\"ip_prefix\":42,\"region\":\"eu-west-1\",\"service\":\"EC2\"}",
            "7",
            V6Prefix("2001:db8::/129", "eu-west-1", "EC2"),
            V6Prefix("::ffff:1.2.3.4/120", "eu-west-1", "EC2"))));

        AwsService ec2 = Assert.Single(parsed!.Services);
        Assert.Equal(["10.1.0.0/16"], ec2.AllRegionsPrefixes.Select(prefix => prefix.Block.Text).ToArray());
        Assert.Equal(18, parsed.SkippedPrefixes);
    }

    [Fact]
    public void Accepts_The_Global_Region_And_Rejects_Other_Capitals()
    {
        ParsedAwsRanges? parsed = AwsRangesParser.TryParse(Bytes(Doc(
            Prefix("10.1.0.0/16", "GLOBAL", "CLOUDFRONT"),
            Prefix("10.2.0.0/16", "Global", "CLOUDFRONT"),
            Prefix("10.3.0.0/16", "US-EAST-1", "CLOUDFRONT"))));

        Assert.Equal(["GLOBAL"], parsed!.Services.Single().Regions.Select(region => region.Region).ToArray());
        Assert.Equal(2, parsed.SkippedPrefixes);
    }

    [Fact]
    public void A_File_With_Only_IPv6_Prefixes_Is_Usable()
    {
        string json = "{\"syncToken\":\"1\",\"createDate\":\"2026-10-16-07-47-06\",\"ipv6_prefixes\":["
            + V6Prefix("2001:db8::/32", "eu-west-1", "EC2") + "]}";

        ParsedAwsRanges? parsed = AwsRangesParser.TryParse(Bytes(json));

        Assert.Equal(1, parsed!.Services.Single().Ipv6Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("<html>Service unavailable</html>")]
    [InlineData("{\"createDate\":\"2026-10-16-07-47-06\",\"prefixes\":[{\"ip_prefix\":\"10.1.0.0/16\",\"region\":\"eu-west-1\",\"service\":\"EC2\"}]}")]
    [InlineData("{\"syncToken\":\"12ab\",\"createDate\":\"2026-10-16-07-47-06\",\"prefixes\":[{\"ip_prefix\":\"10.1.0.0/16\",\"region\":\"eu-west-1\",\"service\":\"EC2\"}]}")]
    [InlineData("{\"syncToken\":\"\",\"createDate\":\"2026-10-16-07-47-06\",\"prefixes\":[{\"ip_prefix\":\"10.1.0.0/16\",\"region\":\"eu-west-1\",\"service\":\"EC2\"}]}")]
    [InlineData("{\"syncToken\":\"12345678901234567890123\",\"createDate\":\"2026-10-16-07-47-06\",\"prefixes\":[{\"ip_prefix\":\"10.1.0.0/16\",\"region\":\"eu-west-1\",\"service\":\"EC2\"}]}")]
    [InlineData("{\"syncToken\":1792136826,\"createDate\":\"2026-10-16-07-47-06\",\"prefixes\":[{\"ip_prefix\":\"10.1.0.0/16\",\"region\":\"eu-west-1\",\"service\":\"EC2\"}]}")]
    [InlineData("{\"syncToken\":\"1\",\"prefixes\":[{\"ip_prefix\":\"10.1.0.0/16\",\"region\":\"eu-west-1\",\"service\":\"EC2\"}]}")]
    [InlineData("{\"syncToken\":\"1\",\"createDate\":\"yesterday\",\"prefixes\":[{\"ip_prefix\":\"10.1.0.0/16\",\"region\":\"eu-west-1\",\"service\":\"EC2\"}]}")]
    [InlineData("{\"syncToken\":\"1\",\"createDate\":\"2026-13-16-07-47-06\",\"prefixes\":[{\"ip_prefix\":\"10.1.0.0/16\",\"region\":\"eu-west-1\",\"service\":\"EC2\"}]}")]
    [InlineData("{\"syncToken\":\"1\",\"createDate\":\"2026-10-16-07-47-06\"}")]
    [InlineData("{\"syncToken\":\"1\",\"createDate\":\"2026-10-16-07-47-06\",\"prefixes\":[],\"ipv6_prefixes\":[]}")]
    [InlineData("{\"syncToken\":\"1\",\"createDate\":\"2026-10-16-07-47-06\",\"prefixes\":\"none\"}")]
    [InlineData("{\"syncToken\":\"1\",\"createDate\":\"2026-10-16-07-47-06\",\"prefixes\":[{\"ip_prefix\":\"bad\",\"region\":\"eu-west-1\",\"service\":\"EC2\"}]}")]
    public void A_File_That_Is_Not_Usable_Gives_Null(string json)
    {
        Assert.Null(AwsRangesParser.TryParse(Encoding.UTF8.GetBytes(json)));
    }

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private static string Doc(params string[] prefixes)
    {
        IEnumerable<string> v4 = prefixes.Where(text => !text.Contains("ipv6_prefix"));
        IEnumerable<string> v6 = prefixes.Where(text => text.Contains("ipv6_prefix"));
        return "{\"syncToken\":\"1792136826\",\"createDate\":\"2026-10-16-07-47-06\",\"prefixes\":["
            + string.Join(",", v4) + "],\"ipv6_prefixes\":[" + string.Join(",", v6) + "]}";
    }

    private static string Prefix(string cidr, string region, string service, string? borderGroup = null) =>
        "{\"ip_prefix\":\"" + cidr + "\",\"region\":\"" + region + "\",\"service\":\"" + service
        + "\",\"network_border_group\":\"" + (borderGroup ?? region) + "\"}";

    private static string V6Prefix(string cidr, string region, string service) =>
        "{\"ipv6_prefix\":\"" + cidr + "\",\"region\":\"" + region + "\",\"service\":\"" + service
        + "\",\"network_border_group\":\"" + region + "\"}";
}
