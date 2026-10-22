using System.Text;
using System.Text.Json;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

/// <summary>exceptions.json: a missing file is an empty list, anything wrong with it fails closed, and a save is atomic.</summary>
public sealed class ExceptionListStoreTests
{
    [Fact]
    public void A_missing_file_is_an_empty_readable_list_and_is_not_created()
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");

        ExceptionList list = new ExceptionListStore(path).Load();

        Assert.True(list.IsReadable);
        Assert.Empty(list.Entries);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_file_of_ToJson_loads_every_entry_its_note_and_the_order()
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");
        var store = new ExceptionListStore(path);
        ExceptionList saved = ExceptionList.Create(
        [
            ExceptionEntryTests.Entry("2001:db8::/32", "Lab"),
            ExceptionEntryTests.Entry("192.0.2.0/24", "Head office"),
            ExceptionEntryTests.Entry("198.51.100.7", null),
        ]);

        File.WriteAllBytes(path, ExceptionListStore.ToJson(saved));
        ExceptionList loaded = store.Load();

        Assert.True(loaded.IsReadable);
        Assert.Equal(saved.Entries, loaded.Entries);
    }

    [Fact]
    public void The_file_has_a_schema_version_and_one_object_per_exception()
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");

        File.WriteAllBytes(path, ExceptionListStore.ToJson(ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "Head office")])));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        JsonElement entry = Assert.Single(document.RootElement.GetProperty("exceptions").EnumerateArray().ToList());
        Assert.Equal("192.0.2.0/24", entry.GetProperty("network").GetString());
        Assert.Equal("Head office", entry.GetProperty("note").GetString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ \"schemaVersion\": 2, \"exceptions\": [] }")]
    [InlineData("{ \"schemaVersion\": 0, \"exceptions\": [] }")]
    [InlineData("{ \"exceptions\": [] }")]
    [InlineData("{ \"schemaVersion\": 1 }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": null }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": [ null ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": [ { \"note\": \"no network\" } ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": [ { \"network\": \"010.0.0.1\" } ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": [ { \"network\": \"10.0.0.0/33\" } ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": [ { \"network\": \"192.0.2.0/24\" }, { \"network\": \"bad\" } ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": [ { \"network\": 42 } ] }")]
    public void A_file_that_is_not_the_expected_form_is_unreadable_and_left_as_it_was(string content)
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");
        File.WriteAllText(path, content);
        byte[] before = File.ReadAllBytes(path);

        ExceptionList list = new ExceptionListStore(path).Load();

        Assert.False(list.IsReadable);
        Assert.Throws<InvalidOperationException>(() => list.Entries);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_file_with_more_entries_than_the_cap_is_unreadable()
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");
        var entries = Enumerable.Range(0, ExceptionList.MaxEntries + 1)
            .Select(i => new { network = $"10.{i / 256}.{i % 256}.0/24", note = "" });
        File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 1, exceptions = entries }));

        Assert.False(new ExceptionListStore(path).Load().IsReadable);
    }

    [Fact]
    public void A_file_larger_than_the_size_cap_is_unreadable_and_left_as_it_was()
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");
        File.WriteAllBytes(path, ValidFileOfLength((int)ExceptionListStore.MaxFileBytes + 1));
        byte[] before = File.ReadAllBytes(path);

        ExceptionList list = new ExceptionListStore(path).Load();

        Assert.False(list.IsReadable);
        Assert.Throws<InvalidOperationException>(() => list.Entries);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_file_of_exactly_the_size_cap_loads()
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");
        File.WriteAllBytes(path, ValidFileOfLength((int)ExceptionListStore.MaxFileBytes));

        ExceptionList list = new ExceptionListStore(path).Load();

        Assert.True(list.IsReadable);
        Assert.Equal("192.0.2.0/24", Assert.Single(list.Entries).Network.Text);
    }

    [Fact]
    public void The_largest_list_ToJson_can_give_is_under_the_size_cap_and_loads_again()
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");
        var store = new ExceptionListStore(path);
        // A 43-character IPv6 network (four hex digits a group, so nothing is compressed) and a full note of a
        // character the encoder writes as six bytes.
        string note = new('\u00E9', ExceptionEntry.MaxNoteLength);
        ExceptionList largest = ExceptionList.Create(Enumerable.Range(0, ExceptionList.MaxEntries)
            .Select(i => ExceptionEntryTests.Entry($"1111:2222:3333:4444:5555:6666:{0x1000 + i / 4096:x4}:{0x1000 + i % 4096:x4}/128", note)));
        Assert.Equal(ExceptionList.MaxEntries, largest.Entries.Count);

        File.WriteAllBytes(path, ExceptionListStore.ToJson(largest));
        long length = new FileInfo(path).Length;
        ExceptionList loaded = store.Load();

        Assert.InRange(length, 6_000_000, ExceptionListStore.MaxFileBytes);
        Assert.True(loaded.IsReadable);
        Assert.Equal(largest.Entries, loaded.Entries);
        Assert.All(loaded.Entries, entry => Assert.Equal(note, entry.Note));
    }

    [Fact]
    public void A_repeated_network_keeps_its_first_entry()
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");
        File.WriteAllText(path, """
            { "schemaVersion": 1, "exceptions": [
              { "network": "192.0.2.0/24", "note": "first" },
              { "network": "192.0.2.77/24", "note": "again" } ] }
            """);

        ExceptionList list = new ExceptionListStore(path).Load();

        Assert.True(list.IsReadable);
        ExceptionEntry entry = Assert.Single(list.Entries);
        Assert.Equal("first", entry.Note);
    }

    [Fact]
    public void A_hand_written_file_is_read_with_host_bits_cleared_and_notes_on_one_line()
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");
        File.WriteAllText(path, """
            { "schemaVersion": 1, "exceptions": [
              { "network": " 10.1.2.3/8 ", "note": "line one\nline two" },
              { "network": "2001:db8::1" } ] }
            """);

        ExceptionList list = new ExceptionListStore(path).Load();

        Assert.True(list.IsReadable);
        Assert.Equal(["10.0.0.0/8", "2001:db8::1/128"], list.Entries.Select(entry => entry.Network.Text));
        Assert.Equal("line one line two", list.Entries[0].Note);
        Assert.Equal("", list.Entries[1].Note);
    }

    [Fact]
    public void A_file_that_starts_with_a_byte_order_mark_loads_as_Notepad_saves_it()
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");
        byte[] json = Encoding.UTF8.GetBytes("""
            { "schemaVersion": 1, "exceptions": [
              { "network": "192.0.2.0/24", "note": "Head office" },
              { "network": "2001:db8::/32", "note": "Lab" } ] }
            """);
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. json]);

        ExceptionList list = new ExceptionListStore(path).Load();

        Assert.True(list.IsReadable);
        Assert.Equal(["192.0.2.0/24", "2001:db8::/32"], list.Entries.Select(entry => entry.Network.Text));
        Assert.Equal(["Head office", "Lab"], list.Entries.Select(entry => entry.Note));
    }

    [Fact]
    public void Only_one_byte_order_mark_is_skipped()
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");
        byte[] json = Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"exceptions\": [] }");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, .. json]);

        Assert.False(new ExceptionListStore(path).Load().IsReadable);
    }

    /// <summary>A valid one-entry file, padded with spaces after the JSON to exactly the given length.</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ \"schemaVersion\": 2, \"exceptions\": [] }")]
    [InlineData("{ \"schemaVersion\": 0, \"exceptions\": [] }")]
    [InlineData("{ \"exceptions\": [] }")]
    [InlineData("{ \"schemaVersion\": 1 }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": null }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": [ null ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": [ { \"note\": \"no network\" } ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": [ { \"network\": \"010.0.0.1\" } ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": [ { \"network\": \"10.0.0.0/33\" } ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": [ { \"network\": \"192.0.2.0/24\" }, { \"network\": \"bad\" } ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"exceptions\": [ { \"network\": 42 } ] }")]
    public void Read_gives_unreadable_for_bytes_that_are_not_the_expected_form(string content)
    {
        ExceptionList list = ExceptionListStore.Read(Encoding.UTF8.GetBytes(content));

        Assert.False(list.IsReadable);
    }

    [Fact]
    public void Read_gives_unreadable_over_the_entry_cap_and_over_the_size_cap()
    {
        var entries = Enumerable.Range(0, ExceptionList.MaxEntries + 1)
            .Select(i => new { network = $"10.{i / 256}.{i % 256}.0/24", note = "" });

        Assert.False(ExceptionListStore.Read(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, exceptions = entries })).IsReadable);
        Assert.False(ExceptionListStore.Read(ValidFileOfLength((int)ExceptionListStore.MaxFileBytes + 1)).IsReadable);
        Assert.True(ExceptionListStore.Read(ValidFileOfLength((int)ExceptionListStore.MaxFileBytes)).IsReadable);
    }

    [Fact]
    public void Read_skips_one_byte_order_mark_and_refuses_two()
    {
        byte[] json = Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"exceptions\": [ { \"network\": \"192.0.2.0/24\", \"note\": \"Head office\" } ] }");

        ExceptionList one = ExceptionListStore.Read([0xEF, 0xBB, 0xBF, .. json]);

        Assert.True(one.IsReadable);
        Assert.Equal("Head office", Assert.Single(one.Entries).Note);
        Assert.False(ExceptionListStore.Read([0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, .. json]).IsReadable);
    }

    [Fact]
    public void ToJson_then_Read_keeps_every_entry_its_note_and_the_order()
    {
        ExceptionList saved = ExceptionList.Create(
        [
            ExceptionEntryTests.Entry("2001:db8::/32", "Lab"),
            ExceptionEntryTests.Entry("192.0.2.0/24", "Head office"),
            ExceptionEntryTests.Entry("198.51.100.7", null),
        ]);

        ExceptionList read = ExceptionListStore.Read(ExceptionListStore.ToJson(saved));

        Assert.True(read.IsReadable);
        Assert.Equal(saved.Entries, read.Entries);
    }

    [Fact]
    public void ToJson_gives_a_file_with_no_byte_order_mark_that_Load_reads()
    {
        using var temp = new TempFolder();
        string path = temp.File("exceptions.json");
        ExceptionList list = ExceptionList.Create(
        [
            ExceptionEntryTests.Entry("192.0.2.0/24", "Head office \u00e9"),
            ExceptionEntryTests.Entry("2001:db8::/32", null),
        ]);
        byte[] json = ExceptionListStore.ToJson(list);

        Assert.False(json.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        File.WriteAllBytes(path, json);
        Assert.Equal(list.Entries, new ExceptionListStore(path).Load().Entries);
        Assert.Equal(ExceptionListStore.ToJson(ExceptionList.Empty), ExceptionListStore.ToJson(ExceptionList.Create([])));
    }

    private static byte[] ValidFileOfLength(int length)
    {
        byte[] json = Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"exceptions\": [ { \"network\": \"192.0.2.0/24\" } ] }");
        byte[] file = new byte[length];
        Array.Fill(file, (byte)' ');
        json.CopyTo(file, 0);
        return file;
    }
}
