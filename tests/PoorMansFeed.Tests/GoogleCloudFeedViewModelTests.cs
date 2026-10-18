using System.Net;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.GoogleCloud;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class GoogleCloudFeedViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ipinfo-cloud-vm", Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http;
    private readonly FakeIpInfoService _ipinfo = new()
    {
        Dataset = new DatasetInfo(new DateOnly(2026, 10, 8), 3, "abc", DateTimeOffset.UnixEpoch, true),
    };

    public GoogleCloudFeedViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        _http = new HttpClient(new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CatalogueJson, Encoding.UTF8, "application/json"),
        }));
        _ipinfo.OverlapFunc = blocks => new OverlapResult(
            OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, Groups(block))).ToList(),
            _ipinfo.Dataset);
    }

    public void Dispose()
    {
        _http.Dispose();
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Confirmed_Starts_Included_Mixed_Starts_Excluded_And_Changing_Region_Clears_Rows()
    {
        var feeds = new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), _folder);
        var vm = new GoogleCloudFeedViewModel(feeds, _ipinfo, new FakeUi(), _folder);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "us-central1");

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Rows.Count);
        CloudRangeRow confirmed = vm.Rows.Single(row => row.NetworkText == "34.1.0.0/16");
        CloudRangeRow mixed = vm.Rows.Single(row => row.NetworkText == "35.1.0.0/16");
        Assert.True(confirmed.IsIncluded);
        Assert.Equal(RangeConfirmation.Confirmed, confirmed.Status);
        Assert.False(mixed.IsIncluded);
        Assert.Equal(RangeConfirmation.Mixed, mixed.Status);

        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "europe-west1");

        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
    }

    [Fact]
    public async Task Missing_Dataset_Says_So_And_Writes_No_File()
    {
        _ipinfo.Dataset = null;
        _ipinfo.OverlapFunc = _ => throw new InvalidOperationException("queried");
        var feeds = new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), _folder);
        var vm = new GoogleCloudFeedViewModel(feeds, _ipinfo, new FakeUi(), _folder);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "us-central1");

        await vm.CheckCommand.ExecuteAsync(null);
        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.CloudNoDataset, vm.EmptyText);
        Assert.Empty(vm.Rows);
        Assert.False(vm.WriteFeedCommand.CanExecute(null));
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());
    }

    private static IReadOnlyList<OverlapGroup> Groups(CidrBlock block) => block.Text == "34.1.0.0/16"
        ? [new OverlapGroup("US", "United States", "AS15169", "Google LLC", "google.com", 1)]
        : [new OverlapGroup("US", "United States", "AS64500", "Other", "example.com", 1)];

    private const string CatalogueJson = """
        {
          "syncToken": "1",
          "creationTime": "2026-01-15T00:00:00Z",
          "prefixes": [
            {"ipv4Prefix": "34.1.0.0/16", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv4Prefix": "35.1.0.0/16", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv4Prefix": "8.8.8.0/24", "service": "Google Cloud", "scope": "europe-west1"}
          ]
        }
        """;
}