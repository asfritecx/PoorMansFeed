using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.GoogleCloud;
using PoorMansFeed.Core.History;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>Refresh all's order, its mapping of each refresher's result, the one commit and the cancel.</summary>
public sealed class FeedRefresherTests
{
    private const string AsnKey = "AS15169";
    private const string AwsKey = "AWS_EC2_us-east-1";
    private const string AzureKey = "Azure_Storage_SoutheastAsia";
    private const string GoogleKey = "GoogleCloud_us-central1";
    private const string RollingKey = AbuseIpDbFeedWriter.FileStem;

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 30), 4, "abc", Now, true);

    private readonly Log _log = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeRecords _records;
    private readonly FakeIpInfo _ipinfo;
    private readonly FakeAbuse _abuse;
    private readonly FakeGoogle _google;
    private readonly FakeAzure _azure;
    private readonly FakeAws _aws;

    public FeedRefresherTests()
    {
        _records = new FakeRecords(_log);
        _ipinfo = new FakeIpInfo(_log, _records);
        _abuse = new FakeAbuse(_log) { HasKey = true };
        _google = new FakeGoogle(_log);
        _azure = new FakeAzure(_log);
        _aws = new FakeAws(_log);
    }

    private FeedRefresher Refresher() => new(_ipinfo, _abuse, _google, _azure, _aws, _time);

    private Task<RefreshRunResult> RunAsync(CancellationToken ct = default, IProgress<RefreshProgress>? progress = null) =>
        Refresher().RunAsync(progress, ct);

    // ----- The saved feeds -----

    private static SavedFeedPick Pick(FeedKind kind) => kind switch
    {
        FeedKind.Asn => new SavedFeedPick(AsnKey, null, false, null, null, null, [], [], [], [], true, 12, 24),
        FeedKind.GoogleCloud => new SavedFeedPick(null, "us-central1", false, null, null, null, [], [], [], [], null, null, null),
        FeedKind.Azure => new SavedFeedPick(null, null, false, "Storage.SoutheastAsia", null, null, [], [], [], [], null, null, null),
        FeedKind.Aws => new SavedFeedPick(null, null, false, null, "EC2", "us-east-1", [], [], [], [], null, null, null),
        _ => new SavedFeedPick(null, null, false, null, null, null, [], [], [], [], null, null, null),
    };

    private static string KeyOf(FeedKind kind) => kind switch
    {
        FeedKind.Asn => AsnKey,
        FeedKind.GoogleCloud => GoogleKey,
        FeedKind.Azure => AzureKey,
        FeedKind.Aws => AwsKey,
        _ => RollingKey,
    };

    private static SavedFeed Feed(FeedKind kind, bool retired = false, string? key = null, SavedFeedPick? pick = null) =>
        new(key ?? KeyOf(kind), kind, pick ?? Pick(kind), Now.AddDays(-1), null, retired);

    private void Save(params SavedFeed[] feeds) => _records.Feeds.AddRange(feeds);

    private void SaveEveryKind() =>
        Save(Feed(FeedKind.GoogleCloud), Feed(FeedKind.AbuseIpDbRolling), Feed(FeedKind.Azure), Feed(FeedKind.Asn), Feed(FeedKind.Aws));

    private static SavedFeedStamp StampOf(SavedFeed feed) => new(feed, null, null, Now);

    private static FeedFileChange Change(FeedType type, string[] added, string[] removed, string[]? added6 = null) =>
        new("p", type, FeedFamilies.Both, new FeedEntryChanges(added, removed), new FeedEntryChanges(added6 ?? [], []),
            FeedFileState.Read);

    private static FeedRefreshResult Only(RefreshRunResult run, string key) => Assert.Single(run.Feeds, feed => feed.Key == key);

    // ----- The order -----

    [Fact]
    public async Task The_steps_run_dataset_sources_download_then_each_feed_in_key_order_and_one_commit()
    {
        SaveEveryKind();
        var progress = new List<RefreshProgress>();

        RefreshRunResult run = await RunAsync(progress: new ImmediateProgress<RefreshProgress>(progress.Add));

        Assert.Equal(
            [
                "dataset", "google load", "azure cached", "azure update", "aws cached", "aws update", "download",
                "asn " + AsnKey, "aws check", "aws refresh", "rolling", "azure check", "azure refresh",
                "google check", "google refresh", "commit 5",
            ],
            _log);
        Assert.True(run.SavedFeedsAvailable);
        Assert.False(run.Cancelled);
        Assert.Equal(UpdateFailure.None, run.Dataset?.Failure);
        Assert.Equal(
            [
                new SourceRefresh(FeedKind.GoogleCloud, SourceRefreshState.Fresh),
                new SourceRefresh(FeedKind.Azure, SourceRefreshState.Fresh),
                new SourceRefresh(FeedKind.Aws, SourceRefreshState.Fresh),
            ],
            run.Sources);
        Assert.Equal(AbuseIpDbFailure.None, run.AbuseIpDb?.Failure);
        Assert.Equal([AsnKey, AwsKey, RollingKey, AzureKey, GoogleKey], run.Feeds.Select(feed => feed.Key));
        Assert.All(run.Feeds, feed => Assert.Equal((FeedRefreshOutcome.Unchanged, FeedRefreshReason.None), (feed.Outcome, feed.Reason)));
        Assert.Equal([0, 1, 2, 3, 4, 5], progress.Where(p => p.Step == RefreshStep.Feeds).Select(p => p.Done));
        Assert.All(progress.Where(p => p.Step == RefreshStep.Feeds), p => Assert.Equal(5, p.Total));
        Assert.Equal([null, AsnKey, AwsKey, RollingKey, AzureKey, GoogleKey],
                     progress.Where(p => p.Step == RefreshStep.Feeds).Select(p => p.FeedKey));
        Assert.Equal(RefreshStep.Saving, progress[^1].Step);
    }

    [Fact]
    public async Task The_dataset_update_keeps_IPinfo_downloads_for_the_user()
    {
        SaveEveryKind();

        await RunAsync();

        Assert.Same(IpInfoUpdatePolicy.RefreshAll, Assert.Single(_ipinfo.Policies));
    }

    [Fact]
    public async Task Each_cloud_check_takes_the_time_from_before_its_providers_load()
    {
        Save(Feed(FeedKind.GoogleCloud), Feed(FeedKind.Azure));
        _google.OnLoad = () => _time.Advance(TimeSpan.FromMinutes(1));
        _azure.OnLoad = () => _time.Advance(TimeSpan.FromMinutes(1));

        await RunAsync();

        Assert.Equal(Now, _google.RefreshStarted);
        Assert.Equal(Now.AddMinutes(1), _azure.RefreshStarted);
    }

    [Fact]
    public async Task Saved_feeds_that_cant_be_read_send_nothing()
    {
        _records.Available = false;
        Save(Feed(FeedKind.Asn));

        RefreshRunResult run = await RunAsync();

        Assert.False(run.SavedFeedsAvailable);
        Assert.Empty(run.Feeds);
        Assert.Null(run.Dataset);
        Assert.Empty(_log);
    }

    [Fact]
    public async Task No_saved_feeds_report_no_feeds_step()
    {
        var progress = new List<RefreshProgress>();

        await RunAsync(progress: new ImmediateProgress<RefreshProgress>(progress.Add));

        Assert.DoesNotContain(progress, p => p.Step == RefreshStep.Feeds);
    }

    [Fact]
    public async Task No_saved_feeds_send_nothing()
    {
        RefreshRunResult run = await RunAsync();

        Assert.True(run.SavedFeedsAvailable);
        Assert.Empty(run.Feeds);
        Assert.Empty(_log);
    }

    [Fact]
    public async Task Only_the_rolling_feed_skips_the_dataset_and_the_sources()
    {
        Save(Feed(FeedKind.AbuseIpDbRolling));

        RefreshRunResult run = await RunAsync();

        Assert.Equal(["download", "rolling", "commit 1"], _log);
        Assert.Null(run.Dataset);
        Assert.Empty(run.Sources);
    }

    [Fact]
    public async Task Retired_feeds_ask_for_no_step_and_a_provider_used_only_by_them_isnt_loaded()
    {
        Save(Feed(FeedKind.Asn, retired: true), Feed(FeedKind.Azure, retired: true), Feed(FeedKind.AbuseIpDbRolling));
        _ipinfo.AsnResult = _ => new AsnFeedRefresh(AsnRefreshOutcome.Retired, null, null, []);

        RefreshRunResult run = await RunAsync();

        Assert.Equal(["download", "asn " + AsnKey, "rolling", "commit 1"], _log);
        Assert.Null(run.Dataset);
        Assert.Empty(run.Sources);
        Assert.Equal((FeedRefreshOutcome.Skipped, FeedRefreshReason.Retired), (Only(run, AsnKey).Outcome, Only(run, AsnKey).Reason));
        Assert.Equal((FeedRefreshOutcome.Skipped, FeedRefreshReason.Retired), (Only(run, AzureKey).Outcome, Only(run, AzureKey).Reason));
    }

    [Fact]
    public async Task Google_falls_back_to_its_saved_copy()
    {
        Save(Feed(FeedKind.GoogleCloud));
        _google.Load = new CatalogueResult(CatalogueFailure.Offline, null);

        RefreshRunResult run = await RunAsync();

        Assert.Equal([new SourceRefresh(FeedKind.GoogleCloud, SourceRefreshState.Saved)], run.Sources);
        Assert.Equal(["dataset", "google load", "google saved", "google check", "google refresh", "commit 1"], _log);
        Assert.Same(_google.SavedCatalogue, _google.CheckedCatalogue);
        Assert.Equal(FeedRefreshOutcome.Unchanged, Only(run, GoogleKey).Outcome);
    }

    [Fact]
    public async Task Google_with_no_source_fails_its_feeds_and_the_other_providers_still_refresh()
    {
        Save(Feed(FeedKind.GoogleCloud), Feed(FeedKind.Azure));
        _google.Load = new CatalogueResult(CatalogueFailure.ServiceError, null);
        _google.Saved = new SavedRanges<CloudRangeCatalogue>(SavedRangesState.NotSaved, null, null);

        RefreshRunResult run = await RunAsync();

        Assert.Equal(SourceRefreshState.Unavailable, run.Sources[0].State);
        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.SourceUnavailable),
                     (Only(run, GoogleKey).Outcome, Only(run, GoogleKey).Reason));
        Assert.DoesNotContain("google check", _log);
        Assert.Contains("azure refresh", _log);
        Assert.Equal(FeedRefreshOutcome.Unchanged, Only(run, AzureKey).Outcome);
    }

    [Theory]
    [InlineData(AzureUpdateStatus.UpToDate, SourceRefreshState.Fresh)]
    [InlineData(AzureUpdateStatus.Updated, SourceRefreshState.Fresh)]
    [InlineData(AzureUpdateStatus.CheckFailed, SourceRefreshState.Saved)]
    [InlineData(AzureUpdateStatus.Unavailable, SourceRefreshState.Unavailable)]
    public async Task Azure_and_aws_take_the_update_checks_state(AzureUpdateStatus status, SourceRefreshState state)
    {
        Save(Feed(FeedKind.Azure), Feed(FeedKind.Aws));
        _azure.Status = status;
        _aws.Status = (AwsUpdateStatus)(int)status;

        RefreshRunResult run = await RunAsync();

        Assert.Equal([new SourceRefresh(FeedKind.Azure, state), new SourceRefresh(FeedKind.Aws, state)], run.Sources);
        FeedRefreshOutcome expected = state == SourceRefreshState.Unavailable ? FeedRefreshOutcome.Failed : FeedRefreshOutcome.Unchanged;
        Assert.Equal(expected, Only(run, AzureKey).Outcome);
        Assert.Equal(expected, Only(run, AwsKey).Outcome);
        Assert.Equal(state != SourceRefreshState.Unavailable, _log.Contains("azure check"));
    }

    [Fact]
    public async Task A_google_scope_that_cant_be_read_is_pick_gone_with_no_check()
    {
        Save(Feed(FeedKind.GoogleCloud, pick: Pick(FeedKind.GoogleCloud) with { GoogleScope = "us/central1" }));

        RefreshRunResult run = await RunAsync();

        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.PickGone), (Only(run, GoogleKey).Outcome, Only(run, GoogleKey).Reason));
        Assert.DoesNotContain("google check", _log);
        Assert.DoesNotContain(_log, line => line.StartsWith("commit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CheckFailure.PickGone, FeedRefreshReason.PickGone)]
    [InlineData(CheckFailure.NoDataset, FeedRefreshReason.NoDataset)]
    [InlineData(CheckFailure.NothingSelected, FeedRefreshReason.SourceUnavailable)]
    [InlineData(CheckFailure.Failed, FeedRefreshReason.SourceUnavailable)]
    public async Task A_failed_check_fails_the_feed_with_no_refresh(CheckFailure failure, FeedRefreshReason reason)
    {
        Save(Feed(FeedKind.Aws));
        _aws.CheckFailure = failure;

        RefreshRunResult run = await RunAsync();

        Assert.Equal((FeedRefreshOutcome.Failed, reason), (Only(run, AwsKey).Outcome, Only(run, AwsKey).Reason));
        Assert.DoesNotContain("aws refresh", _log);
    }


    [Fact]
    public async Task A_cloud_feed_retired_at_the_start_is_skipped_with_no_check_beside_a_live_one()
    {
        Save(Feed(FeedKind.Azure), Feed(FeedKind.Azure, retired: true, key: "Azure_Gone",
                                         pick: Pick(FeedKind.Azure) with { AzureTag = "Gone.Tag" }),
             Feed(FeedKind.GoogleCloud), Feed(FeedKind.GoogleCloud, retired: true, key: "GoogleCloud_gone",
                                              pick: Pick(FeedKind.GoogleCloud) with { GoogleScope = "us/central1" }));

        RefreshRunResult run = await RunAsync();

        Assert.Equal((FeedRefreshOutcome.Skipped, FeedRefreshReason.Retired), (Only(run, "Azure_Gone").Outcome, Only(run, "Azure_Gone").Reason));
        Assert.Equal((FeedRefreshOutcome.Skipped, FeedRefreshReason.Retired),
                     (Only(run, "GoogleCloud_gone").Outcome, Only(run, "GoogleCloud_gone").Reason));
        Assert.Single(_log, line => line == "azure check");
        Assert.Single(_log, line => line == "google check");
        Assert.Equal(FeedRefreshOutcome.Unchanged, Only(run, AzureKey).Outcome);
        Assert.Equal(FeedRefreshOutcome.Unchanged, Only(run, GoogleKey).Outcome);
    }

    [Fact]
    public async Task A_kind_this_build_doesnt_know_fails_with_no_refresher_called()
    {
        Save(Feed((FeedKind)999, key: "Other"));

        RefreshRunResult run = await RunAsync();

        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.Failed), (Only(run, "Other").Outcome, Only(run, "Other").Reason));
        Assert.Empty(_log);
    }

    [Fact]
    public async Task An_azure_feed_with_no_tag_and_an_aws_feed_with_no_service_are_pick_gone_with_no_check()
    {
        Save(Feed(FeedKind.Azure, pick: Pick(FeedKind.Azure) with { AzureTag = null }),
             Feed(FeedKind.Aws, pick: Pick(FeedKind.Aws) with { AwsService = null }));

        RefreshRunResult run = await RunAsync();

        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.PickGone), (Only(run, AzureKey).Outcome, Only(run, AzureKey).Reason));
        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.PickGone), (Only(run, AwsKey).Outcome, Only(run, AwsKey).Reason));
        Assert.DoesNotContain("azure check", _log);
        Assert.DoesNotContain("aws check", _log);
    }

    // ----- The download -----

    [Fact]
    public async Task No_saved_key_sends_no_download_and_the_rolling_feed_is_still_refreshed()
    {
        Save(Feed(FeedKind.AbuseIpDbRolling));
        _abuse.HasKey = false;

        RefreshRunResult run = await RunAsync();

        Assert.Equal(new AbuseRefreshDownload(AbuseIpDbFailure.NoKey, null), run.AbuseIpDb);
        Assert.Equal(["rolling", "commit 1"], _log);
    }

    [Fact]
    public async Task A_download_kept_for_the_user_still_refreshes_the_rolling_feed_with_the_policy_asked()
    {
        Save(Feed(FeedKind.AbuseIpDbRolling));
        _abuse.Download = new AbuseDownloadResult(AbuseIpDbFailure.KeptForUser, false, 0, 0, 0, 0, 0, null, null);

        RefreshRunResult run = await RunAsync();

        Assert.Same(AbuseDownloadPolicy.RefreshAll, _abuse.Policy);
        Assert.Equal(AbuseIpDbFailure.KeptForUser, run.AbuseIpDb?.Failure);
        Assert.Same(_abuse.Download, run.AbuseIpDb?.Download);
        Assert.Equal(["download", "rolling", "commit 1"], _log);
    }

    [Fact]
    public async Task The_rolling_refresh_waits_until_the_download_has_returned()
    {
        Save(Feed(FeedKind.AbuseIpDbRolling));
        var release = new TaskCompletionSource();
        _abuse.DownloadGate = release.Task;

        Task<RefreshRunResult> running = RunAsync();
        await _abuse.DownloadAsked.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["download"], _log);
        release.SetResult();
        await running;

        Assert.Equal(["download", "download returned", "rolling", "commit 1"], _log);
    }

    // ----- Each refresher's result -----

    public static TheoryData<AsnRefreshOutcome, FeedRefreshOutcome, FeedRefreshReason> AsnOutcomes => new()
    {
        { AsnRefreshOutcome.Retired, FeedRefreshOutcome.Skipped, FeedRefreshReason.Retired },
        { AsnRefreshOutcome.Missing, FeedRefreshOutcome.Skipped, FeedRefreshReason.Removed },
        { AsnRefreshOutcome.AsnFeedsOff, FeedRefreshOutcome.Skipped, FeedRefreshReason.FeedsOff },
        { AsnRefreshOutcome.SaveRefused, FeedRefreshOutcome.Failed, FeedRefreshReason.SaveRefused },
        { AsnRefreshOutcome.SavedFeedsUnavailable, FeedRefreshOutcome.Failed, FeedRefreshReason.SavedFeedsUnavailable },
        { AsnRefreshOutcome.SourceGone, FeedRefreshOutcome.Failed, FeedRefreshReason.SourceGone },
        { AsnRefreshOutcome.NoDataset, FeedRefreshOutcome.Failed, FeedRefreshReason.NoDataset },
        { AsnRefreshOutcome.NoFeedType, FeedRefreshOutcome.Failed, FeedRefreshReason.NoFeedType },
        { AsnRefreshOutcome.Failed, FeedRefreshOutcome.Failed, FeedRefreshReason.Failed },
    };

    [Theory]
    [MemberData(nameof(AsnOutcomes))]
    public async Task An_asn_outcome_with_no_write_maps_to_its_result(AsnRefreshOutcome outcome, FeedRefreshOutcome expected,
                                                                   FeedRefreshReason reason)
    {
        Save(Feed(FeedKind.Asn));
        _ipinfo.AsnResult = _ => new AsnFeedRefresh(outcome, null, null, []);

        FeedRefreshResult result = Only(await RunAsync(), AsnKey);

        Assert.Equal((expected, reason, false, 0, 0, null), (result.Outcome, result.Reason, result.Written, result.Added,
                                                             result.Removed, result.Record));
        Assert.Empty(result.OldFilesLeft);
        Assert.DoesNotContain(_log, line => line.StartsWith("commit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_write_that_changed_entries_is_changed_and_counts_one_feed_types_copy()
    {
        Save(Feed(FeedKind.Asn));
        var record = new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded);
        IReadOnlyList<FeedFileChange> changes =
        [
            Change(FeedType.Fortigate, ["1.0.0.0/24", "2.0.0.0/24"], ["3.0.0.0/24"]),
            Change(FeedType.Fortigate, [], [], ["2001:db8::/32"]),
            Change(FeedType.ModSecurity, ["1.0.0.0/24", "2.0.0.0/24"], ["3.0.0.0/24"]),
            Change(FeedType.ModSecurity, [], [], ["2001:db8::/32"]),
        ];
        _ipinfo.AsnResult = feed => new AsnFeedRefresh(AsnRefreshOutcome.Written,
            new ExportResult(ExportFailure.None, [], 3, 0, FeedChanges: changes, Record: record), StampOf(feed),
            ["8.0.0.0/8", "9.0.0.0/8"]);

        FeedRefreshResult result = Only(await RunAsync(), AsnKey);

        Assert.Equal(new FeedRefreshResult(AsnKey, FeedKind.Asn, FeedRefreshOutcome.Changed, FeedRefreshReason.None, true, 3, 1,
                                           0, 2, null, result.OldFilesLeft, record), result);
        Assert.Empty(result.OldFilesLeft);
    }

    [Fact]
    public async Task A_write_of_only_the_modsecurity_copy_counts_that_copy_once()
    {
        Save(Feed(FeedKind.Asn));
        IReadOnlyList<FeedFileChange> changes =
        [
            Change(FeedType.ModSecurity, ["1.0.0.0/24", "2.0.0.0/24"], ["3.0.0.0/24"]),
            Change(FeedType.ModSecurity, [], [], ["2001:db8::/32"]),
        ];
        _ipinfo.AsnResult = feed => new AsnFeedRefresh(AsnRefreshOutcome.Written,
            new ExportResult(ExportFailure.None, [], 3, 0, FeedChanges: changes), StampOf(feed), []);

        FeedRefreshResult result = Only(await RunAsync(), AsnKey);

        Assert.Equal((FeedRefreshOutcome.Changed, 3, 1), (result.Outcome, result.Added, result.Removed));
    }

    [Fact]
    public async Task A_write_with_no_entry_changed_is_unchanged_but_written()
    {
        Save(Feed(FeedKind.GoogleCloud));
        _google.RefreshResult = feed => new CloudFeedRefresh(CloudRefreshOutcome.Written,
            new CloudFeedWriteResult(CloudFeedWriteFailure.None, [], 1, 0, 0,
                                     FeedChanges: [Change(FeedType.Fortigate, [], [])]),
            StampOf(feed), ["5.0.0.0/24"], []);

        FeedRefreshResult result = Only(await RunAsync(), GoogleKey);

        Assert.Equal((FeedRefreshOutcome.Unchanged, FeedRefreshReason.None, true, 0, 0, 1, 0),
                     (result.Outcome, result.Reason, result.Written, result.Added, result.Removed, result.NewUnconfirmed,
                      result.NewLarge));
    }

    [Fact]
    public async Task A_failed_write_keeps_its_failure_its_files_and_its_stamp()
    {
        Save(Feed(FeedKind.Asn));
        _ipinfo.AsnResult = feed => new AsnFeedRefresh(AsnRefreshOutcome.Failed,
            new ExportResult(ExportFailure.WriteFailed, [], 0, 0, OldFilesLeft: ["old.txt"], FailedFile: "bad.txt",
                             FeedChanges: [Change(FeedType.Fortigate, ["1.0.0.0/24"], [])]),
            new SavedFeedStamp(feed, null, null, null), []);

        RefreshRunResult run = await RunAsync();
        FeedRefreshResult result = Only(run, AsnKey);

        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.WriteFailed, false, 1, "bad.txt"),
                     (result.Outcome, result.Reason, result.Written, result.Added, result.FailedFile));
        Assert.Equal(["old.txt"], result.OldFilesLeft);
        Assert.Contains("commit 1", _log);
        Assert.Null(Assert.Single(_records.Committed).RefreshedUtc);
    }

    [Theory]
    [InlineData(ExportFailure.ExceptionsUnreadable, FeedRefreshReason.ExceptionsUnreadable)]
    [InlineData(ExportFailure.FolderUnavailable, FeedRefreshReason.FolderUnavailable)]
    [InlineData(ExportFailure.NothingToExport, FeedRefreshReason.Failed)]
    public async Task An_asn_write_failure_names_its_reason(ExportFailure failure, FeedRefreshReason reason)
    {
        Save(Feed(FeedKind.Asn));
        _ipinfo.AsnResult = _ => new AsnFeedRefresh(AsnRefreshOutcome.Failed, new ExportResult(failure, [], 0, 0), null, []);

        Assert.Equal(reason, Only(await RunAsync(), AsnKey).Reason);
    }

    [Theory]
    [InlineData(CloudRefreshOutcome.Superseded, null, FeedRefreshOutcome.Skipped, FeedRefreshReason.Superseded)]
    [InlineData(CloudRefreshOutcome.Missing, null, FeedRefreshOutcome.Skipped, FeedRefreshReason.Removed)]
    [InlineData(CloudRefreshOutcome.SaveRefused, null, FeedRefreshOutcome.Failed, FeedRefreshReason.SaveRefused)]
    [InlineData(CloudRefreshOutcome.Failed, CloudFeedWriteFailure.SettingsChanged, FeedRefreshOutcome.Failed, FeedRefreshReason.SettingsChanged)]
    [InlineData(CloudRefreshOutcome.Failed, CloudFeedWriteFailure.NoFeedType, FeedRefreshOutcome.Failed, FeedRefreshReason.NoFeedType)]
    [InlineData(CloudRefreshOutcome.Failed, CloudFeedWriteFailure.ExceptionsUnreadable, FeedRefreshOutcome.Failed, FeedRefreshReason.ExceptionsUnreadable)]
    [InlineData(CloudRefreshOutcome.Failed, null, FeedRefreshOutcome.Failed, FeedRefreshReason.Failed)]
    public async Task A_cloud_outcome_maps_to_its_result(CloudRefreshOutcome outcome, CloudFeedWriteFailure? failure,
                                                         FeedRefreshOutcome expected, FeedRefreshReason reason)
    {
        Save(Feed(FeedKind.Azure));
        _azure.RefreshResult = _ => new CloudFeedRefresh(outcome,
            failure is null ? null : new CloudFeedWriteResult(failure.Value, [], 0, 0, 0), null, [], []);

        FeedRefreshResult result = Only(await RunAsync(), AzureKey);

        Assert.Equal((expected, reason), (result.Outcome, result.Reason));
    }

    [Theory]
    [InlineData(AbuseFeedWriteFailure.ListUnreadable, FeedRefreshReason.ListUnavailable)]
    [InlineData(AbuseFeedWriteFailure.ListNewer, FeedRefreshReason.ListUnavailable)]
    [InlineData(AbuseFeedWriteFailure.StorageError, FeedRefreshReason.ListUnavailable)]
    [InlineData(AbuseFeedWriteFailure.NoDownload, FeedRefreshReason.ListUnavailable)]
    [InlineData(AbuseFeedWriteFailure.ClockWrong, FeedRefreshReason.ClockWrong)]
    [InlineData(AbuseFeedWriteFailure.NothingToWrite, FeedRefreshReason.Failed)]
    [InlineData(AbuseFeedWriteFailure.FolderUnavailable, FeedRefreshReason.FolderUnavailable)]
    [InlineData(AbuseFeedWriteFailure.NoFeedType, FeedRefreshReason.NoFeedType)]
    public async Task A_rolling_write_failure_names_its_reason(AbuseFeedWriteFailure failure, FeedRefreshReason reason)
    {
        Save(Feed(FeedKind.AbuseIpDbRolling));
        _abuse.RefreshResult = new RollingFeedRefresh(RollingRefreshOutcome.Failed,
            new AbuseFeedWriteResult(failure, [], 0, 0, 0, 0, []), null);

        FeedRefreshResult result = Only(await RunAsync(), RollingKey);

        Assert.Equal((FeedRefreshOutcome.Failed, reason), (result.Outcome, result.Reason));
    }

    // ----- The commit -----

    [Theory]
    [InlineData(SavedFeedStampOutcome.Saved, FeedRefreshOutcome.Changed, FeedRefreshReason.None)]
    [InlineData(SavedFeedStampOutcome.Unchanged, FeedRefreshOutcome.Changed, FeedRefreshReason.None)]
    [InlineData(SavedFeedStampOutcome.Superseded, FeedRefreshOutcome.Skipped, FeedRefreshReason.Superseded)]
    [InlineData(SavedFeedStampOutcome.Removed, FeedRefreshOutcome.Skipped, FeedRefreshReason.Removed)]
    [InlineData(SavedFeedStampOutcome.TooMany, FeedRefreshOutcome.Failed, FeedRefreshReason.NotSaved)]
    [InlineData(SavedFeedStampOutcome.Unavailable, FeedRefreshOutcome.Failed, FeedRefreshReason.NotSaved)]
    [InlineData(SavedFeedStampOutcome.StorageError, FeedRefreshOutcome.Failed, FeedRefreshReason.NotSaved)]
    public async Task The_commits_outcome_decides_a_written_feeds_result(SavedFeedStampOutcome commit, FeedRefreshOutcome expected,
                                                                        FeedRefreshReason reason)
    {
        Save(Feed(FeedKind.Asn), Feed(FeedKind.AbuseIpDbRolling));
        _ipinfo.AsnResult = feed => new AsnFeedRefresh(AsnRefreshOutcome.Written,
            new ExportResult(ExportFailure.None, [], 1, 0, FeedChanges: [Change(FeedType.Fortigate, ["1.0.0.0/24"], [])]),
            StampOf(feed), []);
        _records.Outcome = stamp => stamp.Expected.Key == AsnKey ? commit : SavedFeedStampOutcome.Saved;

        RefreshRunResult run = await RunAsync();

        FeedRefreshResult asn = Only(run, AsnKey);
        Assert.Equal((expected, reason, true, 1), (asn.Outcome, asn.Reason, asn.Written, asn.Added));
        Assert.Equal((FeedRefreshOutcome.Unchanged, FeedRefreshReason.None), (Only(run, RollingKey).Outcome, Only(run, RollingKey).Reason));
    }

    [Theory]
    [InlineData(SavedFeedStampOutcome.Saved, false)]
    [InlineData(SavedFeedStampOutcome.Unchanged, false)]
    [InlineData(SavedFeedStampOutcome.Superseded, false)]
    [InlineData(SavedFeedStampOutcome.Removed, false)]
    [InlineData(SavedFeedStampOutcome.TooMany, true)]
    [InlineData(SavedFeedStampOutcome.Unavailable, true)]
    [InlineData(SavedFeedStampOutcome.StorageError, true)]
    public async Task A_save_the_store_couldnt_make_marks_the_run_save_failed(SavedFeedStampOutcome commit, bool saveFailed)
    {
        Save(Feed(FeedKind.Asn), Feed(FeedKind.AbuseIpDbRolling));
        _ipinfo.AsnResult = feed => new AsnFeedRefresh(AsnRefreshOutcome.Written,
            new ExportResult(ExportFailure.None, [], 1, 0, FeedChanges: [Change(FeedType.Fortigate, ["1.0.0.0/24"], [])]),
            StampOf(feed), []);
        _records.Outcome = stamp => stamp.Expected.Key == AsnKey ? commit : SavedFeedStampOutcome.Saved;

        RefreshRunResult run = await RunAsync();

        Assert.Equal(saveFailed, run.SaveFailed);
    }

    [Theory]
    [InlineData(SavedFeedStampOutcome.Superseded, false)]
    [InlineData(SavedFeedStampOutcome.Removed, false)]
    [InlineData(SavedFeedStampOutcome.StorageError, true)]
    public async Task A_failed_write_keeps_its_failure_whatever_the_commit_says(SavedFeedStampOutcome commit, bool saveFailed)
    {
        Save(Feed(FeedKind.Asn));
        _ipinfo.AsnResult = feed => new AsnFeedRefresh(AsnRefreshOutcome.Failed,
            new ExportResult(ExportFailure.WriteFailed, [], 0, 0, FailedFile: "bad.txt"), new SavedFeedStamp(feed, null, null, null), []);
        _records.Outcome = _ => commit;

        RefreshRunResult run = await RunAsync();

        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.WriteFailed), (Only(run, AsnKey).Outcome, Only(run, AsnKey).Reason));
        Assert.Equal(saveFailed, run.SaveFailed);
    }

    [Fact]
    public async Task A_commit_that_throws_fails_the_stamped_feeds_as_not_saved()
    {
        Save(Feed(FeedKind.Asn), Feed(FeedKind.AbuseIpDbRolling), Feed(FeedKind.Aws));
        _ipinfo.AsnResult = feed => new AsnFeedRefresh(AsnRefreshOutcome.Failed,
            new ExportResult(ExportFailure.WriteFailed, [], 0, 0, FailedFile: "bad.txt"), new SavedFeedStamp(feed, null, null, null), []);
        _aws.CheckFailure = CheckFailure.PickGone;
        _records.CommitThrows = true;

        RefreshRunResult run = await RunAsync();

        Assert.True(run.SaveFailed);
        Assert.False(run.Cancelled);
        Assert.Contains("commit 2", _log);
        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.WriteFailed), (Only(run, AsnKey).Outcome, Only(run, AsnKey).Reason));
        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.NotSaved), (Only(run, RollingKey).Outcome, Only(run, RollingKey).Reason));
        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.PickGone), (Only(run, AwsKey).Outcome, Only(run, AwsKey).Reason));
    }

    [Fact]
    public async Task Saved_feeds_whose_read_throws_are_unavailable()
    {
        Save(Feed(FeedKind.Asn));
        _records.ReadThrows = true;

        RefreshRunResult run = await RunAsync();

        Assert.False(run.SavedFeedsAvailable);
        Assert.Empty(run.Feeds);
        Assert.Empty(_log);
    }

    // ----- Cancel and failure -----

    [Fact]
    public async Task A_cancel_between_feeds_commits_the_done_ones_and_marks_the_rest_cancelled()
    {
        SaveEveryKind();
        using var cts = new CancellationTokenSource();
        _abuse.OnRefresh = cts.Cancel;

        RefreshRunResult run = await RunAsync(cts.Token);

        Assert.True(run.Cancelled);
        Assert.Equal([AsnKey, AwsKey, RollingKey], _records.Committed.Select(stamp => stamp.Expected.Key));
        Assert.Equal(FeedRefreshOutcome.Unchanged, Only(run, RollingKey).Outcome);
        Assert.Equal((FeedRefreshOutcome.Skipped, FeedRefreshReason.Cancelled), (Only(run, AzureKey).Outcome, Only(run, AzureKey).Reason));
        Assert.Equal((FeedRefreshOutcome.Skipped, FeedRefreshReason.Cancelled), (Only(run, GoogleKey).Outcome, Only(run, GoogleKey).Reason));
        Assert.DoesNotContain("azure check", _log);
    }

    [Fact]
    public async Task A_refresher_cancelled_by_the_run_is_cancelled_with_no_stamp()
    {
        Save(Feed(FeedKind.Asn), Feed(FeedKind.AbuseIpDbRolling));
        using var cts = new CancellationTokenSource();
        _ipinfo.AsnResult = _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreached");
        };

        RefreshRunResult run = await RunAsync(cts.Token);

        Assert.True(run.Cancelled);
        Assert.All(run.Feeds, feed => Assert.Equal((FeedRefreshOutcome.Skipped, FeedRefreshReason.Cancelled), (feed.Outcome, feed.Reason)));
        Assert.DoesNotContain(_log, line => line.StartsWith("commit", StringComparison.Ordinal));
        Assert.DoesNotContain("rolling", _log);
    }

    [Fact]
    public async Task A_cancel_during_the_dataset_step_ends_the_run_with_no_commit()
    {
        SaveEveryKind();
        using var cts = new CancellationTokenSource();
        _ipinfo.OnUpdate = cts.Cancel;

        RefreshRunResult run = await RunAsync(cts.Token);

        Assert.True(run.Cancelled);
        Assert.Equal(["dataset"], _log);
        Assert.Equal(5, run.Feeds.Count);
        Assert.All(run.Feeds, feed => Assert.Equal((FeedRefreshOutcome.Skipped, FeedRefreshReason.Cancelled), (feed.Outcome, feed.Reason)));
    }

    [Fact]
    public async Task A_cancel_during_the_sources_step_ends_the_run_with_no_commit()
    {
        SaveEveryKind();
        using var cts = new CancellationTokenSource();
        _azure.OnLoad = () =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        };

        RefreshRunResult run = await RunAsync(cts.Token);

        Assert.True(run.Cancelled);
        Assert.Equal(["dataset", "google load", "azure cached"], _log);
        Assert.DoesNotContain(run.Sources, source => source.Provider != FeedKind.GoogleCloud);
        Assert.All(run.Feeds, feed => Assert.Equal((FeedRefreshOutcome.Skipped, FeedRefreshReason.Cancelled), (feed.Outcome, feed.Reason)));
    }

    [Fact]
    public async Task A_cancel_during_the_abuseipdb_step_ends_the_run_with_no_commit()
    {
        SaveEveryKind();
        using var cts = new CancellationTokenSource();
        _abuse.OnDownload = () =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        };

        RefreshRunResult run = await RunAsync(cts.Token);

        Assert.True(run.Cancelled);
        Assert.Equal("download", _log.Last());
        Assert.DoesNotContain(_log, line => line.StartsWith("commit", StringComparison.Ordinal));
        Assert.All(run.Feeds, feed => Assert.Equal((FeedRefreshOutcome.Skipped, FeedRefreshReason.Cancelled), (feed.Outcome, feed.Reason)));
    }

    [Fact]
    public async Task A_provider_load_that_throws_fails_only_that_providers_feeds()
    {
        Save(Feed(FeedKind.GoogleCloud), Feed(FeedKind.Asn), Feed(FeedKind.Azure), Feed(FeedKind.AbuseIpDbRolling));
        _google.OnLoad = () => throw new InvalidOperationException("boom");

        RefreshRunResult run = await RunAsync();

        Assert.False(run.Cancelled);
        Assert.Equal([new SourceRefresh(FeedKind.GoogleCloud, SourceRefreshState.Unavailable),
                      new SourceRefresh(FeedKind.Azure, SourceRefreshState.Fresh)], run.Sources);
        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.SourceUnavailable),
                     (Only(run, GoogleKey).Outcome, Only(run, GoogleKey).Reason));
        Assert.All(run.Feeds.Where(feed => feed.Key != GoogleKey), feed => Assert.Equal(FeedRefreshOutcome.Unchanged, feed.Outcome));
        Assert.Contains("commit 3", _log);
    }

    [Fact]
    public async Task A_providers_own_timeout_makes_only_that_provider_unavailable()
    {
        Save(Feed(FeedKind.GoogleCloud), Feed(FeedKind.Asn));
        _google.OnLoad = () => throw new OperationCanceledException();

        RefreshRunResult run = await RunAsync();

        Assert.False(run.Cancelled);
        Assert.Equal([new SourceRefresh(FeedKind.GoogleCloud, SourceRefreshState.Unavailable)], run.Sources);
        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.SourceUnavailable),
                     (Only(run, GoogleKey).Outcome, Only(run, GoogleKey).Reason));
        Assert.Contains("commit 1", _log);
    }

    [Fact]
    public async Task A_progress_handler_that_throws_costs_the_run_nothing()
    {
        Save(Feed(FeedKind.Asn));

        RefreshRunResult run = await RunAsync(
            progress: new ImmediateProgress<RefreshProgress>(_ => throw new InvalidOperationException("boom")));

        Assert.Equal(FeedRefreshOutcome.Unchanged, Assert.Single(run.Feeds).Outcome);
        Assert.False(run.SaveFailed);
        Assert.Contains("commit 1", _log);
    }

    [Fact]
    public async Task A_refresher_that_throws_fails_only_its_own_feed()
    {
        Save(Feed(FeedKind.Asn), Feed(FeedKind.AbuseIpDbRolling));
        _ipinfo.AsnResult = _ => throw new InvalidOperationException("boom");

        RefreshRunResult run = await RunAsync();

        Assert.False(run.Cancelled);
        Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.Failed), (Only(run, AsnKey).Outcome, Only(run, AsnKey).Reason));
        Assert.Equal(FeedRefreshOutcome.Unchanged, Only(run, RollingKey).Outcome);
        Assert.Equal([RollingKey], _records.Committed.Select(stamp => stamp.Expected.Key));
    }

    [Fact]
    public async Task A_step_that_throws_fails_every_feed_and_the_run_returns()
    {
        Save(Feed(FeedKind.GoogleCloud), Feed(FeedKind.Asn));
        _ipinfo.OnUpdate = () => throw new InvalidOperationException("boom");

        RefreshRunResult run = await RunAsync();

        Assert.False(run.Cancelled);
        Assert.All(run.Feeds, feed => Assert.Equal((FeedRefreshOutcome.Failed, FeedRefreshReason.Failed), (feed.Outcome, feed.Reason)));
        Assert.DoesNotContain(_log, line => line.StartsWith("commit", StringComparison.Ordinal));
    }

    // ----- The reach -----

    [Theory]
    [InlineData(typeof(IpInfoService), typeof(IIpInfoService), "RefreshAsnFeedAsync")]
    [InlineData(typeof(AbuseIpDbService), typeof(IAbuseIpDbService), "RefreshFeedAsync")]
    [InlineData(typeof(GoogleCloudFeedService), typeof(ICloudFeedService), "RefreshAsync")]
    [InlineData(typeof(AzureFeedService), typeof(ICloudFeedService), "RefreshAsync")]
    [InlineData(typeof(AwsFeedService), typeof(ICloudFeedService), "RefreshAsync")]
    [InlineData(typeof(CloudFeedEngine), typeof(ICloudFeedService), "RefreshAsync")]
    public void Each_real_service_answers_the_refresh_itself_not_the_interfaces_default(Type service, Type contract, string member)
    {
        System.Reflection.InterfaceMapping map = service.GetInterfaceMap(contract);
        int index = Array.FindIndex(map.InterfaceMethods, method => method.Name == member);
        Assert.True(index >= 0);

        Assert.Equal(service, map.TargetMethods[index].DeclaringType);
    }

    // ----- The fakes -----

    private sealed class FakeRecords(Log log) : IFeedRecords
    {
        public List<SavedFeed> Feeds { get; } = [];
        public bool Available { get; set; } = true;
        public Func<SavedFeedStamp, SavedFeedStampOutcome> Outcome { get; set; } = _ => SavedFeedStampOutcome.Saved;
        public List<SavedFeedStamp> Committed { get; } = [];
        public bool ReadThrows { get; set; }
        public bool CommitThrows { get; set; }

        public SavedFeedsRead ReadSavedFeeds() =>
            ReadThrows ? throw new InvalidOperationException("boom") : new(Available, Available ? Feeds.ToList() : []);

        IReadOnlyList<SavedFeedStampOutcome> IFeedRecords.CommitRefresh(IReadOnlyList<SavedFeedStamp> stamps)
        {
            log.Add("commit " + stamps.Count);
            if (CommitThrows)
                throw new InvalidOperationException("boom");
            Committed.AddRange(stamps);
            return stamps.Select(Outcome).ToList();
        }

        FeedRecordResult IFeedRecords.Record(FeedRecordRequest request) => throw new NotSupportedException();
        bool IFeedRecords.SaveRefusedThisSession(string feedKey) => false;
        public IDisposable EnterWrite(string feedKey) => throw new NotSupportedException();
        public int WritesInFlight => 0;
        public Task StopWritesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<HistoryRead> SearchHistoryAsync(HistoryQuery query, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<HistoryState> ClearHistoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<SavedFeedsFailure> RemoveSavedFeedAsync(string key, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeIpInfo(Log log, FakeRecords records) : IIpInfoService
    {
        public Func<SavedFeed, AsnFeedRefresh> AsnResult { get; set; } =
            feed => new AsnFeedRefresh(AsnRefreshOutcome.Unchanged, null, StampOf(feed), []);
        public Action? OnUpdate { get; set; }

        Task<AsnFeedRefresh> IIpInfoService.RefreshAsnFeedAsync(string feedKey, CancellationToken ct)
        {
            log.Add("asn " + feedKey);
            return Task.FromResult(AsnResult(records.Feeds.Single(feed => feed.Key == feedKey)));
        }

        public List<IpInfoUpdatePolicy?> Policies { get; } = [];

        public Task<UpdateResult> UpdateDatasetAsync(IProgress<UpdateProgress>? progress, CancellationToken ct,
                                                     IpInfoUpdatePolicy? policy = null)
        {
            log.Add("dataset");
            Policies.Add(policy);
            OnUpdate?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new UpdateResult(UpdateFailure.None, true, Dataset));
        }

        public IFeedRecords Records => records;
        public string DataFolder => throw new NotSupportedException();
        public UserSettings Settings => throw new NotSupportedException();
        public bool HasToken => throw new NotSupportedException();
        DatasetInfo? IIpInfoService.Dataset => throw new NotSupportedException();
        public ExceptionList Exceptions => throw new NotSupportedException();
        public Task<StartupResult> InitializeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<StartupResult> StartFreshAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<StartupResult> StartWithoutOldExceptionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<SearchResult> SearchAsync(string input, int displayLimit, CancellationToken ct) => throw new NotSupportedException();
        public Task<OverlapResult> LookUpOverlapsAsync(IReadOnlyList<CidrBlock> blocks, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<uint, string>> FindAsNamesAsync(IReadOnlyCollection<uint> asns, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ExportPlan> PrepareExportAsync(SearchQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExportResult> ExportAsync(SearchQuery query, IReadOnlyCollection<string> includedLargeNetworks, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<string> GetResultsTextAsync(SearchQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SaveTokenAsync(string token) => throw new NotSupportedException();
        public Task<bool> ClearTokenAsync() => throw new NotSupportedException();
        public Task<bool> SaveSettingsAsync(UserSettings settings) => throw new NotSupportedException();
        public Task<ExceptionsSaveOutcome> SaveExceptionsAsync(IReadOnlyList<ExceptionEntry> entries) => throw new NotSupportedException();
        public Task<ModSecurityRuleIdsResult> AssignModSecurityRuleIdsAsync(IReadOnlyList<string> fileNames, CancellationToken ct) =>
            throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class FakeAbuse(Log log) : IAbuseIpDbService
    {
        public bool HasKey { get; set; }
        public AbuseDownloadResult Download { get; set; } = new(AbuseIpDbFailure.None, true, 1, 0, 1, 0, 0, null, null, true);
        public AbuseDownloadPolicy? Policy { get; private set; }
        public Task? DownloadGate { get; set; }
        public RollingFeedRefresh? RefreshResult { get; set; }
        public Action? OnRefresh { get; set; }
        public Action? OnDownload { get; set; }
        public TaskCompletionSource DownloadAsked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AbuseDownloadResult> DownloadBlacklistAsync(AbuseDownloadPolicy? policy, CancellationToken ct)
        {
            log.Add("download");
            DownloadAsked.TrySetResult();
            OnDownload?.Invoke();
            Policy = policy;
            if (DownloadGate is not null)
            {
                await DownloadGate;
                log.Add("download returned");
            }
            return Download;
        }

        Task<RollingFeedRefresh> IAbuseIpDbService.RefreshFeedAsync(CancellationToken ct)
        {
            log.Add("rolling");
            OnRefresh?.Invoke();
            return Task.FromResult(RefreshResult
                ?? new RollingFeedRefresh(RollingRefreshOutcome.Unchanged, null, StampOf(Feed(FeedKind.AbuseIpDbRolling))));
        }

        public Task<AbuseKeySave> SaveKeyAsync(string key) => throw new NotSupportedException();
        public Task<bool> ClearKeyAsync() => throw new NotSupportedException();
        public Task<AbuseCheckResult> CheckAsync(SearchQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseListStatus> GetStatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseNewAddresses> GetNewAddressesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseIpDbFailure> ClearListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseFeedWriteResult> WriteFeedAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseFeedWriteResult> EmptyFeedAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseNetworksResult> FindOffendingNetworksAsync(IProgress<GroupingProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
        public long ListVersion => 0;
        public event EventHandler<AbuseListChange>? ListChanged { add { } remove { } }
        public AbuseWork? WorkUnderWay => null;
        public event EventHandler? WorkChanged { add { } remove { } }
    }

    /// <summary>One provider's fake: the check and the refresh it is asked for, logged under its name.</summary>
    private abstract class FakeCloud(Log log, string name, FeedKind kind, string key) : ICloudFeedService
    {
        public CheckFailure CheckFailure { get; set; }
        public Func<SavedFeed, CloudFeedRefresh>? RefreshResult { get; set; }
        public Action? OnLoad { get; set; }
        public DateTimeOffset? RefreshStarted { get; private set; }

        protected void Log(string what)
        {
            log.Add(name + " " + what);
            if (what is "load" or "cached")
                OnLoad?.Invoke();
        }

        protected CheckResult Check()
        {
            Log("check");
            if (CheckFailure != CheckFailure.None)
                return new CheckResult(CheckFailure, null);
            var pick = new CloudFeedPick(kind, null, false, null, null, null);
            return new CheckResult(CheckFailure.None,
                new CloudFeedCheck(new FeedDescription("example.com", [], key), Dataset, true, 12, 24, [], pick));
        }

        Task<CloudFeedRefresh> ICloudFeedService.RefreshAsync(CloudFeedCheck check, DateTimeOffset checkStartedUtc, CancellationToken ct)
        {
            Log("refresh");
            RefreshStarted = checkStartedUtc;
            SavedFeed feed = Feed(kind);
            return Task.FromResult(RefreshResult?.Invoke(feed) ?? new CloudFeedRefresh(CloudRefreshOutcome.Unchanged, null, StampOf(feed), [], []));
        }

        public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) => throw new NotSupportedException();
        public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeGoogle(Log log) : FakeCloud(log, "google", FeedKind.GoogleCloud, GoogleKey), IGoogleCloudFeedService
    {
        private static readonly CloudRangeCatalogue Fetched = new(new CloudRangeSource(new Uri("https://example.com/a"), "1", "t"), [], [], 0);

        public CloudRangeCatalogue SavedCatalogue { get; } = new(new CloudRangeSource(new Uri("https://example.com/b"), "2", "t"), [], [], 0);
        public CatalogueResult Load { get; set; } = new(CatalogueFailure.None, Fetched);
        public SavedRanges<CloudRangeCatalogue>? Saved { get; set; }
        public CloudRangeCatalogue? CheckedCatalogue { get; private set; }

        public event EventHandler<CloudSourceUpdated<CloudRangeCatalogue>>? SourceUpdated { add { } remove { } }

        public Task<CatalogueResult> LoadCatalogueAsync(CancellationToken ct)
        {
            Log("load");
            return Task.FromResult(Load);
        }

        public Task<SavedRanges<CloudRangeCatalogue>> LoadSavedAsync(CancellationToken ct)
        {
            Log("saved");
            return Task.FromResult(Saved ?? new SavedRanges<CloudRangeCatalogue>(SavedRangesState.Read, SavedCatalogue, Now));
        }

        public Task<CheckResult> CheckAsync(CloudRangeCatalogue catalogue, RegionSelection selection, CancellationToken ct)
        {
            CheckedCatalogue = catalogue;
            Assert.Equal("us-central1", selection.Region.Id);
            return Task.FromResult(Check());
        }
    }

    private sealed class FakeAzure(Log log) : FakeCloud(log, "azure", FeedKind.Azure, AzureKey), IAzureFeedService
    {
        public AzureUpdateStatus Status { get; set; } = AzureUpdateStatus.UpToDate;

        public event EventHandler<CloudSourceUpdated<AzureTagCatalogue>>? SourceUpdated { add { } remove { } }

        public Task<AzureTagCatalogue?> LoadCachedAsync(CancellationToken ct)
        {
            Log("cached");
            return Task.FromResult<AzureTagCatalogue?>(null);
        }

        public Task<SavedRanges<AzureTagCatalogue>> LoadSavedAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<AzureLoadResult> CheckForUpdateAsync(AzureTagCatalogue? current, CancellationToken ct)
        {
            Log("update");
            AzureTagCatalogue? catalogue = Status == AzureUpdateStatus.Unavailable ? null : AzureTestData.Catalogue();
            return Task.FromResult(new AzureLoadResult(Status, AzureSourceFailure.None, catalogue, Now));
        }

        public Task<CheckResult> CheckAsync(AzureTagCatalogue catalogue, AzureTagSelection selection, CancellationToken ct)
        {
            Assert.Equal("Storage.SoutheastAsia", selection.TagName);
            return Task.FromResult(Check());
        }
    }

    private sealed class FakeAws(Log log) : FakeCloud(log, "aws", FeedKind.Aws, AwsKey), IAwsFeedService
    {
        public AwsUpdateStatus Status { get; set; } = AwsUpdateStatus.UpToDate;

        public event EventHandler<CloudSourceUpdated<AwsRangeCatalogue>>? SourceUpdated { add { } remove { } }

        public Task<AwsRangeCatalogue?> LoadCachedAsync(CancellationToken ct)
        {
            Log("cached");
            return Task.FromResult<AwsRangeCatalogue?>(null);
        }

        public Task<SavedRanges<AwsRangeCatalogue>> LoadSavedAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<AwsLoadResult> CheckForUpdateAsync(AwsRangeCatalogue? current, CancellationToken ct)
        {
            Log("update");
            AwsRangeCatalogue? catalogue = Status == AwsUpdateStatus.Unavailable ? null : AwsTestData.Catalogue();
            return Task.FromResult(new AwsLoadResult(Status, AwsSourceFailure.None, catalogue, Now));
        }

        public Task<CheckResult> CheckAsync(AwsRangeCatalogue catalogue, AwsSelection selection, CancellationToken ct)
        {
            Assert.Equal(("EC2", "us-east-1"), (selection.Service, selection.Region));
            return Task.FromResult(Check());
        }
    }

    /// <summary>The fakes' shared log: a refresher may write it from a pool thread while the test reads it.</summary>
    private sealed class Log : IEnumerable<string>
    {
        private readonly List<string> _lines = [];

        public void Add(string line)
        {
            lock (_lines)
                _lines.Add(line);
        }

        public IEnumerator<string> GetEnumerator()
        {
            lock (_lines)
                return _lines.ToList().GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>Refresh all over the real services in a temporary data folder, with stub answers.</summary>
public sealed class FeedRefresherServiceTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private const string CloudJson = """
        {
          "syncToken": "1",
          "creationTime": "2026-10-30T00:00:00Z",
          "prefixes": [
            {"ipv4Prefix": "8.8.8.0/24", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv6Prefix": "2001:4860::/32", "service": "Google Cloud", "scope": "us-central1"}
          ]
        }
        """;

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly HttpClient _http;
    private readonly List<IDisposable> _owned = [];
    private byte[] _dataset = Gz(
    [
        Line("8.8.8.0/24", 15169), Line("8.8.4.0/24", 15169), Line("2001:4860::/32", 15169), Line("1.1.1.0/24", 13335),
    ]);

    public FeedRefresherServiceTests()
    {
        _http = new HttpClient(new StubHttpHandler(Respond));
    }

    public void Dispose()
    {
        foreach (IDisposable owned in _owned)
            owned.Dispose();
        _http.Dispose();
        _temp.Dispose();
    }

    private static string Line(string network, int asn) =>
        $$"""{"network":"{{network}}","country":"United States","country_code":"US","continent":"North America","continent_code":"NA","asn":"AS{{asn}}","as_name":"Google LLC","as_domain":"google.com"}""";

    private static byte[] Gz(IEnumerable<string> lines)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            foreach (string line in lines)
                writer.WriteLine(line);
        }
        return ms.ToArray();
    }

    private HttpResponseMessage Respond(HttpRequestMessage request, int callNumber)
    {
        if (request.RequestUri!.Host == "www.gstatic.com")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CloudJson, Encoding.UTF8, "application/json") };
        if (request.RequestUri.AbsolutePath.EndsWith("/checksums", StringComparison.Ordinal))
        {
            string sha = Convert.ToHexStringLower(SHA256.HashData(_dataset));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"checksums\":{\"sha256\":\"" + sha + "\"}}", Encoding.UTF8, "application/json"),
            };
        }
        if (request.RequestUri.Host == "ipinfo.io")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_dataset) };
        throw new InvalidOperationException("A request went to an unexpected host.");
    }

    private async Task<IpInfoService> InstalledServiceAsync()
    {
        var service = new IpInfoService(_temp.Paths, _http, new CoreOptions(), _time, new InMemorySecretStore(),
            new InMemorySecretStore(), () => false, (duration, token) => Task.CompletedTask);
        _owned.Add(service);
        Assert.Equal(StartupFailure.None, (await service.InitializeAsync(Ct)).Failure);
        Assert.True(await service.SaveTokenAsync("test-token"));
        Assert.Equal(UpdateFailure.None, (await service.UpdateDatasetAsync(null, Ct)).Failure);
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            WriteFortigateFeeds = true,
            WriteModSecurityLists = true,
            FortigateSplitByFamily = true,
            IPv6Enabled = true,
            IPv4MinPrefix = 12,
            IPv6MinPrefix = 24,
        }));
        return service;
    }

    private (FeedRefresher Refresher, AbuseIpDbService Abuse, GoogleCloudFeedService Google) Compose(IpInfoService ipinfo)
    {
        var abuse = new AbuseIpDbService(_http, ipinfo, _temp.Paths, new CoreOptions(), _time, new InMemorySecretStore(),
                                         new FakeAbuseAccounting());
        string folder = _temp.Paths.DefaultExportFolder;
        var google = new GoogleCloudFeedService(ipinfo, _http, new CoreOptions(), _time, _temp.Paths, folder);
        var azure = new AzureFeedService(ipinfo, _http, new CoreOptions(), _time, _temp.Paths, folder);
        var aws = new AwsFeedService(ipinfo, _http, new CoreOptions(), _time, _temp.Paths, folder);
        return (new FeedRefresher(ipinfo, abuse, google, azure, aws, _time), abuse, google);
    }

    private static SearchQuery Query(string text)
    {
        Assert.True(SearchQuery.TryParse(text, out SearchQuery? query));
        return query;
    }

    private async Task PageWriteAsnAsync(IpInfoService ipinfo, string asn)
    {
        ExportResult written = await ipinfo.ExportAsync(Query(asn), [], Ct);
        Assert.Equal(ExportFailure.None, written.Failure);
        Assert.Equal(FeedSaveOutcome.Saved, written.Record?.Save);
    }

    private async Task PageWriteGoogleAsync(GoogleCloudFeedService google)
    {
        CatalogueResult catalogue = await google.LoadCatalogueAsync(Ct);
        Assert.True(GoogleCloudScope.TryCreate("us-central1", out GoogleCloudScope scope));
        CheckResult check = await google.CheckAsync(catalogue.Catalogue!, new RegionSelection(scope, false), Ct);
        Assert.Equal(CheckFailure.None, check.Failure);
        CloudFeedPlan plan = google.PlanWrite(check.Check!, check.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        CloudFeedWriteResult written = await google.WriteAsync(plan, [], Ct);
        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(FeedSaveOutcome.Saved, written.Record?.Save);
    }

    private async Task PageWriteRollingAsync(AbuseIpDbService abuse)
    {
        DateTimeOffset downloaded = _time.GetUtcNow().AddMinutes(-30);
        var entries = new[] { "9.9.9.9", "2001:4860::8888" }.Select(text =>
        {
            Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
            Assert.True(PublicAddress.TryCreate(address.GetAddressBytes(), out PublicAddress? value));
            return new BlacklistEntry(value, 90, null, null);
        }).ToList();
        Assert.Equal(AbuseListState.Usable, new AbuseIpDbList(_temp.Paths.AbuseIpDbDatabasePath)
            .Merge(new Blacklist(downloaded, entries, 0, 0, 0), downloaded, downloaded.AddDays(-30), Ct).State);
        AbuseFeedWriteResult written = await abuse.WriteFeedAsync(Ct);
        Assert.Equal(AbuseFeedWriteFailure.None, written.Failure);
        Assert.Equal(FeedSaveOutcome.Saved, written.Record?.Save);
    }

    private Dictionary<string, byte[]> Files() =>
        Directory.EnumerateFiles(_temp.ExportFolder, "*", SearchOption.AllDirectories)
                 .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

    private void DeleteFeedFiles()
    {
        foreach (string folder in new[] { "fortigate", "modsecurity" })
            Directory.Delete(Path.Combine(_temp.ExportFolder, folder), recursive: true);
    }

    [Fact]
    public async Task A_first_run_writes_and_commits_and_a_second_changes_nothing()
    {
        IpInfoService ipinfo = await InstalledServiceAsync();
        (FeedRefresher refresher, AbuseIpDbService abuse, GoogleCloudFeedService google) = Compose(ipinfo);
        await PageWriteAsnAsync(ipinfo, "AS15169");
        await PageWriteGoogleAsync(google);
        await PageWriteRollingAsync(abuse);
        DeleteFeedFiles();
        _time.Advance(TimeSpan.FromMinutes(1));

        RefreshRunResult first = await refresher.RunAsync(null, Ct);

        Assert.True(first.SavedFeedsAvailable);
        Assert.False(first.Cancelled);
        Assert.Equal(UpdateFailure.None, first.Dataset?.Failure);
        Assert.Equal([new SourceRefresh(FeedKind.GoogleCloud, SourceRefreshState.Fresh)], first.Sources);
        Assert.Equal(new AbuseRefreshDownload(AbuseIpDbFailure.NoKey, null), first.AbuseIpDb);
        Assert.Equal(["AS15169", AbuseIpDbFeedWriter.FileStem, "GoogleCloud_us-central1"], first.Feeds.Select(feed => feed.Key));
        Assert.All(first.Feeds, feed =>
        {
            Assert.Equal((FeedRefreshOutcome.Changed, FeedRefreshReason.None, true, 0), (feed.Outcome, feed.Reason, feed.Written, feed.Removed));
            Assert.Equal(feed.Kind == FeedKind.Asn ? 3 : 2, feed.Added);
            Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Recorded), feed.Record);
        });
        DateTimeOffset firstRun = DateTimeOffset.FromUnixTimeSeconds(_time.GetUtcNow().ToUnixTimeSeconds());
        Assert.All(ipinfo.Records.ReadSavedFeeds().Feeds, feed =>
        {
            Assert.Equal(firstRun, feed.LastRefreshUtc);
            Assert.NotNull(feed.Proof);
        });
        Dictionary<string, byte[]> written = Files();
        _time.Advance(TimeSpan.FromMinutes(1));

        RefreshRunResult second = await refresher.RunAsync(null, Ct);

        Assert.All(second.Feeds, feed =>
            Assert.Equal((FeedRefreshOutcome.Unchanged, FeedRefreshReason.None, false, 0, 0),
                         (feed.Outcome, feed.Reason, feed.Written, feed.Added, feed.Removed)));
        Dictionary<string, byte[]> after = Files();
        Assert.Equal(written.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach ((string path, byte[] bytes) in written)
            Assert.Equal(bytes, after[path]);
        Assert.DoesNotContain(after.Keys, path => path.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.All(ipinfo.Records.ReadSavedFeeds().Feeds, feed => Assert.True(feed.LastRefreshUtc > firstRun));
    }

    [Fact]
    public async Task Measure_a_run_over_200_saved_asn_feeds()
    {
        // A measurement, not a check: it runs only when asked, and writes its times to the file named
        string? output = Environment.GetEnvironmentVariable("POORMANSFEED_REFRESH_MEASURE");
        if (string.IsNullOrEmpty(output))
        {
            Assert.Skip("Set POORMANSFEED_REFRESH_MEASURE to a file path to measure a run.");
            return;
        }

        _dataset = Gz(Enumerable.Range(0, 200).Select(i => Line($"45.{i}.0.0/24", 1000 + i)));
        IpInfoService ipinfo = await InstalledServiceAsync();
        (FeedRefresher refresher, _, _) = Compose(ipinfo);
        for (int i = 0; i < 200; i++)
            await PageWriteAsnAsync(ipinfo, $"AS{1000 + i}");
        DeleteFeedFiles();
        _time.Advance(TimeSpan.FromMinutes(1));

        var report = new StringBuilder();
        foreach (string label in new[] { "writes", "skips" })
        {
            long saving = 0;
            var clock = Stopwatch.StartNew();
            RefreshRunResult run = await refresher.RunAsync(
                new ImmediateProgress<RefreshProgress>(p => { if (p.Step == RefreshStep.Saving) saving = clock.ElapsedTicks; }), Ct);
            clock.Stop();
            Assert.Equal(200, run.Feeds.Count(feed => feed.Outcome is FeedRefreshOutcome.Changed or FeedRefreshOutcome.Unchanged));
            double total = clock.Elapsed.TotalMilliseconds;
            double commit = (clock.ElapsedTicks - saving) * 1000.0 / Stopwatch.Frequency;
            report.AppendLine(FormattableString.Invariant($"{label}: run {total:F0} ms, commit {commit:F0} ms"));
            _time.Advance(TimeSpan.FromMinutes(1));
        }
        File.WriteAllText(output, report.ToString());
    }
}
