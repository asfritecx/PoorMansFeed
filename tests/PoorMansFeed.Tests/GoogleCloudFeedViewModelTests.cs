using System.Net;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.GoogleCloud;
using PoorMansFeed.Core.History;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class GoogleCloudFeedViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ipinfo-cloud-vm", Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http;
    private readonly FakeIpInfoService _ipinfo = new()
    {
        Dataset = new DatasetInfo(new DateOnly(2026, 10, 8), 3, "abc", DateTimeOffset.UnixEpoch, true),
        // Records nothing it can fail, so a real write's line carries no note
        Records = new RecordingRecords { Locks = FeedRecords.None },
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
        var feeds = new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder);
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
        var feeds = new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder);
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
        Assert.EndsWith("\n" + "34.1.128.0/17" + "\n", feed);
    }

    [Fact]
    public async Task Writing_When_Exceptions_Cover_Everything_Empties_The_Old_File()
    {
        GoogleCloudFeedViewModel vm = await CheckedAsync();
        await vm.WriteFeedCommand.ExecuteAsync(null);
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.0.0.0/8");

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal("Emptied 1 file" + Dash + "exceptions cover every ticked range", vm.ActionText);
        string file = Path.Combine(_folder, "fortigate", "GoogleCloud_us-central1_ipv4.txt");
        Assert.All(File.ReadAllLines(file), line => Assert.StartsWith("#", line));
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
        var feeds = new HeldFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder));
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

    // Each NothingToWrite is worded from what left the ranges out; only the exceptions' part takes the unsaved note
    [Theory]
    [InlineData(0, 0, 0, "no ranges were ticked")]
    [InlineData(2, 0, 0, "every ticked range is a large range that was left out")]
    [InlineData(0, 0, 3, "exceptions cover every ticked range")]
    [InlineData(2, 0, 3, "every ticked range was left out as a large range or removed by exceptions")]
    [InlineData(0, 1, 0, "every ticked range covers every address, which no feed takes")]
    [InlineData(2, 1, 0, "every ticked range was left out as a large range or for covering every address")]
    [InlineData(0, 1, 3, "every ticked range covers every address or was removed by exceptions")]
    [InlineData(2, 1, 3, "every ticked range was left out as a large range or for covering every address, or removed by exceptions")]
    public async Task Nothing_To_Write_Names_What_Left_The_Ranges_Out(int large, int whole, int removals, string reason)
    {
        var feeds = new FixedWriteFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder));
        GoogleCloudFeedViewModel vm = await CheckedAsync(unsaved: new FakeUnsavedSettings(hasUnsavedChanges: true), feeds: feeds);
        feeds.Result = new CloudFeedWriteResult(CloudFeedWriteFailure.NothingToWrite, [], 0, 0, large, 0, removals,
            WholeRangesLeftOut: whole);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal("Nothing was written" + Dash + reason + (removals > 0 ? AppText.UnsavedSettingsNote : ""), vm.ActionText);
    }

    // A write whose only files were emptied gives the reason nothing was left, worded as the line with nothing written;
    // its files were written under the saved settings, so the unsaved note comes with or without exception removals
    [Theory]
    [InlineData(2, 0, 3, 1, "every ticked range was left out as a large range or removed by exceptions")]
    [InlineData(0, 1, 0, 2, "every ticked range covers every address, which no feed takes")]
    [InlineData(2, 1, 3, 2, "every ticked range was left out as a large range or for covering every address, or removed by exceptions")]
    [InlineData(0, 0, 3, 1, "exceptions cover every ticked range")]
    public async Task A_Write_That_Only_Emptied_Files_Names_Them_And_Why(int large, int whole, int removals, int files, string reason)
    {
        var feeds = new FixedWriteFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder));
        GoogleCloudFeedViewModel vm = await CheckedAsync(unsaved: new FakeUnsavedSettings(hasUnsavedChanges: true), feeds: feeds);
        string[] emptied = Enumerable.Range(0, files).Select(i => Path.Combine(_folder, "fortigate", $"a{i}_ipv6.txt")).ToArray();
        feeds.Result = new CloudFeedWriteResult(CloudFeedWriteFailure.None, emptied, 0, 0, large, 0, removals,
            WholeRangesLeftOut: whole, EmptiedFiles: emptied);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal("Emptied " + files + (files == 1 ? " file" : " files") + Dash + reason + AppText.UnsavedSettingsNote,
            vm.ActionText);

        feeds.Result = feeds.Result with { Record = new FeedRecordResult(FeedSaveOutcome.StorageError, HistoryOutcome.Recorded) };
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.StartsWith("The saved feed couldn't be updated. Emptied ", vm.ActionText);
    }

    [Fact]
    public async Task A_Written_Feed_Names_The_Files_It_Emptied_After_The_Exceptions()
    {
        var feeds = new FixedWriteFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder));
        GoogleCloudFeedViewModel vm = await CheckedAsync(unsaved: new FakeUnsavedSettings(hasUnsavedChanges: false), feeds: feeds);
        string ipv4 = Path.Combine(_folder, "fortigate", "GoogleCloud_us-central1_ipv4.txt");
        string ipv6 = Path.Combine(_folder, "fortigate", "GoogleCloud_us-central1_ipv6.txt");
        feeds.Result = new CloudFeedWriteResult(CloudFeedWriteFailure.None, [ipv4, ipv6], 3, 0, 0, 0, 1, EmptiedFiles: [ipv6]);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal("Wrote 3 ranges to 2 files" + Dash + "1 range removed by exceptions" + Dash
            + Path.Combine("fortigate", "GoogleCloud_us-central1_ipv6.txt") + " was emptied", vm.ActionText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_Feed_Type_Gives_Its_Own_Line(bool unsavedEdits)
    {
        var feeds = new FixedWriteFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder));
        GoogleCloudFeedViewModel vm = await CheckedAsync(unsaved: new FakeUnsavedSettings(unsavedEdits), feeds: feeds);
        feeds.Result = new CloudFeedWriteResult(CloudFeedWriteFailure.NoFeedType, [], 0, 0, 2, WholeRangesLeftOut: 1);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal("Nothing was written" + Dash + "no feed type is ticked in Settings"
            + (unsavedEdits ? AppText.UnsavedSettingsNote : ""), vm.ActionText);
    }

    // The ranges covering every address follow the large ranges and come before the unconfirmed and the exceptions
    [Fact]
    public async Task A_Written_Feed_Names_The_Ranges_Covering_Every_Address_In_Order()
    {
        var feeds = new FixedWriteFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder));
        GoogleCloudFeedViewModel vm = await CheckedAsync(unsaved: new FakeUnsavedSettings(hasUnsavedChanges: true), feeds: feeds);
        feeds.Result = new CloudFeedWriteResult(CloudFeedWriteFailure.None, ["a"], 3, 1, 2, 0, 1, WholeRangesLeftOut: 1);

        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal("Wrote 3 ranges to 1 file" + Dash + "2 large ranges left out" + Dash
            + "1 range covering every address left out" + AppText.CloudUnconfirmed(1) + Dash + "1 range removed by exceptions"
            + AppText.UnsavedSettingsNote, vm.ActionText);

        feeds.Result = new CloudFeedWriteResult(CloudFeedWriteFailure.None, ["a"], 3, 0, 0, WholeRangesLeftOut: 2);
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal("Wrote 3 ranges to 1 file" + Dash + "2 ranges covering every address left out"
            + AppText.UnsavedSettingsNote, vm.ActionText);
    }

    [Fact]
    public async Task Every_Write_Failure_Has_A_Line_Of_Its_Own()
    {
        var feeds = new FixedWriteFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder));
        GoogleCloudFeedViewModel vm = await CheckedAsync(feeds: feeds);
        var lines = new List<string>();
        foreach (CloudFeedWriteFailure failure in Enum.GetValues<CloudFeedWriteFailure>().Where(f => f != CloudFeedWriteFailure.None))
        {
            feeds.Result = new CloudFeedWriteResult(failure, [], 0, 0, 0);
            await vm.WriteFeedCommand.ExecuteAsync(null);
            lines.Add(vm.ActionText);
        }

        Assert.DoesNotContain(AppText.Generic, lines);
        Assert.Equal(lines.Count, lines.Distinct().Count());
    }

    // A write that failed on one file names it first, then the files written before it and the old files left; with
    // nothing to name it is the plain line. The unsaved note follows only files that were written
    [Fact]
    public async Task A_Failed_File_Write_Names_The_File_First()
    {
        var unsaved = new FakeUnsavedSettings(hasUnsavedChanges: false);
        var feeds = new FixedWriteFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder));
        GoogleCloudFeedViewModel vm = await CheckedAsync(unsaved: unsaved, feeds: feeds);
        string folder = Path.Combine(_folder, "fortigate");
        string ipv4 = Path.Combine(folder, "AWS_EC2_cn-north-1_ipv4.txt");
        string ipv6 = Path.Combine(folder, "AWS_EC2_cn-north-1_ipv6.txt");
        string name4 = Path.Combine("fortigate", "AWS_EC2_cn-north-1_ipv4.txt");
        string name6 = Path.Combine("fortigate", "AWS_EC2_cn-north-1_ipv6.txt");

        var cases = new (CloudFeedWriteResult Result, bool Unsaved, string Expected)[]
        {
            (new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, [ipv4], 0, 0, 0, FailedFile: ipv6), false,
             $"{name6} couldn't be written. {name4} was written before the failure. "
             + "Please try again or contact the Infra Team."),
            (new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, [], 0, 0, 0, OldFilesLeft: [ipv4], FailedFile: ipv6),
             true,
             $"{name6} couldn't be written. The old {name4} was left as it was. "
             + "Please try again or contact the Infra Team."),
            (new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, [ipv4], 0, 0, 0, FailedFile: ipv6), true,
             $"{name6} couldn't be written. {name4} was written before the failure. "
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
        Assert.Equal($"The feed couldn't be written. The old {name4} and {name6} were "
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

    // ----- the configure dialog, shown when no feed type is ticked -----

    // Both ranges are large at 17, so the large-range question would follow if the dialog didn't end the press
    [Fact]
    public async Task With_No_Feed_Type_Ticked_Write_Feed_Asks_Before_The_Large_Range_Question_And_Writes_Nothing()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false, IPv4MinPrefix = 17 };
        var ui = new FakeUi { LargeRangeAnswer = prompt => [prompt.Choices[0].Network] };
        var feeds = new HeldFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder));
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui, feeds: feeds);
        string before = vm.ActionText;

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(1, ui.ConfigureFeedsAsks);
        Assert.Empty(ui.LargeRangePrompts);
        Assert.False(feeds.WriteStarted.Task.IsCompleted);
        Assert.Equal(before, vm.ActionText);
        Assert.Equal(0, ui.SettingsPageShows);
        Assert.False(vm.IsWorking);
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Open_Settings_From_The_Configure_Dialog_Shows_The_Settings_Page_And_Writes_Nothing()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false };
        var ui = new FakeUi { ConfigureFeedsAnswer = ConfigureFeedsChoice.OpenSettings };
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(1, ui.ConfigureFeedsAsks);
        Assert.Equal(1, ui.SettingsPageShows);
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task The_Configure_Dialog_Shows_On_Every_Press()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false };
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui);

        await vm.WriteFeedCommand.ExecuteAsync(null);
        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(2, ui.ConfigureFeedsAsks);
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Any_Ticked_Feed_Type_Writes_Without_The_Configure_Dialog()
    {
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui);
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal("Wrote 1 range to 1 file", vm.ActionText);

        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false, WriteModSecurityLists = true };
        vm = await CheckedAsync(ui);
        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(0, ui.ConfigureFeedsAsks);
        Assert.Equal("Wrote 1 range to 1 file", vm.ActionText);
    }

    // Not asked while something else holds the gate, as the large-range question isn't; the line stays after a Close
    [Fact]
    public async Task The_Configure_Dialog_Is_Not_Shown_While_Something_Else_Holds_The_Gate()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false };
        var gate = new WorkGate();
        gate.SetStarted(true);
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui, gate);
        IDisposable other = gate.Hold();

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.ConfigureFeedsBusy, vm.ActionText);
        Assert.Equal(0, ui.ConfigureFeedsAsks);
        Assert.False(vm.IsWorking);
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());

        other.Dispose();
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(1, ui.ConfigureFeedsAsks);
        Assert.Equal(AppText.ConfigureFeedsBusy, vm.ActionText);
        Assert.True(gate.CanStart);
    }

    [Fact]
    public async Task The_Configure_Dialog_Holds_The_Gate_And_The_Page_Works_Throughout()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false };
        var gate = new WorkGate();
        gate.SetStarted(true);
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui, gate);
        bool? heldDuringDialog = null;
        bool? workingDuringDialog = null;
        bool? canWriteDuringDialog = null;
        ui.OnDialogOpen = () =>
        {
            heldDuringDialog = gate.IsHeld;
            workingDuringDialog = vm.IsWorking;
            canWriteDuringDialog = vm.WriteFeedCommand.CanExecute(null);
        };

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(1, ui.ConfigureFeedsAsks);
        Assert.True(heldDuringDialog);
        Assert.True(workingDuringDialog);
        Assert.False(canWriteDuringDialog);
        Assert.False(gate.IsHeld);
        Assert.True(gate.CanStart);
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task A_Configure_Dialog_That_Fails_To_Show_Says_So_And_Frees_The_Gate()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false };
        var gate = new WorkGate();
        gate.SetStarted(true);
        var ui = new FakeUi
        {
            ConfigureFeedsAnswer = ConfigureFeedsChoice.OpenSettings,
            ConfigureFeedsException = new InvalidOperationException("dialog"),
        };
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui, gate);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.Generic, vm.ActionText);
        Assert.Equal(0, ui.SettingsPageShows);
        Assert.False(gate.IsHeld);
        Assert.False(vm.IsWorking);
        Assert.True(vm.WriteFeedCommand.CanExecute(null));
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task A_Page_Disposed_While_The_Configure_Dialog_Is_Open_Shows_No_Settings_Page()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new FakeUi { ConfigureFeedsAnswer = ConfigureFeedsChoice.OpenSettings, ConfigureFeedsGate = release };
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui);

        Task write = vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(1, ui.ConfigureFeedsAsks);
        vm.Dispose();
        release.SetResult();
        await write;

        Assert.Equal(0, ui.SettingsPageShows);
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Copy_Network_And_Copy_As_Number_Copy_The_Rows_Texts_With_No_Line()
    {
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui);
        CloudRangeRow row = vm.Rows.Single(range => range.NetworkText == "34.1.0.0/16");

        Assert.True(vm.CopyNetworkCommand.CanExecute(row));
        await vm.CopyNetworkCommand.ExecuteAsync(row);
        Assert.Equal("34.1.0.0/16", ui.ClipboardText);

        Assert.True(vm.CopyAsNumberCommand.CanExecute(row));
        await vm.CopyAsNumberCommand.ExecuteAsync(row);
        Assert.Equal("AS15169", ui.ClipboardText);
        Assert.Equal("", vm.ActionText);
    }

    [Fact]
    public async Task Copy_As_Number_Is_Off_For_A_Row_Under_No_Single_As()
    {
        _ipinfo.OverlapFunc = blocks => new OverlapResult(
            OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, block.Text == "35.1.0.0/16"
                ? [new OverlapGroup("US", "United States", "AS64500", "Other", "example.com", 1),
                   new OverlapGroup("US", "United States", "AS64501", "Other", "example.com", 1)]
                : Groups(block))).ToList(),
            _ipinfo.Dataset);
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui);
        CloudRangeRow row = vm.Rows.Single(range => range.NetworkText == "35.1.0.0/16");

        Assert.Null(row.SingleAsnText);
        Assert.False(vm.CopyAsNumberCommand.CanExecute(row));
        Assert.True(vm.CopyNetworkCommand.CanExecute(row));
        await vm.CopyAsNumberCommand.ExecuteAsync(row);
        Assert.Null(ui.ClipboardText);
    }

    [Fact]
    public async Task The_Copies_Are_Off_After_Dispose_And_Dispose_Says_So()
    {
        GoogleCloudFeedViewModel vm = await CheckedAsync();
        CloudRangeRow row = vm.Rows[0];
        int networkRaised = 0;
        int asRaised = 0;
        vm.CopyNetworkCommand.CanExecuteChanged += (_, _) => networkRaised++;
        vm.CopyAsNumberCommand.CanExecuteChanged += (_, _) => asRaised++;

        vm.Dispose();

        Assert.False(vm.CopyNetworkCommand.CanExecute(row));
        Assert.False(vm.CopyAsNumberCommand.CanExecute(row));
        Assert.True(networkRaised > 0);
        Assert.True(asRaised > 0);
    }

    [Fact]
    public async Task A_Copy_That_Fails_Says_So_And_The_Next_Success_Clears_Only_That_Line()
    {
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui);
        CloudRangeRow row = vm.Rows.Single(range => range.NetworkText == "34.1.0.0/16");

        ui.FailClipboard = true;
        await vm.CopyNetworkCommand.ExecuteAsync(row);
        Assert.Equal(AppText.NetworkCopyFailed, vm.ActionText);
        Assert.True(vm.HasActionText);
        await vm.CopyAsNumberCommand.ExecuteAsync(row);
        Assert.Equal(AppText.AsNumberCopyFailed, vm.ActionText);

        ui.FailClipboard = false;
        await vm.CopyNetworkCommand.ExecuteAsync(row);
        Assert.Equal("", vm.ActionText);
        Assert.False(vm.HasActionText);
    }

    // No success line: it would erase the old files a failed write left, which the operator still has to see
    [Fact]
    public async Task A_Successful_Copy_Keeps_A_Failed_Writes_Old_Files_Line()
    {
        var feeds = new FixedWriteFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder));
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui, feeds: feeds);
        string folder = Path.Combine(_folder, "fortigate");
        feeds.Result = new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, [], 0, 0, 0,
            OldFilesLeft: [Path.Combine(folder, "a_ipv4.txt")], FailedFile: Path.Combine(folder, "a_ipv6.txt"));
        await vm.WriteFeedCommand.ExecuteAsync(null);
        string oldFilesLine = vm.ActionText;
        Assert.Contains("was left as it was", oldFilesLine);

        CloudRangeRow row = vm.Rows[0];
        await vm.CopyNetworkCommand.ExecuteAsync(row);
        await vm.CopyAsNumberCommand.ExecuteAsync(row);

        Assert.Equal(row.SingleAsnText, ui.ClipboardText);
        Assert.Equal(oldFilesLine, vm.ActionText);
    }

    [Fact]
    public async Task The_Copies_Run_While_Something_Else_Holds_The_Gate()
    {
        var gate = new WorkGate();
        gate.SetStarted(true);
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui, gate);
        CloudRangeRow row = vm.Rows.Single(range => range.NetworkText == "34.1.0.0/16");
        using IDisposable other = gate.Hold();

        Assert.True(vm.CopyNetworkCommand.CanExecute(row));
        await vm.CopyNetworkCommand.ExecuteAsync(row);
        Assert.Equal("34.1.0.0/16", ui.ClipboardText);
        Assert.True(vm.CopyAsNumberCommand.CanExecute(row));
        await vm.CopyAsNumberCommand.ExecuteAsync(row);
        Assert.Equal("AS15169", ui.ClipboardText);
    }

    // The us-central1 check, with only the confirmed range ticked
    [Fact]
    public async Task A_Region_That_Is_Gone_Shows_The_Pick_Gone_Line()
    {
        GoogleCloudFeedViewModel vm = await CheckedAsync();
        Assert.True(GoogleCloudScope.TryCreate("europe-west9", out GoogleCloudScope gone));
        vm.SelectedRegion = new RegionOption(gone, "europe-west9");

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(AppText.CloudPickGone, vm.EmptyText);
        Assert.Empty(vm.Rows);
    }

    // The save's and the history's notes come before the unsaved note, on the written line and the failed one alike
    [Fact]
    public async Task A_Failed_Save_Or_History_Is_Noted_On_The_Result_Line()
    {
        var unsaved = new FakeUnsavedSettings(hasUnsavedChanges: false);
        var feeds = new FixedWriteFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder));
        GoogleCloudFeedViewModel vm = await CheckedAsync(unsaved: unsaved, feeds: feeds);
        string ipv4 = Path.Combine(_folder, "fortigate", "GoogleCloud_us-central1_ipv4.txt");
        string ipv6 = Path.Combine(_folder, "fortigate", "GoogleCloud_us-central1_ipv6.txt");
        string name4 = Path.Combine("fortigate", "GoogleCloud_us-central1_ipv4.txt");
        string name6 = Path.Combine("fortigate", "GoogleCloud_us-central1_ipv6.txt");
        var saveFailed = new FeedRecordResult(FeedSaveOutcome.StorageError, HistoryOutcome.Recorded);
        var historyFailed = new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Failed);
        var both = new FeedRecordResult(FeedSaveOutcome.Unavailable, HistoryOutcome.Failed);
        var fine = new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded);
        string written = AppText.CloudFeedWritten(1, 1);
        string failed = $"{name6} couldn't be written. {name4} was written before the failure.";
        const string again = " Please try again or contact the Infra Team.";
        const string unsavedSentence = " Unsaved changes on the Settings page weren't used.";

        var cases = new (FeedRecordResult? Record, bool Failed, bool Unsaved, string Expected)[]
        {
            (saveFailed, false, false, "The saved feed couldn't be updated. " + written),
            (historyFailed, false, true, "This change wasn't recorded in the history. " + written + AppText.UnsavedSettingsNote),
            (both, false, false,
             "The saved feed couldn't be updated and this change wasn't recorded in the history. " + written),
            (fine, false, true, written + AppText.UnsavedSettingsNote),
            (null, false, false, written),
            (saveFailed, true, false, failed + " The saved feed couldn't be updated." + again),
            (historyFailed, true, true, failed + " This change wasn't recorded in the history." + unsavedSentence + again),
            (both, true, true,
             failed + " The saved feed couldn't be updated. This change wasn't recorded in the history." + unsavedSentence + again),
            (fine, true, false, failed + again),
        };

        foreach ((FeedRecordResult? record, bool fails, bool unsavedEdits, string expected) in cases)
        {
            unsaved.HasUnsavedChanges = unsavedEdits;
            feeds.Result = fails
                ? new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, [ipv4], 0, 0, 0, FailedFile: ipv6, Record: record)
                : new CloudFeedWriteResult(CloudFeedWriteFailure.None, [ipv4], 1, 0, 0, Record: record);
            await vm.WriteFeedCommand.ExecuteAsync(null);
            Assert.Equal(expected, vm.ActionText);
        }
    }

    [Fact]
    public async Task Tick_All_And_Untick_All_Set_Every_Row_And_Leave_The_Lines_Alone()
    {
        GoogleCloudFeedViewModel vm = await CheckedAsync();
        string summary = vm.SummaryText;

        vm.TickAllCommand.Execute(null);
        Assert.All(vm.Rows, row => Assert.True(row.IsIncluded));

        vm.UntickAllCommand.Execute(null);
        Assert.All(vm.Rows, row => Assert.False(row.IsIncluded));
        Assert.Equal(summary, vm.SummaryText);
        Assert.Equal("", vm.ActionText);
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Tick_All_And_Untick_All_Are_Off_With_No_Rows_And_While_Working()
    {
        _ipinfo.Settings = _ipinfo.Settings with { IPv4MinPrefix = 17 };
        var ui = new FakeUi();
        var feeds = new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder);
        var vm = new GoogleCloudFeedViewModel(feeds, _ipinfo, ui, new WorkGate(), _folder);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "us-central1");
        Assert.False(vm.TickAllCommand.CanExecute(null));
        Assert.False(vm.UntickAllCommand.CanExecute(null));

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.TickAllCommand.CanExecute(null));
        Assert.True(vm.UntickAllCommand.CanExecute(null));

        bool? tickDuringDialog = null, untickDuringDialog = null;
        ui.OnDialogOpen = () =>
        {
            tickDuringDialog = vm.TickAllCommand.CanExecute(null);
            untickDuringDialog = vm.UntickAllCommand.CanExecute(null);
            vm.TickAllCommand.Execute(null);
        };
        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.False(tickDuringDialog);
        Assert.False(untickDuringDialog);
        Assert.Equal(["34.1.0.0/16"], vm.Rows.Where(row => row.IsIncluded).Select(row => row.NetworkText));
        Assert.True(vm.TickAllCommand.CanExecute(null));

        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "europe-west1");
        Assert.False(vm.TickAllCommand.CanExecute(null));
        Assert.False(vm.UntickAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task Tick_All_Ticks_Large_Ranges_But_The_Large_Range_Question_Still_Starts_Them_Unticked()
    {
        _ipinfo.Settings = _ipinfo.Settings with { IPv4MinPrefix = 17 };
        var ui = new FakeUi();
        GoogleCloudFeedViewModel vm = await CheckedAsync(ui);
        Assert.All(vm.Rows, row => Assert.True(row.IsLargeRange));

        vm.TickAllCommand.Execute(null);
        Assert.All(vm.Rows, row => Assert.True(row.IsIncluded));
        bool[]? startedTicked = null;
        ui.LargeRangeAnswer = prompt =>
        {
            startedTicked = prompt.Choices.Select(choice => choice.IsIncluded).ToArray();
            return null;
        };
        await vm.WriteFeedCommand.ExecuteAsync(null);

        LargeRangePrompt asked = Assert.Single(ui.LargeRangePrompts);
        Assert.Equal(["34.1.0.0/16", "35.1.0.0/16"], asked.Choices.Select(choice => choice.Network));
        Assert.NotNull(startedTicked);
        Assert.Equal([false, false], startedTicked);
        Assert.False(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task A_Check_Applies_The_Saved_Feeds_Hand_Ticks_And_Says_So()
    {
        SavedFeedStore store = UseSavedFeeds();
        store.Seed("GoogleCloud_us-central1", FeedKind.GoogleCloud, ticked: ["35.1.0.0/16", "10.0.0.0/8"],
                   unticked: ["34.1.0.0/16"]);
        GoogleCloudFeedViewModel vm = await CheckedRowsAsync();

        Assert.Equal(["35.1.0.0/16"], vm.Rows.Where(row => row.IsIncluded).Select(row => row.NetworkText));
        Assert.Equal("The saved feed's ticks were applied" + Dash + "1 range ticked and 1 unticked by hand", vm.ActionText);
    }

    [Fact]
    public async Task The_Rows_Ticked_After_A_Check_Are_The_Saved_Picks_Refresh_Ticks()
    {
        SavedFeedStore store = UseSavedFeeds();
        store.Seed("GoogleCloud_us-central1", FeedKind.GoogleCloud, ticked: ["35.1.0.0/16"], unticked: ["34.1.0.0/16"]);
        var feeds = new CheckCapturingFeeds(
            new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder));
        var vm = new GoogleCloudFeedViewModel(feeds, _ipinfo, new FakeUi(), new WorkGate(), _folder);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "us-central1");
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.NotNull(feeds.Check);
        Assert.Contains(feeds.Check.Ranges, range => range.Prefix.Block.Text == "35.1.0.0/16" && !range.StartsIncluded);

        RefreshPicked picked = RefreshPick.Apply(feeds.Check, Assert.Single(store.Read().Feeds).Pick);

        Assert.Equal(["35.1.0.0/16"], picked.Ticked.Select(block => block.Text));
        Assert.Equal(picked.Ticked.Select(block => block.Text),
                     vm.Rows.Where(row => row.IsIncluded).Select(row => row.NetworkText));
    }

    [Fact]
    public async Task A_Retired_Saved_Feeds_Ticks_Are_Applied_Too_Matching_The_Key_Ignoring_Case()
    {
        SavedFeedStore store = UseSavedFeeds();
        store.Seed("googlecloud_US-CENTRAL1", FeedKind.GoogleCloud, ticked: ["35.1.0.0/16"], unticked: []);
        store.Retire("googlecloud_US-CENTRAL1", FeedKind.GoogleCloud);
        Assert.True(Assert.Single(store.Read().Feeds).Retired);
        GoogleCloudFeedViewModel vm = await CheckedRowsAsync();

        Assert.Equal(["34.1.0.0/16", "35.1.0.0/16"], vm.Rows.Where(row => row.IsIncluded).Select(row => row.NetworkText));
        Assert.Equal("The saved feed's ticks were applied" + Dash + "1 range ticked by hand", vm.ActionText);
    }

    [Theory]
    [InlineData("GoogleCloud_europe-west1", FeedKind.GoogleCloud, true)]
    [InlineData("GoogleCloud_us-central1_global", FeedKind.GoogleCloud, true)]
    [InlineData("GoogleCloud_us-central1", FeedKind.Azure, true)]
    [InlineData("GoogleCloud_us-central1", FeedKind.GoogleCloud, false)]
    public async Task Another_Feeds_Ticks_Or_An_Unavailable_Read_Change_Nothing(string key, FeedKind kind, bool available)
    {
        SavedFeedStore store = UseSavedFeeds();
        store.Seed(key, kind, ticked: ["35.1.0.0/16"], unticked: ["34.1.0.0/16"]);
        store.Available = available;
        GoogleCloudFeedViewModel vm = await CheckedRowsAsync();

        Assert.Equal(["34.1.0.0/16"], vm.Rows.Where(row => row.IsIncluded).Select(row => row.NetworkText));
        Assert.Equal("", vm.ActionText);
    }

    [Fact]
    public async Task Saved_Ticks_That_Change_No_Row_Leave_The_Action_Line_As_It_Was()
    {
        SavedFeedStore store = UseSavedFeeds();
        GoogleCloudFeedViewModel vm = await CheckedRowsAsync();
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal("Wrote 1 range to 1 file", vm.ActionText);
        // A ticked entry for a range that starts ticked, and one for a range not in the check
        store.Seed("GoogleCloud_us-central1", FeedKind.GoogleCloud, ticked: ["34.1.0.0/16", "10.0.0.0/8"], unticked: []);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(["34.1.0.0/16"], vm.Rows.Where(row => row.IsIncluded).Select(row => row.NetworkText));
        Assert.Equal("Wrote 1 range to 1 file", vm.ActionText);
    }

    [Fact]
    public async Task A_Later_Check_That_Applies_No_Saved_Tick_Clears_The_Earlier_Line()
    {
        SavedFeedStore store = UseSavedFeeds();
        store.Seed("GoogleCloud_us-central1", FeedKind.GoogleCloud, ticked: ["35.1.0.0/16"], unticked: []);
        GoogleCloudFeedViewModel vm = await CheckedRowsAsync();
        Assert.Equal("The saved feed's ticks were applied" + Dash + "1 range ticked by hand", vm.ActionText);

        Assert.Equal(SavedFeedsFailure.None, store.Remove("GoogleCloud_us-central1"));
        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(["34.1.0.0/16"], vm.Rows.Where(row => row.IsIncluded).Select(row => row.NetworkText));
        Assert.Equal("", vm.ActionText);
    }

    [Fact]
    public async Task A_Later_Check_That_Applies_Nothing_Keeps_A_Write_Line_Shown_Since()
    {
        SavedFeedStore store = UseSavedFeeds();
        store.Seed("GoogleCloud_us-central1", FeedKind.GoogleCloud, ticked: ["35.1.0.0/16"], unticked: []);
        GoogleCloudFeedViewModel vm = await CheckedRowsAsync();
        vm.Rows.Single(row => row.NetworkText == "35.1.0.0/16").IsIncluded = false;
        await vm.WriteFeedCommand.ExecuteAsync(null);
        string written = vm.ActionText;
        Assert.StartsWith("Wrote 1 range to 1 file", written);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(["34.1.0.0/16"], vm.Rows.Where(row => row.IsIncluded).Select(row => row.NetworkText));
        Assert.Equal(written, vm.ActionText);
    }

    [Fact]
    public async Task A_Failed_Check_Applies_And_Says_Nothing()
    {
        SavedFeedStore store = UseSavedFeeds();
        store.Seed("GoogleCloud_us-central1", FeedKind.GoogleCloud, ticked: ["35.1.0.0/16"], unticked: []);
        _ipinfo.Dataset = null;

        GoogleCloudFeedViewModel vm = await CheckedRowsAsync();

        Assert.Empty(vm.Rows);
        Assert.Equal(AppText.CloudNoDataset, vm.EmptyText);
        Assert.Equal("", vm.ActionText);
    }

    [Fact]
    public async Task Ticks_Changed_By_Hand_Come_Back_After_Writing_And_Checking_Again()
    {
        UseSavedFeeds();
        GoogleCloudFeedViewModel vm = await CheckedRowsAsync();
        vm.Rows.Single(row => row.NetworkText == "34.1.0.0/16").IsIncluded = false;
        vm.Rows.Single(row => row.NetworkText == "35.1.0.0/16").IsIncluded = true;
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.StartsWith("Wrote 1 range to 1 file", vm.ActionText);

        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "europe-west1");
        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "us-central1");
        Assert.Empty(vm.Rows);
        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(["35.1.0.0/16"], vm.Rows.Where(row => row.IsIncluded).Select(row => row.NetworkText));
        Assert.Equal("The saved feed's ticks were applied" + Dash + "1 range ticked and 1 unticked by hand", vm.ActionText);
    }

    [Theory]
    [InlineData(1, 0, "1 range ticked by hand")]
    [InlineData(2, 0, "2 ranges ticked by hand")]
    [InlineData(0, 1, "1 range unticked by hand")]
    [InlineData(0, 3, "3 ranges unticked by hand")]
    [InlineData(1, 1, "1 range ticked and 1 unticked by hand")]
    [InlineData(2, 1500, "2 ranges ticked and 1,500 unticked by hand")]
    public void The_Saved_Ticks_Line_Counts_Each_Way(int ticked, int unticked, string counts) =>
        Assert.Equal("The saved feed's ticks were applied" + Dash + counts, AppText.CloudSavedTicksApplied(ticked, unticked));

    private SavedFeedStore UseSavedFeeds()
    {
        var store = new SavedFeedStore();
        var paths = new AppPaths(Directory.CreateDirectory(Path.Combine(_folder, "data")).FullName, Path.Combine(_folder, "unused"));
        _ipinfo.Records = new FeedRecords(paths, new FakeTimeProvider(), store);
        return store;
    }

    private async Task<GoogleCloudFeedViewModel> CheckedRowsAsync()
    {
        var feeds = new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder);
        var vm = new GoogleCloudFeedViewModel(feeds, _ipinfo, new FakeUi(), new WorkGate(), _folder);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "us-central1");
        await vm.CheckCommand.ExecuteAsync(null);
        return vm;
    }

    // Read gives the feeds even while unavailable, so only the Available flag can keep them off the page
    private sealed class SavedFeedStore : ISavedFeedStore
    {
        private SavedFeedList _list = SavedFeedList.Empty;

        public bool Available { get; set; } = true;

        public SavedFeedsRead Read() => new(Available, _list.Feeds);

        public void Seed(string key, FeedKind kind, string[] ticked, string[] unticked)
        {
            var pick = kind == FeedKind.Azure
                ? new SavedFeedPick(null, null, false, "Storage", null, null, ticked, unticked, [], [], null, null, null)
                : new SavedFeedPick(null, "us-central1", false, null, null, null, ticked, unticked, [], [], null, null, null);
            Assert.Equal(SavedFeedsFailure.None, Apply(key, kind, new SavedFeedIntent.Written(pick), DateTimeOffset.UnixEpoch));
        }

        public void Retire(string key, FeedKind kind) =>
            Assert.Equal(SavedFeedsFailure.None, Apply(key, kind, new SavedFeedIntent.Retired(), DateTimeOffset.UnixEpoch));

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

    private async Task<GoogleCloudFeedViewModel> CheckedAsync(
        FakeUi? ui = null, IWorkGate? gate = null, IUnsavedSettings? unsaved = null, IGoogleCloudFeedService? feeds = null)
    {
        feeds ??= new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder);
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
        public event EventHandler<CloudSourceUpdated<CloudRangeCatalogue>>? SourceUpdated
        {
            add => inner.SourceUpdated += value;
            remove => inner.SourceUpdated -= value;
        }

        public TaskCompletionSource? WriteGate { get; set; }

        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CatalogueResult> LoadCatalogueAsync(CancellationToken ct) => inner.LoadCatalogueAsync(ct);

        public Task<SavedRanges<CloudRangeCatalogue>> LoadSavedAsync(CancellationToken ct) => inner.LoadSavedAsync(ct);

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

    // The real service, keeping the last check it gave the page
    private sealed class CheckCapturingFeeds(IGoogleCloudFeedService inner) : IGoogleCloudFeedService
    {
        public event EventHandler<CloudSourceUpdated<CloudRangeCatalogue>>? SourceUpdated
        {
            add => inner.SourceUpdated += value;
            remove => inner.SourceUpdated -= value;
        }

        public CloudFeedCheck? Check { get; private set; }

        public Task<CatalogueResult> LoadCatalogueAsync(CancellationToken ct) => inner.LoadCatalogueAsync(ct);

        public Task<SavedRanges<CloudRangeCatalogue>> LoadSavedAsync(CancellationToken ct) => inner.LoadSavedAsync(ct);

        public async Task<CheckResult> CheckAsync(CloudRangeCatalogue catalogue, RegionSelection selection, CancellationToken ct)
        {
            CheckResult result = await inner.CheckAsync(catalogue, selection, ct);
            Check = result.Check;
            return result;
        }

        public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) =>
            inner.PlanWrite(check, ticked);

        public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges,
                                                     CancellationToken ct) =>
            inner.WriteAsync(plan, includedLargeRanges, ct);
    }

    // The real service, except that a write gives the result the test sets
    private sealed class FixedWriteFeeds(IGoogleCloudFeedService inner) : IGoogleCloudFeedService
    {
        public event EventHandler<CloudSourceUpdated<CloudRangeCatalogue>>? SourceUpdated
        {
            add => inner.SourceUpdated += value;
            remove => inner.SourceUpdated -= value;
        }

        public CloudFeedWriteResult? Result { get; set; }

        public Task<CatalogueResult> LoadCatalogueAsync(CancellationToken ct) => inner.LoadCatalogueAsync(ct);

        public Task<SavedRanges<CloudRangeCatalogue>> LoadSavedAsync(CancellationToken ct) => inner.LoadSavedAsync(ct);

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

    [Fact]
    public async Task The_Large_Range_Question_Starts_A_Range_The_Saved_Feed_Includes_Ticked_And_Keeps_It()
    {
        _ipinfo.Settings = _ipinfo.Settings with { IPv4MinPrefix = 17 };
        SavedFeedStore store = UseSavedFeeds();
        var saved = new SavedFeedPick(null, "us-central1", false, null, null, null, ["35.1.0.0/16"], [], ["35.1.0.0/16"], [],
                                      null, null, null);
        Assert.Equal(SavedFeedsFailure.None, store.Apply("GoogleCloud_us-central1", FeedKind.GoogleCloud,
                                                         new SavedFeedIntent.Written(saved), DateTimeOffset.UnixEpoch));
        var ui = new FakeUi
        {
            LargeRangeAnswer = prompt => prompt.Choices.Where(choice => choice.IsIncluded).Select(choice => choice.Network).ToList(),
        };
        var feeds = new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder);
        var vm = new GoogleCloudFeedViewModel(feeds, _ipinfo, ui, new WorkGate(), _folder);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "us-central1");
        await vm.CheckCommand.ExecuteAsync(null);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        LargeRangePrompt prompt = Assert.Single(ui.LargeRangePrompts);
        Assert.Equal(["34.1.0.0/16", "35.1.0.0/16"], prompt.Choices.Select(choice => choice.Network));
        Assert.Equal([false, true], prompt.Choices.Select(choice => choice.IsIncluded));
        Assert.StartsWith("Wrote 1 range to 1 file", vm.ActionText);
        SavedFeedPick pick = Assert.Single(_ipinfo.Records.ReadSavedFeeds().Feeds).Pick;
        Assert.Equal(["35.1.0.0/16"], pick.LargeIncluded);
        Assert.Equal(["34.1.0.0/16"], pick.LargeLeftOut);
    }
}
