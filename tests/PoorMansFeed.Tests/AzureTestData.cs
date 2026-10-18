using System.Text;
using PoorMansFeed.Core.Azure;

namespace PoorMansFeed.Tests;

/// <summary>A small Azure service tags file and the catalogue read from it, shared by the view model and window tests.</summary>
internal static class AzureTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 16, 8, 5, 0, TimeSpan.Zero);

    public const string TagsJson = """
        {"changeNumber":422,"cloud":"Public","values":[
          {"name":"AzureCloud","properties":{"changeNumber":5,"region":"","addressPrefixes":["20.0.0.0/16","2603:2000::/32"]}},
          {"name":"AzureCloud.southeastasia","properties":{"changeNumber":4,"region":"southeastasia","addressPrefixes":["20.44.0.0/24"]}},
          {"name":"AzureCloud.eastus","properties":{"changeNumber":4,"region":"eastus","addressPrefixes":["20.50.0.0/24","20.50.1.0/24"]}},
          {"name":"Storage","properties":{"changeNumber":3,"region":"","addressPrefixes":["20.60.0.0/16"]}},
          {"name":"Storage.SoutheastAsia","properties":{"changeNumber":2,"region":"southeastasia","addressPrefixes":["20.44.1.0/24","20.44.2.0/24","2603:1010::/48"]}},
          {"name":"AzureFrontDoor.Frontend","properties":{"changeNumber":1,"region":"","addressPrefixes":["13.1.0.0/24"]}},
          {"name":"AzureFrontDoor.Backend","properties":{"changeNumber":1,"region":"","addressPrefixes":["13.2.0.0/24"]}},
          {"name":"Twin.One","properties":{"changeNumber":1,"region":"x","addressPrefixes":["14.1.0.0/24"]}},
          {"name":"Twin.Two","properties":{"changeNumber":1,"region":"x","addressPrefixes":["14.2.0.0/24"]}}
        ]}
        """;

    public static AzureTagCatalogue Catalogue(string json = TagsJson, long change = 422, string date = "20261012", string folder = "aaaa")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json.Replace("\"changeNumber\":422,", $"\"changeNumber\":{change},"));
        ParsedAzureTags parsed = AzureServiceTagsParser.TryParse(bytes)!;
        var source = new AzureTagFileSource(
            new Uri($"https://download.microsoft.com/download/7/1/d/{folder}/ServiceTags_Public_{date}.json"),
            $"ServiceTags_Public_{date}.json", DateOnly.ParseExact(date, "yyyyMMdd"), parsed.ChangeNumber, parsed.Cloud, Now, "abc", bytes.Length);
        return new AzureTagCatalogue(source, parsed.Services, parsed.SkippedTags, parsed.SkippedPrefixes);
    }
}
