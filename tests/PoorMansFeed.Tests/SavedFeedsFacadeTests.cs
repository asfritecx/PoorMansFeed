using System.Net;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

public sealed class SavedFeedsFacadeTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset T = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(T);
    private readonly HttpClient _http = new(new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    private readonly InMemorySecretStore _secrets = new();
    private readonly InMemorySecretStore _configKeys = new();
    private readonly List<IpInfoService> _services = [];

    public void Dispose()
    {
        foreach (IpInfoService service in _services)
            service.Dispose();
        _http.Dispose();
        _temp.Dispose();
    }

    private IpInfoService NewService()
    {
        var service = new IpInfoService(_temp.Paths, _http, new CoreOptions(), _time, _secrets, _configKeys,
            () => false, (duration, token) => Task.CompletedTask);
        _services.Add(service);
        return service;
    }

    private async Task<IpInfoService> LaunchedServiceAsync()
    {
        IpInfoService service = NewService();
        StartupResult result = await service.InitializeAsync(Ct);
        Assert.Equal(StartupFailure.None, result.Failure);
        return service;
    }

    private static SavedFeedIntent WrittenAsn(string asn = "15169") =>
        new SavedFeedIntent.Written(SavedFeedListTests.AsnPick(asn));

    private static FeedRecordResult Record(IpInfoService service, string key, SavedFeedIntent intent, DateTimeOffset at)
    {
        using IDisposable hold = service.Records.EnterWrite(key);
        return IpInfoService.RecordFeedWrite(service,
            new FeedWriteRecord(key, FeedKind.Asn, FeedChangeCause.PageWrite, at, [FeedType.Fortigate], null, null,
                                null, null, intent));
    }

    private static readonly FeedRecordResult Saved = new(FeedSaveOutcome.Saved, HistoryOutcome.NothingToRecord);

    [Fact]
    public async Task A_written_feed_is_read_back_and_survives_a_new_launch()
    {
        IpInfoService first = await LaunchedServiceAsync();

        Assert.Equal(Saved, Record(first, "AS15169", WrittenAsn(), T));

        SavedFeedsRead read = first.Records.ReadSavedFeeds();
        Assert.True(read.Available);
        SavedFeed feed = Assert.Single(read.Feeds);
        Assert.Equal(("AS15169", FeedKind.Asn, T, false), (feed.Key, feed.Kind, feed.LastWriteUtc, feed.Retired));
        Assert.Equal("15169", feed.Pick.Asn);

        first.Dispose();
        IpInfoService second = await LaunchedServiceAsync();
        SavedFeed again = Assert.Single(second.Records.ReadSavedFeeds().Feeds);
        Assert.Equal(("AS15169", T, false), (again.Key, again.LastWriteUtc, again.Retired));
    }

    [Fact]
    public async Task A_retire_marks_the_saved_feed()
    {
        IpInfoService service = await LaunchedServiceAsync();
        Assert.Equal(Saved, Record(service, "AS15169", WrittenAsn(), T));

        Assert.Equal(Saved, Record(service, "AS15169", new SavedFeedIntent.Retired(), T.AddHours(1)));

        Assert.True(Assert.Single(service.Records.ReadSavedFeeds().Feeds).Retired);
    }

    [Fact]
    public async Task Remove_forgets_the_feed_and_the_second_remove_finds_nothing()
    {
        IpInfoService service = await LaunchedServiceAsync();
        Assert.Equal(Saved, Record(service, "AS15169", WrittenAsn(), T));

        Assert.Equal(SavedFeedsFailure.None, await service.Records.RemoveSavedFeedAsync("AS15169", Ct));
        Assert.Empty(service.Records.ReadSavedFeeds().Feeds);
        Assert.Equal(SavedFeedsFailure.NotFound, await service.Records.RemoveSavedFeedAsync("AS15169", Ct));
    }

    [Fact]
    public async Task Before_the_launch_a_save_fails_and_the_read_is_unavailable()
    {
        IpInfoService service = NewService();

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.StorageError, HistoryOutcome.NothingToRecord),
                     Record(service, "AS15169", WrittenAsn(), T));
        SavedFeedsRead read = service.Records.ReadSavedFeeds();
        Assert.False(read.Available);
        Assert.Empty(read.Feeds);
        Assert.Equal(SavedFeedsFailure.StorageError, await service.Records.RemoveSavedFeedAsync("AS15169", Ct));
    }

    [Fact]
    public async Task A_settings_save_between_two_records_keeps_the_saved_feeds()
    {
        IpInfoService service = await LaunchedServiceAsync();
        Assert.Equal(Saved, Record(service, "AS15169", WrittenAsn(), T));

        Assert.True(await service.SaveSettingsAsync(service.Settings with { ModSecurityFirstRuleId = 20_000 }));
        Assert.Equal(Saved, Record(service, "AS13335", WrittenAsn("13335"), T.AddHours(1)));

        Assert.Equal(["AS13335", "AS15169"],
                     service.Records.ReadSavedFeeds().Feeds.Select(feed => feed.Key).Order(StringComparer.Ordinal));
    }
}
