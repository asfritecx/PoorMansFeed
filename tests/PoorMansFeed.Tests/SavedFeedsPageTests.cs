using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PoorMansFeed.Core;
using PoorMansFeed.Core.History;
using static PoorMansFeed.Tests.SavedFeedsTextTests;

namespace PoorMansFeed.Tests;

/// <summary>The Saved feeds page in the real main window, over the scripted saved feeds service.</summary>
public sealed partial class MainWindowTests
{
    private static readonly DateTimeOffset SavedFeedsNow = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly SavedFeed SavedAsn = Feed("AS64500", FeedKind.Asn, Pick(asn: "64500"));
    private static readonly SavedFeed SavedAws = Feed("AWS_EC2_all", FeedKind.Aws, Pick(awsService: "EC2"));

    private static HistoryRow SavedFeedsChange(string key, string entry, bool added = true) =>
        new(SavedFeedsNow, key, FeedKind.Asn, FeedChangeCause.PageWrite, false, null, added, entry,
            added ? FeedRemovalReason.NotTold : FeedRemovalReason.SourceDropped, FeedFileState.Read, null, null);

    private static HistoryRead SavedFeedsHistory(params HistoryRow[] rows) =>
        new(HistoryState.Usable, rows, rows.Length, rows.Select(row => row.FeedKey).Distinct().Order().ToList(), false);

    // On a flow of its own: a flow that holds a feed lock can't enter another
    private static IDisposable HoldWrite(IFeedRecords records, string key) =>
        Task.Run(() => records.EnterWrite(key)).GetAwaiter().GetResult();

    private static WorkGate StartedGate()
    {
        var gate = new WorkGate();
        gate.SetStarted(true);
        return gate;
    }

    private static async Task<(MainWindow Window, SavedFeedsViewModel Page, WorkGate Gate)> ShowSavedFeedsAsync(
        FakeSavedFeedsService feeds, FakeUi? ui = null, IFeedRecords? records = null, string item = "NavSavedFeeds")
    {
        var vm = await StartAsync(WithDataset());
        WorkGate gate = StartedGate();
        ui ??= new FakeUi();
        var page = new SavedFeedsViewModel(feeds, ui, gate, TimeProvider.System);
        var window = new MainWindow();
        window.AttachViewModel(vm, savedFeeds: page, records: records, ui: ui, gate: gate);
        window.Show();
        Navigate(window, item);
        return (window, page, gate);
    }

    // The line's only scroller is the page's, and with the page scrolled to its end the line is inside its viewport
    private static void AssertInView(MainWindow window, string line)
    {
        ScrollViewer scroll = window.FindControl<ScrollViewer>("PageScroll")!;
        TextBlock text = window.FindControl<TextBlock>(line)!;
        Assert.Same(scroll, text.FindAncestorOfType<ScrollViewer>());
        scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        Settle(window);
        Rect bounds = new(text.TranslatePoint(new Point(0, 0), scroll)!.Value, text.Bounds.Size);
        Assert.True(bounds.Height >= 36 && bounds.Top >= 0 && bounds.Bottom <= scroll.Viewport.Height + 0.5,
            $"{line} is at {bounds} in a {scroll.Viewport.Height} px viewport at {window.Bounds.Size}.");
    }

    private static List<Border> RealisedRows(MainWindow window, string list) =>
        window.FindControl<ItemsControl>(list)!.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("row")).ToList();

    private static List<string?> TextsOf(Visual visual) =>
        visual.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();

    // The first `onFirstLine` texts share one line and the text `secondLineStart` starts the line below it
    private static void AssertTwoLines(Border row, int onFirstLine, string secondLineStart)
    {
        List<TextBlock> texts = row.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.FindAncestorOfType<Button>() is null).ToList();
        double bottom = texts[0].TranslatePoint(default, row)!.Value.Y + texts[0].Bounds.Height;
        Assert.All(texts.Take(onFirstLine), text => Assert.True(text.TranslatePoint(default, row)!.Value.Y < bottom,
            $"\"{text.Text}\" isn't on the first line."));
        TextBlock second = texts.Single(text => text.Text == secondLineStart);
        Assert.True(second.TranslatePoint(default, row)!.Value.Y >= bottom, $"\"{secondLineStart}\" isn't below the first line.");
    }

    private static Button RowButton(Border row, string name) =>
        row.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);

    [AvaloniaFact]
    public async Task ChoosingSavedFeedsShowsOnlyItsPageAndReadsItOnEachShow()
    {
        var feeds = new FakeSavedFeedsService { Saved = new SavedFeedsRead(true, [SavedAsn]) };
        (MainWindow window, SavedFeedsViewModel page, _) = await ShowSavedFeedsAsync(feeds);
        try
        {
            AssertShowing(window, "NavSavedFeeds");
            Assert.Equal(1, feeds.SavedReads);
            Assert.Single(feeds.Queries);
            Assert.Single(page.Feeds);

            Navigate(window, "NavLookup");
            Navigate(window, "NavSavedFeeds");
            Assert.Equal(2, feeds.SavedReads);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ChoosingChangeHistoryShowsOnlyItsPageAndReadsItOnEachShow()
    {
        var feeds = new FakeSavedFeedsService
        {
            Saved = new SavedFeedsRead(true, [SavedAsn]),
            History = SavedFeedsHistory(SavedFeedsChange("AS64500", "203.0.113.0/24")),
        };
        (MainWindow window, SavedFeedsViewModel page, _) = await ShowSavedFeedsAsync(feeds, item: "NavChangeHistory");
        try
        {
            AssertShowing(window, "NavChangeHistory");
            Assert.Equal(1, feeds.SavedReads);
            Assert.Single(feeds.Queries);
            Assert.Single(page.HistoryRows);
            Assert.Same(page, window.FindControl<Control>("ChangeHistoryPage")!.DataContext);
            Assert.Same(page, window.FindControl<Control>("ChangeHistoryHeading")!.DataContext);

            Navigate(window, "NavSavedFeeds");
            AssertShowing(window, "NavSavedFeeds");
            Assert.Equal(2, feeds.SavedReads);
            Navigate(window, "NavChangeHistory");
            AssertShowing(window, "NavChangeHistory");
            Assert.Equal(3, feeds.SavedReads);
            Assert.Equal(3, feeds.Queries.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheSavedFeedsListSwapsItsEmptyLineForTheRows()
    {
        var feeds = new FakeSavedFeedsService();
        (MainWindow window, SavedFeedsViewModel page, _) = await ShowSavedFeedsAsync(feeds);
        try
        {
            Assert.True(window.FindControl<TextBlock>("SavedFeedsEmpty")!.IsVisible);
            Assert.False(window.FindControl<ScrollViewer>("SavedFeedsScroll")!.IsVisible);
            Assert.Equal(AppText.SavedFeedsEmpty, window.FindControl<TextBlock>("SavedFeedsEmpty")!.Text);

            feeds.Saved = new SavedFeedsRead(true, [SavedAsn, SavedAws]);
            feeds.AsNames[64500] = "EXAMPLE-NET";
            Navigate(window, "NavLookup");
            Navigate(window, "NavSavedFeeds");

            Assert.False(window.FindControl<TextBlock>("SavedFeedsEmpty")!.IsVisible);
            Assert.True(window.FindControl<ScrollViewer>("SavedFeedsScroll")!.IsVisible);

            Settle(window);
            List<Border> feedRows = RealisedRows(window, "SavedFeedsList");
            Assert.True(feedRows.Count == 2, $"{feedRows.Count} feed rows, {page.Feeds.Count} feeds.");
            SavedFeedRow aws = page.Feeds[1];
            Assert.Equal(aws.AccessibleText, AutomationProperties.GetName(feedRows[1]));
            // The kind and the state share the name's line, so the second holds only the two times
            Assert.Equal([aws.NameText, aws.KindText, aws.StateText, aws.LastWriteText, aws.LastRefreshText],
                TextsOf(feedRows[1]).Take(5));
            AssertTwoLines(feedRows[1], 3, aws.LastWriteText);
            // An ASN feed's name cell carries the AS name the dataset gave
            Assert.Equal("AS64500" + AppText.Joiner + "EXAMPLE-NET", TextsOf(feedRows[0]).First());
            Assert.Equal(page.FeedsSummaryText, window.FindControl<TextBlock>("SavedFeedsSummary")!.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheChangeHistorySwapsItsEmptyLineForTheRows()
    {
        var feeds = new FakeSavedFeedsService();
        (MainWindow window, SavedFeedsViewModel page, _) = await ShowSavedFeedsAsync(feeds, item: "NavChangeHistory");
        try
        {
            Assert.True(window.FindControl<TextBlock>("HistoryEmpty")!.IsVisible);
            Assert.False(window.FindControl<ScrollViewer>("HistoryScroll")!.IsVisible);
            Assert.Equal(page.HistoryEmptyText, window.FindControl<TextBlock>("HistoryEmpty")!.Text);

            feeds.Saved = new SavedFeedsRead(true, [SavedAsn, SavedAws]);
            feeds.History = SavedFeedsHistory(SavedFeedsChange("AS64500", "203.0.113.0/24"),
                                              SavedFeedsChange("AWS_EC2_all", "198.51.100.0/24", added: false));
            Navigate(window, "NavLookup");
            Navigate(window, "NavChangeHistory");

            Assert.False(window.FindControl<TextBlock>("HistoryEmpty")!.IsVisible);
            Assert.True(window.FindControl<ScrollViewer>("HistoryScroll")!.IsVisible);

            Settle(window);
            List<Border> historyRows = RealisedRows(window, "HistoryList");
            Assert.True(historyRows.Count == 2, $"{historyRows.Count} history rows, {page.HistoryRows.Count} changes, "
                        + $"card {window.FindControl<Border>("HistoryCard")!.Bounds}.");
            HistoryChangeRow removed = page.HistoryRows[1];
            Assert.Equal(removed.AccessibleText, AutomationProperties.GetName(historyRows[1]));
            // Two lines in a notice row: the entry, its change and its time, then the feed, the cause and the reason
            Assert.Equal([removed.EntryText, removed.ChangeText, removed.WhenText, removed.FeedText, removed.CauseText,
                          removed.ReasonText], TextsOf(historyRows[1]));
            Assert.All(historyRows, row => Assert.Contains("notice", row.Classes));
            AssertTwoLines(historyRows[1], 3, removed.FeedText);
            Assert.Contains("mono", historyRows[1].GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == removed.EntryText).Classes);
            Assert.Equal(page.HistorySummaryText, window.FindControl<TextBlock>("HistorySummary")!.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AtTheMinimumWidthARowsButtonsShowWholeAndRunTheirCommandsWithThatRow()
    {
        var feeds = new FakeSavedFeedsService { Saved = new SavedFeedsRead(true, [SavedAsn, SavedAws]) };
        var ui = new FakeUi();
        (MainWindow window, SavedFeedsViewModel page, _) = await ShowSavedFeedsAsync(feeds, ui);
        try
        {
            window.Width = window.MinWidth;
            Settle(window);
            Border card = window.FindControl<Border>("SavedFeedsCard")!;
            Border row = RealisedRows(window, "SavedFeedsList")[1];
            SavedFeedRow aws = page.Feeds[1];
            foreach (string name in new[] { "RetireSavedFeedButton", "RemoveSavedFeedButton" })
            {
                Button button = RowButton(row, name);
                Rect inCard = new(button.TranslatePoint(new Point(0, 0), card)!.Value, button.Bounds.Size);
                Assert.True(inCard.X >= 0 && inCard.Right <= card.Bounds.Width, $"{name} is at {inCard} in {card.Bounds}.");
                Assert.True(button.Bounds.Width >= button.DesiredSize.Width - 0.5, $"{name} is squeezed.");
            }

            Button retire = RowButton(row, "RetireSavedFeedButton");
            Assert.Equal(aws.RetireName, ToolTip.GetTip(retire));
            Assert.Equal(aws.RetireName, AutomationProperties.GetName(retire));
            Assert.Contains(AppText.RetireFeed, TextsOf(retire));
            Click(window, retire);
            await WaitUntilAsync(() => feeds.Retires.Count == 1 && !page.IsWorking);
            Assert.Equal((FeedKind.Aws, "AWS_EC2_all"), feeds.Retires[0]);
            Assert.Equal(AppText.RetireSavedFeedTitle(aws.NameText), ui.Confirms[^1].Title);

            Settle(window);
            Button remove = RowButton(RealisedRows(window, "SavedFeedsList")[1], "RemoveSavedFeedButton");
            Assert.Equal(page.Feeds[1].RemoveName, ToolTip.GetTip(remove));
            Assert.Equal(page.Feeds[1].RemoveName, AutomationProperties.GetName(remove));
            Assert.Contains(AppText.RemoveSavedFeedButton, TextsOf(remove));
            Click(window, remove);
            await WaitUntilAsync(() => feeds.Removes.Count == 1 && !page.IsWorking);
            Assert.Equal(["AWS_EC2_all"], feeds.Removes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheHistorySearchesOnEnterWithNoModifierOnlyAndItsProblemLineKeepsItsHeight()
    {
        var feeds = new FakeSavedFeedsService();
        (MainWindow window, SavedFeedsViewModel page, _) = await ShowSavedFeedsAsync(feeds, item: "NavChangeHistory");
        try
        {
            TextBlock problem = window.FindControl<TextBlock>("HistorySearchProblem")!;
            Assert.Contains("placeholder", problem.Classes);
            double height = problem.Bounds.Height;
            int queries = feeds.Queries.Count;
            TextBox box = window.FindControl<TextBox>("HistorySearchBox")!;
            Assert.Contains("mono", box.Classes);
            box.Focus();
            window.KeyTextInput("203.0.113.7");
            Settle(window);
            Assert.Equal("203.0.113.7", page.SearchText);

            window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
            window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
            Settle(window);
            Assert.Equal(queries, feeds.Queries.Count);

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await WaitUntilAsync(() => feeds.Queries.Count == queries + 1);
            Assert.NotNull(feeds.Queries[^1].Query.Match);

            window.KeyTextInput("x");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle(window);
            Assert.Equal(queries + 1, feeds.Queries.Count);
            Assert.Equal(AppText.HistorySearchInvalid, problem.Text);
            Assert.DoesNotContain("placeholder", problem.Classes);
            Assert.Equal(height, problem.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ChoosingInEachHistoryBoxSendsAQueryAndARefillKeepsTheFeedShown()
    {
        var feeds = new FakeSavedFeedsService
        {
            Saved = new SavedFeedsRead(true, [SavedAsn, SavedAws]),
            History = SavedFeedsHistory(SavedFeedsChange("AS64500", "203.0.113.0/24"),
                                        SavedFeedsChange("AWS_EC2_all", "198.51.100.0/24")),
        };
        (MainWindow window, SavedFeedsViewModel page, _) = await ShowSavedFeedsAsync(feeds, item: "NavChangeHistory");
        try
        {
            ComboBox feedBox = window.FindControl<ComboBox>("HistoryFeedBox")!;
            ComboBox periodBox = window.FindControl<ComboBox>("HistoryPeriodBox")!;
            Assert.Equal(AppText.HistoryFeedBoxName, AutomationProperties.GetName(feedBox));
            Assert.Equal(AppText.HistoryPeriodBoxName, AutomationProperties.GetName(periodBox));
            Assert.Equal(3, page.FeedChoices.Count);
            int queries = feeds.Queries.Count;

            feedBox.SelectedItem = page.FeedChoices[1];
            Settle(window);
            await WaitUntilAsync(() => feeds.Queries.Count == queries + 1);
            Settle(window);
            Assert.Equal("AS64500", feeds.Queries[^1].Query.FeedKey);

            // The answer refills the choices: the box shows the same feed and nothing more is asked
            Assert.Equal("AS64500", page.SelectedFeed?.Key);
            Assert.Equal("AS64500", (feedBox.SelectedItem as HistoryFeedChoice)?.Key);
            Assert.Equal(queries + 1, feeds.Queries.Count);

            periodBox.SelectedIndex = 2;
            Settle(window);
            await WaitUntilAsync(() => feeds.Queries.Count == queries + 2);
            Assert.Same(page.PeriodChoices[2], page.SelectedPeriod);
            Assert.NotNull(feeds.Queries[^1].Query.SinceUtc);
            Assert.Equal("AS64500", feeds.Queries[^1].Query.FeedKey);
            Settle(window);
            Assert.Equal("AS64500", (feedBox.SelectedItem as HistoryFeedChoice)?.Key);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EachPagesActionLineKeepsItsHeightWhenEmpty()
    {
        foreach ((string item, string line) in new[] { ("NavSavedFeeds", "SavedFeedsAction"),
                                                       ("NavChangeHistory", "HistoryAction") })
        {
            var feeds = new FakeSavedFeedsService { Saved = new SavedFeedsRead(true, [SavedAsn]) };
            (MainWindow window, SavedFeedsViewModel page, _) = await ShowSavedFeedsAsync(feeds, item: item);
            try
            {
                TextBlock action = window.FindControl<TextBlock>(line)!;
                Assert.Equal("", page.ActionText);
                Assert.Contains("placeholder", action.Classes);
                Assert.Equal(36, action.Bounds.Height);

                await page.RetireCommand.ExecuteAsync(page.Feeds[0]);
                Settle(window);
                Assert.Equal(page.ActionText, action.Text);
                Assert.DoesNotContain("placeholder", action.Classes);
                Assert.Equal(36, action.Bounds.Height);
                Assert.Equal(page.ActionText, ToolTip.GetTip(action));
                Assert.Equal("Polite", AutomationProperties.GetLiveSetting(action).ToString());
            }
            finally
            {
                window.Close();
            }
        }
    }

    // Neither page scrolls inside the frame: at the window's minimum and default sizes each list card keeps 216 or
    // more, and the result line has no scroller of its own, so scrolling the page brings it into view
    [AvaloniaFact]
    public async Task EachPagesResultLineIsReachedByThePageScrollAfterItsCommand()
    {
        foreach ((double width, double height) in new[] { (960d, 600d), (1280d, 880d) })
        {
            var feeds = new FakeSavedFeedsService
            {
                Saved = new SavedFeedsRead(true, [SavedAsn, SavedAws]),
                History = SavedFeedsHistory(SavedFeedsChange("AS64500", "203.0.113.0/24")),
            };
            (MainWindow window, SavedFeedsViewModel page, _) = await ShowSavedFeedsAsync(feeds);
            try
            {
                window.Width = width;
                Resize(window, height);
                await page.RetireCommand.ExecuteAsync(page.Feeds[0]);
                Settle(window);
                Assert.NotEqual("", page.ActionText);
                Assert.True(window.FindControl<Border>("SavedFeedsCard")!.Bounds.Height >= 216, $"{width}x{height}");
                AssertInView(window, "SavedFeedsAction");

                Navigate(window, "NavChangeHistory");
                await page.ClearHistoryCommand.ExecuteAsync(null);
                Settle(window);
                Assert.Equal(1, feeds.Clears);
                Assert.Equal(AppText.HistoryClearLine(HistoryState.Missing), page.ActionText);
                Assert.True(window.FindControl<Border>("HistoryCard")!.Bounds.Height >= 216, $"{width}x{height}");
                AssertInView(window, "HistoryAction");
            }
            finally
            {
                window.Close();
            }
        }
    }

    // ----- closing while the page has a call out or a feed write is in flight -----

    // A retire out with the service: the page's question is open to a fresh list of confirmations
    private static async Task<(MainWindow Window, SavedFeedsViewModel Page, TaskCompletionSource Retire)> RetireOutAsync(
        FakeSavedFeedsService feeds, FakeUi ui, IFeedRecords? records = null)
    {
        var retire = new TaskCompletionSource();
        feeds.Saved = new SavedFeedsRead(true, [SavedAsn]);
        feeds.RetireGate = retire;
        (MainWindow window, SavedFeedsViewModel page, _) = await ShowSavedFeedsAsync(feeds, ui, records);
        bool answer = ui.ConfirmResult;
        ui.ConfirmResult = true;
        page.RetireCommand.Execute(page.Feeds[0]);
        ui.ConfirmResult = answer;
        Assert.True(page.AsksBeforeClose);
        ui.Confirms.Clear();
        return (window, page, retire);
    }

    [AvaloniaFact]
    public async Task ClosingDuringASavedFeedsRetireAsksThePageAndClosesOnceTheCallHasEnded()
    {
        var feeds = new FakeSavedFeedsService();
        var ui = new FakeUi { ConfirmResult = false };
        (MainWindow window, SavedFeedsViewModel page, TaskCompletionSource retire) = await RetireOutAsync(feeds, ui);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Close();
            Settle(window);
            var (title, message, button) = Assert.Single(ui.Confirms);
            Assert.Equal(AppText.CloseSavedFeedsWorkTitle, title);
            Assert.Equal(AppText.CloseSavedFeedsWorkBody, message);
            Assert.Equal(AppText.CloseFeedWriteButton, button);
            Assert.False(closed);
            Assert.True(page.IsWorking);

            ui.ConfirmResult = true;
            window.Close();
            Settle(window);
            await Task.Delay(50);
            Settle(window);
            Assert.Equal(2, ui.Confirms.Count);
            Assert.False(closed);
            Assert.False(feeds.RetireTokens[0].IsCancellationRequested);

            retire.SetResult();
            await WaitUntilAsync(() => closed);
        }
        finally
        {
            retire.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task ANoToTheUpdateQuestionTurnsTheSavedFeedsRunsOnAgain()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        MainViewModel vm = NewViewModel(service, ui);
        await vm.InitializeAsync();
        if (vm.PendingCheck is { } check)
            await check;
        var feeds = new FakeSavedFeedsService { Saved = new SavedFeedsRead(true, [SavedAsn]) };
        var retire = new TaskCompletionSource();
        feeds.RetireGate = retire;
        WorkGate gate = StartedGate();
        var page = new SavedFeedsViewModel(feeds, ui, gate, TimeProvider.System);
        var window = new MainWindow();
        window.AttachViewModel(vm, savedFeeds: page, ui: ui, gate: gate);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var update = new TaskCompletionSource();
        try
        {
            Navigate(window, "NavSavedFeeds");
            service.UpdateGate = update;
            vm.UpdateDatasetCommand.Execute(null);
            page.RetireCommand.Execute(page.Feeds[0]);
            Settle(window);
            Assert.True(vm.IsBusy);
            Assert.True(page.AsksBeforeClose);

            // Yes to the page's question; while the update's is open the page's runs stay off; then no to it
            bool? runsOffDuringUpdateQuestion = null;
            ui.OnDialogOpen = () =>
            {
                ui.ConfirmResult = ui.Confirms.Count == 1;
                if (ui.Confirms.Count == 2)
                    runsOffDuringUpdateQuestion = !page.ClearHistoryCommand.CanExecute(null);
            };
            ui.Confirms.Clear();
            window.Close();
            await WaitUntilAsync(() => ui.Confirms.Count == 2);
            Settle(window);
            Assert.Equal([AppText.CloseSavedFeedsWorkTitle, AppText.CloseBusyTitle], ui.Confirms.Select(c => c.Title));
            Assert.False(closed);

            retire.SetResult();
            await WaitUntilAsync(() => !page.IsWorking);
            Assert.True(runsOffDuringUpdateQuestion);
            Assert.True(page.RetireCommand.CanExecute(page.Feeds[0]));
            Assert.True(page.ClearHistoryCommand.CanExecute(null));
        }
        finally
        {
            ui.OnDialogOpen = null;
            ui.ConfirmResult = true;
            update.TrySetResult();
            retire.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task AFeedWriteQuestionThatFailsToShowKeepsTheWindowOpen()
    {
        IFeedRecords records = FeedRecords.None;
        IDisposable write = HoldWrite(records, "AS64500");
        var ui = new FakeUi { ConfirmException = new InvalidOperationException("no dialog") };
        (MainWindow window, _, _) = await ShowSavedFeedsAsync(new FakeSavedFeedsService(), ui, records);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Close();
            Settle(window);
            await Task.Delay(50);
            Settle(window);

            Assert.False(closed);
            HoldWrite(records, "AWS_EC2_all").Dispose();
        }
        finally
        {
            write.Dispose();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task ClosingDuringAFeedWriteAsksAndClosesOnlyOnceTheWriteHasEnded()
    {
        IFeedRecords records = FeedRecords.None;
        IDisposable write = HoldWrite(records, "AS64500");
        var ui = new FakeUi { ConfirmResult = false };
        (MainWindow window, _, WorkGate gate) = await ShowSavedFeedsAsync(new FakeSavedFeedsService(), ui, records);
        bool closed = false;
        bool? gateHeldDuringQuestion = null;
        window.Closed += (_, _) => closed = true;
        ui.OnDialogOpen = () => gateHeldDuringQuestion = gate.IsHeld;
        try
        {
            Assert.Equal(1, records.WritesInFlight);
            window.Close();
            Settle(window);
            var (title, message, button) = Assert.Single(ui.Confirms);
            Assert.Equal(AppText.CloseFeedWriteTitle, title);
            Assert.Equal(AppText.CloseFeedWriteBody, message);
            Assert.Equal(AppText.CloseFeedWriteButton, button);
            Assert.True(gateHeldDuringQuestion);
            Assert.False(gate.IsHeld);
            Assert.False(closed);

            // A no leaves writes admitted
            HoldWrite(records, "AWS_EC2_all").Dispose();

            ui.ConfirmResult = true;
            window.Close();
            Settle(window);
            await Task.Delay(50);
            Settle(window);
            Assert.Equal(2, ui.Confirms.Count);
            Assert.False(closed);
            Assert.ThrowsAny<Exception>(() => HoldWrite(records, "AWS_EC2_all").Dispose());

            write.Dispose();
            await WaitUntilAsync(() => closed);
        }
        finally
        {
            write.Dispose();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    // A second close while the first waits for a feed write asks nothing more and closes only once the write has ended
    [AvaloniaFact]
    public async Task ASecondCloseWhileTheCloseWaitsForAFeedWriteAsksNothingAndWaits()
    {
        IFeedRecords records = FeedRecords.None;
        IDisposable write = HoldWrite(records, "AS64500");
        var ui = new FakeUi();
        (MainWindow window, _, _) = await ShowSavedFeedsAsync(new FakeSavedFeedsService(), ui, records);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Close();
            Settle(window);
            Assert.Equal(AppText.CloseFeedWriteTitle, Assert.Single(ui.Confirms).Title);

            window.Close();
            Settle(window);
            await Task.Delay(100);
            Settle(window);
            Assert.Single(ui.Confirms);
            Assert.False(closed);

            write.Dispose();
            await WaitUntilAsync(() => closed);
            Assert.Single(ui.Confirms);
        }
        finally
        {
            write.Dispose();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task ClosingDuringARetireAndAFeedWriteAsksOnlyThePageAndWaitsForBoth()
    {
        IFeedRecords records = FeedRecords.None;
        IDisposable write = HoldWrite(records, "AS64500");
        var feeds = new FakeSavedFeedsService();
        var ui = new FakeUi();
        (MainWindow window, _, TaskCompletionSource retire) = await RetireOutAsync(feeds, ui, records);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Close();
            Settle(window);
            Assert.Equal(AppText.CloseSavedFeedsWorkTitle, Assert.Single(ui.Confirms).Title);

            retire.SetResult();
            Settle(window);
            await Task.Delay(50);
            Settle(window);
            Assert.False(closed);

            write.Dispose();
            await WaitUntilAsync(() => closed);
            Assert.Single(ui.Confirms);
        }
        finally
        {
            retire.TrySetResult();
            write.Dispose();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task ClosingWithEveryQuestionAsksInOrderThenStopsEach()
    {
        var service = WithDataset();
        var abuse = new FakeAbuseIpDbService();
        var download = new TaskCompletionSource();
        abuse.DownloadGate = download;
        var ui = new FakeUi();
        (MainViewModel vm, SettingsPageViewModel settings, AbuseIpDbViewModel abusePage) =
            await StartWithAllPagesAsync(service, ui, abuse);
        var feeds = new FakeSavedFeedsService { Saved = new SavedFeedsRead(true, [SavedAsn]) };
        var retire = new TaskCompletionSource();
        feeds.RetireGate = retire;
        WorkGate gate = StartedGate();
        var page = new SavedFeedsViewModel(feeds, ui, gate, TimeProvider.System);
        IFeedRecords records = FeedRecords.None;
        IDisposable write = HoldWrite(records, "AS64500");
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: settings, abuseIpDb: abusePage, savedFeeds: page, records: records, ui: ui,
                               gate: gate);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var update = new TaskCompletionSource();
        try
        {
            Navigate(window, "NavSavedFeeds");
            settings.Settings.AbuseIpDbKeepDays = 60;
            abusePage.DownloadCommand.Execute(null);
            page.RetireCommand.Execute(page.Feeds[0]);
            service.UpdateGate = update;
            vm.UpdateDatasetCommand.Execute(null);
            Settle(window);
            Assert.True(settings.HasUnsavedChanges && vm.IsBusy && abusePage.AsksBeforeClose && page.AsksBeforeClose);
            ui.Confirms.Clear();

            window.Close();
            await WaitUntilAsync(() => ui.Confirms.Count == 4);
            Assert.Equal([AppText.CloseUnsavedTitle, AppText.CloseAbuseWorkTitle, AppText.CloseSavedFeedsWorkTitle,
                          AppText.CloseBusyTitle], ui.Confirms.Select(confirm => confirm.Title));
            await Task.Delay(50);
            Settle(window);
            Assert.False(closed);
            Assert.True(abuse.DownloadTokens[0].IsCancellationRequested);

            retire.SetResult();
            await Task.Delay(50);
            Settle(window);
            Assert.False(closed);

            write.Dispose();
            await WaitUntilAsync(() => closed);
            Assert.Equal(4, ui.Confirms.Count);
        }
        finally
        {
            update.TrySetResult();
            download.TrySetResult();
            retire.TrySetResult();
            write.Dispose();
            if (!closed)
            {
                settings.RevertSettingsCommand.Execute(null);
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task ClosingWithUnsavedEditsAFeedWriteAndAnUpdateAsksTheWriteBetween()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        (MainViewModel vm, SettingsPageViewModel settings, AbuseIpDbViewModel abusePage) =
            await StartWithAllPagesAsync(service, ui, new FakeAbuseIpDbService());
        IFeedRecords records = FeedRecords.None;
        IDisposable write = HoldWrite(records, "AS64500");
        WorkGate gate = StartedGate();
        var page = new SavedFeedsViewModel(new FakeSavedFeedsService(), ui, gate, TimeProvider.System);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: settings, abuseIpDb: abusePage, savedFeeds: page, records: records, ui: ui,
                               gate: gate);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var update = new TaskCompletionSource();
        try
        {
            Settle(window);
            settings.Settings.AbuseIpDbKeepDays = 60;
            service.UpdateGate = update;
            vm.UpdateDatasetCommand.Execute(null);
            Settle(window);
            ui.Confirms.Clear();

            window.Close();
            await WaitUntilAsync(() => ui.Confirms.Count == 3);
            Assert.Equal([AppText.CloseUnsavedTitle, AppText.CloseFeedWriteTitle, AppText.CloseBusyTitle],
                ui.Confirms.Select(confirm => confirm.Title));
            await Task.Delay(50);
            Settle(window);
            Assert.False(closed);

            write.Dispose();
            await WaitUntilAsync(() => closed);
        }
        finally
        {
            update.TrySetResult();
            write.Dispose();
            if (!closed)
            {
                settings.RevertSettingsCommand.Execute(null);
                window.Close();
            }
        }
    }

    // The session ending is let through with a call out and a write in flight; the close button still asks
    [AvaloniaFact]
    public async Task TheSessionEndingIsNotHeldByTheSavedFeedsPageOrAFeedWrite()
    {
        IFeedRecords records = FeedRecords.None;
        IDisposable write = HoldWrite(records, "AS64500");
        var feeds = new FakeSavedFeedsService();
        var ui = new FakeUi { ConfirmResult = false };
        (MainWindow window, SavedFeedsViewModel page, TaskCompletionSource retire) =
            await RetireOutAsync(feeds, ui, records);
        try
        {
            Assert.False(window.HoldsClose(WindowCloseReason.OSShutdown, isProgrammatic: false, isWindows: false));
            Assert.False(window.HoldsClose(WindowCloseReason.OSShutdown, isProgrammatic: false, isWindows: true));
            Assert.False(window.HoldsClose(WindowCloseReason.ApplicationShutdown, isProgrammatic: false, isWindows: true));
            Settle(window);
            Assert.Empty(ui.Confirms);

            Assert.True(window.HoldsClose(WindowCloseReason.ApplicationShutdown, isProgrammatic: false, isWindows: false));
            Settle(window);
            Assert.Equal(AppText.CloseSavedFeedsWorkTitle, Assert.Single(ui.Confirms).Title);

            retire.SetResult();
            await WaitUntilAsync(() => !page.AsksBeforeClose && !page.IsWorking);
            ui.Confirms.Clear();
            Assert.True(window.HoldsClose(WindowCloseReason.ApplicationShutdown, isProgrammatic: false, isWindows: false));
            Settle(window);
            Assert.Equal(AppText.CloseFeedWriteTitle, Assert.Single(ui.Confirms).Title);
        }
        finally
        {
            retire.TrySetResult();
            write.Dispose();
            ui.ConfirmResult = true;
            window.Close();
        }
    }
}
