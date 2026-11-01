using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.CloudSearch;
using PoorMansFeed.Core.GoogleCloud;
using Microsoft.Extensions.Time.Testing;
using static PoorMansFeed.Tests.CloudAddressIndexTests;

namespace PoorMansFeed.Tests;

public sealed class CloudAddressSearchServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 15, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly ServiceTempFolder _temp = new();
    private readonly StubGoogle _google = new();
    private readonly StubAzure _azure = new();
    private readonly StubAws _aws = new();

    public CloudAddressSearchServiceTests()
    {
        _google.Next = () => Read(Google(("34.80.1.0/24", "us-central1")));
        _azure.Next = () => Read(Azure(Group("Storage", Tag("Storage", "", "34.80.0.0/16"))));
        _aws.Next = () => Read(Aws(Service("EC2", Region("eu-west-1", ("34.80.1.0/25", "eu-west-1")))));
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("AS15169")]
    [InlineData("15169")]
    public async Task An_Asn_Is_Not_An_Address_And_Reads_Nothing(string input)
    {
        CloudAddressSearchResult result = await StubService().SearchAsync(input, Ct);

        Assert.Equal(CloudAddressSearchOutcome.NotAnAddress, result.Outcome);
        Assert.Equal(SearchKind.Asn, result.Query!.Kind);
        Assert.Empty(result.Matches);
        Assert.Empty(result.Sources);
        Assert.Equal((0, 0, 0), (_google.Calls, _azure.Calls, _aws.Calls));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("34.80.1.0/24")]
    [InlineData("junk")]
    [InlineData("034.80.1.7")]
    public async Task A_Cidr_Or_Junk_Is_Invalid_Input_And_Reads_Nothing(string? input)
    {
        CloudAddressSearchResult result = await StubService().SearchAsync(input, Ct);

        Assert.Equal(CloudAddressSearchOutcome.InvalidInput, result.Outcome);
        Assert.Null(result.Query);
        Assert.Empty(result.Matches);
        Assert.Empty(result.Sources);
        Assert.Equal((0, 0, 0), (_google.Calls, _azure.Calls, _aws.Calls));
    }

    [Fact]
    public async Task The_Fakes_Load_Nothing_For_An_Asn_Or_A_Cidr()
    {
        var http = new HttpClient(new StubHttpHandler((_, _) => throw new InvalidOperationException("No request is expected.")));
        var ipinfo = new FakeIpInfoService();
        var time = new FakeTimeProvider(Now);
        var azure = new FakeAzureFeedService(new AzureFeedService(ipinfo, http, new CoreOptions(), time, _temp.Paths, _temp.ExportFolder));
        var aws = new FakeAwsFeedService(new AwsFeedService(ipinfo, http, new CoreOptions(), time, _temp.Paths, _temp.ExportFolder));
        var service = new CloudAddressSearchService(_temp.Paths, _google, azure, aws);

        await service.SearchAsync("AS15169", Ct);
        await service.SearchAsync("34.80.1.0/24", Ct);

        Assert.Equal((0, 0, 0), (_google.Calls, azure.LoadSavedCalls, aws.LoadSavedCalls));
    }

    [Fact]
    public async Task An_Address_Is_Found_In_Every_Provider_That_Holds_It_Narrowest_First()
    {
        CloudAddressSearchResult result = await StubService().SearchAsync(" 34.80.1.7 ", Ct);

        Assert.Equal(CloudAddressSearchOutcome.Found, result.Outcome);
        Assert.Equal("34.80.1.7", result.Query!.Text);
        Assert.Equal(
        [
            "Aws|EC2|eu-west-1||34.80.1.0/25|False",
            "GoogleCloud|Google Cloud|us-central1||34.80.1.0/24|False",
            "Azure|Storage|||34.80.0.0/16|False",
        ], result.Matches.Select(Describe));
        Assert.Equal(
        [
            new SavedRangesStatus(CloudProvider.GoogleCloud, SavedRangesState.Read, Now),
            new SavedRangesStatus(CloudProvider.Azure, SavedRangesState.Read, Now),
            new SavedRangesStatus(CloudProvider.Aws, SavedRangesState.Read, Now),
        ], result.Sources);
    }

    [Fact]
    public async Task An_IPv4_Mapped_Address_Matches_The_IPv4_Ranges()
    {
        CloudAddressSearchResult result = await StubService().SearchAsync("::ffff:34.80.1.7", Ct);

        Assert.Equal(CloudAddressSearchOutcome.Found, result.Outcome);
        Assert.Equal("34.80.1.7", result.Query!.Text);
        Assert.Equal(3, result.Matches.Count);
    }

    [Fact]
    public async Task An_IPv6_Address_Is_Searched()
    {
        _aws.Next = () => Read(Aws(Service("EC2", Region("cn-north-1", ("2406:da00::/32", "cn-north-1")))));

        CloudAddressSearchResult result = await StubService().SearchAsync("2406:da00::1", Ct);

        Assert.Equal(CloudAddressSearchOutcome.Found, result.Outcome);
        Assert.Equal(["Aws|EC2|cn-north-1||2406:da00::/32|False"], result.Matches.Select(Describe));
    }

    [Fact]
    public async Task An_Address_No_Range_Holds_Is_No_Matches()
    {
        CloudAddressSearchResult result = await StubService().SearchAsync("9.9.9.9", Ct);

        Assert.Equal(CloudAddressSearchOutcome.NoMatches, result.Outcome);
        Assert.Empty(result.Matches);
        Assert.Equal(3, result.Sources.Count);
    }

    [Fact]
    public async Task An_Unchanged_Record_Loads_Nothing_Again()
    {
        WriteSavedFiles("one");
        CloudAddressSearchService service = StubService();

        await service.SearchAsync("34.80.1.7", Ct);
        CloudAddressSearchResult second = await service.SearchAsync("34.80.1.7", Ct);

        Assert.Equal(CloudAddressSearchOutcome.Found, second.Outcome);
        Assert.Equal(3, second.Matches.Count);
        Assert.Equal((1, 1, 1), (_google.Calls, _azure.Calls, _aws.Calls));
    }

    [Fact]
    public async Task A_Changed_Record_Loads_Again_And_Answers_From_The_New_Catalogue()
    {
        WriteSavedFiles("one");
        CloudAddressSearchService service = StubService();
        await service.SearchAsync("34.80.1.7", Ct);

        _azure.Next = () => Read(Azure(Group("Sql", Tag("Sql", "", "34.80.0.0/16"))));
        File.WriteAllText(_temp.Paths.AzureServiceTagsSourcePath, "two, longer");
        CloudAddressSearchResult second = await service.SearchAsync("34.80.1.7", Ct);

        Assert.Equal((1, 2, 1), (_google.Calls, _azure.Calls, _aws.Calls));
        Assert.Contains("Azure|Sql|||34.80.0.0/16|False", second.Matches.Select(Describe));
        Assert.DoesNotContain("Azure|Storage|||34.80.0.0/16|False", second.Matches.Select(Describe));
    }

    [Fact]
    public async Task A_Changed_Saved_File_With_Its_Record_Untouched_Loads_Again()
    {
        WriteSavedFiles("one");
        CloudAddressSearchService service = StubService();
        await service.SearchAsync("34.80.1.7", Ct);

        File.WriteAllText(_temp.Paths.GoogleCloudRangesPath, "edited by hand");
        _google.Next = () => Unusable<CloudRangeCatalogue>();
        CloudAddressSearchResult second = await service.SearchAsync("34.80.1.7", Ct);

        Assert.Equal((2, 1, 1), (_google.Calls, _azure.Calls, _aws.Calls));
        Assert.Equal(SavedRangesState.Unusable, second.Sources[0].State);
        Assert.DoesNotContain(second.Matches, match => match.Provider == CloudProvider.GoogleCloud);
    }

    [Fact]
    public async Task A_Record_Rewritten_At_The_Same_Length_With_A_New_Write_Time_Loads_Again()
    {
        WriteSavedFiles("one");
        CloudAddressSearchService service = StubService();
        await service.SearchAsync("34.80.1.7", Ct);

        DateTime written = File.GetLastWriteTimeUtc(_temp.Paths.AzureServiceTagsSourcePath);
        File.WriteAllText(_temp.Paths.AzureServiceTagsSourcePath, "two");
        File.SetLastWriteTimeUtc(_temp.Paths.AzureServiceTagsSourcePath, written.AddMinutes(-5));
        await service.SearchAsync("34.80.1.7", Ct);

        Assert.Equal((1, 2, 1), (_google.Calls, _azure.Calls, _aws.Calls));
    }

    [Fact]
    public async Task A_Save_Landing_During_A_Load_Makes_The_Next_Search_Load_Again()
    {
        WriteSavedFiles("one");
        _aws.NextAsync = _ =>
        {
            File.WriteAllText(_temp.Paths.AwsIpRangesSourcePath, "saved while loading");
            return Task.FromResult(Read(Aws(Service("EC2", Region("eu-west-1", ("34.80.1.0/25", "eu-west-1"))))));
        };
        CloudAddressSearchService service = StubService();

        await service.SearchAsync("34.80.1.7", Ct);
        await service.SearchAsync("34.80.1.7", Ct);

        Assert.Equal((1, 1, 2), (_google.Calls, _azure.Calls, _aws.Calls));
    }

    [Fact]
    public async Task Two_Searches_At_Once_Load_Each_Provider_Once()
    {
        WriteSavedFiles("one");
        var held = new TaskCompletionSource<SavedRanges<CloudRangeCatalogue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _google.NextAsync = _ => held.Task;
        CloudAddressSearchService service = StubService();

        Task<CloudAddressSearchResult> first = service.SearchAsync("34.80.1.7", Ct);
        Task<CloudAddressSearchResult> second = service.SearchAsync("34.80.1.7", Ct);
        Assert.Equal(1, _google.Calls);
        Assert.False(second.IsCompleted);

        held.SetResult(Read(Google(("34.80.1.0/24", "us-central1"))));
        CloudAddressSearchResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.Equal(CloudAddressSearchOutcome.Found, result.Outcome));
        Assert.Equal((1, 1, 1), (_google.Calls, _azure.Calls, _aws.Calls));
    }

    [Fact]
    public async Task A_Caller_Who_Cancels_During_A_Load_Gets_Its_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        _azure.NextAsync = _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        CloudAddressSearchService service = StubService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SearchAsync("34.80.1.7", cts.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal((1, 1, 0), (_google.Calls, _azure.Calls, _aws.Calls));

        _azure.NextAsync = null;
        CloudAddressSearchResult next = await service.SearchAsync("34.80.1.7", Ct).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CloudAddressSearchOutcome.Found, next.Outcome);
    }

    [Fact]
    public async Task After_A_Failed_Search_The_Next_Search_Still_Loads()
    {
        _azure.Next = () => throw new InvalidOperationException("boom");
        CloudAddressSearchService service = StubService();
        CloudAddressSearchResult failed = await service.SearchAsync("34.80.1.7", Ct).WaitAsync(TimeSpan.FromSeconds(5));

        _azure.Next = () => Read(Azure(Group("Storage", Tag("Storage", "", "34.80.0.0/16"))));
        CloudAddressSearchResult second = await service.SearchAsync("34.80.1.7", Ct).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CloudAddressSearchOutcome.Failed, failed.Outcome);
        Assert.Equal(CloudAddressSearchOutcome.Found, second.Outcome);
    }

    [Fact]
    public async Task A_Removed_Record_Loads_Again()
    {
        WriteSavedFiles("one");
        CloudAddressSearchService service = StubService();
        await service.SearchAsync("34.80.1.7", Ct);

        File.Delete(_temp.Paths.GoogleCloudRangesSourcePath);
        _google.Next = () => NotSaved<CloudRangeCatalogue>();
        CloudAddressSearchResult second = await service.SearchAsync("34.80.1.7", Ct);

        Assert.Equal(2, _google.Calls);
        Assert.Equal(SavedRangesState.NotSaved, second.Sources[0].State);
        Assert.DoesNotContain(second.Matches, match => match.Provider == CloudProvider.GoogleCloud);
    }

    [Theory]
    [InlineData(SavedRangesState.NotSaved)]
    [InlineData(SavedRangesState.Unusable)]
    public async Task A_Provider_Not_Read_Is_Loaded_Again_On_The_Next_Search(SavedRangesState state)
    {
        WriteSavedFiles("one");
        _azure.Next = () => new SavedRanges<AzureTagCatalogue>(state, null, null);
        CloudAddressSearchService service = StubService();

        await service.SearchAsync("34.80.1.7", Ct);
        await service.SearchAsync("34.80.1.7", Ct);

        Assert.Equal((1, 2, 1), (_google.Calls, _azure.Calls, _aws.Calls));
    }

    [Fact]
    public async Task A_Provider_Unusable_While_Saving_Answers_From_Its_Read_Catalogue_Next_Time()
    {
        WriteSavedFiles("one");
        var answers = new Queue<SavedRanges<AwsRangeCatalogue>>(
            [Unusable<AwsRangeCatalogue>(), Read(Aws(Service("EC2", Region("eu-west-1", ("34.80.1.0/25", "eu-west-1")))))]);
        _aws.Next = answers.Dequeue;
        CloudAddressSearchService service = StubService();

        CloudAddressSearchResult first = await service.SearchAsync("34.80.1.7", Ct);
        CloudAddressSearchResult second = await service.SearchAsync("34.80.1.7", Ct);
        CloudAddressSearchResult third = await service.SearchAsync("34.80.1.7", Ct);

        Assert.Equal(SavedRangesState.Unusable, first.Sources[2].State);
        Assert.DoesNotContain(first.Matches, match => match.Provider == CloudProvider.Aws);
        Assert.Equal(SavedRangesState.Read, second.Sources[2].State);
        Assert.Contains("Aws|EC2|eu-west-1||34.80.1.0/25|False", second.Matches.Select(Describe));
        Assert.Equal(second.Matches.Select(Describe), third.Matches.Select(Describe));
        Assert.Equal(2, _aws.Calls);
    }

    [Fact]
    public async Task A_Read_State_With_No_Catalogue_Is_Unusable_And_Never_Kept()
    {
        WriteSavedFiles("one");
        _google.Next = () => new SavedRanges<CloudRangeCatalogue>(SavedRangesState.Read, null, Now);
        CloudAddressSearchService service = StubService();

        CloudAddressSearchResult first = await service.SearchAsync("34.80.1.7", Ct);
        await service.SearchAsync("34.80.1.7", Ct);

        Assert.Equal(new SavedRangesStatus(CloudProvider.GoogleCloud, SavedRangesState.Unusable, null), first.Sources[0]);
        Assert.Equal(CloudAddressSearchOutcome.Found, first.Outcome);
        Assert.Equal(2, _google.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_Read_Is_Not_Kept_Unless_Both_Saved_Files_Exist(bool dataFile, bool recordFile)
    {
        WriteSavedFiles("one");
        foreach ((string data, string record) in new[]
                 {
                     (_temp.Paths.GoogleCloudRangesPath, _temp.Paths.GoogleCloudRangesSourcePath),
                     (_temp.Paths.AzureServiceTagsPath, _temp.Paths.AzureServiceTagsSourcePath),
                     (_temp.Paths.AwsIpRangesPath, _temp.Paths.AwsIpRangesSourcePath),
                 })
        {
            if (!dataFile)
                File.Delete(data);
            if (!recordFile)
                File.Delete(record);
        }

        CloudAddressSearchService service = StubService();

        await service.SearchAsync("34.80.1.7", Ct);
        await service.SearchAsync("34.80.1.7", Ct);

        Assert.Equal((2, 2, 2), (_google.Calls, _azure.Calls, _aws.Calls));
    }

    [Fact]
    public async Task Without_Paths_Nothing_Is_Kept()
    {
        WriteSavedFiles("one");
        var service = new CloudAddressSearchService(null, _google, _azure, _aws);

        await service.SearchAsync("34.80.1.7", Ct);
        await service.SearchAsync("34.80.1.7", Ct);

        Assert.Equal((2, 2, 2), (_google.Calls, _azure.Calls, _aws.Calls));
    }

    [Fact]
    public async Task One_Provider_Unusable_Still_Searches_The_Others()
    {
        _google.Next = () => Unusable<CloudRangeCatalogue>();

        CloudAddressSearchResult result = await StubService().SearchAsync("34.80.1.7", Ct);

        Assert.Equal(CloudAddressSearchOutcome.Found, result.Outcome);
        Assert.Equal(["Aws|EC2|eu-west-1||34.80.1.0/25|False", "Azure|Storage|||34.80.0.0/16|False"],
            result.Matches.Select(Describe));
        Assert.Equal([SavedRangesState.Unusable, SavedRangesState.Read, SavedRangesState.Read],
            result.Sources.Select(source => source.State));
    }

    [Fact]
    public async Task No_Provider_Read_Is_No_Saved_Ranges_With_The_Three_Sources()
    {
        _google.Next = () => NotSaved<CloudRangeCatalogue>();
        _azure.Next = () => Unusable<AzureTagCatalogue>();
        _aws.Next = () => NotSaved<AwsRangeCatalogue>();

        CloudAddressSearchResult result = await StubService().SearchAsync("34.80.1.7", Ct);

        Assert.Equal(CloudAddressSearchOutcome.NoSavedRanges, result.Outcome);
        Assert.Empty(result.Matches);
        Assert.Equal(
        [
            new SavedRangesStatus(CloudProvider.GoogleCloud, SavedRangesState.NotSaved, null),
            new SavedRangesStatus(CloudProvider.Azure, SavedRangesState.Unusable, null),
            new SavedRangesStatus(CloudProvider.Aws, SavedRangesState.NotSaved, null),
        ], result.Sources);
    }

    [Fact]
    public async Task Reading_The_Sources_Gives_Each_Providers_State_And_Shares_The_Searchs_Kept_Catalogues()
    {
        WriteSavedFiles("one");
        _azure.Next = () => NotSaved<AzureTagCatalogue>();
        CloudAddressSearchService service = StubService();

        IReadOnlyList<SavedRangesStatus> sources = await service.ReadSourcesAsync(Ct);
        CloudAddressSearchResult search = await service.SearchAsync("34.80.1.7", Ct);

        Assert.Equal([CloudProvider.GoogleCloud, CloudProvider.Azure, CloudProvider.Aws], sources.Select(s => s.Provider));
        Assert.Equal([SavedRangesState.Read, SavedRangesState.NotSaved, SavedRangesState.Read], sources.Select(s => s.State));
        Assert.Equal(search.Sources, sources);
        Assert.Equal(2, search.Matches.Count);
        // Read catalogues are kept from the source read; the provider not read is loaded again
        Assert.Equal((1, 2, 1), (_google.Calls, _azure.Calls, _aws.Calls));
    }

    [Fact]
    public async Task Reading_The_Sources_Gives_None_On_An_Unexpected_Exception_And_Lets_The_Callers_Cancel_Escape()
    {
        _azure.Next = () => throw new InvalidOperationException("boom");
        CloudAddressSearchService service = StubService();

        Assert.Empty(await service.ReadSourcesAsync(Ct));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadSourcesAsync(cancelled.Token));
    }

    [Fact]
    public async Task An_Unexpected_Exception_Is_Failed()
    {
        _azure.Next = () => throw new InvalidOperationException("boom");

        CloudAddressSearchResult result = await StubService().SearchAsync("34.80.1.7", Ct);

        Assert.Equal(CloudAddressSearchOutcome.Failed, result.Outcome);
        Assert.Empty(result.Matches);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public async Task A_Cancel_The_Caller_Did_Not_Ask_For_Is_Failed()
    {
        _aws.Next = () => throw new OperationCanceledException();

        CloudAddressSearchResult result = await StubService().SearchAsync("34.80.1.7", Ct);

        Assert.Equal(CloudAddressSearchOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_Cancelled_Caller_Gets_Its_Cancellation()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StubService().SearchAsync("34.80.1.7", new CancellationToken(canceled: true)));
        Assert.Equal((0, 0, 0), (_google.Calls, _azure.Calls, _aws.Calls));
    }

    [Fact]
    public async Task The_Real_Services_Answer_From_Their_Saved_Pairs_And_Load_Once()
    {
        using var http = new HttpClient(new StubHttpHandler(Respond));
        var ipinfo = new FakeIpInfoService();
        var time = new FakeTimeProvider(Now);
        var google = new GoogleCloudFeedService(ipinfo, http, new CoreOptions(), time, _temp.Paths, _temp.ExportFolder);
        var azureInner = new AzureFeedService(ipinfo, http, new CoreOptions(), time, _temp.Paths, _temp.ExportFolder);
        var awsInner = new AwsFeedService(ipinfo, http, new CoreOptions(), time, _temp.Paths, _temp.ExportFolder);
        Assert.Equal(CatalogueFailure.None, (await google.LoadCatalogueAsync(Ct)).Failure);
        await azureInner.CheckForUpdateAsync(null, Ct);
        await awsInner.CheckForUpdateAsync(null, Ct);
        var azure = new FakeAzureFeedService(azureInner);
        var aws = new FakeAwsFeedService(awsInner);
        var service = new CloudAddressSearchService(_temp.Paths, google, azure, aws);

        CloudAddressSearchResult first = await service.SearchAsync("34.80.1.7", Ct);
        CloudAddressSearchResult second = await service.SearchAsync("34.80.1.200", Ct);

        Assert.Equal(CloudAddressSearchOutcome.Found, first.Outcome);
        Assert.Equal(
        [
            "Aws|EC2|eu-west-1||34.80.1.0/25|False",
            "GoogleCloud|Google Cloud|us-central1||34.80.1.0/24|False",
            "Azure|Storage.SoutheastAsia|southeastasia||34.80.0.0/16|False",
            "Azure|Storage|||34.80.0.0/16|False",
        ], first.Matches.Select(Describe));
        Assert.Equal([SavedRangesState.Read, SavedRangesState.Read, SavedRangesState.Read], first.Sources.Select(source => source.State));
        Assert.Equal(
        [
            "GoogleCloud|Google Cloud|us-central1||34.80.1.0/24|False",
            "Azure|Storage.SoutheastAsia|southeastasia||34.80.0.0/16|False",
            "Azure|Storage|||34.80.0.0/16|False",
        ], second.Matches.Select(Describe));
        Assert.Equal((1, 1), (azure.LoadSavedCalls, aws.LoadSavedCalls));
    }

    private CloudAddressSearchService StubService() => new(_temp.Paths, _google, _azure, _aws);

    private void WriteSavedFiles(string text)
    {
        foreach (string path in new[]
                 {
                     _temp.Paths.GoogleCloudRangesPath, _temp.Paths.GoogleCloudRangesSourcePath,
                     _temp.Paths.AzureServiceTagsPath, _temp.Paths.AzureServiceTagsSourcePath,
                     _temp.Paths.AwsIpRangesPath, _temp.Paths.AwsIpRangesSourcePath,
                 })
        {
            File.WriteAllText(path, text);
        }
    }

    private static SavedRanges<T> Read<T>(T catalogue) where T : class => new(SavedRangesState.Read, catalogue, Now);

    private static SavedRanges<T> NotSaved<T>() where T : class => new(SavedRangesState.NotSaved, null, null);

    private static SavedRanges<T> Unusable<T>() where T : class => new(SavedRangesState.Unusable, null, null);

    private static readonly byte[] GoogleFile = Encoding.UTF8.GetBytes("""
        {"syncToken": "1770000000000", "creationTime": "2026-01-15T00:00:00Z", "prefixes": [
          {"ipv4Prefix": "34.80.1.0/24", "service": "Google Cloud", "scope": "us-central1"},
          {"ipv6Prefix": "2600:1900:4000::/44", "service": "Google Cloud", "scope": "us-central1"}]}
        """);

    private static readonly byte[] AzureFile = Encoding.UTF8.GetBytes(
        "{\"changeNumber\":422,\"cloud\":\"Public\",\"values\":[" +
        "{\"name\":\"Storage\",\"id\":\"Storage\",\"properties\":{\"changeNumber\":3,\"region\":\"\",\"addressPrefixes\":[\"34.80.0.0/16\"]}}," +
        "{\"name\":\"Storage.SoutheastAsia\",\"id\":\"Storage.SoutheastAsia\",\"properties\":{\"changeNumber\":2,\"region\":\"southeastasia\",\"addressPrefixes\":[\"34.80.0.0/16\"]}}]}");

    private static readonly byte[] AwsFile = Encoding.UTF8.GetBytes(
        "{\"syncToken\":\"1792136826\",\"createDate\":\"2026-10-16-07-47-06\",\"prefixes\":[" +
        "{\"ip_prefix\":\"34.80.1.0/25\",\"region\":\"eu-west-1\",\"service\":\"EC2\",\"network_border_group\":\"eu-west-1\"}]," +
        "\"ipv6_prefixes\":[]}");

    private const string AzureFileUrl = "https://download.microsoft.com/download/7/1/d/aaaa/ServiceTags_Public_20261012.json";

    private static HttpResponseMessage Respond(HttpRequestMessage request, int call)
    {
        (byte[] body, string type) = request.RequestUri!.Host switch
        {
            "www.gstatic.com" => (GoogleFile, "application/json"),
            "www.microsoft.com" => (Encoding.UTF8.GetBytes(
                "<html><script>window.__DLCDetails__={\"dlcDetailsView\":{\"error\":\"\",\"downloadFile\":[{\"name\":\"n\",\"url\":\""
                + AzureFileUrl + "\"}]}};</script></html>"), "text/html"),
            "download.microsoft.com" => (AzureFile, "application/octet-stream"),
            "ip-ranges.amazonaws.com" => (AwsFile, "application/json"),
            _ => throw new InvalidOperationException("Unexpected host."),
        };
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private abstract class CountingLoader<T> where T : class
    {
        public Func<SavedRanges<T>> Next { get; set; } = () => throw new InvalidOperationException("No answer was scripted.");

        // When set, it answers in place of Next, so a test can act during the load or hold it
        public Func<CancellationToken, Task<SavedRanges<T>>>? NextAsync { get; set; }

        public int Calls => _calls;

        private int _calls;

        public Task<SavedRanges<T>> LoadSavedAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return NextAsync is { } nextAsync ? nextAsync(ct) : Task.FromResult(Next());
        }

        public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) => throw new NotSupportedException();

        public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges,
                                                     CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StubGoogle : CountingLoader<CloudRangeCatalogue>, IGoogleCloudFeedService
    {
        public event EventHandler<CloudSourceUpdated<CloudRangeCatalogue>>? SourceUpdated { add { } remove { } }

        public Task<CatalogueResult> LoadCatalogueAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<CheckResult> CheckAsync(CloudRangeCatalogue catalogue, RegionSelection selection, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubAzure : CountingLoader<AzureTagCatalogue>, IAzureFeedService
    {
        public event EventHandler<CloudSourceUpdated<AzureTagCatalogue>>? SourceUpdated { add { } remove { } }

        public Task<AzureTagCatalogue?> LoadCachedAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<AzureLoadResult> CheckForUpdateAsync(AzureTagCatalogue? current, CancellationToken ct) => throw new NotSupportedException();

        public Task<CheckResult> CheckAsync(AzureTagCatalogue catalogue, AzureTagSelection selection, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubAws : CountingLoader<AwsRangeCatalogue>, IAwsFeedService
    {
        public event EventHandler<CloudSourceUpdated<AwsRangeCatalogue>>? SourceUpdated { add { } remove { } }

        public Task<AwsRangeCatalogue?> LoadCachedAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<AwsLoadResult> CheckForUpdateAsync(AwsRangeCatalogue? current, CancellationToken ct) => throw new NotSupportedException();

        public Task<CheckResult> CheckAsync(AwsRangeCatalogue catalogue, AwsSelection selection, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
