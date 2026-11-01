using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.GoogleCloud;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

// What a cloud page does when Refresh all, or its own load, saves a newer provider file
public sealed class CloudRefreshListenerTests : IDisposable
{
    private static readonly DateTimeOffset Now = AzureTestData.Now;
    private static readonly DateTimeOffset UpdatedUtc = new(2026, 10, 16, 9, 30, 0, TimeSpan.Zero);

    private const string GoogleJson = """
        {"syncToken": "1", "creationTime": "2026-01-15T00:00:00Z", "prefixes": [
          {"ipv4Prefix": "34.1.0.0/16", "service": "Google Cloud", "scope": "us-central1"},
          {"ipv4Prefix": "35.1.0.0/16", "service": "Google Cloud", "scope": "us-central1"}
        ]}
        """;

    private const string NewGoogleJson = """
        {"syncToken": "2", "creationTime": "2026-01-16T00:00:00Z", "prefixes": [
          {"ipv4Prefix": "34.1.0.0/16", "service": "Google Cloud", "scope": "us-central1"},
          {"ipv4Prefix": "35.1.0.0/16", "service": "Google Cloud", "scope": "us-central1"},
          {"ipv4Prefix": "34.2.0.0/16", "service": "Google Cloud", "scope": "europe-west1"}
        ]}
        """;

    private const string GoneGoogleJson = """
        {"syncToken": "3", "creationTime": "2026-01-17T00:00:00Z", "prefixes": [
          {"ipv4Prefix": "34.2.0.0/16", "service": "Google Cloud", "scope": "europe-west1"}
        ]}
        """;

    private const string LargeGoogleJson = """
        {"syncToken": "4", "creationTime": "2026-01-18T00:00:00Z", "prefixes": [
          {"ipv4Prefix": "34.0.0.0/8", "service": "Google Cloud", "scope": "us-central1"}
        ]}
        """;

    private const string GoneAzureJson = """
        {"changeNumber":422,"cloud":"Public","values":[
          {"name":"Other.x","properties":{"changeNumber":1,"region":"x","addressPrefixes":["14.9.0.0/24"]}}
        ]}
        """;

    private const string GoneAwsJson = """
        {"syncToken":"1792136827","createDate":"2026-10-16-08-47-06","prefixes":[
          {"ip_prefix":"54.239.0.0/24","region":"cn-northwest-1","service":"S3","network_border_group":"cn-northwest-1"}
        ],"ipv6_prefixes":[]}
        """;

    private static readonly IReadOnlyList<string> ModFiles = [Path.Combine("x", "modsecurity", "GoogleCloud_us-central1_ipv4.txt")];

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeUi _ui = new();
    private readonly WorkGate _gate = new();
    private readonly HttpClient _http;
    private readonly FakeIpInfoService _ipinfo = new()
    {
        Dataset = new DatasetInfo(new DateOnly(2026, 10, 8), 4, "abc", Now, true),
        Records = new RecordingRecords { Locks = FeedRecords.None },
    };

    private string _googleJson = GoogleJson;
    private string _domain = "google.com";
    // Prefixes the fake dataset shows under a second, unrelated organisation as well, so they check as mixed
    private readonly HashSet<string> _mixed = new(StringComparer.Ordinal);

    public CloudRefreshListenerTests()
    {
        _gate.SetStarted(true);
        _http = new HttpClient(new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_googleJson, Encoding.UTF8, "application/json"),
        }));
        _ipinfo.OverlapFunc = blocks => new OverlapResult(OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, _mixed.Contains(block.Text)
                ? [new OverlapGroup("US", "United States", "AS1", "Cloud", _domain, 1),
                   new OverlapGroup("US", "United States", "AS2", "Other", "example.net", 1)]
                : [new OverlapGroup("US", "United States", "AS1", "Cloud", _domain, 1)])).ToList(),
            _ipinfo.Dataset);
    }

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    // ----- one page of each provider, behind the same handles -----

    private sealed record Page(
        CloudFeedPageViewModel Vm, Func<Task> Activate, Func<Task<object>> NewCatalogue, Action<object> Raise,
        Func<int> Checks, Func<object, bool> Shows, Func<string> Status, Func<object, string> UpdatedStatus,
        Func<Task<object>> GoneCatalogue);

    private Page Google(GoogleFeeds? feeds = null)
    {
        _domain = "google.com";
        feeds ??= GoogleService();
        var vm = new GoogleCloudFeedViewModel(feeds, _ipinfo, _ui, _gate, _temp.ExportFolder);
        return new Page(vm, vm.ActivateAsync,
            async () =>
            {
                _googleJson = NewGoogleJson;
                return (await feeds.Inner.LoadCatalogueAsync(CancellationToken.None)).Catalogue!;
            },
            catalogue => feeds.RaiseSourceUpdated(new((CloudRangeCatalogue)catalogue, UpdatedUtc)),
            () => feeds.Checks,
            _ => vm.Regions.Any(region => region.Scope.Id == "europe-west1"),
            () => "", _ => "",
            async () =>
            {
                _googleJson = GoneGoogleJson;
                return (await feeds.Inner.LoadCatalogueAsync(CancellationToken.None)).Catalogue!;
            });
    }

    private GoogleFeeds GoogleService() =>
        new(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _temp.ExportFolder));

    private Page Azure(FakeAzureFeedService? feeds = null)
    {
        _domain = "microsoft.com";
        feeds ??= AzureService();
        var vm = new AzureFeedViewModel(feeds, _ipinfo, _ui, _time, _gate, _temp.ExportFolder);
        return new Page(vm, vm.ActivateAsync,
            () => Task.FromResult<object>(AzureTestData.Catalogue(change: 423)),
            catalogue => feeds.RaiseSourceUpdated(new((AzureTagCatalogue)catalogue, UpdatedUtc)),
            () => feeds.CheckCalls,
            catalogue => vm.Services.Count > 0 && ReferenceEquals(vm.Services[0].Group, ((AzureTagCatalogue)catalogue).Services[0]),
            () => vm.StatusText,
            catalogue => AppText.AzureStatusUpdated(((AzureTagCatalogue)catalogue).Source.FileDate,
                                                    ((AzureTagCatalogue)catalogue).Source.ChangeNumber, new TimeOnly(9, 30)),
            () => Task.FromResult<object>(AzureTestData.Catalogue(GoneAzureJson, change: 424)));
    }

    private FakeAzureFeedService AzureService() =>
        new(new AzureFeedService(_ipinfo, _http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder))
        {
            Cached = AzureTestData.Catalogue(),
            UpdateFunc = current => new AzureLoadResult(AzureUpdateStatus.UpToDate, AzureSourceFailure.None, current, Now),
        };

    private Page Aws(FakeAwsFeedService? feeds = null)
    {
        _domain = "amazon.com";
        feeds ??= AwsService();
        var vm = new AwsFeedViewModel(feeds, _ipinfo, _ui, _time, _gate, _temp.ExportFolder);
        return new Page(vm, vm.ActivateAsync,
            () => Task.FromResult<object>(AwsTestData.Catalogue(
                AwsTestData.RangesJson.Replace("\"service\":\"S3\"", "\"service\":\"ROUTE53\""), "\"new\"")),
            catalogue => feeds.RaiseSourceUpdated(new((AwsRangeCatalogue)catalogue, UpdatedUtc)),
            () => feeds.CheckCalls,
            _ => vm.Services.Any(option => option.Name == "ROUTE53"),
            () => vm.StatusText,
            catalogue => AppText.AwsStatusUpdated(((AwsRangeCatalogue)catalogue).Source.CreatedUtc, new TimeOnly(9, 30)),
            () => Task.FromResult<object>(AwsTestData.Catalogue(GoneAwsJson, "\"gone\"")));
    }

    private FakeAwsFeedService AwsService() =>
        new(new AwsFeedService(_ipinfo, _http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder))
        {
            Cached = AwsTestData.Catalogue(),
            UpdateFunc = current => new AwsLoadResult(AwsUpdateStatus.UpToDate, AwsSourceFailure.None, current, Now),
        };

    private Page Make(string provider) => provider switch
    {
        "google" => Google(),
        "azure" => Azure(),
        _ => Aws(),
    };

    private static async Task<Page> CheckedAsync(Page page)
    {
        await page.Activate();
        await page.Vm.CheckCommand.ExecuteAsync(null);
        Assert.True(page.Vm.HasRows);
        Assert.True(page.Vm.WriteFeedCommand.CanExecute(null));
        return page;
    }

    private static async Task SettleAsync(CloudFeedPageViewModel vm)
    {
        for (int i = 0; i < 500 && vm.IsWorking; i++)
            await Task.Delay(10);

        Assert.False(vm.IsWorking);
    }

    private static List<bool> Ticks(CloudFeedPageViewModel vm) => vm.Rows.Select(row => row.IsIncluded).ToList();

    // ----- no rows -----

    [Theory]
    [InlineData("google")]
    [InlineData("azure")]
    [InlineData("aws")]
    public async Task With_no_rows_the_page_takes_the_new_file_and_says_when(string provider)
    {
        Page page = Make(provider);
        using CloudFeedPageViewModel vm = page.Vm;
        await page.Activate();
        object catalogue = await page.NewCatalogue();
        _gate.SetRefreshing(true);

        page.Raise(catalogue);
        _ui.RunPosted();

        Assert.True(page.Shows(catalogue));
        Assert.Equal(page.UpdatedStatus(catalogue), page.Status());
        Assert.Equal("", vm.ActionText);
        Assert.False(vm.HasRows);
        _gate.SetRefreshing(false);
        await SettleAsync(vm);
        Assert.Equal(0, page.Checks());
    }

    // ----- rows, no hand changes -----

    [Theory]
    [InlineData("google")]
    [InlineData("azure")]
    [InlineData("aws")]
    public async Task Rows_with_no_hand_changes_are_checked_again_once_the_refresh_ends(string provider)
    {
        Page page = await CheckedAsync(Make(provider));
        using CloudFeedPageViewModel vm = page.Vm;
        object catalogue = await page.NewCatalogue();
        _gate.SetRefreshing(true);

        page.Raise(catalogue);
        _ui.RunPosted();

        Assert.False(vm.HasRows);
        Assert.True(page.Shows(catalogue));
        Assert.Equal(page.UpdatedStatus(catalogue), page.Status());
        Assert.Equal(1, page.Checks());

        _gate.SetRefreshing(false);
        await SettleAsync(vm);

        Assert.Equal(2, page.Checks());
        Assert.True(vm.HasRows);
        Assert.Equal(AppText.CloudSourceRechecked, vm.ActionText);
        Assert.True(vm.WriteFeedCommand.CanExecute(null));

        _gate.SetRefreshing(true);
        _gate.SetRefreshing(false);
        await SettleAsync(vm);
        Assert.Equal(2, page.Checks());
    }

    [Fact]
    public async Task An_update_after_the_refresh_has_ended_checks_again_at_once()
    {
        Page page = await CheckedAsync(Azure());
        using CloudFeedPageViewModel vm = page.Vm;

        page.Raise(await page.NewCatalogue());
        _ui.RunPosted();
        await SettleAsync(vm);

        Assert.Equal(2, page.Checks());
        Assert.Equal(AppText.CloudSourceRechecked, vm.ActionText);
    }

    [Fact]
    public async Task The_check_again_applies_the_saved_feeds_ticks_and_its_line_leaves_them_to_the_rows()
    {
        var store = new SavedFeedStore();
        var paths = new AppPaths(Directory.CreateDirectory(Path.Combine(_temp.Folder, "data")).FullName,
                                 Path.Combine(_temp.Folder, "unused"));
        _ipinfo.Records = new FeedRecords(paths, new FakeTimeProvider(), store);
        store.Seed("GoogleCloud_us-central1", ["34.1.0.0/16"]);
        Page page = Google();
        using CloudFeedPageViewModel vm = page.Vm;
        await page.Activate();
        await vm.CheckCommand.ExecuteAsync(null);
        string savedLine = AppText.CloudSavedTicksApplied(0, 1);
        Assert.Equal(savedLine, vm.ActionText);
        _gate.SetRefreshing(true);

        page.Raise(await page.NewCatalogue());
        _ui.RunPosted();
        _gate.SetRefreshing(false);
        await SettleAsync(vm);

        Assert.Equal(2, page.Checks());
        Assert.Equal(AppText.CloudSourceRechecked, vm.ActionText);
        Assert.Equal([false, true], Ticks(vm));
    }

    // ----- rows with hand changes: the check again carries them -----

    private static readonly DatasetInfo OtherDataset = new(new DateOnly(2026, 10, 9), 5, "def", Now, true);

    private static CloudRangeRow Row(CloudFeedPageViewModel vm, string network) =>
        vm.Rows.Single(row => row.NetworkText == network);

    private async Task RefreshWithAsync(Page page, object catalogue)
    {
        _gate.SetRefreshing(true);
        page.Raise(catalogue);
        _ui.RunPosted();
        Assert.Equal(AppText.CloudWaitsForRefresh, page.Vm.EmptyText);
        _gate.SetRefreshing(false);
        await SettleAsync(page.Vm);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("azure")]
    [InlineData("aws")]
    public async Task A_hand_change_is_carried_into_the_check_again_and_counted(string provider)
    {
        Page page = await CheckedAsync(Make(provider));
        using CloudFeedPageViewModel vm = page.Vm;
        vm.Rows[0].IsIncluded = !vm.Rows[0].IsIncluded;
        List<bool> ticks = Ticks(vm);

        await RefreshWithAsync(page, await page.NewCatalogue());

        Assert.Equal(2, page.Checks());
        Assert.Equal(ticks, Ticks(vm));
        Assert.Equal(AppText.CloudSourceRechecked + " " + AppText.CloudSourceTicksKept(1), vm.ActionText);
        Assert.True(vm.WriteFeedCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_hand_unticked_confirmed_range_stays_unticked_and_only_it_is_counted()
    {
        Page page = await CheckedAsync(Google());
        using CloudFeedPageViewModel vm = page.Vm;
        Assert.All(vm.Rows, row => Assert.True(row.IsConfirmed && row.IsIncluded));
        Row(vm, "34.1.0.0/16").IsIncluded = false;

        await RefreshWithAsync(page, await page.NewCatalogue());

        Assert.True(Row(vm, "34.1.0.0/16").IsConfirmed);
        Assert.False(Row(vm, "34.1.0.0/16").IsIncluded);
        Assert.True(Row(vm, "35.1.0.0/16").IsIncluded);
        Assert.Equal(AppText.CloudSourceRechecked + " " + AppText.CloudSourceTicksKept(1), vm.ActionText);
    }

    [Fact]
    public async Task A_range_whose_status_changed_takes_the_check_agains_tick()
    {
        _mixed.UnionWith(["34.1.0.0/16", "35.1.0.0/16"]);
        Page page = Google();
        using CloudFeedPageViewModel vm = page.Vm;
        await page.Activate();
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.All(vm.Rows, row => Assert.True(row.IsMixed && !row.IsIncluded));
        Row(vm, "35.1.0.0/16").IsIncluded = true;
        _mixed.Clear();

        await RefreshWithAsync(page, await page.NewCatalogue());

        Assert.All(vm.Rows, row => Assert.True(row.IsConfirmed && row.IsIncluded));
        Assert.Equal(AppText.CloudSourceRechecked, vm.ActionText);
    }

    [Fact]
    public async Task Ticks_put_back_as_they_were_are_no_hand_change()
    {
        Page page = await CheckedAsync(Google());
        using CloudFeedPageViewModel vm = page.Vm;
        vm.Rows[0].IsIncluded = !vm.Rows[0].IsIncluded;
        vm.Rows[0].IsIncluded = !vm.Rows[0].IsIncluded;

        page.Raise(await page.NewCatalogue());
        _ui.RunPosted();
        await SettleAsync(vm);

        Assert.Equal(2, page.Checks());
        Assert.Equal(AppText.CloudSourceRechecked, vm.ActionText);
    }

    [Fact]
    public async Task After_a_write_of_the_hand_changes_the_check_again_still_carries_them()
    {
        Page page = await CheckedAsync(Google());
        using CloudFeedPageViewModel vm = page.Vm;
        Row(vm, "35.1.0.0/16").IsIncluded = false;
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.StartsWith("Wrote 1 range to 1 file", vm.ActionText);

        await RefreshWithAsync(page, await page.NewCatalogue());

        Assert.Equal(2, page.Checks());
        Assert.False(Row(vm, "35.1.0.0/16").IsIncluded);
        Assert.Equal(AppText.CloudSourceRechecked + " " + AppText.CloudSourceTicksKept(1), vm.ActionText);
    }

    [Fact]
    public async Task A_later_Check_by_hand_clears_the_refresh_line()
    {
        Page page = await CheckedAsync(Google());
        using CloudFeedPageViewModel vm = page.Vm;
        vm.Rows[0].IsIncluded = false;
        await RefreshWithAsync(page, await page.NewCatalogue());
        Assert.StartsWith(AppText.CloudSourceRechecked, vm.ActionText);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(3, page.Checks());
        Assert.Equal("", vm.ActionText);
    }

    // ----- the pick gone, or changed while the check again waits -----

    [Theory]
    [InlineData("google")]
    [InlineData("azure")]
    [InlineData("aws")]
    public async Task A_pick_gone_from_the_new_file_clears_the_rows_and_checks_nothing(string provider)
    {
        Page page = await CheckedAsync(Make(provider));
        using CloudFeedPageViewModel vm = page.Vm;
        object catalogue = await page.GoneCatalogue();
        _gate.SetRefreshing(true);

        page.Raise(catalogue);
        _ui.RunPosted();

        Assert.False(vm.HasRows);
        Assert.Equal(AppText.CloudSourcePickGone, vm.ActionText);
        Assert.Equal(page.UpdatedStatus(catalogue), page.Status());
        Assert.NotEqual(AppText.CloudWaitsForRefresh, vm.EmptyText);
        _gate.SetRefreshing(false);
        await SettleAsync(vm);
        Assert.Equal(1, page.Checks());
        Assert.Equal(AppText.CloudSourcePickGone, vm.ActionText);

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Checks());
        Assert.NotEqual(AppText.CloudSourcePickGone, vm.ActionText);
    }

    [Fact]
    public async Task A_pick_change_while_the_check_again_waits_drops_it()
    {
        Page page = await CheckedAsync(Google());
        using CloudFeedPageViewModel vm = page.Vm;
        vm.Rows[0].IsIncluded = false;
        _gate.SetRefreshing(true);
        page.Raise(await page.NewCatalogue());
        _ui.RunPosted();

        var google = (GoogleCloudFeedViewModel)vm;
        google.SelectedRegion = google.Regions.Single(region => region.Scope.Id == "europe-west1");
        _gate.SetRefreshing(false);
        await SettleAsync(vm);

        Assert.Equal(1, page.Checks());
        Assert.False(vm.HasRows);
        Assert.Equal("", vm.ActionText);
        Assert.NotEqual(AppText.CloudWaitsForRefresh, vm.EmptyText);
    }

    // ----- the ModSecurity rules press out during a refresh -----

    private async Task<(Page Page, GoogleFeeds Feeds, TaskCompletionSource Open, Task Press)> PressOutAsync()
    {
        GoogleFeeds feeds = GoogleService();
        Page page = await CheckedAsync(Google(feeds));
        feeds.WriteResult = new CloudFeedWriteResult(CloudFeedWriteFailure.None, ["f.txt"], 2, 0, 0, ModSecurityFiles: ModFiles);
        await page.Vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.True(page.Vm.ShowsModSecurityRules);
        var open = new TaskCompletionSource();
        _ui.ModSecurityRulesAnswer = _ => open.Task;
        _gate.SetRefreshing(true);
        Task press = page.Vm.ModSecurityRulesCommand.ExecuteAsync(null);
        Assert.True(page.Vm.IsWorking);
        return (page, feeds, open, press);
    }

    [Fact]
    public async Task An_update_heard_while_the_rules_press_is_out_waits_for_it_then_checks_once()
    {
        (Page page, GoogleFeeds feeds, TaskCompletionSource open, Task press) = await PressOutAsync();
        using CloudFeedPageViewModel vm = page.Vm;
        object catalogue = await page.NewCatalogue();

        page.Raise(catalogue);
        _ui.RunPosted();

        Assert.True(vm.HasRows);
        Assert.False(page.Shows(catalogue));

        open.SetResult();
        await press;

        Assert.True(page.Shows(catalogue));
        Assert.False(vm.HasRows);
        Assert.Equal(1, page.Checks());
        Assert.Equal(AppText.CloudWaitsForRefresh, vm.EmptyText);

        _gate.SetRefreshing(false);
        await SettleAsync(vm);
        _gate.SetRefreshing(true);
        _gate.SetRefreshing(false);
        await SettleAsync(vm);

        Assert.Equal(2, page.Checks());
        Assert.Equal(AppText.CloudSourceRechecked, vm.ActionText);
        feeds.WriteResult = new CloudFeedWriteResult(CloudFeedWriteFailure.None, ["f.txt"], 2, 0, 0);
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(2, feeds.Writes);
        Assert.NotNull(feeds.LastCheck);
        Assert.Same(feeds.LastCheck, feeds.PlannedCheck);
    }

    [Fact]
    public async Task A_press_still_out_when_the_refresh_ends_checks_again_only_once_it_ends()
    {
        (Page page, _, TaskCompletionSource open, Task press) = await PressOutAsync();
        using CloudFeedPageViewModel vm = page.Vm;
        object catalogue = await page.NewCatalogue();
        page.Raise(catalogue);
        _ui.RunPosted();

        _gate.SetRefreshing(false);

        Assert.Equal(1, page.Checks());
        Assert.True(vm.HasRows);
        Assert.False(page.Shows(catalogue));

        open.SetResult();
        await press;
        await SettleAsync(vm);

        Assert.True(page.Shows(catalogue));
        Assert.Equal(2, page.Checks());
        Assert.Equal(AppText.CloudSourceRechecked, vm.ActionText);
    }

    // ----- the dataset the ranges were checked against -----

    [Fact]
    public async Task Write_feed_refuses_a_check_made_on_an_older_dataset_until_the_next_Check()
    {
        GoogleFeeds feeds = GoogleService();
        Page page = await CheckedAsync(Google(feeds));
        using CloudFeedPageViewModel vm = page.Vm;
        feeds.WriteResult = new CloudFeedWriteResult(CloudFeedWriteFailure.None, ["f.txt"], 2, 0, 0);
        _ipinfo.Dataset = OtherDataset;

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.CloudDatasetChanged, vm.ActionText);
        Assert.Equal(0, feeds.Writes);

        await vm.CheckCommand.ExecuteAsync(null);
        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(1, feeds.Writes);
        Assert.StartsWith("Wrote 2 ranges to 1 file", vm.ActionText);
    }

    [Fact]
    public async Task Write_feed_refuses_a_dataset_that_changed_during_the_large_range_question()
    {
        _googleJson = LargeGoogleJson;
        GoogleFeeds feeds = GoogleService();
        Page page = await CheckedAsync(Google(feeds));
        using CloudFeedPageViewModel vm = page.Vm;
        feeds.WriteResult = new CloudFeedWriteResult(CloudFeedWriteFailure.None, ["f.txt"], 1, 0, 0);
        _ui.LargeRangeAnswer = _ =>
        {
            _ipinfo.Dataset = OtherDataset;
            return [];
        };

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Single(_ui.LargeRangePrompts);
        Assert.Equal(AppText.CloudDatasetChanged, vm.ActionText);
        Assert.Equal(0, feeds.Writes);
    }

    // ----- the page's own load -----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_pages_own_Check_for_updates_says_nothing_of_Refresh_all(bool postedWhileOut)
    {
        FakeAzureFeedService feeds = AzureService();
        Page page = await CheckedAsync(Azure(feeds));
        using CloudFeedPageViewModel vm = page.Vm;
        vm.Rows[0].IsIncluded = !vm.Rows[0].IsIncluded;
        AzureTagCatalogue updated = AzureTestData.Catalogue(change: 423);
        bool rowsWhileOut = false;
        feeds.UpdateFunc = _ =>
        {
            feeds.RaiseSourceUpdated(new(updated, UpdatedUtc));
            if (postedWhileOut)
            {
                _ui.RunPosted();
                rowsWhileOut = vm.HasRows && vm.ActionText == "";
            }
            return new AzureLoadResult(AzureUpdateStatus.Updated, AzureSourceFailure.None, updated, Now);
        };

        await ((AzureFeedViewModel)vm).CheckForUpdatesCommand.ExecuteAsync(null);
        _ui.RunPosted();
        await SettleAsync(vm);

        Assert.Equal(1, _ui.PostedCount);
        Assert.Equal(postedWhileOut, rowsWhileOut);
        Assert.Equal("", vm.ActionText);
        Assert.False(vm.HasRows);
        Assert.Equal(1, page.Checks());
        Assert.True(page.Shows(updated));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_pages_own_Reload_ranges_says_nothing_of_Refresh_all(bool postedWhileOut)
    {
        GoogleFeeds feeds = GoogleService();
        Page page = await CheckedAsync(Google(feeds));
        using CloudFeedPageViewModel vm = page.Vm;
        bool rowsWhileOut = false;
        _googleJson = NewGoogleJson;
        feeds.OnLoaded = catalogue =>
        {
            feeds.RaiseSourceUpdated(new(catalogue, UpdatedUtc));
            if (postedWhileOut)
            {
                _ui.RunPosted();
                rowsWhileOut = vm.HasRows;
            }
        };

        await ((GoogleCloudFeedViewModel)vm).ReloadRangesCommand.ExecuteAsync(null);
        _ui.RunPosted();
        await SettleAsync(vm);

        Assert.Equal(1, _ui.PostedCount);
        Assert.Equal(postedWhileOut, rowsWhileOut);
        Assert.Equal("", vm.ActionText);
        Assert.False(vm.HasRows);
        Assert.Equal(1, page.Checks());
        Assert.True(page.Shows(null!));
    }

    // ----- pages that ignore the update -----

    [Theory]
    [InlineData("google")]
    [InlineData("azure")]
    [InlineData("aws")]
    public async Task A_page_never_activated_ignores_the_update(string provider)
    {
        Page page = Make(provider);
        using CloudFeedPageViewModel vm = page.Vm;
        object catalogue = await page.NewCatalogue();

        page.Raise(catalogue);
        _ui.RunPosted();

        Assert.Equal(1, _ui.PostedCount);
        Assert.False(page.Shows(catalogue));
        Assert.Equal("", page.Status());
        Assert.Equal(0, page.Checks());
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("aws")]
    public async Task A_first_load_waiting_for_the_refresh_ignores_the_update_and_still_runs_once(string provider)
    {
        FakeAzureFeedService azure = AzureService();
        FakeAwsFeedService aws = AwsService();
        Page page = provider == "azure" ? Azure(azure) : Aws(aws);
        using CloudFeedPageViewModel vm = page.Vm;
        Func<int> loads = provider == "azure" ? () => azure.LoadCachedCalls : () => aws.LoadCachedCalls;
        object catalogue = await page.NewCatalogue();
        _gate.SetRefreshing(true);
        await page.Activate();
        Assert.Equal(AppText.CloudWaitsForRefresh, vm.EmptyText);

        page.Raise(catalogue);
        _ui.RunPosted();

        Assert.Equal(1, _ui.PostedCount);
        Assert.Equal(0, loads());
        Assert.False(page.Shows(catalogue));
        Assert.Equal(AppText.CloudWaitsForRefresh, vm.EmptyText);

        _gate.SetRefreshing(false);
        await SettleAsync(vm);
        _gate.SetRefreshing(true);
        _gate.SetRefreshing(false);
        await SettleAsync(vm);

        Assert.Equal(1, loads());
        Assert.NotEmpty(page.Status());
        Assert.Equal(0, page.Checks());
    }

    [Theory]
    [InlineData("google")]
    [InlineData("azure")]
    public async Task A_disposed_page_ignores_a_posted_update_and_hears_no_more(string provider)
    {
        Page page = await CheckedAsync(Make(provider));
        CloudFeedPageViewModel vm = page.Vm;
        object catalogue = await page.NewCatalogue();
        page.Raise(catalogue);
        Assert.Equal(1, _ui.PostedCount);

        vm.Dispose();
        _ui.RunPosted();

        Assert.True(vm.HasRows);
        Assert.False(page.Shows(catalogue));
        Assert.Equal(1, page.Checks());

        page.Raise(catalogue);
        Assert.Equal(1, _ui.PostedCount);
    }

    // ----- an open check, which holds a timed Refresh all back -----

    [Theory]
    [InlineData("google")]
    [InlineData("azure")]
    [InlineData("aws")]
    public async Task A_check_with_rows_holds_until_a_write_succeeds(string provider)
    {
        Page page = Make(provider);
        using CloudFeedPageViewModel vm = page.Vm;
        await page.Activate();
        Assert.False(vm.HoldsUnwrittenCheck);

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasRows);
        Assert.True(vm.HoldsUnwrittenCheck);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.StartsWith("Wrote ", vm.ActionText);
        Assert.False(vm.HoldsUnwrittenCheck);
    }

    [Fact]
    public async Task A_tick_changed_after_the_write_holds_again_until_it_is_put_back()
    {
        Page page = await CheckedAsync(Google());
        using CloudFeedPageViewModel vm = page.Vm;
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.False(vm.HoldsUnwrittenCheck);

        Row(vm, "35.1.0.0/16").IsIncluded = false;
        Assert.True(vm.HoldsUnwrittenCheck);

        Row(vm, "35.1.0.0/16").IsIncluded = true;
        Assert.False(vm.HoldsUnwrittenCheck);
    }

    [Fact]
    public async Task A_check_with_every_row_unticked_holds_nothing_until_one_is_ticked()
    {
        Page page = await CheckedAsync(Google());
        using CloudFeedPageViewModel vm = page.Vm;
        Assert.True(vm.HoldsUnwrittenCheck);

        vm.UntickAllCommand.Execute(null);
        Assert.False(vm.HoldsUnwrittenCheck);

        Row(vm, "34.1.0.0/16").IsIncluded = true;
        Assert.True(vm.HoldsUnwrittenCheck);
    }

    [Fact]
    public async Task Untick_all_after_the_write_holds_nothing()
    {
        Page page = await CheckedAsync(Google());
        using CloudFeedPageViewModel vm = page.Vm;
        await vm.WriteFeedCommand.ExecuteAsync(null);

        vm.UntickAllCommand.Execute(null);

        Assert.All(vm.Rows, row => Assert.False(row.IsIncluded));
        Assert.False(vm.HoldsUnwrittenCheck);
        vm.TickAllCommand.Execute(null);
        Assert.False(vm.HoldsUnwrittenCheck);
        Row(vm, "34.1.0.0/16").IsIncluded = false;
        Assert.True(vm.HoldsUnwrittenCheck);
    }

    [Fact]
    public async Task A_failed_write_keeps_the_hold()
    {
        GoogleFeeds feeds = GoogleService();
        Page page = await CheckedAsync(Google(feeds));
        using CloudFeedPageViewModel vm = page.Vm;
        feeds.WriteResult = new CloudFeedWriteResult(CloudFeedWriteFailure.FolderUnavailable, [], 0, 0, 0);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(1, feeds.Writes);
        Assert.True(vm.HoldsUnwrittenCheck);
    }

    [Fact]
    public async Task A_new_check_after_the_write_holds_again()
    {
        Page page = await CheckedAsync(Google());
        using CloudFeedPageViewModel vm = page.Vm;
        await vm.WriteFeedCommand.ExecuteAsync(null);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.HasRows);
        Assert.True(vm.HoldsUnwrittenCheck);
    }

    [Fact]
    public async Task A_failed_check_holds_nothing()
    {
        Page page = await CheckedAsync(Google());
        using CloudFeedPageViewModel vm = page.Vm;
        Assert.True(vm.HoldsUnwrittenCheck);
        _ipinfo.OverlapFunc = _ => throw new InvalidOperationException("test");

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.False(vm.HasRows);
        Assert.False(vm.HoldsUnwrittenCheck);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("azure")]
    [InlineData("aws")]
    public async Task Rows_cleared_hold_nothing(string provider)
    {
        Page page = await CheckedAsync(Make(provider));
        using CloudFeedPageViewModel vm = page.Vm;
        Assert.True(vm.HoldsUnwrittenCheck);

        page.Raise(await page.GoneCatalogue());
        _ui.RunPosted();

        Assert.False(vm.HasRows);
        Assert.False(vm.HoldsUnwrittenCheck);
    }

    [Fact]
    public async Task A_disposed_page_holds_nothing()
    {
        Page page = await CheckedAsync(Google());
        CloudFeedPageViewModel vm = page.Vm;
        Assert.True(vm.HoldsUnwrittenCheck);

        vm.Dispose();

        Assert.True(vm.HasRows);
        Assert.False(vm.HoldsUnwrittenCheck);
    }

    [Fact]
    public async Task A_check_again_after_a_refresh_keeps_whether_the_rows_were_written()
    {
        Page page = await CheckedAsync(Google());
        using CloudFeedPageViewModel vm = page.Vm;
        await vm.WriteFeedCommand.ExecuteAsync(null);

        await RefreshWithAsync(page, await page.NewCatalogue());

        Assert.Equal(2, page.Checks());
        Assert.False(vm.HoldsUnwrittenCheck);
        Row(vm, "35.1.0.0/16").IsIncluded = false;
        Assert.True(vm.HoldsUnwrittenCheck);
    }

    [Fact]
    public async Task A_check_again_after_a_refresh_keeps_an_unwritten_hold()
    {
        Page page = await CheckedAsync(Google());
        using CloudFeedPageViewModel vm = page.Vm;

        await RefreshWithAsync(page, await page.NewCatalogue());

        Assert.Equal(2, page.Checks());
        Assert.True(vm.HoldsUnwrittenCheck);
    }

    // The real service, with an event the test raises and a hook inside the page's own load
    private sealed class GoogleFeeds(GoogleCloudFeedService inner) : IGoogleCloudFeedService
    {
        public event EventHandler<CloudSourceUpdated<CloudRangeCatalogue>>? SourceUpdated;

        public GoogleCloudFeedService Inner => inner;

        public Action<CloudRangeCatalogue>? OnLoaded { get; set; }

        public int Checks { get; private set; }

        public int Writes { get; private set; }

        public CloudFeedCheck? LastCheck { get; private set; }

        public CloudFeedCheck? PlannedCheck { get; private set; }

        /// <summary>Answers Write feed in place of the real write when set.</summary>
        public CloudFeedWriteResult? WriteResult { get; set; }

        public void RaiseSourceUpdated(CloudSourceUpdated<CloudRangeCatalogue> update) => SourceUpdated?.Invoke(this, update);

        public async Task<CatalogueResult> LoadCatalogueAsync(CancellationToken ct)
        {
            CatalogueResult result = await inner.LoadCatalogueAsync(ct);
            if (result.Catalogue is { } catalogue)
                OnLoaded?.Invoke(catalogue);
            return result;
        }

        public Task<SavedRanges<CloudRangeCatalogue>> LoadSavedAsync(CancellationToken ct) => inner.LoadSavedAsync(ct);

        public async Task<CheckResult> CheckAsync(CloudRangeCatalogue catalogue, RegionSelection selection, CancellationToken ct)
        {
            Checks++;
            CheckResult result = await inner.CheckAsync(catalogue, selection, ct);
            LastCheck = result.Check;
            return result;
        }

        public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked)
        {
            PlannedCheck = check;
            return inner.PlanWrite(check, ticked);
        }

        public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges,
                                                     CancellationToken ct)
        {
            Writes++;
            return WriteResult is { } result ? Task.FromResult(result) : inner.WriteAsync(plan, includedLargeRanges, ct);
        }
    }

    private sealed class SavedFeedStore : ISavedFeedStore
    {
        private SavedFeedList _list = SavedFeedList.Empty;

        public SavedFeedsRead Read() => new(true, _list.Feeds);

        public void Seed(string key, string[] unticked)
        {
            var pick = new SavedFeedPick(null, "us-central1", false, null, null, null, [], unticked, [], [], null, null, null);
            Assert.Equal(SavedFeedsFailure.None,
                Apply(key, FeedKind.GoogleCloud, new SavedFeedIntent.Written(pick), DateTimeOffset.UnixEpoch));
        }

        public SavedFeedsFailure Apply(string key, FeedKind kind, SavedFeedIntent intent, DateTimeOffset atUtc)
        {
            (SavedFeedsFailure failure, _list) = _list.Apply(key, kind, intent, atUtc);
            return failure;
        }

        public IReadOnlyList<SavedFeedStampOutcome> ApplyStamps(IReadOnlyList<SavedFeedStamp> stamps)
        {
            (IReadOnlyList<SavedFeedStampOutcome> outcomes, _list) = _list.ApplyStamps(stamps, SavedFeedList.MaxJsonBytes);
            return outcomes;
        }

        public SavedFeedsFailure Remove(string key)
        {
            (SavedFeedsFailure failure, _list) = _list.Remove(key);
            return failure;
        }
    }
}
