using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.CloudSearch;

namespace PoorMansFeed.Tests;

/// <summary>Cloud Address Lookup's view model and rows, over a stub search service.</summary>
public sealed class CloudAddressSearchViewModelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset Saved = new(2026, 10, 24, 10, 59, 59, TimeSpan.Zero);

    [Fact]
    public void BeforeAnySearchNothingIsShownAndNothingIsRead()
    {
        var search = new StubSearch();
        using var model = new CloudAddressSearchViewModel(search, new FakeUi());

        Assert.Equal(AppText.CloudSearchInitial, model.SummaryText);
        Assert.Equal("", model.GoogleSourceText);
        Assert.Equal("", model.AzureSourceText);
        Assert.Equal("", model.AwsSourceText);
        Assert.False(model.HasSourceTexts);
        Assert.Empty(model.Rows);
        Assert.False(model.HasRows);
        Assert.Equal("", model.ActionText);
        Assert.Equal(AppText.CloudSearchEmptyInitial, model.EmptyText);
        Assert.Equal(0, search.Calls);
    }

    [Fact]
    public async Task FoundShowsTheRowsAndTheirCount()
    {
        var search = new StubSearch { Next = Result(CloudAddressSearchOutcome.Found, Match("10.0.0.0/8"), Match("10.1.0.0/16")) };
        using var model = Model(search, "10.1.2.3");

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        Assert.Equal("10.1.2.3", search.LastInput);
        Assert.Equal(2, model.Rows.Count);
        Assert.True(model.HasRows);
        Assert.Equal(AppText.CloudSearchFound(2, 2, "10.1.2.3"), model.SummaryText);
        Assert.Equal("2 saved ranges hold 10.1.2.3", model.SummaryText);
    }

    [Fact]
    public async Task OneMatchReadsInTheSingular()
    {
        using var model = Model(new StubSearch { Next = Result(CloudAddressSearchOutcome.Found, Match("10.0.0.0/8")) });

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        Assert.Equal("1 saved range holds 10.1.2.3", model.SummaryText);
    }

    [Fact]
    public async Task MoreThanThreeHundredMatchesShowThreeHundredAndSaySo()
    {
        CloudAddressMatch[] matches = Enumerable.Range(0, 305).Select(i => Match($"10.{i / 256}.{i % 256}.0/24")).ToArray();
        using var model = Model(new StubSearch { Next = Result(CloudAddressSearchOutcome.Found, matches) });

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        Assert.Equal(300, model.Rows.Count);
        Assert.Equal("Showing 300 of 305 saved ranges that hold 10.1.2.3", model.SummaryText);
    }

    [Theory]
    [InlineData(CloudAddressSearchOutcome.NoMatches, "No saved Google Cloud, Azure or AWS range holds 10.1.2.3.")]
    [InlineData(CloudAddressSearchOutcome.InvalidInput, AppText.CloudSearchInvalidInput)]
    [InlineData(CloudAddressSearchOutcome.NotAnAddress, AppText.CloudSearchNotAnAddress)]
    [InlineData(CloudAddressSearchOutcome.NoSavedRanges, AppText.CloudSearchNoSavedRanges)]
    [InlineData(CloudAddressSearchOutcome.Failed, AppText.CloudSearchFailed)]
    public async Task EveryOtherOutcomeHasItsOwnLineAndNoRows(CloudAddressSearchOutcome outcome, string expected)
    {
        // Matches handed with a non-Found outcome still give no rows
        using var model = Model(new StubSearch { Next = Result(outcome, Match("10.0.0.0/8")) });

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        Assert.Equal(expected, model.SummaryText);
        Assert.Empty(model.Rows);
        Assert.False(model.HasRows);
        Assert.Equal(AppText.CloudSearchEmptyNone, model.EmptyText);
    }

    [Fact]
    public void TheSevenOutcomeLinesDiffer()
    {
        string[] lines =
        [
            AppText.CloudSearchFound(1, 1, "10.1.2.3"), AppText.CloudSearchNoMatches("10.1.2.3", []),
            AppText.CloudSearchInvalidInput, AppText.CloudSearchNotAnAddress, AppText.CloudSearchNoSavedRanges,
            AppText.CloudSearchNoReadableRanges, AppText.CloudSearchFailed,
        ];

        Assert.Equal(lines.Length, lines.Distinct().Count());
    }

    [Theory]
    [InlineData(SavedRangesState.Read, SavedRangesState.Unusable, SavedRangesState.NotSaved, "No saved Google Cloud range holds 10.1.2.3.")]
    [InlineData(SavedRangesState.Read, SavedRangesState.NotSaved, SavedRangesState.Read, "No saved Google Cloud or AWS range holds 10.1.2.3.")]
    [InlineData(SavedRangesState.Unusable, SavedRangesState.Read, SavedRangesState.Read, "No saved Azure or AWS range holds 10.1.2.3.")]
    public async Task NoMatchesNamesOnlyTheProvidersWhoseRangesWereRead(SavedRangesState google, SavedRangesState azure,
                                                                       SavedRangesState aws, string expected)
    {
        using var model = Model(new StubSearch { Next = Result(CloudAddressSearchOutcome.NoMatches, Sources(google, azure, aws), []) });

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        Assert.Equal(expected, model.SummaryText);
    }

    [Theory]
    [InlineData(SavedRangesState.NotSaved, SavedRangesState.NotSaved, SavedRangesState.NotSaved, AppText.CloudSearchNoSavedRanges)]
    [InlineData(SavedRangesState.NotSaved, SavedRangesState.NotSaved, SavedRangesState.Unusable, AppText.CloudSearchNoReadableRanges)]
    [InlineData(SavedRangesState.Unusable, SavedRangesState.Unusable, SavedRangesState.Unusable, AppText.CloudSearchNoReadableRanges)]
    public async Task NoSavedRangesSaysNothingIsSavedOnlyWhenNoFileIsThere(SavedRangesState google, SavedRangesState azure,
                                                                          SavedRangesState aws, string expected)
    {
        using var model = Model(new StubSearch { Next = Result(CloudAddressSearchOutcome.NoSavedRanges, Sources(google, azure, aws), []) });

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        Assert.Equal(expected, model.SummaryText);
    }

    [Fact]
    public async Task AnAnswerAndAFailedCopyRaiseEveryPropertyTheViewBinds()
    {
        var ui = new FakeUi { FailClipboard = true };
        using var model = Model(new StubSearch { Next = Result(CloudAddressSearchOutcome.Found, Match("10.0.0.0/8")) }, ui: ui);
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);
        await model.CopyNetworkCommand.ExecuteAsync(model.Rows[0]).WaitAsync(Wait);

        foreach (string name in new[]
                 {
                     nameof(model.IsWorking), nameof(model.SummaryText), nameof(model.Rows), nameof(model.HasRows),
                     nameof(model.EmptyText), nameof(model.GoogleSourceText), nameof(model.AzureSourceText),
                     nameof(model.AwsSourceText), nameof(model.HasSourceTexts), nameof(model.ActionText),
                     nameof(model.HasActionText),
                 })
            Assert.Contains(name, changed);
    }

    [Fact]
    public async Task AFailureAfterDisposeChangesNothing()
    {
        var held = new TaskCompletionSource<CloudAddressSearchResult>();
        var model = Model(new StubSearch { Held = held });
        Task press = model.SearchCommand.ExecuteAsync(null);
        model.Dispose();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        held.SetException(new InvalidOperationException("boom"));
        await press.WaitAsync(Wait);

        Assert.Empty(changed);
        Assert.Equal(AppText.Searching, model.SummaryText);
    }

    [Fact]
    public async Task AThrowingServiceGivesTheFailedLine()
    {
        using var model = Model(new StubSearch { Throw = new InvalidOperationException("boom") });

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        Assert.Equal(AppText.CloudSearchFailed, model.SummaryText);
        Assert.False(model.IsWorking);
        Assert.True(model.SearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task EachSourceStateGivesItsLine()
    {
        var sources = new[]
        {
            new SavedRangesStatus(CloudProvider.GoogleCloud, SavedRangesState.Read, Saved.AddTicks(9_000_000)),
            new SavedRangesStatus(CloudProvider.Azure, SavedRangesState.NotSaved, null),
            new SavedRangesStatus(CloudProvider.Aws, SavedRangesState.Unusable, null),
        };
        using var model = Model(new StubSearch { Next = Result(CloudAddressSearchOutcome.NoMatches, sources, []) });

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        Assert.Equal("Google Cloud: saved ranges downloaded 2026-10-24 10:59 UTC", model.GoogleSourceText);
        Assert.Equal("Azure: no ranges saved yet. Download them on Azure Lookup.", model.AzureSourceText);
        Assert.Equal("AWS: the saved ranges couldn't be read. Search again, or restart and open AWS Lookup.",
                     model.AwsSourceText);
        Assert.True(model.HasSourceTexts);
    }

    [Theory]
    [InlineData(CloudProvider.GoogleCloud, "Google Cloud", AppText.GoogleCloudTitle)]
    [InlineData(CloudProvider.Azure, "Azure", AppText.AzureTitle)]
    [InlineData(CloudProvider.Aws, "AWS", AppText.AwsTitle)]
    public void EveryProviderHasEveryLineAndNoneNamesReload(CloudProvider provider, string name, string page)
    {
        string read = AppText.CloudSearchSource(new SavedRangesStatus(provider, SavedRangesState.Read, Saved));
        string notSaved = AppText.CloudSearchSource(new SavedRangesStatus(provider, SavedRangesState.NotSaved, null));
        string unusable = AppText.CloudSearchSource(new SavedRangesStatus(provider, SavedRangesState.Unusable, null));

        Assert.Equal(name + ": saved ranges downloaded 2026-10-24 10:59 UTC", read);
        Assert.Equal(name + ": no ranges saved yet. Download them on " + page + ".", notSaved);
        Assert.Contains("Search again", unusable);
        Assert.EndsWith("open " + page + ".", unusable);
        foreach (string line in new[] { read, notSaved, unusable })
        {
            Assert.StartsWith(name + ":", line);
            Assert.DoesNotContain("reload", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("checked", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("updated", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ASavedTimeInAnotherOffsetIsShownInUtc()
    {
        var at = new DateTimeOffset(2026, 10, 24, 18, 59, 59, TimeSpan.FromHours(8)).AddTicks(9_999_999);

        Assert.EndsWith("2026-10-24 10:59 UTC",
            AppText.CloudSearchSource(new SavedRangesStatus(CloudProvider.Aws, SavedRangesState.Read, at)));
    }

    [Theory]
    [InlineData(CloudAddressSearchOutcome.InvalidInput)]
    [InlineData(CloudAddressSearchOutcome.NotAnAddress)]
    public async Task ASearchThatReadNothingKeepsTheSourceLines(CloudAddressSearchOutcome outcome)
    {
        var search = new StubSearch { Next = Result(CloudAddressSearchOutcome.NoMatches, AllRead(), []) };
        using var model = Model(search);
        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);
        string google = model.GoogleSourceText, azure = model.AzureSourceText, aws = model.AwsSourceText;

        search.Next = new CloudAddressSearchResult(outcome, null, [], []);
        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        Assert.NotEqual("", google);
        Assert.Equal(google, model.GoogleSourceText);
        Assert.Equal(azure, model.AzureSourceText);
        Assert.Equal(aws, model.AwsSourceText);
    }

    [Fact]
    public async Task AHeldSearchShowsSearchingAndKeepsEverythingElse()
    {
        var search = new StubSearch { Next = Result(CloudAddressSearchOutcome.Found, AllRead(), [Match("10.0.0.0/8")]) };
        var ui = new FakeUi { FailClipboard = true };
        using var model = Model(search, ui: ui);
        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);
        await model.CopyNetworkCommand.ExecuteAsync(model.Rows[0]).WaitAsync(Wait);
        Assert.Equal(AppText.NetworkCopyFailed, model.ActionText);
        IReadOnlyList<CloudAddressRow> rows = model.Rows;
        string google = model.GoogleSourceText;
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var held = new TaskCompletionSource<CloudAddressSearchResult>();
        search.Held = held;
        Task press = model.SearchCommand.ExecuteAsync(null);

        Assert.True(model.IsWorking);
        Assert.Equal(AppText.Searching, model.SummaryText);
        Assert.False(model.SearchCommand.CanExecute(null));
        Assert.Same(rows, model.Rows);
        Assert.True(model.HasRows);
        Assert.Equal(google, model.GoogleSourceText);
        Assert.Equal("", model.ActionText);
        Assert.DoesNotContain(nameof(model.HasRows), changed);
        Assert.DoesNotContain(nameof(model.Rows), changed);

        held.SetResult(Result(CloudAddressSearchOutcome.NoMatches, AllRead(), []));
        await press.WaitAsync(Wait);

        Assert.False(model.IsWorking);
        Assert.True(model.SearchCommand.CanExecute(null));
        Assert.Equal("No saved Google Cloud, Azure or AWS range holds 10.1.2.3.", model.SummaryText);
    }

    [Fact]
    public async Task EveryProviderInTheEnumGetsALineOfItsOwn()
    {
        CloudProvider[] providers = Enum.GetValues<CloudProvider>();
        SavedRangesStatus[] sources =
            providers.Select(provider => new SavedRangesStatus(provider, SavedRangesState.Read, Saved)).ToArray();
        using var model = Model(new StubSearch { Next = Result(CloudAddressSearchOutcome.NoMatches, sources, []) });

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        Assert.Equal(sources.Select(AppText.CloudSearchSource),
                     [model.GoogleSourceText, model.AzureSourceText, model.AwsSourceText]);
    }

    [Fact]
    public async Task AShowBeforeAnySearchFillsTheSourceLinesAndChangesNothingElse()
    {
        var search = new StubSearch
        {
            Sources = Sources(SavedRangesState.Read, SavedRangesState.NotSaved, SavedRangesState.Unusable),
        };
        using var model = Model(search, query: "10.1.2.3");

        await model.RefreshAsync().WaitAsync(Wait);

        Assert.Equal(1, search.SourceReads);
        Assert.Equal(0, search.Calls);
        Assert.Equal("Google Cloud: saved ranges downloaded 2026-10-24 10:59 UTC", model.GoogleSourceText);
        Assert.Equal("Azure: no ranges saved yet. Download them on Azure Lookup.", model.AzureSourceText);
        Assert.Equal("AWS: the saved ranges couldn't be read. Search again, or restart and open AWS Lookup.",
                     model.AwsSourceText);
        Assert.True(model.HasSourceTexts);
        Assert.Equal(AppText.CloudSearchInitial, model.SummaryText);
        Assert.Equal(AppText.CloudSearchEmptyInitial, model.EmptyText);
        Assert.False(model.HasRows);
        Assert.False(model.IsWorking);
    }

    [Fact]
    public async Task AShowAfterASearchThatReadTheFilesSearchesAgainInsteadOfReadingTheSources()
    {
        var search = new StubSearch { Next = Result(CloudAddressSearchOutcome.NoMatches, AllRead(), []) };
        using var model = Model(search);
        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        await model.RefreshAsync().WaitAsync(Wait);

        Assert.Equal(2, search.Calls);
        Assert.Equal(0, search.SourceReads);
    }

    [Fact]
    public async Task AFailedSourceReadKeepsTheLinesItHad()
    {
        var search = new StubSearch { Sources = AllRead() };
        using var model = Model(search);
        await model.RefreshAsync().WaitAsync(Wait);
        string google = model.GoogleSourceText;

        search.Sources = [];
        await model.RefreshAsync().WaitAsync(Wait);
        search.SourcesThrow = new IOException();
        await model.RefreshAsync().WaitAsync(Wait);

        Assert.Equal(3, search.SourceReads);
        Assert.Equal(google, model.GoogleSourceText);
        Assert.Equal(AppText.CloudSearchInitial, model.SummaryText);
    }

    [Fact]
    public async Task ASourceReadThatLandsAfterASearchStartedLeavesTheSearchsLines()
    {
        var held = new TaskCompletionSource<IReadOnlyList<SavedRangesStatus>>();
        var search = new StubSearch
        {
            HeldSources = held,
            Next = Result(CloudAddressSearchOutcome.NoMatches, AllRead(), []),
        };
        using var model = Model(search);
        Task show = model.RefreshAsync();

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);
        string afterSearch = model.AzureSourceText;
        held.SetResult(Sources(SavedRangesState.NotSaved, SavedRangesState.NotSaved, SavedRangesState.NotSaved));
        await show.WaitAsync(Wait);

        Assert.Equal(afterSearch, model.AzureSourceText);
        Assert.Equal("Azure: saved ranges downloaded 2026-10-24 10:59 UTC", model.AzureSourceText);
    }

    [Fact]
    public async Task ASourceReadThatLandsAfterASearchThatReadNoFilesStillFillsTheLines()
    {
        var held = new TaskCompletionSource<IReadOnlyList<SavedRangesStatus>>();
        var search = new StubSearch
        {
            HeldSources = held,
            Next = Result(CloudAddressSearchOutcome.InvalidInput, [], []),
        };
        using var model = Model(search);
        Task show = model.RefreshAsync();

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);
        Assert.False(model.HasSourceTexts);
        held.SetResult(AllRead());
        await show.WaitAsync(Wait);

        Assert.True(model.HasSourceTexts);
        Assert.Equal("Azure: saved ranges downloaded 2026-10-24 10:59 UTC", model.AzureSourceText);
    }

    [Fact]
    public async Task ASourceReadThatLandsAfterDisposeChangesNothing()
    {
        var held = new TaskCompletionSource<IReadOnlyList<SavedRangesStatus>>();
        var search = new StubSearch { HeldSources = held };
        var model = Model(search);
        Task show = model.RefreshAsync();

        model.Dispose();
        await show.WaitAsync(Wait);

        Assert.Equal("", model.GoogleSourceText);
    }

    [Fact]
    public async Task RefreshSearchesTheLastAddressThatReadTheFilesAndNotTheBox()
    {
        SavedRangesStatus[] none = Sources(SavedRangesState.NotSaved, SavedRangesState.NotSaved, SavedRangesState.NotSaved);
        var search = new StubSearch { Next = Result(CloudAddressSearchOutcome.NoSavedRanges, none, []) };
        using var model = Model(search, query: "10.1.2.3");
        await model.RefreshAsync().WaitAsync(Wait);
        Assert.Equal(0, search.Calls);

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);
        Assert.Equal(AppText.CloudSearchNoSavedRanges, model.SummaryText);
        model.Query = "9.9.9.9";
        search.Next = Result(CloudAddressSearchOutcome.Found, AllRead(), [Match("10.0.0.0/8")]);

        await model.RefreshAsync().WaitAsync(Wait);

        Assert.Equal(2, search.Calls);
        Assert.Equal("10.1.2.3", search.LastInput);
        Assert.Equal("9.9.9.9", model.Query);
        Assert.True(model.HasRows);
        Assert.Equal("Azure: saved ranges downloaded 2026-10-24 10:59 UTC", model.AzureSourceText);
        Assert.False(model.IsWorking);
    }

    [Theory]
    [InlineData(CloudAddressSearchOutcome.InvalidInput)]
    [InlineData(CloudAddressSearchOutcome.NotAnAddress)]
    [InlineData(CloudAddressSearchOutcome.Failed)]
    public async Task RefreshSearchesNothingAfterAnAnswerThatReadNoFile(CloudAddressSearchOutcome outcome)
    {
        var search = new StubSearch { Next = Result(CloudAddressSearchOutcome.NoMatches, AllRead(), []) };
        using var model = Model(search);
        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);
        search.Next = new CloudAddressSearchResult(outcome, null, [], []);
        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        await model.RefreshAsync().WaitAsync(Wait);

        Assert.Equal(2, search.Calls);
    }

    [Fact]
    public async Task RefreshSearchesNothingAfterAThrowingSearch()
    {
        var search = new StubSearch { Next = Result(CloudAddressSearchOutcome.NoMatches, AllRead(), []) };
        using var model = Model(search);
        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);
        search.Throw = new IOException();
        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);
        Assert.Equal(AppText.CloudSearchFailed, model.SummaryText);

        await model.RefreshAsync().WaitAsync(Wait);

        Assert.Equal(2, search.Calls);
    }

    [Fact]
    public async Task RefreshSearchesNothingWhileASearchIsOutOrOnceDisposed()
    {
        var search = new StubSearch { Next = Result(CloudAddressSearchOutcome.NoMatches, AllRead(), []) };
        var model = Model(search);
        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);
        var held = new TaskCompletionSource<CloudAddressSearchResult>();
        search.Held = held;
        Task press = model.SearchCommand.ExecuteAsync(null);

        await model.RefreshAsync().WaitAsync(Wait);
        Assert.Equal(2, search.Calls);
        held.SetResult(Result(CloudAddressSearchOutcome.NoMatches, AllRead(), []));
        await press.WaitAsync(Wait);

        model.Dispose();
        await model.RefreshAsync().WaitAsync(Wait);
        Assert.Equal(2, search.Calls);
        Assert.False(model.IsWorking);
    }

    [Fact]
    public void SearchIsOffForABlankQueryAndOnOnceTextIsTyped()
    {
        using var model = new CloudAddressSearchViewModel(new StubSearch(), new FakeUi());
        int raised = 0;
        model.SearchCommand.CanExecuteChanged += (_, _) => raised++;

        Assert.False(model.SearchCommand.CanExecute(null));
        model.Query = "   ";
        Assert.False(model.SearchCommand.CanExecute(null));

        raised = 0;
        model.Query = "10.1.2.3";
        Assert.True(raised > 0);
        Assert.True(model.SearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task ABlankQueryPressSearchesNothing()
    {
        var search = new StubSearch();
        using var model = Model(search, " ");

        await model.SearchCommand.ExecuteAsync(null).WaitAsync(Wait);

        Assert.Equal(0, search.Calls);
        Assert.Equal(AppText.CloudSearchInitial, model.SummaryText);
    }

    [Fact]
    public void DisposeTurnsTheCommandsOffAndSaysSo()
    {
        var model = Model(new StubSearch());
        CloudAddressRow row = CloudAddressRow.From(Match("10.0.0.0/8"));
        int raised = 0;
        model.SearchCommand.CanExecuteChanged += (_, _) => raised++;
        Assert.True(model.SearchCommand.CanExecute(null));
        Assert.True(model.CopyNetworkCommand.CanExecute(row));

        model.Dispose();

        Assert.True(raised > 0);
        Assert.False(model.SearchCommand.CanExecute(null));
        Assert.False(model.CopyNetworkCommand.CanExecute(row));
    }

    [Fact]
    public async Task DisposeDuringAHeldSearchCancelsItAndAppliesNothing()
    {
        var held = new TaskCompletionSource<CloudAddressSearchResult>();
        var search = new StubSearch { Held = held };
        var model = Model(search);
        Task press = model.SearchCommand.ExecuteAsync(null);

        model.Dispose();
        Assert.True(search.LastToken.IsCancellationRequested);
        held.SetResult(Result(CloudAddressSearchOutcome.Found, AllRead(), [Match("10.0.0.0/8")]));
        await press.WaitAsync(Wait);

        Assert.Empty(model.Rows);
        Assert.Equal("", model.GoogleSourceText);
        Assert.Equal(AppText.Searching, model.SummaryText);
    }

    [Fact]
    public async Task DisposeDuringASearchThatHonoursItsTokenEndsQuietly()
    {
        var search = new StubSearch { Held = new TaskCompletionSource<CloudAddressSearchResult>(), Cancels = true };
        var model = Model(search);
        Task press = model.SearchCommand.ExecuteAsync(null);

        model.Dispose();
        await press.WaitAsync(Wait);

        Assert.Empty(model.Rows);
        Assert.Equal(AppText.Searching, model.SummaryText);
    }

    [Fact]
    public void ARowCarriesItsFields()
    {
        CloudAddressRow row = CloudAddressRow.From(
            new CloudAddressMatch(CloudProvider.Aws, "EC2", "us-east-1", "us-east-1-iah-1", Cidr("3.5.0.0/16"), false));

        Assert.Equal("3.5.0.0/16", row.NetworkText);
        Assert.Equal("AWS", row.ProviderText);
        Assert.Equal("EC2", row.ServiceText);
        Assert.Equal("us-east-1", row.RegionText);
        Assert.Equal("us-east-1-iah-1", row.ScopeText);
        Assert.Equal("3.5.0.0/16, AWS, EC2, us-east-1, us-east-1-iah-1", row.AccessibleText);
    }

    [Fact]
    public void AnAzureBaseTagShowsAllRegionsAndASuffixShowsAsGiven()
    {
        CloudAddressRow all = CloudAddressRow.From(
            new CloudAddressMatch(CloudProvider.Azure, "AzureCloud", "", null, Cidr("20.0.0.0/8"), true));
        CloudAddressRow suffix = CloudAddressRow.From(
            new CloudAddressMatch(CloudProvider.Azure, "AzureFrontDoor", "Frontend", null, Cidr("20.0.0.0/16"), false));

        Assert.Equal(AppText.AzureAllRegions, all.RegionText);
        Assert.Equal("Azure", all.ProviderText);
        Assert.Equal("", all.ScopeText);
        Assert.Equal("20.0.0.0/8, Azure, AzureCloud, All regions", all.AccessibleText);
        Assert.Equal("Frontend", suffix.RegionText);
    }

    [Fact]
    public void AnAwsGlobalRegionShowsGlobalAndGooglesScopeIdShowsAsGiven()
    {
        CloudAddressRow aws = CloudAddressRow.From(
            new CloudAddressMatch(CloudProvider.Aws, "CLOUDFRONT", "GLOBAL", "GLOBAL", Cidr("13.32.0.0/15"), false));
        CloudAddressRow google = CloudAddressRow.From(
            new CloudAddressMatch(CloudProvider.GoogleCloud, "Google Cloud", "us-east1", null, Cidr("34.23.0.0/16"), false));
        CloudAddressRow googleEmpty = CloudAddressRow.From(
            new CloudAddressMatch(CloudProvider.GoogleCloud, "Google Cloud", "", null, Cidr("34.0.0.0/16"), false));

        Assert.Equal(AppText.AwsGlobalRegion, aws.RegionText);
        Assert.Equal("us-east1", google.RegionText);
        Assert.Equal("Google Cloud", google.ProviderText);
        Assert.Equal("", googleEmpty.RegionText);
    }

    [Fact]
    public async Task CopyNetworkCopiesTheRowsNetwork()
    {
        var ui = new FakeUi();
        using var model = Model(new StubSearch(), ui: ui);

        await model.CopyNetworkCommand.ExecuteAsync(CloudAddressRow.From(Match("10.0.0.0/8"))).WaitAsync(Wait);

        Assert.Equal("10.0.0.0/8", ui.ClipboardText);
        Assert.Equal("", model.ActionText);
    }

    [Fact]
    public async Task AFailedCopySaysSoAndALaterSuccessClearsIt()
    {
        var ui = new FakeUi { FailClipboard = true };
        using var model = Model(new StubSearch(), ui: ui);
        CloudAddressRow row = CloudAddressRow.From(Match("10.0.0.0/8"));

        await model.CopyNetworkCommand.ExecuteAsync(row).WaitAsync(Wait);
        Assert.Equal(AppText.NetworkCopyFailed, model.ActionText);
        Assert.True(model.HasActionText);

        ui.FailClipboard = false;
        await model.CopyNetworkCommand.ExecuteAsync(row).WaitAsync(Wait);
        Assert.Equal("", model.ActionText);
        Assert.False(model.HasActionText);
    }

    [Fact]
    public async Task CopyStaysOnWhileASearchIsHeld()
    {
        var held = new TaskCompletionSource<CloudAddressSearchResult>();
        var search = new StubSearch { Held = held };
        using var model = Model(search);
        Task press = model.SearchCommand.ExecuteAsync(null);

        Assert.True(model.CopyNetworkCommand.CanExecute(CloudAddressRow.From(Match("10.0.0.0/8"))));
        Assert.False(model.CopyNetworkCommand.CanExecute(null));

        held.SetResult(Result(CloudAddressSearchOutcome.NoMatches, AllRead(), []));
        await press.WaitAsync(Wait);
    }

    private static CloudAddressSearchViewModel Model(StubSearch search, string query = "10.1.2.3", FakeUi? ui = null) =>
        new(search, ui ?? new FakeUi()) { Query = query };

    private static CloudAddressSearchResult Result(CloudAddressSearchOutcome outcome, params CloudAddressMatch[] matches) =>
        Result(outcome, AllRead(), matches);

    private static CloudAddressSearchResult Result(CloudAddressSearchOutcome outcome, SavedRangesStatus[] sources,
                                                   CloudAddressMatch[] matches)
    {
        Assert.True(SearchQuery.TryParse("10.1.2.3", out SearchQuery? query));
        return new CloudAddressSearchResult(outcome, query, matches, sources);
    }

    private static SavedRangesStatus[] Sources(SavedRangesState google, SavedRangesState azure, SavedRangesState aws) =>
    [
        new(CloudProvider.GoogleCloud, google, google == SavedRangesState.Read ? Saved : null),
        new(CloudProvider.Azure, azure, azure == SavedRangesState.Read ? Saved : null),
        new(CloudProvider.Aws, aws, aws == SavedRangesState.Read ? Saved : null),
    ];

    private static SavedRangesStatus[] AllRead() =>
    [
        new(CloudProvider.GoogleCloud, SavedRangesState.Read, Saved),
        new(CloudProvider.Azure, SavedRangesState.Read, Saved),
        new(CloudProvider.Aws, SavedRangesState.Read, Saved),
    ];

    private static CloudAddressMatch Match(string network) =>
        new(CloudProvider.Aws, "EC2", "us-east-1", null, Cidr(network), false);

    private static CidrBlock Cidr(string text)
    {
        Assert.True(CidrBlock.TryParse(text, out CidrBlock? block));
        return block;
    }

    private sealed class StubSearch : ICloudAddressSearchService
    {
        public CloudAddressSearchResult? Next { get; set; }
        public TaskCompletionSource<CloudAddressSearchResult>? Held { get; set; }
        public Exception? Throw { get; set; }

        // Ends the held search with the token's own cancellation, as the real service does
        public bool Cancels { get; set; }

        public int Calls { get; private set; }
        public string? LastInput { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public IReadOnlyList<SavedRangesStatus> Sources { get; set; } = [];
        public TaskCompletionSource<IReadOnlyList<SavedRangesStatus>>? HeldSources { get; set; }
        public Exception? SourcesThrow { get; set; }
        public int SourceReads { get; private set; }

        public async Task<IReadOnlyList<SavedRangesStatus>> ReadSourcesAsync(CancellationToken ct)
        {
            SourceReads++;
            if (SourcesThrow is not null)
                throw SourcesThrow;
            if (HeldSources is { } held)
            {
                HeldSources = null;
                return await held.Task.WaitAsync(ct);
            }
            return Sources;
        }

        public async Task<CloudAddressSearchResult> SearchAsync(string? input, CancellationToken ct)
        {
            Calls++;
            LastInput = input;
            LastToken = ct;
            if (Throw is not null)
                throw Throw;

            if (Held is { } held)
            {
                Held = null;
                return Cancels ? await held.Task.WaitAsync(ct) : await held.Task;
            }

            return Next ?? new CloudAddressSearchResult(CloudAddressSearchOutcome.NoMatches, null, [], []);
        }
    }
}
