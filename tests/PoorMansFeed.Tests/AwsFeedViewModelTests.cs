using System.Net;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.CloudFeeds;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class AwsFeedViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = AwsTestData.Now;
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", Now, true);

    private const string FileText = "AWS ranges of 16 October 2026, 07:47 UTC";
    private const string UpToDateText = FileText + "\u00A0\u00B7\u00A0up to date, checked 16:05";

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeIpInfoService _ipinfo = new() { Dataset = Dataset };
    private readonly FakeUi _ui = new();
    private readonly HttpClient _http = new(new StubHttpHandler((_, _) => throw new InvalidOperationException("no request expected")));

    public AwsFeedViewModelTests()
    {
        // The local zone is UTC+8, so 08:05 UTC reads 16:05 in the status line.
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(8), "test", "test"));
        _ipinfo.OverlapFunc = blocks => new OverlapResult(
            OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, [new OverlapGroup("US", "United States", "AS16509", "Amazon.com, Inc.", "amazon.com", 1)])).ToList(),
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
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        AwsFeedViewModel vm = Vm(feeds);

        await vm.ActivateAsync();
        await vm.ActivateAsync();

        Assert.Equal(1, feeds.LoadCachedCalls);
        Assert.Equal(1, feeds.UpdateCalls);
    }

    [Fact]
    public async Task The_Cached_List_Shows_While_The_Update_Check_Is_Still_Running()
    {
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        feeds.UpdateGate = new TaskCompletionSource();
        AwsFeedViewModel vm = Vm(feeds);

        Task activation = vm.ActivateAsync();

        Assert.Equal(["cache", "update"], feeds.Calls);
        Assert.Equal(["AMAZON", "CLOUDFRONT", "EC2", "S3"], vm.Services.Select(service => service.Name).ToArray());
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
    public async Task EC2_And_All_Regions_Are_Picked_First_And_The_List_Asks_For_A_Check()
    {
        AwsFeedViewModel vm = Vm(Feeds(AwsTestData.Catalogue()));

        await vm.ActivateAsync();

        Assert.Equal("EC2", vm.SelectedService!.Name);
        Assert.Null(vm.SelectedRegion!.Region);
        Assert.Equal(AppText.AwsEmptyInitial, vm.EmptyText);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Without_EC2_The_First_Service_Is_Picked()
    {
        string noEc2 = AwsTestData.RangesJson.Replace("\"EC2\"", "\"EC2X\"");
        AwsFeedViewModel vm = Vm(Feeds(AwsTestData.Catalogue(noEc2)));

        await vm.ActivateAsync();

        Assert.Equal("AMAZON", vm.SelectedService!.Name);
    }

    [Fact]
    public async Task Region_Labels_Put_All_Regions_First_Then_Name_Each_Region_By_Its_Id()
    {
        AwsFeedViewModel vm = Vm(Feeds(AwsTestData.Catalogue()));
        await vm.ActivateAsync();

        Assert.Equal(
            [
                AppText.RegionChoice(AppText.AwsAllRegions, 9),
                AppText.RegionChoice("ap-east-1", 1),
                AppText.RegionChoice("cn-north-1", 3),
                AppText.RegionChoice("cn-northwest-1", 1),
                AppText.RegionChoice("us-east-1", 4),
            ],
            vm.Regions.Select(region => region.Text).ToArray());
        Assert.Equal(new string?[] { null, "ap-east-1", "cn-north-1", "cn-northwest-1", "us-east-1" }, vm.Regions.Select(region => region.Region).ToArray());

        vm.SelectedService = vm.Services.Single(service => service.Name == "CLOUDFRONT");

        Assert.Equal(
            [AppText.RegionChoice(AppText.AwsAllRegions, 1), AppText.RegionChoice(AppText.AwsGlobalRegion, 1)],
            vm.Regions.Select(region => region.Text).ToArray());
        Assert.Equal("GLOBAL", vm.Regions[1].Region);
    }

    [Fact]
    public async Task The_Range_Counts_Leave_Out_IPv6_While_IPv6_Is_Off()
    {
        _ipinfo.Settings = new UserSettings { IPv6Enabled = false };
        AwsFeedViewModel vm = Vm(Feeds(AwsTestData.Catalogue()));

        await vm.ActivateAsync();

        Assert.Equal(AppText.RegionChoice(AppText.AwsAllRegions, 7), vm.Regions[0].Text);
        Assert.Equal(AppText.RegionChoice("cn-north-1", 2), vm.Regions.Single(region => region.Region == "cn-north-1").Text);
    }

    [Fact]
    public async Task Changing_The_Service_Rebuilds_The_Regions_Picks_All_Regions_And_Clears_The_Rows()
    {
        AwsFeedViewModel vm = Vm(Feeds(AwsTestData.Catalogue()));
        await vm.ActivateAsync();
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Rows);

        vm.SelectedService = vm.Services.Single(service => service.Name == "S3");

        Assert.Equal(new string?[] { null, "cn-northwest-1" }, vm.Regions.Select(region => region.Region).ToArray());
        Assert.Null(vm.SelectedRegion!.Region);
        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
        Assert.False(vm.WriteFeedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Changing_The_Region_Clears_The_Rows()
    {
        AwsFeedViewModel vm = Vm(Feeds(AwsTestData.Catalogue()));
        await vm.ActivateAsync();
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasRows);

        vm.SelectedRegion = vm.Regions.Single(region => region.Region == "cn-north-1");

        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
        Assert.Equal(AppText.CloudSummaryInitial, vm.SummaryText);
    }

    [Fact]
    public async Task A_Picker_Losing_Its_Items_Writes_Null_And_The_Pick_Survives()
    {
        AwsFeedViewModel vm = Vm(Feeds(AwsTestData.Catalogue()));
        await vm.ActivateAsync();
        vm.SelectedService = vm.Services.Single(service => service.Name == "EC2");
        vm.SelectedRegion = vm.Regions.Single(region => region.Region == "cn-north-1");

        vm.SelectedRegion = null;
        vm.SelectedService = null;

        Assert.Equal("EC2", vm.SelectedService!.Name);
        Assert.Equal("cn-north-1", vm.SelectedRegion!.Region);
    }

    [Fact]
    public async Task Check_Looks_Up_The_Picked_Region_And_Lists_Its_Ranges()
    {
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        AwsFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Region == "cn-north-1");

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(new AwsSelection("EC2", "cn-north-1"), feeds.LastSelection);
        Assert.Equal(["52.80.0.0/24", "52.80.1.0/24", "2406:da00::/32"], vm.Rows.Select(row => row.NetworkText).ToArray());
        Assert.All(vm.Rows, row => Assert.Equal(RangeConfirmation.Confirmed, row.Status));
        Assert.All(vm.Rows, row => Assert.True(row.IsIncluded));
        Assert.Equal(AppText.CloudSummary(3, 3, 0, 0), vm.SummaryText);
    }

    [Fact]
    public async Task Check_With_All_Regions_Asks_For_Every_Region()
    {
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        AwsFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(new AwsSelection("EC2", null), feeds.LastSelection);
        Assert.Equal(9, vm.Rows.Count);
    }

    [Fact]
    public async Task A_Failed_Update_Check_Keeps_The_Cached_List_And_Says_So()
    {
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        feeds.UpdateFunc = current => new AwsLoadResult(AwsUpdateStatus.CheckFailed, AwsSourceFailure.Offline, current, Now);
        AwsFeedViewModel vm = Vm(feeds);

        await vm.ActivateAsync();

        Assert.Equal(4, vm.Services.Count);
        Assert.Equal(FileText + "\u00A0\u00B7\u00A0couldn't check for a newer file", vm.StatusText);
        Assert.Equal(AppText.AwsEmptyInitial, vm.EmptyText);
        Assert.True(vm.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_Update_Keeps_The_Pick_Clears_The_Rows_And_Names_The_New_File()
    {
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        AwsFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Region == "cn-north-1");
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasRows);
        AwsRangeCatalogue newer = AwsTestData.Catalogue(AwsTestData.RangesJson.Replace("1792136826", "1792200000").Replace("07-47-06", "09-30-00"));
        feeds.UpdateFunc = _ => new AwsLoadResult(AwsUpdateStatus.Updated, AwsSourceFailure.None, newer, Now);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("EC2", vm.SelectedService!.Name);
        Assert.Equal("cn-north-1", vm.SelectedRegion!.Region);
        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
        Assert.Equal("AWS ranges of 16 October 2026, 09:30 UTC\u00A0\u00B7\u00A0updated, checked 16:05", vm.StatusText);
        Assert.Same(feeds.Cached, feeds.LastCurrent);
    }

    [Fact]
    public async Task An_Update_That_Drops_The_Picked_Region_Falls_Back_To_All_Regions_Of_The_Service()
    {
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        AwsFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Region == "cn-north-1");
        AwsRangeCatalogue newer = AwsTestData.Catalogue(AwsTestData.RangesJson.Replace("cn-north-1", "cn-north-2"));
        feeds.UpdateFunc = _ => new AwsLoadResult(AwsUpdateStatus.Updated, AwsSourceFailure.None, newer, Now);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("EC2", vm.SelectedService!.Name);
        Assert.Null(vm.SelectedRegion!.Region);
    }

    [Fact]
    public async Task An_Update_That_Drops_The_Whole_Service_Falls_Back_To_The_Default_Service()
    {
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        AwsFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        vm.SelectedService = vm.Services.Single(service => service.Name == "S3");
        AwsRangeCatalogue newer = AwsTestData.Catalogue(AwsTestData.RangesJson.Replace("\"S3\"", "\"S4\""));
        feeds.UpdateFunc = _ => new AwsLoadResult(AwsUpdateStatus.Updated, AwsSourceFailure.None, newer, Now);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("EC2", vm.SelectedService!.Name);
        Assert.Null(vm.SelectedRegion!.Region);
    }

    [Theory]
    [InlineData(AwsSourceFailure.Offline, "The AWS IP ranges couldn't be loaded. Check the network connection, then try again.")]
    [InlineData(AwsSourceFailure.ServiceError, "The AWS IP ranges couldn't be loaded. Please try again or contact the Infra Team.")]
    [InlineData(AwsSourceFailure.FileUnreadable, "The AWS IP ranges couldn't be read.")]
    public async Task With_Nothing_Cached_And_No_File_The_Lists_Are_Empty_Check_Is_Off_And_The_Reason_Shows(
        AwsSourceFailure failure, string expected)
    {
        FakeAwsFeedService feeds = Feeds(null);
        feeds.UpdateFunc = _ => new AwsLoadResult(AwsUpdateStatus.Unavailable, failure, null, Now);
        AwsFeedViewModel vm = Vm(feeds);

        await vm.ActivateAsync();

        Assert.Empty(vm.Services);
        Assert.Empty(vm.Regions);
        Assert.Null(vm.SelectedService);
        Assert.Null(vm.SelectedRegion);
        Assert.Equal(expected, vm.EmptyText);
        Assert.Equal(AppText.AwsStatusUnavailable, vm.StatusText);
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.True(vm.CheckForUpdatesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Checking_For_Updates_After_A_Failure_Recovers()
    {
        FakeAwsFeedService feeds = Feeds(null);
        feeds.UpdateFunc = _ => new AwsLoadResult(AwsUpdateStatus.Unavailable, AwsSourceFailure.Offline, null, Now);
        AwsFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        AwsRangeCatalogue found = AwsTestData.Catalogue();
        feeds.UpdateFunc = _ => new AwsLoadResult(AwsUpdateStatus.Updated, AwsSourceFailure.None, found, Now);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.Services.Count);
        Assert.Equal("EC2", vm.SelectedService!.Name);
        Assert.Equal(AppText.AwsEmptyInitial, vm.EmptyText);
        Assert.True(vm.CheckCommand.CanExecute(null));
        Assert.Null(feeds.LastCurrent);
    }

    [Fact]
    public async Task An_Exception_From_The_Update_Check_Is_Reported_And_Does_Not_Escape()
    {
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        feeds.UpdateFunc = _ => throw new InvalidOperationException("secret detail");
        AwsFeedViewModel vm = Vm(feeds);

        await vm.ActivateAsync();

        Assert.False(vm.IsWorking);
        Assert.DoesNotContain("secret", vm.StatusText);
        Assert.DoesNotContain("secret", vm.EmptyText);
        Assert.Equal(4, vm.Services.Count);
    }

    [Fact]
    public async Task A_Ticked_Range_Selection_Is_Written_To_The_AWS_Feed_File()
    {
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        AwsFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Region == "cn-north-1");
        await vm.CheckCommand.ExecuteAsync(null);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        string ipv4 = Path.Combine(folder, "AWS_EC2_cn-north-1_ipv4.txt");
        Assert.True(File.Exists(ipv4));
        Assert.True(File.Exists(Path.Combine(folder, "AWS_EC2_cn-north-1_ipv6.txt")));
        Assert.Contains("# Region: cn-north-1", File.ReadAllText(ipv4));
        Assert.Equal(AppText.CloudFeedWritten(3, 2), vm.ActionText);
    }

    [Fact]
    public async Task With_No_Feed_Type_Ticked_Write_Feed_Asks_And_Writes_No_AWS_Feed()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false };
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        AwsFeedViewModel vm = Vm(feeds);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Region == "cn-north-1");
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Rows);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(1, _ui.ConfigureFeedsAsks);
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "fortigate")));
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task Open_Folder_Opens_The_Export_Folder()
    {
        AwsFeedViewModel vm = Vm(Feeds(AwsTestData.Catalogue()));

        await vm.OpenFeedFolderCommand.ExecuteAsync(null);

        Assert.Equal([_temp.ExportFolder], _ui.OpenedFolders);
    }

    [Fact]
    public async Task Disposing_During_The_Update_Check_Stops_It_Quietly()
    {
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        feeds.UpdateGate = new TaskCompletionSource();
        AwsFeedViewModel vm = Vm(feeds);
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
        FakeAwsFeedService feeds = Feeds(AwsTestData.Catalogue());
        AwsFeedViewModel vm = Vm(feeds);
        vm.Dispose();

        await vm.ActivateAsync();

        Assert.Equal(0, feeds.LoadCachedCalls);
        Assert.Equal(0, feeds.UpdateCalls);
    }

    [Fact]
    public async Task With_The_Real_Service_The_File_Is_Downloaded_Once_Then_Answered_With_A_304_And_Then_Used_Offline()
    {
        string mode = "online";
        var handler = new StubHttpHandler((request, _) =>
        {
            if (mode == "offline")
                throw new HttpRequestException("offline");
            if (mode == "304" && request.Headers.IfNoneMatch.Any(tag => tag.ToString() == AwsTestData.ETag))
                return new HttpResponseMessage(HttpStatusCode.NotModified);

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AwsTestData.RangesJson, Encoding.UTF8) };
            response.Headers.TryAddWithoutValidation("ETag", AwsTestData.ETag);
            return response;
        });
        using var http = new HttpClient(handler);

        var first = new AwsFeedViewModel(
            new AwsFeedService(_ipinfo, http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder),
            _ipinfo, _ui, _time, new WorkGate(), _temp.ExportFolder);
        await first.ActivateAsync();
        Assert.Equal(4, first.Services.Count);
        Assert.Equal(FileText + "\u00A0\u00B7\u00A0updated, checked 16:05", first.StatusText);
        Assert.True(File.Exists(_temp.Paths.AwsIpRangesPath));
        first.Dispose();

        mode = "304";
        var second = new AwsFeedViewModel(
            new AwsFeedService(_ipinfo, http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder),
            _ipinfo, _ui, _time, new WorkGate(), _temp.ExportFolder);
        await second.ActivateAsync();
        Assert.Equal(4, second.Services.Count);
        Assert.Equal(UpToDateText, second.StatusText);
        second.Dispose();

        mode = "offline";
        var third = new AwsFeedViewModel(
            new AwsFeedService(_ipinfo, http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder),
            _ipinfo, _ui, _time, new WorkGate(), _temp.ExportFolder);
        await third.ActivateAsync();
        Assert.Equal(4, third.Services.Count);
        Assert.Equal(FileText + "\u00A0\u00B7\u00A0couldn't check for a newer file", third.StatusText);
        third.Dispose();
    }

    private AwsFeedViewModel Vm(FakeAwsFeedService feeds) =>
        new(feeds, _ipinfo, _ui, _time, new WorkGate(), _temp.ExportFolder);

    /// <summary>A fake whose cache holds the catalogue and whose update check reports "up to date".</summary>
    private FakeAwsFeedService Feeds(AwsRangeCatalogue? cached)
    {
        var real = new AwsFeedService(_ipinfo, _http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder);
        return new FakeAwsFeedService(real)
        {
            Cached = cached,
            UpdateFunc = current => new AwsLoadResult(AwsUpdateStatus.UpToDate, AwsSourceFailure.None, current, Now),
        };
    }
}
