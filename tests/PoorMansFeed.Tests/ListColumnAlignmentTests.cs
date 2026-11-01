using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>Each two-line list header's labels start where their column's cells start, on both lines.</summary>
public sealed partial class MainWindowTests
{
    [AvaloniaFact]
    public async Task CloudAddressLookupsHeaderLabelsStartOverTheirCellsOnBothLines()
    {
        (MainWindow window, CloudAddressSearchViewModel page, CloudSearchStub search, _) = await ShowCloudSearchAsync();
        try
        {
            search.Next = CloudSearchFound(TwoCloudMatches());
            page.Query = "2600:1f18::1";
            await page.SearchCommand.ExecuteAsync(null);
            Settle(window);

            Border card = window.FindControl<Border>("CloudSearchCard")!;
            Grid header = window.FindControl<Grid>("CloudSearchHeader")!;
            AssertHeaderOverCells(card, header, RealisedRows(window, "CloudSearchList")[0],
                [[AppText.ColumnNetwork, AppText.ColumnProvider, AppText.ColumnRegion],
                 [AppText.ColumnService, AppText.ColumnScope]]);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ChangeHistorysHeaderLabelsStartOverTheirCellsOnBothLines()
    {
        var feeds = new FakeSavedFeedsService
        {
            Saved = new SavedFeedsRead(true, [SavedAsn]),
            History = SavedFeedsHistory(SavedFeedsChange("AS64500", "203.0.113.0/24")),
        };
        (MainWindow window, _, _) = await ShowSavedFeedsAsync(feeds, item: "NavChangeHistory");
        try
        {
            Settle(window);
            Border card = window.FindControl<Border>("HistoryCard")!;
            Grid header = card.GetVisualDescendants().OfType<Grid>()
                .First(grid => grid.RowDefinitions.Count == 2 && grid.Children.All(child => child is Grid));
            AssertHeaderOverCells(card, header, RealisedRows(window, "HistoryList")[0],
                [[AppText.HistoryEntryHeader, AppText.HistoryChangeHeader, AppText.HistoryTimeHeader],
                 [AppText.HistoryFeedHeader, AppText.HistoryCauseHeader, AppText.HistoryReasonHeader]]);
        }
        finally
        {
            window.Close();
        }
    }

    // Each label over the cell of the same column in the row, and a column's start the same on both lines
    private static void AssertHeaderOverCells(Visual common, Grid header, Border row, string[][] labels)
    {
        Grid[] headerLines = header.Children.OfType<Grid>().ToArray();
        Grid rowBody = row.GetVisualDescendants().OfType<Grid>().First(grid => grid.RowDefinitions.Count == 2);
        Grid[] rowLines = rowBody.Children.OfType<Grid>().ToArray();
        Assert.Equal(2, headerLines.Length);
        Assert.Equal(2, rowLines.Length);

        var starts = new Dictionary<int, double>();
        for (int line = 0; line < 2; line++)
        {
            TextBlock[] heads = TextsByColumn(headerLines[line]);
            TextBlock[] cells = TextsByColumn(rowLines[line]);
            Assert.Equal(labels[line], heads.Select(text => text.Text));
            Assert.Equal(heads.Select(Grid.GetColumn), cells.Select(Grid.GetColumn));
            for (int i = 0; i < heads.Length; i++)
            {
                double head = LeftOf(heads[i], common);
                double cell = LeftOf(cells[i], common);
                Assert.True(Math.Abs(head - cell) < 0.5,
                    $"\"{heads[i].Text}\" starts at {head}, its cell \"{cells[i].Text}\" at {cell}.");
                int column = Grid.GetColumn(heads[i]);
                if (starts.TryGetValue(column, out double first))
                    Assert.True(Math.Abs(first - head) < 0.5,
                        $"Column {column} starts at {first} on the first line and {head} (\"{heads[i].Text}\") on the second.");
                else
                    starts[column] = head;
            }
        }
    }

    private static TextBlock[] TextsByColumn(Grid line) =>
        line.Children.OfType<TextBlock>().OrderBy(Grid.GetColumn).ToArray();

    private static double LeftOf(Visual visual, Visual common) => visual.TranslatePoint(default, common)!.Value.X;
}
