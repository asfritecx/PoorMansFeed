using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

/// <summary>The exceptions editor's rules, on the Settings page but without a window: adding, refusing, removing and what Save returns.</summary>
public sealed class ExceptionsEditorModelTests
{
    // The longest feedback text in FeedbackTexts (the unreadable warning), which a Skia render of the editor's feedback
    // line measured whole. A count of characters is only a proxy for width: it catches a message made longer, not
    // one made of wider glyphs. The Settings page's render script shows every text in FeedbackTexts on that line, with
    // the longest network, and fails when one is trimmed; a new text needs a state there as well as a row here.
    private const int FeedbackLineBudget = 77;

    // .NET writes groups 5 and 6 of 0:5efe with a dotted IPv4 tail, so this is the longest canonical network
    // text (46 characters), and zeros and twos are among the widest glyphs
    private const string LongestNetwork = "2000:2000:2000:2000:0:5efe:200.200.200.200/125";

    // Every text the editor's feedback line can show, by its AppText member, at its longest
    private static readonly IReadOnlyDictionary<string, string> FeedbackTexts = new Dictionary<string, string>
    {
        [nameof(AppText.ExceptionsUnreadableWarning)] = AppText.ExceptionsUnreadableWarning,
        [nameof(AppText.ExceptionsFull)] = AppText.ExceptionsFull(ExceptionList.MaxEntries),
        [nameof(AppText.ExceptionInvalid)] = AppText.ExceptionInvalid,
        [nameof(AppText.ExceptionDuplicate)] = AppText.ExceptionDuplicate(LongestNetwork),
        [nameof(AppText.ExceptionAddedMasked)] = AppText.ExceptionAddedMasked(LongestNetwork),
        [nameof(AppText.ExceptionAdded)] = AppText.ExceptionAdded(LongestNetwork),
        [nameof(AppText.ExceptionRemoved)] = AppText.ExceptionRemoved(LongestNetwork),
    };

    // Each read of HasChanges builds a snapshot of every row, so a load that raised it per row would be quadratic
    [Fact]
    public void Load_raises_the_row_notifications_once_after_every_row_is_in()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Empty);
        ExceptionList hundred = ExceptionList.Create(Enumerable.Range(0, 100)
            .Select(i => ExceptionEntryTests.Entry($"10.0.{i}.0/24", "Row " + i)).ToList());
        var raised = new List<string?>();
        int rowsAtHasChanges = -1;
        model.PropertyChanged += (_, e) =>
        {
            raised.Add(e.PropertyName);
            if (e.PropertyName == nameof(ExceptionsEditorModel.HasChanges))
            {
                rowsAtHasChanges = model.Rows.Count;
            }
        };
        int collectionChanges = 0;
        model.Rows.CollectionChanged += (_, _) => collectionChanges++;

        model.Load(hundred);

        Assert.Equal(1, raised.Count(name => name == nameof(ExceptionsEditorModel.HasChanges)));
        Assert.Equal(1, raised.Count(name => name == nameof(ExceptionsEditorModel.HasRows)));
        Assert.Equal(1, raised.Count(name => name == nameof(ExceptionsEditorModel.CountText)));
        Assert.Equal(100, rowsAtHasChanges);
        Assert.Equal(101, collectionChanges);
        Assert.False(model.HasChanges);

        // After the load, an edit raises them again, once
        raised.Clear();
        model.Remove(model.Rows[0]);
        Assert.Equal(1, raised.Count(name => name == nameof(ExceptionsEditorModel.HasChanges)));
        Assert.True(model.HasChanges);
    }

    [Fact]
    public void Load_replaces_the_rows_and_clears_HasChanges()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Unreadable);
        Assert.Equal(AppText.ExceptionsUnreadableWarning, model.FeedbackText);
        model.NetworkText = "203.0.113.0/24";
        model.NoteText = "Typed";
        Assert.True(model.TryAdd());
        model.NetworkText = "198.51.100.7";
        Assert.True(model.HasChanges);

        model.Load(ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "Head office"), ExceptionEntryTests.Entry("2001:db8::/32", null)]));

        Assert.Equal(["192.0.2.0/24", "2001:db8::/32"], model.Rows.Select(row => row.NetworkText));
        Assert.Equal("", model.NetworkText);
        Assert.Equal("", model.NoteText);
        Assert.Equal("", model.FeedbackText);
        Assert.False(model.HasFeedback);
        Assert.False(model.IsFeedbackError);
        Assert.False(model.HasChanges);

        // An unreadable list keeps its warning
        model.Load(ExceptionList.Unreadable);
        Assert.Empty(model.Rows);
        Assert.Equal(AppText.ExceptionsUnreadableWarning, model.FeedbackText);
        Assert.True(model.IsFeedbackError);
        Assert.False(model.HasChanges);
    }

    [Fact]
    public void Adding_or_removing_a_row_is_a_change()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "kept")]));
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(model.HasChanges);

        model.NetworkText = "203.0.113.0/24";
        Assert.False(model.HasChanges);
        Assert.True(model.TryAdd());
        Assert.True(model.HasChanges);

        model.Remove(model.Rows[1]);
        Assert.False(model.HasChanges);
        model.Remove(model.Rows[0]);
        Assert.True(model.HasChanges);
        Assert.True(raised.Count(name => name == nameof(ExceptionsEditorModel.HasChanges)) >= 3);
    }

    [Fact]
    public void Removing_then_re_adding_the_same_entry_is_no_change()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "Head office")]));

        model.Remove(model.Rows[0]);
        model.NetworkText = "192.0.2.0/24";
        model.NoteText = "Head office";
        Assert.True(model.TryAdd());
        Assert.False(model.HasChanges);

        // The same network with another note is a change
        model.Remove(model.Rows[0]);
        model.NetworkText = "192.0.2.0/24";
        model.NoteText = "Branch";
        Assert.True(model.TryAdd());
        Assert.True(model.HasChanges);
    }

    [Fact]
    public void TryAddPending_is_true_for_a_blank_box_and_adds_otherwise()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Empty);

        model.NetworkText = "   ";
        Assert.True(model.TryAddPending());
        Assert.Empty(model.Rows);

        model.NetworkText = "203.0.113.0/24";
        Assert.True(model.TryAddPending());
        Assert.Equal("203.0.113.0/24", Assert.Single(model.Rows).NetworkText);

        model.NetworkText = "not a network";
        Assert.False(model.TryAddPending());
        Assert.Single(model.Rows);
        Assert.Equal("not a network", model.NetworkText);
        Assert.Equal(AppText.ExceptionInvalid, model.FeedbackText);
    }

    [Fact]
    public void The_saved_entries_are_listed_in_their_order()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Create(
        [
            ExceptionEntryTests.Entry("2001:db8::/32", "Lab"),
            ExceptionEntryTests.Entry("192.0.2.0/24", null),
        ]));

        Assert.Equal(["2001:db8::/32", "192.0.2.0/24"], model.Rows.Select(row => row.NetworkText));
        Assert.Equal("Lab", model.Rows[0].NoteText);
        Assert.Equal(AppText.Missing, model.Rows[1].NoteText);
        Assert.Equal(AppText.ExceptionRemoveName("192.0.2.0/24"), model.Rows[1].RemoveName);
        Assert.True(model.HasRows);
        Assert.Equal("2 exceptions", model.CountText);
        Assert.False(model.HasFeedback);
    }

    [Fact]
    public void Adding_a_network_lists_it_with_its_note_and_clears_the_boxes()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Empty);
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.NetworkText = " 203.0.113.0/24 ";
        model.NoteText = "Head office";

        Assert.True(model.TryAdd());

        ExceptionRow row = Assert.Single(model.Rows);
        Assert.Equal("203.0.113.0/24", row.NetworkText);
        Assert.Equal("Head office", row.NoteText);
        Assert.Equal("", model.NetworkText);
        Assert.Equal("", model.NoteText);
        Assert.Equal(AppText.ExceptionAdded("203.0.113.0/24"), model.FeedbackText);
        Assert.False(model.IsFeedbackError);
        Assert.Equal("1 exception", model.CountText);
        Assert.Contains(nameof(ExceptionsEditorModel.HasRows), changed);
        Assert.Contains(nameof(ExceptionsEditorModel.CountText), changed);
        Assert.Contains(nameof(ExceptionsEditorModel.HasFeedback), changed);
    }

    [Fact]
    public void A_network_with_host_bits_set_is_added_as_its_block_and_the_operator_is_told()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Empty);
        model.NetworkText = "10.1.2.3/16";

        Assert.True(model.TryAdd());

        Assert.Equal("10.1.0.0/16", Assert.Single(model.Rows).NetworkText);
        Assert.Equal(AppText.ExceptionAddedMasked("10.1.0.0/16"), model.FeedbackText);
    }

    [Fact]
    public void A_long_masked_network_is_named_in_full_within_the_feedback_line()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Empty);
        model.NetworkText = "2001:db8:1234:5678:9abc:def0:1234:5679/127";

        Assert.True(model.TryAdd());

        Assert.Contains("2001:db8:1234:5678:9abc:def0:1234:5678/127", model.FeedbackText);
        Assert.InRange(model.FeedbackText.Length, 1, FeedbackLineBudget);
    }

    [Fact]
    public void Every_feedback_text_fits_the_feedback_line_for_the_longest_network()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Empty);
        model.NetworkText = "2000:2000:2000:2000:0:5efe:200.200.200.201/125";
        Assert.True(model.TryAdd());
        Assert.Equal(LongestNetwork, Assert.Single(model.Rows).NetworkText);
        Assert.Equal(AppText.ExceptionAddedMasked(LongestNetwork), model.FeedbackText);

        Assert.All(FeedbackTexts.Values, text => Assert.InRange(text.Length, 1, FeedbackLineBudget));
    }

    [Fact]
    public void Every_text_the_editor_shows_as_feedback_is_budgeted()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "PoorMansFeed", "ExceptionsEditorModel.cs"));
        var named = new List<string>();
        int calls = 0;
        for (int at = source.IndexOf("ShowFeedback(", StringComparison.Ordinal); at >= 0;
             at = source.IndexOf("ShowFeedback(", at + 1, StringComparison.Ordinal))
        {
            // The whole argument list, which can span lines and hold a ternary
            int open = at + "ShowFeedback".Length;
            int depth = 0;
            int close = open;
            for (; close < source.Length; close++)
            {
                if (source[close] == '(') depth++;
                else if (source[close] == ')' && --depth == 0) break;
            }

            string arguments = source[open..close];
            if (arguments.StartsWith("(string text", StringComparison.Ordinal))
                continue;

            // Load clears the line, which shows nothing and so needs no budget
            if (arguments == "(string.Empty, isError: false")
                continue;

            calls++;
            string[] members = Regex.Matches(arguments, @"\bAppText\.(\w+)").Select(match => match.Groups[1].Value).ToArray();
            Assert.True(members.Length > 0, $"A ShowFeedback call names no AppText text: {arguments}");
            // No literal and no concatenation, so the budgeted text is the whole text shown
            Assert.False(arguments.Contains('"'), $"A ShowFeedback call holds a string literal: {arguments}");
            Assert.False(arguments.Contains('+'), $"A ShowFeedback call joins texts: {arguments}");
            named.AddRange(members);
        }

        Assert.True(calls >= 6, $"Only {calls} ShowFeedback calls were found.");
        Assert.All(named.Distinct(), name => Assert.True(FeedbackTexts.ContainsKey(name), $"{name} has no row in FeedbackTexts."));
    }

    [Fact]
    public void Only_ShowFeedback_sets_the_feedback_text()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "PoorMansFeed", "ExceptionsEditorModel.cs"));

        // One assignment to the property, and it sits in ShowFeedback's body, which holds no other braces
        MatchCollection assignments = Regex.Matches(source, @"\bFeedbackText\s*=(?![=>])");
        Assert.Single(assignments);
        int body = source.IndexOf("private void ShowFeedback(string text", StringComparison.Ordinal);
        Assert.True(body >= 0, "ShowFeedback wasn't found.");
        Assert.InRange(assignments[0].Index, body, source.IndexOf('}', body));

        // The field is named only by its declaration, the getter and the setter's SetProperty, so any other write
        // to it adds a fourth mention
        Assert.Equal(3, Regex.Matches(source, @"\b_feedbackText\b").Count);
        Assert.Contains("private string _feedbackText = \"\";", source, StringComparison.Ordinal);
        Assert.Contains("get => _feedbackText;", source, StringComparison.Ordinal);
        Assert.Contains("SetProperty(ref _feedbackText, value)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_address_is_added_as_a_single_address_block()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Empty);
        model.NetworkText = "2001:db8::1";

        Assert.True(model.TryAdd());

        Assert.Equal("2001:db8::1/128", Assert.Single(model.Rows).NetworkText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("010.0.0.1")]
    [InlineData("10.0.0.0/33")]
    [InlineData("example.com")]
    public void Text_that_is_not_a_network_is_refused_and_kept_in_the_box(string text)
    {
        var model = new ExceptionsEditorModel(ExceptionList.Empty);
        model.NetworkText = text;
        model.NoteText = "note";

        Assert.False(model.TryAdd());

        Assert.Empty(model.Rows);
        Assert.Equal(AppText.ExceptionInvalid, model.FeedbackText);
        Assert.True(model.IsFeedbackError);
        Assert.Equal(text, model.NetworkText);
        Assert.Equal("note", model.NoteText);
    }

    [Fact]
    public void A_network_already_listed_is_refused_however_it_is_written()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Create([ExceptionEntryTests.Entry("2001:db8::/32", null)]));
        model.NetworkText = "2001:DB8:0::5/32";

        Assert.False(model.TryAdd());

        Assert.Single(model.Rows);
        Assert.Equal(AppText.ExceptionDuplicate("2001:db8::/32"), model.FeedbackText);
        Assert.True(model.IsFeedbackError);
    }

    [Fact]
    public void A_full_list_refuses_another_network()
    {
        var entries = Enumerable.Range(0, ExceptionList.MaxEntries)
            .Select(i => ExceptionEntryTests.Entry($"10.{i / 256}.{i % 256}.0/24", null));
        var model = new ExceptionsEditorModel(ExceptionList.Create(entries));
        model.NetworkText = "192.0.2.0/24";

        Assert.False(model.TryAdd());

        Assert.Equal(ExceptionList.MaxEntries, model.Rows.Count);
        Assert.Equal(AppText.ExceptionsFull(ExceptionList.MaxEntries), model.FeedbackText);
    }

    [Fact]
    public void A_row_removes_itself()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Create(
        [
            ExceptionEntryTests.Entry("192.0.2.0/24", null),
            ExceptionEntryTests.Entry("198.51.100.0/24", null),
        ]));

        model.Rows[0].RemoveCommand.Execute(null);

        Assert.Equal("198.51.100.0/24", Assert.Single(model.Rows).NetworkText);
        Assert.Equal(AppText.ExceptionRemoved("192.0.2.0/24"), model.FeedbackText);
        Assert.Equal("1 exception", model.CountText);

        model.Rows[0].RemoveCommand.Execute(null);
        Assert.False(model.HasRows);
        Assert.Equal("0 exceptions", model.CountText);
    }

    [Fact]
    public void ToEntries_returns_the_rows_in_their_order()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "kept")]));
        model.NetworkText = "2001:db8::/32";
        model.NoteText = "added";
        Assert.True(model.TryAdd());

        IReadOnlyList<ExceptionEntry> entries = model.ToEntries();

        Assert.Equal(["192.0.2.0/24", "2001:db8::/32"], entries.Select(entry => entry.Network.Text));
        Assert.Equal(["kept", "added"], entries.Select(entry => entry.Note));
    }

    [Fact]
    public void An_unreadable_list_opens_empty_with_the_warning()
    {
        var model = new ExceptionsEditorModel(ExceptionList.Unreadable);

        Assert.Empty(model.Rows);
        Assert.Equal(AppText.ExceptionsUnreadableWarning, model.FeedbackText);
        Assert.True(model.IsFeedbackError);
        Assert.True(model.HasFeedback);
    }

    private static string RepositoryRoot([CallerFilePath] string testFile = "")
    {
        for (DirectoryInfo? directory = new FileInfo(testFile).Directory; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PoorMansFeed.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("The repository root wasn't found.");
    }
}
