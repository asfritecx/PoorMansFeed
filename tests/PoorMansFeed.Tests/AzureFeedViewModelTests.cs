using System.Net;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class AzureFeedViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = AzureTestData.Now;
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", Now, true);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private const string UpToDateText =
        "Service tags of 12 October 2026\u00A0\u00B7\u00A0change 422\u00A0\u00B7\u00A0up to date, checked 16:05";

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeIpInfoService _ipinfo = new() { Dataset = Dataset };
    private readonly FakeUi _ui = new();
    private readonly HttpClient _http = new(new StubHttpHandler((_, _) => throw new InvalidOperationException("no request expected")));

    public AzureFeedViewModelTests()
    {
        // The local zone is UTC+8, so 08:05 UTC reads 16:05 in the status line.
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(8), "test", "test"));
        _ipinfo.OverlapFunc = blocks => new OverlapResult(
            OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, [new OverlapGroup("US", "United States", "AS8075", "Microsoft Corporation", "microsoft.com", 1)])).ToList(),
            Dataset);
    }

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Activation_Runs_Once()
    {
        FakeAzureFeedService feeds = Feeds(AzureTestData.Catalogue());
        AzureFeedViewModel vm = Vm(feeds);

        await vm.ActivateAsync();
        await vm.ActivateAsync();

        Assert.Equal(1, feeds.LoadCachedCalls);
        Assert.Equal(1, feeds.UpdateCalls);
    }

    [Fact]
    public async Task The_Cached_List_Shows_While_The_Update_Check_Is_Still_Running()
    {
        FakeAzureFeedService feeds = Feeds(AzureTestData.Catalogue());
        feeds.UpdateGate = new TaskCompletionSource();
        AzureFeedViewModel vm = Vm(feeds);

        Task activation = vm.ActivateAsync();

        Assert.Equal(["cache", "update"], feeds.Calls);
        Assert.Equal(["AzureCloud", "AzureFrontDoor", "Storage", "Twin"], vm.Services.Select(service => service.BaseName).ToArray());
        Assert.Equal(AppText.CheckingForUpdates, vm.StatusText);
        Assert.True(vm.IsWorking);
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.False(vm.CheckForUpdatesCommand.CanExecute(null));

        feeds.UpdateGate.SetResult();
        await activation;

        Assert.False(vm.IsWorking);
        Assert.Equal(UpToDateText, vm.StatusText);
        Assert.True(vm.CheckCommand.CanExecute(null));
        Assert.True(vm.CheckForUpdatesCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_First_Service_And_Its_First_Region_Are_Picked_And_The_List_Asks_For_A_Check()
    {
        AzureFeedViewModel vm = Vm(Feeds(AzureTestData.Catalogue()));

        await vm.ActivateAsync();

        Assert.Equal("AzureCloud", vm.SelectedService!.BaseName);
        Assert.Equal("AzureCloud", vm.SelectedRegion!.TagName);
        Assert.Equal(AppText.AzureEmptyInitial, vm.EmptyText);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Region_Labels_Put_All_Regions_First_Then_Name_The_Region_Or_The_Suffix()
    {
        AzureFeedViewModel vm = Vm(Feeds(AzureTestData.Catalogue()));
        await vm.ActivateAsync();

        Assert.Equal(
            [AppText.RegionChoice(AppText.AzureAllRegions, 2), AppText.RegionChoice("eastus", 2), AppText.RegionChoice("southeastasia", 1)],
            vm.Regions.Select(region => region.Text).ToArray());

        vm.SelectedService = vm.Services.Single(service => service.BaseName == "AzureFrontDoor");

        Assert.Equal(
            [AppText.RegionChoice("Backend", 1), AppText.RegionChoice("Frontend", 1)],
            vm.Regions.Select(region => region.Text).ToArray());

        vm.SelectedService = vm.Services.Single(service => service.BaseName == "Twin");

        Assert.Equal(
            [AppText.RegionChoice("x (One)", 1), AppText.RegionChoice("x (Two)", 1)],
            vm.Regions.Select(region => region.Text).ToArray());
    }

    [Fact]
    public async Task The_Region_Count_Leaves_Out_IPv6_While_IPv6_Is_Off()
    {
        _ipinfo.Settings = new UserSettings { IPv6Enabled = false };
        AzureFeedViewModel vm = Vm(Feeds(AzureTestData.Catalogue()));

        await vm.ActivateAsync();

        Assert.Equal(AppText.RegionChoice(AppText.AzureAllRegions, 1), vm.Regions[0].Text);
    }

    [Fact]
    public async Task Changing_The_Service_Rebuilds_The_Regions_Picks_The_First_And_Clears_The_Rows()
    {
        FakeAzureFeedService feeds = Feeds(AzureTestData.Catalogue());
        AzureFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Rows);

        vm.SelectedService = vm.Services.Single(service => service.BaseName == "Storage");

        Assert.Equal(["Storage", "Storage.SoutheastAsia"], vm.Regions.Select(region => region.TagName).ToArray());
        Assert.Equal("Storage", vm.SelectedRegion!.TagName);
        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
        Assert.False(vm.WriteFeedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Changing_The_Region_Clears_The_Rows()
    {
        AzureFeedViewModel vm = Vm(Feeds(AzureTestData.Catalogue()));
        await vm.ActivateAsync();
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasRows);

        vm.SelectedRegion = vm.Regions.Single(region => region.TagName == "AzureCloud.eastus");

        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
        Assert.Equal(AppText.CloudSummaryInitial, vm.SummaryText);
    }

    [Fact]
    public async Task Check_Looks_Up_The_Picked_Tag_And_Lists_Its_Ranges()
    {
        FakeAzureFeedService feeds = Feeds(AzureTestData.Catalogue());
        AzureFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        vm.SelectedService = vm.Services.Single(service => service.BaseName == "Storage");
        vm.SelectedRegion = vm.Regions.Single(region => region.TagName == "Storage.SoutheastAsia");

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal("Storage.SoutheastAsia", feeds.LastSelection!.TagName);
        Assert.Equal(["20.44.1.0/24", "20.44.2.0/24", "2603:1010::/48"], vm.Rows.Select(row => row.NetworkText).ToArray());
        Assert.All(vm.Rows, row => Assert.Equal(RangeConfirmation.Confirmed, row.Status));
        Assert.All(vm.Rows, row => Assert.True(row.IsIncluded));
        Assert.Equal(AppText.CloudSummary(3, 3, 0, 0), vm.SummaryText);
    }

    [Fact]
    public async Task A_Failed_Update_Check_Keeps_The_Cached_List_And_Says_So()
    {
        FakeAzureFeedService feeds = Feeds(AzureTestData.Catalogue());
        feeds.UpdateFunc = current => new AzureLoadResult(AzureUpdateStatus.CheckFailed, AzureSourceFailure.Offline, current, Now);
        AzureFeedViewModel vm = Vm(feeds);

        await vm.ActivateAsync();

        Assert.Equal(4, vm.Services.Count);
        Assert.Equal(
            "Service tags of 12 October 2026\u00A0\u00B7\u00A0change 422\u00A0\u00B7\u00A0couldn't check for a newer file",
            vm.StatusText);
        Assert.Equal(AppText.AzureEmptyInitial, vm.EmptyText);
        Assert.True(vm.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_Update_Keeps_The_Picked_Tag_Clears_The_Rows_And_Names_The_New_File()
    {
        FakeAzureFeedService feeds = Feeds(AzureTestData.Catalogue());
        AzureFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        vm.SelectedService = vm.Services.Single(service => service.BaseName == "Storage");
        vm.SelectedRegion = vm.Regions.Single(region => region.TagName == "Storage.SoutheastAsia");
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasRows);
        AzureTagCatalogue newer = AzureTestData.Catalogue(change: 423, date: "20261019", folder: "bbbb");
        feeds.UpdateFunc = _ => new AzureLoadResult(AzureUpdateStatus.Updated, AzureSourceFailure.None, newer, Now);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("Storage", vm.SelectedService!.BaseName);
        Assert.Equal("Storage.SoutheastAsia", vm.SelectedRegion!.TagName);
        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
        Assert.Equal(
            "Service tags of 19 October 2026\u00A0\u00B7\u00A0change 423\u00A0\u00B7\u00A0updated, checked 16:05",
            vm.StatusText);
        Assert.Same(feeds.Cached, feeds.LastCurrent);
    }

    [Fact]
    public async Task An_Update_That_Drops_The_Picked_Tag_Falls_Back_To_The_First_Region_Of_The_Service()
    {
        FakeAzureFeedService feeds = Feeds(AzureTestData.Catalogue());
        AzureFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        vm.SelectedService = vm.Services.Single(service => service.BaseName == "Storage");
        vm.SelectedRegion = vm.Regions.Single(region => region.TagName == "Storage.SoutheastAsia");
        string withoutTag = AzureTestData.TagsJson.Replace("Storage.SoutheastAsia", "Storage.EastUS");
        AzureTagCatalogue newer = AzureTestData.Catalogue(withoutTag, change: 423, date: "20261019", folder: "bbbb");
        feeds.UpdateFunc = _ => new AzureLoadResult(AzureUpdateStatus.Updated, AzureSourceFailure.None, newer, Now);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("Storage", vm.SelectedService!.BaseName);
        Assert.Equal("Storage", vm.SelectedRegion!.TagName);
    }

    [Fact]
    public async Task An_Update_That_Drops_The_Whole_Service_Falls_Back_To_The_First_Service()
    {
        FakeAzureFeedService feeds = Feeds(AzureTestData.Catalogue());
        AzureFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        vm.SelectedService = vm.Services.Single(service => service.BaseName == "Twin");
        string withoutTwin = AzureTestData.TagsJson.Replace("Twin.", "Pair.");
        AzureTagCatalogue newer = AzureTestData.Catalogue(withoutTwin, change: 423, date: "20261019", folder: "bbbb");
        feeds.UpdateFunc = _ => new AzureLoadResult(AzureUpdateStatus.Updated, AzureSourceFailure.None, newer, Now);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("AzureCloud", vm.SelectedService!.BaseName);
        Assert.Equal("AzureCloud", vm.SelectedRegion!.TagName);
    }

    [Theory]
    [InlineData(AzureSourceFailure.Offline, "The Azure service tags couldn't be loaded. Check the network connection, then try again.")]
    [InlineData(AzureSourceFailure.ServiceError, "The Azure service tags couldn't be loaded. Please try again or contact the Infra Team.")]
    [InlineData(AzureSourceFailure.PageUnreadable, "The Azure service tags couldn't be read.")]
    [InlineData(AzureSourceFailure.FileUnreadable, "The Azure service tags couldn't be read.")]
    public async Task With_Nothing_Cached_And_No_File_The_Lists_Are_Empty_Check_Is_Off_And_The_Reason_Shows(
        AzureSourceFailure failure, string expected)
    {
        FakeAzureFeedService feeds = Feeds(null);
        feeds.UpdateFunc = _ => new AzureLoadResult(AzureUpdateStatus.Unavailable, failure, null, Now);
        AzureFeedViewModel vm = Vm(feeds);

        await vm.ActivateAsync();

        Assert.Empty(vm.Services);
        Assert.Empty(vm.Regions);
        Assert.Null(vm.SelectedService);
        Assert.Null(vm.SelectedRegion);
        Assert.Equal(expected, vm.EmptyText);
        Assert.Equal(AppText.AzureStatusUnavailable, vm.StatusText);
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.True(vm.CheckForUpdatesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Checking_For_Updates_After_A_Failure_Recovers()
    {
        FakeAzureFeedService feeds = Feeds(null);
        feeds.UpdateFunc = _ => new AzureLoadResult(AzureUpdateStatus.Unavailable, AzureSourceFailure.Offline, null, Now);
        AzureFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        AzureTagCatalogue found = AzureTestData.Catalogue();
        feeds.UpdateFunc = _ => new AzureLoadResult(AzureUpdateStatus.Updated, AzureSourceFailure.None, found, Now);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.Services.Count);
        Assert.Equal("AzureCloud", vm.SelectedRegion!.TagName);
        Assert.Equal(AppText.AzureEmptyInitial, vm.EmptyText);
        Assert.True(vm.CheckCommand.CanExecute(null));
        Assert.Null(feeds.LastCurrent);
    }

    [Fact]
    public async Task An_Exception_From_The_Update_Check_Is_Reported_And_Does_Not_Escape()
    {
        FakeAzureFeedService feeds = Feeds(AzureTestData.Catalogue());
        feeds.UpdateFunc = _ => throw new InvalidOperationException("secret detail");
        AzureFeedViewModel vm = Vm(feeds);

        await vm.ActivateAsync();

        Assert.False(vm.IsWorking);
        Assert.DoesNotContain("secret", vm.StatusText);
        Assert.DoesNotContain("secret", vm.EmptyText);
        Assert.Equal(4, vm.Services.Count);
    }

    [Fact]
    public async Task A_Ticked_Range_Selection_Is_Written_To_The_Azure_Feed_File()
    {
        FakeAzureFeedService feeds = Feeds(AzureTestData.Catalogue());
        AzureFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        vm.SelectedService = vm.Services.Single(service => service.BaseName == "Storage");
        vm.SelectedRegion = vm.Regions.Single(region => region.TagName == "Storage.SoutheastAsia");
        await vm.CheckCommand.ExecuteAsync(null);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        string ipv4 = Path.Combine(folder, "Azure_Storage_SoutheastAsia_ipv4.txt");
        Assert.True(File.Exists(ipv4));
        Assert.True(File.Exists(Path.Combine(folder, "Azure_Storage_SoutheastAsia_ipv6.txt")));
        Assert.Contains("# Service tag: Storage.SoutheastAsia", File.ReadAllText(ipv4));
        Assert.Equal(AppText.CloudFeedWritten(3, 2), vm.ActionText);
    }

    [Fact]
    public async Task Open_Folder_Opens_The_Export_Folder()
    {
        AzureFeedViewModel vm = Vm(Feeds(AzureTestData.Catalogue()));

        await vm.OpenFeedFolderCommand.ExecuteAsync(null);

        Assert.Equal([_temp.ExportFolder], _ui.OpenedFolders);
    }

    [Fact]
    public async Task Disposing_During_The_Update_Check_Stops_It_Quietly()
    {
        FakeAzureFeedService feeds = Feeds(AzureTestData.Catalogue());
        feeds.UpdateGate = new TaskCompletionSource();
        AzureFeedViewModel vm = Vm(feeds);
        Task activation = vm.ActivateAsync();
        Assert.Equal(AppText.CheckingForUpdates, vm.StatusText);

        vm.Dispose();
        await activation;

        Assert.Equal(AppText.CheckingForUpdates, vm.StatusText);
        Assert.False(vm.CheckForUpdatesCommand.CanExecute(null));
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.False(vm.OpenFeedFolderCommand.CanExecute(null));
    }

    [Fact]
    public async Task Activating_A_Disposed_Page_Does_Nothing()
    {
        FakeAzureFeedService feeds = Feeds(AzureTestData.Catalogue());
        AzureFeedViewModel vm = Vm(feeds);
        vm.Dispose();

        await vm.ActivateAsync();

        Assert.Equal(0, feeds.LoadCachedCalls);
        Assert.Equal(0, feeds.UpdateCalls);
    }

    [Fact]
    public async Task With_The_Real_Service_The_File_Is_Downloaded_Once_And_Then_Used_Offline()
    {
        bool online = true;
        var handler = new StubHttpHandler((request, _) =>
        {
            if (!online)
                throw new HttpRequestException("offline");
            return request.RequestUri!.Host == "www.microsoft.com"
                ? Reply("<html><script>window.__DLCDetails__={\"dlcDetailsView\":{\"downloadFile\":[{\"url\":\"https://download.microsoft.com/download/7/1/d/aaaa/ServiceTags_Public_20261012.json\"}]}};</script></html>")
                : Reply(AzureTestData.TagsJson);
        });
        using var http = new HttpClient(handler);
        var first = new AzureFeedViewModel(
            new AzureFeedService(_ipinfo, http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder),
            _ipinfo, _ui, _time, _temp.ExportFolder);

        await first.ActivateAsync();

        Assert.Equal(4, first.Services.Count);
        Assert.Equal(
            "Service tags of 12 October 2026\u00A0\u00B7\u00A0change 422\u00A0\u00B7\u00A0updated, checked 16:05",
            first.StatusText);
        Assert.True(File.Exists(_temp.Paths.AzureServiceTagsPath));
        first.Dispose();

        online = false;
        var second = new AzureFeedViewModel(
            new AzureFeedService(_ipinfo, http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder),
            _ipinfo, _ui, _time, _temp.ExportFolder);
        await second.ActivateAsync();

        Assert.Equal(4, second.Services.Count);
        Assert.Equal(
            "Service tags of 12 October 2026\u00A0\u00B7\u00A0change 422\u00A0\u00B7\u00A0couldn't check for a newer file",
            second.StatusText);
        second.Dispose();
    }

    private AzureFeedViewModel Vm(FakeAzureFeedService feeds) =>
        new(feeds, _ipinfo, _ui, _time, _temp.ExportFolder);

    /// <summary>A fake whose cache holds the catalogue and whose update check reports "up to date".</summary>
    private FakeAzureFeedService Feeds(AzureTagCatalogue? cached)
    {
        var real = new AzureFeedService(_ipinfo, _http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder);
        return new FakeAzureFeedService(real)
        {
            Cached = cached,
            UpdateFunc = current => new AzureLoadResult(AzureUpdateStatus.UpToDate, AzureSourceFailure.None, current, Now),
        };
    }

    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
}
