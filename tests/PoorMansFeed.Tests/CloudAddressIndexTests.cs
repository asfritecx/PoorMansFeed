using System.Net;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudSearch;
using PoorMansFeed.Core.GoogleCloud;

namespace PoorMansFeed.Tests;

public sealed class CloudAddressIndexTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Umbrella_Ranges_Come_Last_And_Each_Part_Narrowest_First_Then_By_Provider_Region_Service_And_Region_Text()
    {
        // Each list is built in reverse of the expected order, so dropping a deciding tie-break changes the answer
        CloudRangeCatalogue google = Google(("20.44.0.0/16", "global"), ("20.44.1.0/24", "us-central1"));
        AzureTagCatalogue azure = Azure(
            Group("AzureCloud", Tag("AzureCloud", "", "20.44.0.0/16"), Tag("AzureCloud.eastus", "eastus", "20.44.1.0/24")),
            Group("Storage", Tag("Storage", "", "20.44.1.0/24"), Tag("Storage.EastUS", "eastus", "20.44.1.0/24")),
            Group("Sql", Tag("Sql.EastUS", "eastus", "20.44.1.0/24")));
        AwsRangeCatalogue aws = Aws(
            Service("AMAZON", Region("us-east-1", ("20.44.1.0/24", "us-east-1"))),
            Service("EC2",
                Region("us-east-1", ("20.44.1.0/24", "us-east-1"), ("20.44.1.0/25", "us-east-1-iah-1")),
                Region("ap-east-1", ("20.44.1.0/24", "ap-east-1"))));

        IReadOnlyList<CloudAddressMatch> matches = CloudAddressIndex.Find(Bytes("20.44.1.5"), google, azure, aws);

        Assert.Equal(
        [
            "Aws|EC2|us-east-1|us-east-1-iah-1|20.44.1.0/25|False",
            "GoogleCloud|Google Cloud|us-central1||20.44.1.0/24|False",
            "Azure|Sql.EastUS|eastus||20.44.1.0/24|False",
            "Azure|Storage.EastUS|eastus||20.44.1.0/24|False",
            "Azure|Storage|||20.44.1.0/24|False",
            "Aws|EC2|ap-east-1||20.44.1.0/24|False",
            "Aws|EC2|us-east-1||20.44.1.0/24|False",
            "GoogleCloud|Google Cloud|global||20.44.0.0/16|False",
            "Azure|AzureCloud.eastus|eastus||20.44.1.0/24|True",
            "Aws|AMAZON|us-east-1||20.44.1.0/24|True",
            "Azure|AzureCloud|||20.44.0.0/16|True",
        ], matches.Select(Describe));
    }

    [Fact]
    public void An_Azure_Regional_Tag_With_No_Region_Is_Named_By_Its_Suffix_And_Sorts_Before_All_Regions()
    {
        AzureTagCatalogue azure = Azure(
            Group("AzureFrontDoor", Tag("AzureFrontDoor", "", "13.107.0.0/16"), Tag("AzureFrontDoor.Frontend", "", "13.107.0.0/16")));

        IReadOnlyList<CloudAddressMatch> matches = CloudAddressIndex.Find(Bytes("13.107.1.1"), null, azure, null);

        Assert.Equal(
        [
            "Azure|AzureFrontDoor.Frontend|Frontend||13.107.0.0/16|False",
            "Azure|AzureFrontDoor|||13.107.0.0/16|False",
        ], matches.Select(Describe));
    }

    [Fact]
    public void Aws_Gives_One_Match_Per_Region_Holding_The_Prefix_With_A_Local_Zone_Named_By_Its_Border_Group()
    {
        AwsRangeCatalogue aws = AwsTestData.Catalogue("""
            {"syncToken":"1792136826","createDate":"2026-10-16-07-47-06","prefixes":[
              {"ip_prefix":"3.5.0.0/22","region":"us-east-1","service":"EC2","network_border_group":"us-east-1"},
              {"ip_prefix":"3.5.0.0/22","region":"eu-west-1","service":"EC2","network_border_group":"eu-west-1"},
              {"ip_prefix":"3.5.0.0/24","region":"us-east-1","service":"EC2","network_border_group":"us-east-1-iah-1"},
              {"ip_prefix":"3.5.0.0/22","region":"us-east-1","service":"AMAZON","network_border_group":"us-east-1"},
              {"ip_prefix":"3.6.0.0/22","region":"us-east-1","service":"EC2","network_border_group":"us-east-1"}
            ],"ipv6_prefixes":[]}
            """);

        IReadOnlyList<CloudAddressMatch> matches = CloudAddressIndex.Find(Bytes("3.5.0.9"), null, null, aws);

        Assert.Equal(
        [
            "Aws|EC2|us-east-1|us-east-1-iah-1|3.5.0.0/24|False",
            "Aws|EC2|eu-west-1||3.5.0.0/22|False",
            "Aws|EC2|us-east-1||3.5.0.0/22|False",
            "Aws|AMAZON|us-east-1||3.5.0.0/22|True",
        ], matches.Select(Describe));
    }

    [Fact]
    public void A_Local_Zone_Range_From_The_Shared_Test_File_Carries_Its_Border_Group()
    {
        IReadOnlyList<CloudAddressMatch> matches = CloudAddressIndex.Find(Bytes("15.181.0.1"), null, null, AwsTestData.Catalogue());

        CloudAddressMatch match = Assert.Single(matches);
        Assert.Equal("Aws|EC2|us-east-1|us-east-1-iah-1|15.181.0.0/17|False", Describe(match));
    }

    [Fact]
    public void Azure_Marks_Every_AzureCloud_Tag_As_The_Umbrella_And_Keeps_The_Tag_Region()
    {
        AzureTagCatalogue azure = AzureTestData.Catalogue();

        Assert.Equal(["Azure|Storage.SoutheastAsia|southeastasia||20.44.1.0/24|False"],
            CloudAddressIndex.Find(Bytes("20.44.1.7"), null, azure, null).Select(Describe));
        Assert.Equal(["Azure|AzureCloud.southeastasia|southeastasia||20.44.0.0/24|True"],
            CloudAddressIndex.Find(Bytes("20.44.0.7"), null, azure, null).Select(Describe));
        Assert.Equal(["Azure|AzureCloud|||20.0.0.0/16|True"],
            CloudAddressIndex.Find(Bytes("20.0.0.1"), null, azure, null).Select(Describe));
    }

    [Fact]
    public void An_IPv6_Address_Matches_Only_IPv6_Ranges()
    {
        AzureTagCatalogue azure = AzureTestData.Catalogue();
        AwsRangeCatalogue aws = AwsTestData.Catalogue();

        IReadOnlyList<CloudAddressMatch> matches = CloudAddressIndex.Find(Bytes("2406:da00::1"), null, azure, aws);

        Assert.Equal(["Aws|EC2|cn-north-1||2406:da00::/32|False"], matches.Select(Describe));
    }

    [Fact]
    public void One_Prefix_Listed_Twice_For_One_Scope_Gives_One_Match()
    {
        CloudRangeCatalogue google = Google(("34.80.1.0/24", "us-central1"), ("34.80.1.0/24", "us-central1"), ("34.80.1.0/24", "global"));

        IReadOnlyList<CloudAddressMatch> matches = CloudAddressIndex.Find(Bytes("34.80.1.1"), google, null, null);

        Assert.Equal(
        [
            "GoogleCloud|Google Cloud|global||34.80.1.0/24|False",
            "GoogleCloud|Google Cloud|us-central1||34.80.1.0/24|False",
        ], matches.Select(Describe));
    }

    [Fact]
    public void No_Catalogue_Gives_No_Match()
    {
        Assert.Empty(CloudAddressIndex.Find(Bytes("8.8.8.8"), null, null, null));
    }

    internal static string Describe(CloudAddressMatch match) =>
        string.Join("|", match.Provider, match.Service, match.Region, match.Scope, match.Prefix.Text, match.IsUmbrella);

    internal static byte[] Bytes(string text)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
        return address.GetAddressBytes();
    }

    internal static CidrBlock Block(string text)
    {
        Assert.True(CidrBlock.TryParse(text, out CidrBlock? block));
        return block;
    }

    internal static CloudRangeCatalogue Google(params (string Prefix, string Scope)[] prefixes)
    {
        var list = new List<CloudPrefix>();
        foreach ((string prefix, string scopeId) in prefixes)
        {
            Assert.True(GoogleCloudScope.TryCreate(scopeId, out GoogleCloudScope scope));
            list.Add(new CloudPrefix(Block(prefix), scope));
        }

        var source = new CloudRangeSource(new Uri("https://www.gstatic.com/ipranges/cloud.json"), "1770000000000", "2026-01-15T00:00:00Z");
        return new CloudRangeCatalogue(source, list, [], 0);
    }

    internal static AzureServiceTag Tag(string name, string region, params string[] prefixes) =>
        new(name, region, 1, prefixes.Select(Block).ToList());

    internal static AzureServiceGroup Group(string baseName, params AzureServiceTag[] tags) => new(baseName, tags);

    internal static AzureTagCatalogue Azure(params AzureServiceGroup[] groups)
    {
        var source = new AzureTagFileSource(
            new Uri("https://download.microsoft.com/download/7/1/d/aaaa/ServiceTags_Public_20261012.json"),
            "ServiceTags_Public_20261012.json", new DateOnly(2026, 10, 12), 422, "Public", Now, "abc", 1);
        return new AzureTagCatalogue(source, groups, 0, 0);
    }

    internal static AwsRegionRanges Region(string region, params (string Prefix, string BorderGroup)[] prefixes) =>
        new(region, prefixes.Select(prefix => new AwsPrefix(Block(prefix.Prefix), prefix.BorderGroup)).ToList());

    internal static AwsService Service(string name, params AwsRegionRanges[] regions) =>
        new(name, regions, regions.SelectMany(region => region.Prefixes).DistinctBy(prefix => prefix.Block).ToList());

    internal static AwsRangeCatalogue Aws(params AwsService[] services) =>
        new(new AwsFileSource("1792136826", Now, null, Now, "abc", 1), services, 0);
}
