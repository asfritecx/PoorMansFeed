using System.Text;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class FeedFileEntriesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Write(string name, string text)
    {
        string path = _folder.File(name);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
        return path;
    }

    private static string[] Texts(IEnumerable<FeedEntry> entries) => entries.Select(entry => entry.Text).ToArray();

    private static string KeyOf(string line)
    {
        Assert.True(FeedFileEntries.TryParse(line, out FeedEntry entry));
        return entry.Key;
    }

    private static void AssertNone(FeedFileState state, FeedFileRead read)
    {
        Assert.Equal(state, read.State);
        Assert.Empty(read.Entries);
    }

    [Fact]
    public void A_missing_file_is_missing_with_no_entries()
    {
        AssertNone(FeedFileState.Missing, FeedFileEntries.Read(_folder.File("missing.txt")));
    }

    [Fact]
    public void A_file_in_a_missing_folder_is_missing_with_no_entries()
    {
        AssertNone(FeedFileState.Missing, FeedFileEntries.Read(Path.Combine(_folder.Path, "no-folder", "missing.txt")));
    }

    [Fact]
    public void A_folder_at_the_path_is_missing_with_no_entries()
    {
        string path = _folder.File("feed.txt");
        Directory.CreateDirectory(path);

        AssertNone(FeedFileState.Missing, FeedFileEntries.Read(path));
    }

    [Fact]
    public void An_empty_file_is_read_with_no_entries()
    {
        AssertNone(FeedFileState.Read, FeedFileEntries.Read(Write("feed.txt", "")));
    }

    [Fact]
    public void A_file_with_no_feed_line_is_read_with_no_entries()
    {
        AssertNone(FeedFileState.Read, FeedFileEntries.Read(Write("feed.txt", "# header only\n\njunk\n")));
    }

    [Fact]
    public void A_file_the_account_cant_open_is_unreadable_with_no_entries()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows has no Unix file mode.");
            return;
        }

        string path = Write("feed.txt", "8.8.8.8\n");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            try
            {
                File.ReadAllBytes(path);
                Assert.Skip("This account can read a file with no permissions.");
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            AssertNone(FeedFileState.Unreadable, FeedFileEntries.Read(path));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void A_file_that_grows_past_the_bound_while_it_is_read_is_unreadable_with_no_entries()
    {
        const int bound = 1024 * 1024;
        string path = Write("feed.txt", string.Concat(Enumerable.Repeat("8.8.8.8\n", bound / 8)));
        var listener = new RecordingTraceListener();
        using var stop = new CancellationTokenSource();
        Thread grower = new(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            while (!stop.IsCancellationRequested)
            {
                stream.SetLength(bound * 2L);
                stream.SetLength(bound);
            }
        });
        System.Diagnostics.Trace.Listeners.Add(listener);
        FeedFileRead? grown = null;
        try
        {
            grower.Start();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (grown is null && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                int heard = listener.Count(GrewTrace);
                FeedFileRead read = FeedFileEntries.Read(path, bound);
                if (listener.Count(GrewTrace) > heard)
                    grown = read;
            }
        }
        finally
        {
            stop.Cancel();
            grower.Join();
            System.Diagnostics.Trace.Listeners.Remove(listener);
        }

        if (grown is null)
        {
            Assert.Skip("The file never grew between its size check and its read in this run.");
            return;
        }

        AssertNone(FeedFileState.Unreadable, grown.Value);
    }

    private const string GrewTrace = "A feed file grew too large to compare with";

    // Trace listeners are process-wide, so this also hears other tests; it only counts a text they don't write
    private sealed class RecordingTraceListener : System.Diagnostics.TraceListener
    {
        private readonly StringBuilder _text = new();

        public int Count(string text)
        {
            lock (_text)
                return _text.ToString().Split(text).Length - 1;
        }

        public override void Write(string? message)
        {
            lock (_text)
                _text.Append(message);
        }

        public override void WriteLine(string? message)
        {
            lock (_text)
                _text.AppendLine(message);
        }
    }

    [Fact]
    public void A_link_is_never_followed()
    {
        string target = Write("target.txt", "8.8.8.8\n9.9.9.9\n");
        string link = _folder.File("feed.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account can't make a symbolic link.");
            return;
        }

        Assert.Equal(2, FeedFileEntries.Read(target).Entries.Count);
        AssertNone(FeedFileState.Unreadable, FeedFileEntries.Read(link));
    }

    [Fact]
    public void A_file_over_the_bound_is_unreadable_with_no_entries()
    {
        string path = Write("feed.txt", "8.8.8.8\n9.9.9.9\n");
        long length = new FileInfo(path).Length;

        FeedFileRead within = FeedFileEntries.Read(path, length);
        Assert.Equal(FeedFileState.Read, within.State);
        Assert.Equal(2, within.Entries.Count);
        AssertNone(FeedFileState.Unreadable, FeedFileEntries.Read(path, length - 1));
    }

    [Fact]
    public void A_byte_that_isnt_utf8_costs_only_its_own_line()
    {
        string path = _folder.File("feed.txt");
        File.WriteAllBytes(path, [.. Encoding.ASCII.GetBytes("# note "), 0xE9, (byte)'\n', .. Encoding.ASCII.GetBytes("8.8.8.8\n"),
                                  0xFF, 0xFE, (byte)'\n', .. Encoding.ASCII.GetBytes("9.9.9.9"), 0xC3, (byte)'\n',
                                  .. Encoding.ASCII.GetBytes("1.1.1.1\n")]);

        Assert.Equal(["8.8.8.8", "1.1.1.1"], Texts(FeedFileEntries.Read(path).Entries));
    }

    [Fact]
    public async Task A_fifo_is_never_opened()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows has no FIFO in the file system.");
            return;
        }

        // Opening a FIFO for reading blocks until a writer opens it, so a read that opened it would hang here
        string path = _folder.File("feed.txt");
        Run("/usr/bin/mkfifo", path);
        Task<FeedFileRead> read = Task.Run(() => FeedFileEntries.Read(path));
        await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));
        bool finished = read.IsCompleted;
        if (!finished)
        {
            // Opening for write frees a read stuck in its open but blocks while none is, so it runs apart, unawaited
            _ = Task.Run(() => new FileStream(path, FileMode.Open, FileAccess.Write).Dispose());
            await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        Assert.True(finished);
        AssertNone(FeedFileState.Read, await read);
    }

    private static void Run(string program, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void A_byte_order_mark_is_dropped()
    {
        string path = _folder.File("feed.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.ASCII.GetBytes("8.8.8.8\n9.9.9.9\n")]);

        Assert.Equal(["8.8.8.8", "9.9.9.9"], Texts(FeedFileEntries.Read(path).Entries));
    }

    [Fact]
    public void Crlf_line_ends_are_read_and_the_cr_dropped()
    {
        string path = Write("feed.txt", "# header\r\n8.8.8.8\r\n10.0.0.0/8\r\n2001:4860::8888");

        Assert.Equal(["8.8.8.8", "10.0.0.0/8", "2001:4860::8888"], Texts(FeedFileEntries.Read(path).Entries));
    }

    [Fact]
    public void Blank_whitespace_and_hash_lines_are_skipped_wherever_they_are()
    {
        string path = Write("feed.txt", "# PoorMansFeed\n# Entries: 3\n\n8.8.8.8\n   \n\t\n# a note in the middle\n9.9.9.9\n\n10.0.0.0/8\n");

        Assert.Equal(["8.8.8.8", "9.9.9.9", "10.0.0.0/8"], Texts(FeedFileEntries.Read(path).Entries));
    }

    [Fact]
    public void Junk_lines_are_skipped_and_the_rest_read()
    {
        string path = Write("feed.txt",
            "8.8.8.8\nnot an address\n010.0.0.1\n10.0.0.0/33\n 9.9.9.9\n9.9.9.9\n::ffff:zz\n2001:4860::8888/129\n2001:4860::8888\n8.8.8.8/\n");

        Assert.Equal(["8.8.8.8", "9.9.9.9", "2001:4860::8888"], Texts(FeedFileEntries.Read(path).Entries));
    }

    [Fact]
    public void An_unsplit_file_gives_both_families_in_file_order()
    {
        string path = Write("feed.txt", "8.8.8.8\n2001:4860::8888\n10.0.0.0/8\n2001:4860::/32\n");

        FeedFileRead read = FeedFileEntries.Read(path);
        IReadOnlyList<FeedEntry> entries = read.Entries;

        Assert.Equal(FeedFileState.Read, read.State);
        Assert.Equal(["8.8.8.8", "2001:4860::8888", "10.0.0.0/8", "2001:4860::/32"], Texts(entries));
        Assert.Equal([false, true, false, true], entries.Select(entry => entry.IsIPv6).ToArray());
    }

    [Fact]
    public void Read_never_throws_for_any_content()
    {
        var random = new Random(4242);
        for (int i = 0; i < 200; i++)
        {
            byte[] bytes = new byte[random.Next(1, 300)];
            random.NextBytes(bytes);
            string path = _folder.File($"random-{i}.txt");
            File.WriteAllBytes(path, bytes);

            _ = FeedFileEntries.Read(path);
        }

        AssertNone(FeedFileState.Missing, FeedFileEntries.Read(_folder.Path));
    }

    [Theory]
    [InlineData("10.0.0.1/32", "10.0.0.1")]
    [InlineData("2001:4860::8888/128", "2001:4860::8888")]
    [InlineData("10.0.0.1/8", "10.0.0.0/8")]
    [InlineData("10.255.255.255/8", "10.0.0.0/8")]
    [InlineData("2001:4860::8888/32", "2001:4860::/32")]
    [InlineData("2001:4860::ABCD", "2001:4860::abcd")]
    [InlineData("2001:4860:0000:0000:0000:0000:0000:8888", "2001:4860::8888")]
    [InlineData("2001:4860:0:0:0:0:0:8888/128", "2001:4860::8888")]
    public void Lines_that_name_the_same_network_have_the_same_key(string line, string same)
    {
        Assert.Equal(KeyOf(same), KeyOf(line));
    }

    [Theory]
    [InlineData("10.0.0.0/8", "10.0.0.0/9")]
    [InlineData("10.0.0.0/8", "11.0.0.0/8")]
    [InlineData("10.0.0.0/8", "9.255.255.255/8")]
    [InlineData("10.0.0.0/8", "::a00:0/104")]
    [InlineData("10.0.0.1", "::a00:1")]
    [InlineData("10.0.0.1", "::ffff:10.0.0.1")]
    [InlineData("2001:4860::8888", "2001:4860::8889")]
    [InlineData("2001:4860::/32", "2001:4860::/33")]
    public void Lines_that_name_different_networks_have_different_keys(string line, string other)
    {
        Assert.NotEqual(KeyOf(other), KeyOf(line));
    }

    [Fact]
    public void The_family_comes_from_the_parsed_address_and_the_text_is_kept_as_given()
    {
        Assert.True(FeedFileEntries.TryParse("10.0.0.1/8", out FeedEntry ipv4));
        Assert.False(ipv4.IsIPv6);
        Assert.Equal("10.0.0.1/8", ipv4.Text);

        Assert.True(FeedFileEntries.TryParse("::a00:0/104", out FeedEntry decoy));
        Assert.True(decoy.IsIPv6);

        Assert.False(FeedFileEntries.TryParse("", out _));
        Assert.False(FeedFileEntries.TryParse("# 8.8.8.8", out _));
        Assert.False(FeedFileEntries.TryParse("8.8.8.8 ", out _));
    }

    private const string FeedPath = "/feeds/feed.txt";

    private static FeedFileRead Before(params string[] lines) => new(FeedFileState.Read, lines.Select(line =>
    {
        Assert.True(FeedFileEntries.TryParse(line, out FeedEntry entry));
        return entry;
    }).ToArray());

    private static FeedFileChange Compare(FeedFileRead before, IEnumerable<string> written) =>
        FeedFileEntries.Compare(FeedPath, FeedType.Fortigate, FeedFamilies.Both, before, written);

    [Fact]
    public void Writing_the_lines_the_file_holds_changes_nothing()
    {
        string[] lines = ["8.8.8.8", "10.0.0.0/8", "2001:4860::8888"];

        FeedFileChange change = Compare(Before(lines), lines);

        Assert.True(change.IsEmpty);
        Assert.Equal(FeedPath, change.Path);
    }

    [Fact]
    public void The_same_networks_in_another_form_change_nothing()
    {
        FeedFileChange change = Compare(
            Before("8.8.8.8/32", "10.0.0.1/8", "2001:4860:0:0:0:0:0:8888/128"),
            ["8.8.8.8", "10.0.0.0/8", "2001:4860::8888"]);

        Assert.True(change.IsEmpty);
    }

    [Fact]
    public void A_missing_file_makes_every_written_line_added_in_write_order()
    {
        FeedFileChange change = Compare(new FeedFileRead(FeedFileState.Missing, []),
                                        ["9.9.9.9", "8.8.8.8", "2001:4860::8888"]);

        Assert.Equal(["9.9.9.9", "8.8.8.8"], change.IPv4.Added);
        Assert.Empty(change.IPv4.Removed);
        Assert.Equal(["2001:4860::8888"], change.IPv6.Added);
        Assert.Empty(change.IPv6.Removed);
    }

    [Fact]
    public void Added_is_as_written_and_removed_as_the_file_had_it_each_in_its_own_order()
    {
        FeedFileChange change = Compare(
            Before("9.9.9.9/32", "10.0.0.0/8", "8.8.8.8/32", "172.16.0.0/12"),
            ["10.0.0.0/8", "185.199.108.153", "8.8.4.4", "1.1.1.0/24"]);

        Assert.Equal(["185.199.108.153", "8.8.4.4", "1.1.1.0/24"], change.IPv4.Added);
        Assert.Equal(["9.9.9.9/32", "8.8.8.8/32", "172.16.0.0/12"], change.IPv4.Removed);
        Assert.True(change.IPv6.IsEmpty);
        Assert.False(change.IsEmpty);
    }

    [Fact]
    public void Duplicates_count_once_on_either_side()
    {
        FeedFileChange change = Compare(
            Before("9.9.9.9", "9.9.9.9/32", "8.8.8.8"),
            ["10.0.0.0/8", "10.0.0.1/8", "10.0.0.0/8", "8.8.8.8"]);

        Assert.Equal(["10.0.0.0/8"], change.IPv4.Added);
        Assert.Equal(["9.9.9.9"], change.IPv4.Removed);
    }

    [Fact]
    public void Each_family_gets_its_own_changes()
    {
        FeedFileChange change = Compare(
            Before("8.8.8.8", "2001:4860::8888", "10.0.0.0/8"),
            ["8.8.8.8", "2001:4860::/32", "::a00:0/104"]);

        Assert.Empty(change.IPv4.Added);
        Assert.Equal(["10.0.0.0/8"], change.IPv4.Removed);
        Assert.Equal(["2001:4860::/32", "::a00:0/104"], change.IPv6.Added);
        Assert.Equal(["2001:4860::8888"], change.IPv6.Removed);
    }

    [Fact]
    public void A_written_line_that_doesnt_parse_is_always_added_to_the_family_its_colon_says()
    {
        FeedFileChange change = Compare(
            Before("8.8.8.8", "2001:4860::8888"),
            ["8.8.8.8", "8.8.8.8 ", "2001:4860::8888", "2001:4860::zz"]);

        Assert.Equal(["8.8.8.8 "], change.IPv4.Added);
        Assert.Equal(["2001:4860::zz"], change.IPv6.Added);
        Assert.Empty(change.IPv4.Removed);
        Assert.Empty(change.IPv6.Removed);
    }

    [Fact]
    public void A_written_line_that_doesnt_parse_never_matches_an_entry_even_when_it_reads_as_its_key()
    {
        FeedFileRead before = Before("10.0.0.0/8");
        string decoy = before.Entries[0].Key;

        FeedFileChange change = Compare(before, [decoy]);

        Assert.Equal([decoy], change.IPv6.Added);
        Assert.Equal(["10.0.0.0/8"], change.IPv4.Removed);
    }

    [Fact]
    public void Writing_nothing_removes_every_entry()
    {
        FeedFileChange change = Compare(Before("8.8.8.8", "2001:4860::8888"), []);

        Assert.Equal(["8.8.8.8"], change.IPv4.Removed);
        Assert.Equal(["2001:4860::8888"], change.IPv6.Removed);
        Assert.Empty(change.IPv4.Added);
        Assert.Empty(change.IPv6.Added);
    }

    [Fact]
    public void No_changes_is_empty()
    {
        Assert.True(FeedEntryChanges.None.IsEmpty);
        Assert.True(new FeedFileChange(FeedPath, FeedType.Fortigate, FeedFamilies.IPv4, FeedEntryChanges.None,
                                       FeedEntryChanges.None, FeedFileState.Read).IsEmpty);
        Assert.False(new FeedFileChange(FeedPath, FeedType.Fortigate, FeedFamilies.IPv6, FeedEntryChanges.None,
                                        new FeedEntryChanges([], ["::1"]), FeedFileState.Read).IsEmpty);
    }

    [Theory]
    [InlineData(FeedType.Fortigate)]
    [InlineData(FeedType.ModSecurity)]
    public void Compare_carries_the_feed_type(FeedType type)
    {
        FeedFileChange change = FeedFileEntries.Compare(FeedPath, type, FeedFamilies.IPv4, Before("8.8.8.8"), ["8.8.8.8"]);

        Assert.Equal(type, change.Type);
    }

    [Theory]
    [InlineData(FeedFamilies.IPv4)]
    [InlineData(FeedFamilies.IPv6)]
    [InlineData(FeedFamilies.Both)]
    public void Compare_carries_the_families_the_file_holds(FeedFamilies families)
    {
        FeedFileChange change =
            FeedFileEntries.Compare(FeedPath, FeedType.ModSecurity, families, Before("8.8.8.8"), ["8.8.8.8"]);

        Assert.Equal(families, change.Families);
    }

    [Theory]
    [InlineData(FeedFileState.Missing)]
    [InlineData(FeedFileState.Unreadable)]
    [InlineData(FeedFileState.Read)]
    public void Compare_carries_the_old_files_state_and_compares_as_before(FeedFileState state)
    {
        FeedFileChange change = Compare(new FeedFileRead(state, []), ["8.8.8.8"]);

        Assert.Equal(state, change.OldFile);
        Assert.Equal(["8.8.8.8"], change.IPv4.Added);
    }

    [Fact]
    public void The_hash_is_of_the_bytes_of_a_whole_regular_file_read()
    {
        string path = Write("feed.txt", "# head \u00e9\r\n8.8.8.8\n\uFEFF9.9.9.9\n");

        FeedFileRead read = FeedFileEntries.Read(path);

        Assert.Equal(FeedFileState.Read, read.State);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), read.Sha256);
    }

    [Fact]
    public void No_hash_for_a_missing_path_a_folder_an_empty_file_or_one_over_the_bound()
    {
        Assert.Null(FeedFileEntries.Read(_folder.File("missing.txt")).Sha256);
        string folder = _folder.File("folder.txt");
        Directory.CreateDirectory(folder);
        Assert.Null(FeedFileEntries.Read(folder).Sha256);
        Assert.Null(FeedFileEntries.Read(Write("empty.txt", "")).Sha256);
        string path = Write("feed.txt", "8.8.8.8\n9.9.9.9\n");
        long length = new FileInfo(path).Length;
        Assert.NotNull(FeedFileEntries.Read(path, length).Sha256);
        Assert.Null(FeedFileEntries.Read(path, length - 1).Sha256);
    }

    [Fact]
    public void No_hash_for_a_link_even_to_a_regular_file()
    {
        string target = Write("target.txt", "8.8.8.8\n");
        string link = _folder.File("feed.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account can't make a symbolic link.");
            return;
        }

        Assert.NotNull(FeedFileEntries.Read(target).Sha256);
        Assert.Null(FeedFileEntries.Read(link).Sha256);
    }
}
