using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed.Tests;

/// <summary>Help's ModSecurity section, in a headless Help dialog.</summary>
public sealed class HelpDialogModSecurityTests
{
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static TextBlock[] Paragraphs(HelpDialog dialog) =>
        dialog.FindControl<ItemsControl>("HelpModSecurityList")!.GetVisualDescendants().OfType<TextBlock>().ToArray();

    [Fact]
    public void The_section_follows_AbuseIPDB_and_its_paragraphs_carry_the_rules_dialog_notes()
    {
        Assert.Equal("5. AbuseIPDB", AppText.HelpAbuseHeading);
        Assert.Equal("6. ModSecurity", AppText.HelpModSecurityHeading);

        IReadOnlyList<string> texts = AppText.HelpModSecurityTexts;
        Assert.Equal(11, texts.Count);
        Assert.StartsWith("Your web server reads the ModSecurity lists only when it starts and when it reloads", texts[0]);
        Assert.Contains("nginx -s reload, or apachectl graceful on Apache", texts[0]);
        Assert.Contains("by default a server won't start with a list that fails", texts[1]);
        Assert.Contains("https://", texts[2]);
        Assert.Equal(AppText.ModSecurityRulesNoteIds, texts[3]);
        Assert.Equal(AppText.ModSecurityStartFreshRuleIds, texts[4]);
        // Empty feed empties an unticked type's files, so the paragraph before it names only the writes
        Assert.Equal("Unticking a feed type in Settings leaves its files as they were, and they stop updating: Write feed "
            + "and Export no longer write or delete them.", texts[5]);
        Assert.Equal("Empty feed, on " + AppText.AbuseIpDbTitle + ", empties the AbuseIPDB files in both the fortigate and "
            + "modsecurity folders, whatever is ticked. To retire the AbuseIPDB list of a type you unticked, press Empty "
            + "feed and then Write feed, which fills the ticked type's files again.", texts[6]);
        Assert.Contains("error_log at info", texts[7]);
        Assert.Contains("the audit log has it too while SecAuditEngine is On", texts[7]);
        Assert.Equal(AppText.ModSecurityRulesNoteClose, texts[8]);
        Assert.Equal(AppText.ModSecurityRulesNoteListen, texts[9]);
        Assert.StartsWith("Known limits: ", texts[10]);
        Assert.Contains(AbuseIpDbLimits.MaxFeedEntries.ToString("N0", CultureInfo.InvariantCulture) + " addresses a family",
            texts[10]);
        Assert.Contains(texts[3], AppText.ModSecurityRulesNotes);
        Assert.Contains(texts[4], AppText.ModSecurityRulesNotes);
        Assert.Contains(texts[8], AppText.ModSecurityRulesNotes);
        Assert.Contains(texts[9], AppText.ModSecurityRulesNotes);
    }

    // The proxy's own error is what a visitor behind one sees, so the paragraph names it with the logs
    [Fact]
    public void The_close_paragraph_says_nothing_is_sent_and_where_it_shows()
    {
        Assert.Equal("Close with no reply (444)", AppText.ModSecurityClose);
        Assert.StartsWith(AppText.ModSecurityClose + " sends nothing back", AppText.ModSecurityRulesNoteClose);
        Assert.Contains("nginx logs the request as 444; Apache logs it as 403", AppText.ModSecurityRulesNoteClose);
        Assert.Contains("the proxy answers the visitor with its own error, and may first try another server",
            AppText.ModSecurityRulesNoteClose);
    }

    // The feed texts name a threat feed, never a Fortigate feed alone, as a ModSecurity copy can be written too
    [Fact]
    public void The_feed_texts_say_threat_feed_and_the_feed_types_caption_names_Empty_feed()
    {
        Assert.Equal("Build a threat feed from Google Cloud ranges", AppText.GoogleCloudCaption);
        Assert.Equal("Build a threat feed from Azure service tags", AppText.AzureCaption);
        Assert.Equal("Build a threat feed from AWS IP ranges", AppText.AwsCaption);
        Assert.Equal("Download AbuseIPDB's blacklist and write a threat feed from it", AppText.AbuseIpDbCaption);
        Assert.Equal("Write the ticked ranges to a threat feed of each type ticked in Settings", AppText.WriteFeedTooltip);
        Assert.Equal("Networks listed here are cut out of every feed before it's written, so no feed blocks them. The ASN "
            + "report still lists every network.", AppText.ExceptionsMessage);
        Assert.Contains("it also writes a feed of each type ticked, FortiGate or ModSecurity.", AppText.HelpExportText);
        Assert.EndsWith("Networks listed under Exceptions in Settings are cut out of every feed.", AppText.HelpExportText);
        Assert.Equal("Write feed and Export write each feed once for every type ticked, and a type you untick keeps its "
            + "files as they were. Empty feed is the exception: it empties the AbuseIPDB files in both folders, whatever "
            + "is ticked.", AppText.FeedTypesCaption);
        string[] texts = [AppText.GoogleCloudCaption, AppText.AzureCaption, AppText.AwsCaption, AppText.AbuseIpDbCaption,
            AppText.WriteFeedTooltip, AppText.ExceptionsMessage, AppText.HelpExportText, AppText.HelpAbuseText(),
            AppText.WriteAbuseFeedTooltip(true), AppText.WriteAbuseFeedTooltip(false)];
        Assert.All(texts, text =>
        {
            Assert.DoesNotContain("Fortigate feed", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Fortigate threat feed", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [AvaloniaFact]
    public void The_section_is_the_last_in_the_scroll_with_a_card_title_and_a_wrapped_paragraph_each()
    {
        var dialog = new HelpDialog();
        dialog.Show();
        try
        {
            Settle(dialog);
            ScrollViewer scroll = dialog.FindControl<ScrollViewer>("HelpScroll")!;
            TextBlock heading = dialog.FindControl<TextBlock>("ModSecurityHeading")!;
            Assert.Equal(AppText.HelpModSecurityHeading, heading.Text);
            Assert.Contains("card-title", heading.Classes);

            var section = (StackPanel)heading.Parent!;
            var sections = (StackPanel)section.Parent!;
            var abuseSection = (StackPanel)dialog.FindControl<TextBlock>("AbuseText")!.Parent!;
            Assert.Same(section, sections.Children[^1]);
            Assert.Equal(sections.Children.IndexOf(abuseSection) + 1, sections.Children.IndexOf(section));
            Assert.Contains(scroll, section.GetVisualAncestors());

            ItemsControl list = dialog.FindControl<ItemsControl>("HelpModSecurityList")!;
            TextBlock[] paragraphs = Paragraphs(dialog);
            Assert.Equal(AppText.HelpModSecurityTexts, paragraphs.Select(block => block.Text));
            Assert.All(paragraphs, block =>
            {
                Assert.Equal(TextWrapping.Wrap, block.TextWrapping);
                Assert.True(block.Bounds.Width <= list.Bounds.Width + 0.5,
                    $"A paragraph is {block.Bounds.Width} px wide in a {list.Bounds.Width} px list.");
            });
            Assert.True(paragraphs[0].Bounds.Height > paragraphs[2].Bounds.Height,
                $"The first paragraph is {paragraphs[0].Bounds.Height} px high and the third {paragraphs[2].Bounds.Height}, so they don't wrap.");
        }
        finally
        {
            dialog.Close();
        }
    }

    // Close keeps the focus, so End must bring the last paragraph whole into view
    [AvaloniaFact]
    public void End_scrolls_the_last_paragraph_into_view_above_Close()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var dialog = new HelpDialog();
            Task shown = dialog.ShowDialog(owner);
            Settle(dialog);
            ScrollViewer scroll = dialog.FindControl<ScrollViewer>("HelpScroll")!;
            Button close = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content is string text && text == AppText.Close);
            close.Focus();
            Settle(dialog);

            TextBlock last = Paragraphs(dialog)[^1];
            Point before = last.TranslatePoint(new Point(0, last.Bounds.Height), scroll)!.Value;
            Assert.True(before.Y > scroll.Viewport.Height, $"The last paragraph ends at {before.Y} in a {scroll.Viewport.Height} px view.");

            dialog.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
            Settle(dialog);

            Point top = last.TranslatePoint(new Point(0, 0), scroll)!.Value;
            Point end = last.TranslatePoint(new Point(0, last.Bounds.Height), scroll)!.Value;
            Assert.True(top.Y >= 0 && end.Y <= scroll.Viewport.Height + 0.5,
                $"After End the last paragraph runs from {top.Y} to {end.Y} in a {scroll.Viewport.Height} px view.");
            Assert.True(close.IsFocused);
            Assert.False(shown.IsCompleted);
            dialog.Close();
        }
        finally
        {
            owner.Close();
        }
    }
}
