using System.Net;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.FeedExceptions;
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
        var vm = new GoogleCloudFeedViewModel(feeds, _ipinfo, new FakeUi(), new WorkGate(), _folder);
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
        var vm = new GoogleCloudFeedViewModel(feeds, _ipinfo, new FakeUi(), new WorkGate(), _folder);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "us-central1");

        await vm.CheckCommand.ExecuteAsync(null);
        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.CloudNoDataset, vm.EmptyText);
        Assert.Empty(vm.Rows);
        Assert.False(vm.WriteFeedCommand.CanExecute(null));
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Writing_With_Exceptions_Says_What_They_Cut()
    {
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.1.0.0/17");
        GoogleCloudFeedViewModel vm = await CheckedAsync();

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal("Wrote 1 range to 1 file" + Dash + "1 range cut by exceptions", vm.ActionText);
        string feed = File.ReadAllText(Path.Combine(_folder, "fortigate", "GoogleCloud_us-central1_ipv4.txt"));
        Assert.EndsWith(Environment.NewLine + "34.1.128.0/17" + Environment.NewLine, feed);
    }

    [Fact]
    public async Task Writing_When_Exceptions_Cover_Everything_Names_The_Old_File()
    {
        GoogleCloudFeedViewModel vm = await CheckedAsync();
        await vm.WriteFeedCommand.ExecuteAsync(null);
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.0.0.0/8");

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(
            "Nothing was written" + Dash + "exceptions cover every ticked range" + Dash + "the old GoogleCloud_us-central1_ipv4.txt was left as it was",
            vm.ActionText);
    }

    [Fact]
    public async Task Writing_While_The_Exceptions_Are_Unreadable_Writes_No_File()
    {
        _ipinfo.Exceptions = ExceptionList.Unreadable;
        GoogleCloudFeedViewModel vm = await CheckedAsync();

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal("The exceptions list couldn't be read, so no feed was written. Open Settings to fix it.", vm.ActionText);
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task A_Region_Change_Clears_The_Line()
    {
        _ipinfo.Exceptions = ExceptionList.Unreadable;
        GoogleCloudFeedViewModel vm = await CheckedAsync();
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(AppText.ExceptionsUnreadable, vm.ActionText);

        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "europe-west1");

        Assert.Equal("", vm.ActionText);
        Assert.False(vm.HasActionText);
    }

    [Fact]
    public async Task The_Pages_Own_Message_Clears_With_The_Rows()
    {
        GoogleCloudFeedViewModel vm = await CheckedAsync();
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.StartsWith("Wrote 1 range to 1 file", vm.ActionText);

        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "europe-west1");

        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.ActionText);
        Assert.False(vm.HasActionText);
    }

    // ----- the unsaved-settings note and the large-range question -----

    [Fact]
    public async Task Writing_While_The_Settings_Page_Has_Unsaved_Changes_Says_The_Saved_Settings_Were_Used()
    {
        var unsaved = new FakeUnsavedSettings(hasUnsavedChanges: true);
        GoogleCloudFeedViewModel vm = await CheckedAsync(unsaved: unsaved);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal("Wrote 1 range to 1 file" + AppText.UnsavedSettingsNote, vm.ActionText);
        Assert.Equal(Dash + "unsaved changes on the Settings page weren't used", AppText.UnsavedSettingsNote);
    }

    // The page is read once per write, just before the write, so an edit made or saved since the check counts as it
    // stands then
    [Fact]
    public async Task The_Note_Follows_The_Settings_Page_At_The_Time_Of_The_Write()
    {
        var unsaved = new FakeUnsavedSettings(hasUnsavedChanges: false);
        GoogleCloudFeedViewModel vm = await CheckedAsync(unsaved: unsaved);

        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal("Wrote 1 range to 1 file", vm.ActionText);
        Assert.Equal(1, unsaved.Reads);

        unsaved.HasUnsavedChanges = true;
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal("Wrote 1 range to 1 file" + AppText.UnsavedSettingsNote, vm.ActionText);
        Assert.Equal(2, unsaved.Reads);

        unsaved.HasUnsavedChanges = false;
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal("Wrote 1 range to 1 file", vm.ActionText);
        Assert.Equal(3, unsaved.Reads);
    }

    // The note describes what the write read: a save or an edit while the write runs doesn't change it
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_Note_Is_Decided_When_The_Write_Starts(bool unsavedAtStart)
    {
        var unsaved = new FakeUnsavedSettings(hasUnsavedChanges: unsavedAtStart);
        var feeds = new HeldFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), _folder));
        GoogleCloudFeedViewModel vm = await CheckedAsync(unsaved: unsaved, feeds: feeds);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feeds.WriteGate = release;

        Task write = vm.WriteFeedCommand.ExecuteAsync(null);
        await feeds.WriteStarted.Task;
        unsaved.HasUnsavedChanges = !unsavedAtStart;
        release.SetResult();
        await write;

        Assert.Equal("Wrote 1 range to 1 file" + (unsavedAtStart ? AppText.UnsavedSettingsNote : ""), vm.ActionText);
    }

    // The note goes on a written feed and on the lines where exceptions decided that nothing was written, never on a
    // failure, a pick with nothing ticked or one whose every range was a large range left out
    [Fact]
    public async Task Only_A_Write_Or_A_Write_The_Exceptions_Emptied_Gets_The_Note()
    {
        var unsaved = new FakeUnsavedSettings(hasUnsavedChanges: true);
        _ipinfo.Exceptions = ExceptionList.Unreadable;
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui, unsaved: unsaved);

        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(AppText.ExceptionsUnreadable, vm.ActionText);

        _ipinfo.Exceptions = RangeSubtractionTests.List("34.0.0.0/8");
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal("Nothing was written" + Dash + "exceptions cover every ticked range" + AppText.UnsavedSettingsNote,
            vm.ActionText);

        foreach (CloudRangeRow row in vm.Rows)
            row.IsIncluded = false;
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(AppText.CloudNothingTicked, vm.ActionText);

        // Both ranges are /16, so with 17 as the threshold both are large: none allowed leaves both out
        _ipinfo.Exceptions = ExceptionList.Empty;
        _ipinfo.Settings = _ipinfo.Settings with { IPv4MinPrefix = 17 };
        vm = await CheckedAsync(ui, unsaved: unsaved);
        ui.LargeRangeAnswer = _ => [];
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(AppText.CloudAllLeftOut, vm.ActionText);

        // One allowed and cut away by the exceptions, the other left out
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.0.0.0/8");
        foreach (CloudRangeRow row in vm.Rows)
            row.IsIncluded = true;
        ui.LargeRangeAnswer = _ => ["34.1.0.0/16"];
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(AppText.CloudAllLeftOutOrExcepted + AppText.UnsavedSettingsNote, vm.ActionText);
    }

    // A write that failed on one file names it first, then the files written before it and the old files left; with
    // nothing to name it is the plain line. The unsaved note follows only files that were written
    [Fact]
    public async Task A_Failed_File_Write_Names_The_File_First()
    {
        var unsaved = new FakeUnsavedSettings(hasUnsavedChanges: false);
        var feeds = new FixedWriteFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), _folder));
        GoogleCloudFeedViewModel vm = await CheckedAsync(unsaved: unsaved, feeds: feeds);
        string folder = Path.Combine(_folder, "fortigate");
        string ipv4 = Path.Combine(folder, "AWS_EC2_cn-north-1_ipv4.txt");
        string ipv6 = Path.Combine(folder, "AWS_EC2_cn-north-1_ipv6.txt");

        var cases = new (CloudFeedWriteResult Result, bool Unsaved, string Expected)[]
        {
            (new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, [ipv4], 0, 0, 0, FailedFile: ipv6), false,
             "AWS_EC2_cn-north-1_ipv6.txt couldn't be written. AWS_EC2_cn-north-1_ipv4.txt was written before the failure. "
             + "Please try again or contact the Infra Team."),
            (new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, [], 0, 0, 0, OldFilesLeft: [ipv4], FailedFile: ipv6),
             true,
             "AWS_EC2_cn-north-1_ipv6.txt couldn't be written. The old AWS_EC2_cn-north-1_ipv4.txt was left as it was. "
             + "Please try again or contact the Infra Team."),
            (new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, [ipv4], 0, 0, 0, FailedFile: ipv6), true,
             "AWS_EC2_cn-north-1_ipv6.txt couldn't be written. AWS_EC2_cn-north-1_ipv4.txt was written before the failure. "
             + "Unsaved changes on the Settings page weren't used. Please try again or contact the Infra Team."),
            (new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, [], 0, 0, 0), true,
             "The feed couldn't be written. Please try again or contact the Infra Team."),
        };

        foreach ((CloudFeedWriteResult result, bool unsavedEdits, string expected) in cases)
        {
            unsaved.HasUnsavedChanges = unsavedEdits;
            feeds.Result = result;
            await vm.WriteFeedCommand.ExecuteAsync(null);
            Assert.Equal(expected, vm.ActionText);
        }

        Assert.Equal(AppText.CloudWriteFailed, AppText.CloudWriteFailedLine(null, [], null, unsaved: true));
        Assert.Equal("The feed couldn't be written. The old AWS_EC2_cn-north-1_ipv4.txt and AWS_EC2_cn-north-1_ipv6.txt were "
            + "left as they were. Please try again or contact the Infra Team.",
            AppText.CloudWriteFailedLine(null, [], [ipv4, ipv6], unsaved: false));
    }

    // The question holds the shared gate, so nothing on another page (the menu's Reset API token) opens over it, and
    // the page is working from the press to the end
    [Fact]
    public async Task The_Large_Range_Question_Holds_The_Gate_And_The_Page_Works_Throughout()
    {
        _ipinfo.Settings = _ipinfo.Settings with { IPv4MinPrefix = 17 };
        var gate = new WorkGate();
        gate.SetStarted(true);
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui, gate);
        bool? canStartDuringDialog = null;
        bool? workingDuringDialog = null;
        ui.OnDialogOpen = () =>
        {
            canStartDuringDialog = gate.CanStart;
            workingDuringDialog = vm.IsWorking;
        };
        ui.LargeRangeAnswer = prompt => [prompt.Choices[0].Network];
        var working = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsWorking))
                working.Add(vm.IsWorking);
        };

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Single(ui.LargeRangePrompts);
        Assert.False(canStartDuringDialog);
        Assert.True(workingDuringDialog);
        Assert.Equal([true, false], working);
        Assert.True(gate.CanStart);
        Assert.False(vm.IsWorking);
        Assert.Equal("Wrote 1 range to 1 file", vm.ActionText);
    }

    [Fact]
    public async Task The_Gate_Is_Released_When_The_Question_Is_Cancelled_Or_Throws()
    {
        _ipinfo.Settings = _ipinfo.Settings with { IPv4MinPrefix = 17 };
        var gate = new WorkGate();
        gate.SetStarted(true);
        var ui = new FakeUi { LargeRangeAnswer = _ => null };
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui, gate);

        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.True(gate.CanStart);
        Assert.False(vm.IsWorking);
        Assert.True(vm.WriteFeedCommand.CanExecute(null));

        ui.LargeRangeAnswer = _ => throw new InvalidOperationException("dialog");
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.True(gate.CanStart);
        Assert.False(vm.IsWorking);
        Assert.Equal(AppText.Generic, vm.ActionText);
        Assert.True(vm.WriteFeedCommand.CanExecute(null));
        Assert.Equal(2, ui.LargeRangePrompts.Count);
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());
    }

    // Write feed doesn't wait on the gate, so an update or another page's work doesn't lock it
    [Fact]
    public async Task Write_Feed_Runs_While_Something_Else_Holds_The_Gate()
    {
        var gate = new WorkGate();
        gate.SetStarted(true);
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui, gate);
        IDisposable other = gate.Hold();

        Assert.True(vm.WriteFeedCommand.CanExecute(null));
        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Empty(ui.LargeRangePrompts);
        Assert.Equal("Wrote 1 range to 1 file", vm.ActionText);
        Assert.False(gate.CanStart);
        other.Dispose();
        Assert.True(gate.CanStart);
    }

    // But its large-range question isn't asked then: the holder may still show its own dialog (Reset token while it
    // removes the token)
    [Fact]
    public async Task The_Large_Range_Question_Is_Not_Asked_While_Something_Else_Holds_The_Gate()
    {
        _ipinfo.Settings = _ipinfo.Settings with { IPv4MinPrefix = 17 };
        var gate = new WorkGate();
        gate.SetStarted(true);
        var ui = new FakeUi { LargeRangeAnswer = prompt => [prompt.Choices[0].Network] };
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui, gate);
        IDisposable other = gate.Hold();

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Empty(ui.LargeRangePrompts);
        Assert.Equal("The feed wasn't written \u2014 press Write feed again when the current task ends", vm.ActionText);
        Assert.False(vm.IsWorking);
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());

        other.Dispose();
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Single(ui.LargeRangePrompts);
        Assert.Equal("Wrote 1 range to 1 file", vm.ActionText);
        Assert.True(gate.CanStart);
    }

    // The us-central1 check, with only the confirmed range ticked
    private async Task<GoogleCloudFeedViewModel> CheckedAsync(
        FakeUi? ui = null, IWorkGate? gate = null, IUnsavedSettings? unsaved = null, IGoogleCloudFeedService? feeds = null)
    {
        feeds ??= new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), _folder);
        var vm = new GoogleCloudFeedViewModel(feeds, _ipinfo, ui ?? new FakeUi(), gate ?? new WorkGate(), _folder, unsaved);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "us-central1");
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(["34.1.0.0/16"], vm.Rows.Where(row => row.IsIncluded).Select(row => row.NetworkText));
        return vm;
    }

    private const string Dash = " \u2014 ";

    // The real service, with a gate that holds the NEXT write after it has been asked for
    private sealed class HeldFeeds(IGoogleCloudFeedService inner) : IGoogleCloudFeedService
    {
        public TaskCompletionSource? WriteGate { get; set; }

        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CatalogueResult> LoadCatalogueAsync(CancellationToken ct) => inner.LoadCatalogueAsync(ct);

        public Task<CheckResult> CheckAsync(CloudRangeCatalogue catalogue, RegionSelection selection, CancellationToken ct) =>
            inner.CheckAsync(catalogue, selection, ct);

        public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) =>
            inner.PlanWrite(check, ticked);

        public async Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges,
                                                           CancellationToken ct)
        {
            TaskCompletionSource? gate = WriteGate;
            WriteGate = null;
            WriteStarted.TrySetResult();
            if (gate is not null)
                await gate.Task.WaitAsync(ct);

            return await inner.WriteAsync(plan, includedLargeRanges, ct);
        }
    }

    // The real service, except that a write gives the result the test sets
    private sealed class FixedWriteFeeds(IGoogleCloudFeedService inner) : IGoogleCloudFeedService
    {
        public CloudFeedWriteResult? Result { get; set; }

        public Task<CatalogueResult> LoadCatalogueAsync(CancellationToken ct) => inner.LoadCatalogueAsync(ct);

        public Task<CheckResult> CheckAsync(CloudRangeCatalogue catalogue, RegionSelection selection, CancellationToken ct) =>
            inner.CheckAsync(catalogue, selection, ct);

        public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) =>
            inner.PlanWrite(check, ticked);

        public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges,
                                                     CancellationToken ct) =>
            Result is { } result ? Task.FromResult(result) : inner.WriteAsync(plan, includedLargeRanges, ct);
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