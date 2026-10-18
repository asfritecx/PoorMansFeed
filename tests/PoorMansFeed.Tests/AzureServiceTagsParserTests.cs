using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Azure;

namespace PoorMansFeed.Tests;

public sealed class AzureServiceTagsParserTests
{
    [Fact]
    public void Groups_Tags_By_Base_Name_With_The_Base_Tag_First()
    {
        ParsedAzureTags? parsed = AzureServiceTagsParser.TryParse(Bytes(Doc(
            Tag("Storage.WestEurope", "westeurope", 3, "20.50.0.0/16"),
            Tag("Sql", "", 9, "13.64.0.0/16"),
            Tag("Storage", "", 5, "52.1.0.0/16"),
            Tag("Storage.EastUS", "eastus", 7, "40.1.0.0/16"))));

        Assert.NotNull(parsed);
        Assert.Equal(422, parsed.ChangeNumber);
        Assert.Equal("Public", parsed.Cloud);
        Assert.Equal(["Sql", "Storage"], parsed.Services.Select(group => group.BaseName).ToArray());
        AzureServiceGroup storage = parsed.Services[1];
        Assert.Equal(["Storage", "Storage.EastUS", "Storage.WestEurope"], storage.Tags.Select(tag => tag.Name).ToArray());
        Assert.True(storage.Tags[0].IsBase);
        Assert.False(storage.Tags[1].IsBase);
        Assert.Equal("EastUS", storage.Tags[1].Suffix);
        Assert.Equal("eastus", storage.Tags[1].Region);
        Assert.Equal("Storage", storage.Tags[1].BaseName);
        Assert.Equal(7, storage.Tags[1].ChangeNumber);
    }

    [Fact]
    public void A_Service_With_No_Base_Tag_Lists_Its_Variants_By_Region_Or_Suffix()
    {
        ParsedAzureTags? parsed = AzureServiceTagsParser.TryParse(Bytes(Doc(
            Tag("AzureFrontDoor.Frontend", "", 1, "13.73.0.0/16"),
            Tag("AzureFrontDoor.Backend", "", 1, "13.74.0.0/16"),
            Tag("AzureFrontDoor.FirstParty", "", 1, "13.75.0.0/16"))));

        AzureServiceGroup group = Assert.Single(parsed!.Services);
        Assert.Equal("AzureFrontDoor", group.BaseName);
        Assert.Equal(
            ["AzureFrontDoor.Backend", "AzureFrontDoor.FirstParty", "AzureFrontDoor.Frontend"],
            group.Tags.Select(tag => tag.Name).ToArray());
        Assert.All(group.Tags, tag => Assert.False(tag.IsBase));
    }

    [Fact]
    public void Sorts_Prefixes_IPv4_First_By_Address_Drops_Duplicates_And_Counts_Families()
    {
        ParsedAzureTags? parsed = AzureServiceTagsParser.TryParse(Bytes(Doc(
            Tag("Sql", "", 1, "2603:1000::/32", "13.64.0.0/16", "13.8.0.0/16", "13.64.0.0/16", "2603:1000::/32"))));

        AzureServiceTag tag = parsed!.Services[0].Tags[0];
        Assert.Equal(["13.8.0.0/16", "13.64.0.0/16", "2603:1000::/32"], tag.Prefixes.Select(block => block.Text).ToArray());
        Assert.Equal(2, tag.Ipv4Count);
        Assert.Equal(1, tag.Ipv6Count);
        Assert.Equal(3, tag.CountFor(includeIPv6: true));
        Assert.Equal(2, tag.CountFor(includeIPv6: false));
    }

    [Fact]
    public void Skips_And_Counts_Bad_Tags_And_Bad_Prefixes()
    {
        string json = Doc(
            Tag("Good", "", 1, "10.1.0.0/16", "not a cidr", "10.2.0.0", "10.3.0.0/33"),
            Tag("Bad Name", "", 1, "10.4.0.0/16"),
            Tag("a.b.c", "", 1, "10.5.0.0/16"),
            Tag("Bad.Region", "East US", 1, "10.6.0.0/16"),
            Tag("Good", "", 2, "10.7.0.0/16"),
            Tag("GOOD", "", 3, "10.8.0.0/16"),
            Tag("Empty", "", 1),
            Tag("Fine.Eastus", "eastus", 1, "10.9.0.0/16"),
            "42",
            "{\"name\":7,\"properties\":{}}",
            "{\"name\":\"NoProps\"}",
            "{\"name\":\"WrongPrefixes\",\"properties\":{\"addressPrefixes\":\"10.1.0.0/16\"}}");

        ParsedAzureTags? parsed = AzureServiceTagsParser.TryParse(Bytes(json));

        Assert.NotNull(parsed);
        Assert.Equal(["Fine", "Good"], parsed.Services.Select(group => group.BaseName).ToArray());
        Assert.Equal(["10.1.0.0/16"], parsed.Services[1].Tags[0].Prefixes.Select(block => block.Text).ToArray());
        Assert.Equal(3, parsed.SkippedPrefixes);
        Assert.Equal(10, parsed.SkippedTags);
    }

    [Fact]
    public void Treats_A_Missing_Region_As_Global()
    {
        string json = """
            {"changeNumber":1,"cloud":"Public","values":[
              {"name":"Sql","properties":{"changeNumber":1,"addressPrefixes":["10.1.0.0/16"]}},
              {"name":"Sql.Eastus","properties":{"changeNumber":1,"region":null,"addressPrefixes":["10.2.0.0/16"]}}]}
            """;

        ParsedAzureTags? parsed = AzureServiceTagsParser.TryParse(Bytes(json));

        Assert.All(parsed!.Services[0].Tags, tag => Assert.Equal("", tag.Region));
    }

    [Theory]
    [InlineData("""{"changeNumber":1,"cloud":"AzureGovernment","values":[{"name":"Sql","properties":{"addressPrefixes":["10.1.0.0/16"]}}]}""")]
    [InlineData("""{"changeNumber":1,"values":[{"name":"Sql","properties":{"addressPrefixes":["10.1.0.0/16"]}}]}""")]
    [InlineData("""{"changeNumber":1,"cloud":"Public","values":[]}""")]
    [InlineData("""{"changeNumber":1,"cloud":"Public"}""")]
    [InlineData("""{"changeNumber":1,"cloud":"Public","values":"nope"}""")]
    [InlineData("""{"cloud":"Public","values":[{"name":"Sql","properties":{"addressPrefixes":["10.1.0.0/16"]}}]}""")]
    [InlineData("""{"changeNumber":-1,"cloud":"Public","values":[{"name":"Sql","properties":{"addressPrefixes":["10.1.0.0/16"]}}]}""")]
    [InlineData("""{"changeNumber":1,"cloud":"Public","values":[{"name":"Sql","properties":{"addressPrefixes":[]}}]}""")]
    [InlineData("""{"changeNumber":1,"cloud":"Public","values":[""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("<html>not json</html>")]
    public void Returns_Null_When_The_File_Is_Not_Usable_Public_Cloud_Data(string json)
    {
        Assert.Null(AzureServiceTagsParser.TryParse(Bytes(json)));
    }

    [Fact]
    public void Parses_The_Real_Public_File_When_Available()
    {
        string? path = Environment.GetEnvironmentVariable("AZURE_TAGS_SAMPLE");
        if (path is null || !File.Exists(path))
            return;

        ParsedAzureTags? parsed = AzureServiceTagsParser.TryParse(File.ReadAllBytes(path));

        Assert.NotNull(parsed);
        Assert.Equal(0, parsed.SkippedPrefixes);
        Assert.Equal(99, parsed.Services.Count);
        Assert.Equal(3321, parsed.Services.Sum(group => group.Tags.Count) + parsed.SkippedTags);
    }

    private static string Doc(params string[] tags) =>
        "{\"changeNumber\":422,\"cloud\":\"Public\",\"values\":[" + string.Join(",", tags) + "]}";

    private static string Tag(string name, string region, int changeNumber, params string[] prefixes) =>
        "{\"name\":\"" + name + "\",\"id\":\"" + name + "\",\"properties\":{\"changeNumber\":" + changeNumber +
        ",\"region\":\"" + region + "\",\"regionId\":0,\"platform\":\"Azure\",\"systemService\":\"x\",\"addressPrefixes\":[" +
        string.Join(",", prefixes.Select(prefix => "\"" + prefix + "\"")) + "]}}";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
