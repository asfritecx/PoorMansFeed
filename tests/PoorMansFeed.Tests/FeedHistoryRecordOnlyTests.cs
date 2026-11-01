using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed partial class FeedHistoryRecordOnlyTests
{
    // The feed writers that record their changes; each must be found as a recorder
    private static readonly string[] KnownRecorders =
        ["src/PoorMansFeed.Core/IpInfoService.cs", "src/PoorMansFeed.Core/CloudFeeds/CloudFeedEngine.cs",
         "src/PoorMansFeed.Core/AbuseIpDb/AbuseIpDbService.cs", "src/PoorMansFeed.Core/RetiredFeedWriter.cs"];

    private static readonly (string Name, Regex Pattern)[] HistoryReads =
    [
        ("SearchHistory", new Regex(@"SearchHistory", RegexOptions.CultureInvariant)),
        ("HistoryRead", new Regex(@"HistoryRead", RegexOptions.CultureInvariant)),
        ("HistoryRow", new Regex(@"HistoryRow", RegexOptions.CultureInvariant)),
        ("HistoryQuery", new Regex(@"HistoryQuery", RegexOptions.CultureInvariant)),
        ("FeedHistory", FeedHistoryWord()),
        ("HistoryDatabasePath", new Regex(@"HistoryDatabasePath", RegexOptions.CultureInvariant)),
    ];

    // The files that may read the history; an entry ending in a slash covers its folder
    private static readonly string[] HistoryReaders =
        ["src/PoorMansFeed.Core/FeedRecords.cs", "src/PoorMansFeed.Core/History/",
         "src/PoorMansFeed.Core/SavedFeeds/SavedFeedsService.cs", "src/PoorMansFeed/SavedFeedsViewModel.cs",
         "src/PoorMansFeed/HistoryChangeRow.cs"];

    private static readonly string[] HistoryReadNames =
        ["SearchHistoryAsync", "ClearHistoryAsync", "HistoryRead", "HistoryRow", "HistoryQuery", "HistoryMatch"];

    [GeneratedRegex(@"\bRecordFeedWrite\(", RegexOptions.CultureInvariant)]
    private static partial Regex RecordCall();

    [GeneratedRegex(@"\bFeedRecordResult\s+RecordFeedWrite\(", RegexOptions.CultureInvariant)]
    private static partial Regex RecordDefinition();

    [GeneratedRegex(@"\bFeedHistory\b", RegexOptions.CultureInvariant)]
    private static partial Regex FeedHistoryWord();

    [GeneratedRegex(@"\bHistoryDatabasePath\b", RegexOptions.CultureInvariant)]
    private static partial Regex HistoryPathWord();

    internal static bool IsRecorder(string text) => RecordCall().Count(text) > RecordDefinition().Count(text);

    internal static IEnumerable<string> Breaches(string name, string text)
    {
        if (!IsRecorder(text))
            yield break;
        foreach ((string read, Regex pattern) in HistoryReads)
        {
            if (pattern.IsMatch(text))
                yield return $"{name} names {read}";
        }

        if (!text.Contains(".EnterWrite(", StringComparison.Ordinal))
            yield return $"{name} holds no feed lock";
    }

    private const string Lock = "using IDisposable hold = _ipinfo.Records.EnterWrite(stem);\n";
    private const string Call = "FeedRecordResult result = IpInfoService.RecordFeedWrite(_ipinfo, record);\n";

    [Fact]
    public void The_rule_catches_a_recorder_that_reads_the_history_or_holds_no_lock()
    {
        Assert.Empty(Breaches("Clean.cs", Lock + Call));
        Assert.Equal(["Search.cs names SearchHistory"],
                     Breaches("Search.cs", Lock + Call + "var read = await _ipinfo.Records.SearchHistoryAsync(q, 1, ct);"));
        Assert.Equal(["Rows.cs names HistoryRead", "Rows.cs names HistoryRow", "Rows.cs names HistoryQuery"],
                     Breaches("Rows.cs", Lock + Call + "HistoryRead r; HistoryRow w; HistoryQuery q;"));
        Assert.Equal(["Class.cs names FeedHistory"], Breaches("Class.cs", Lock + Call + "var h = new FeedHistory(path, time);"));
        Assert.Equal(["Path.cs names HistoryDatabasePath"], Breaches("Path.cs", Lock + Call + "File.Exists(paths.HistoryDatabasePath);"));
        Assert.Equal(["Unlocked.cs holds no feed lock"], Breaches("Unlocked.cs", Call));
        // The model file's name holds the class's name but not as a word
        Assert.Empty(Breaches("Models.cs", Lock + Call + "// see FeedHistoryModels"));
    }

    [Fact]
    public void The_definition_alone_doesnt_make_a_recorder()
    {
        const string definition = "internal static FeedRecordResult RecordFeedWrite(IIpInfoService service, FeedWriteRecord record)\n" +
                                  "{ new FeedHistory(path, time); }";

        Assert.False(IsRecorder(definition));
        Assert.Empty(Breaches("IpInfoService.cs", definition));
        Assert.True(IsRecorder(definition + "\nRecordFeedWrite(this, record);"));
    }

    [Fact]
    public void No_recorder_reads_the_history_and_every_recorder_holds_the_feed_lock()
    {
        string root = RepositoryRoot();
        var files = SourceFiles(Path.Combine(root, "src"))
            .Select(file => (Path: Relative(root, file), Text: File.ReadAllText(file)))
            .ToList();
        Assert.NotEmpty(files);

        var recorders = files.Where(file => IsRecorder(file.Text)).Select(file => file.Path).ToHashSet();
        Assert.Subset(recorders, KnownRecorders.ToHashSet());
        Assert.Empty(files.SelectMany(file => Breaches(file.Path, file.Text)));
    }

    [Fact]
    public void Only_the_records_and_the_history_name_the_history_class_and_its_path()
    {
        string root = RepositoryRoot();
        List<string> Naming(Regex pattern) => SourceFiles(Path.Combine(root, "src"))
            .Where(file => pattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Relative(root, file))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["src/PoorMansFeed.Core/AppPaths.cs", "src/PoorMansFeed.Core/FeedRecords.cs"], Naming(HistoryPathWord()));

        List<string> classUsers = Naming(FeedHistoryWord());
        Assert.Contains("src/PoorMansFeed.Core/FeedRecords.cs", classUsers);
        Assert.Contains("src/PoorMansFeed.Core/History/FeedHistory.cs", classUsers);
        Assert.All(classUsers, file => Assert.True(
            file == "src/PoorMansFeed.Core/FeedRecords.cs" || file.StartsWith("src/PoorMansFeed.Core/History/", StringComparison.Ordinal),
            file));
    }

    internal static IEnumerable<string> HistoryReadBreaches(string path, string text)
    {
        if (HistoryReaders.Any(allowed => allowed.EndsWith('/') ? path.StartsWith(allowed, StringComparison.Ordinal) : path == allowed))
            yield break;
        foreach (string name in HistoryReadNames)
        {
            if (Regex.IsMatch(text, @"\b" + name + @"\b", RegexOptions.CultureInvariant))
                yield return $"{path} names {name}";
        }
    }

    [Fact]
    public void The_allow_list_catches_each_history_read_name_outside_it()
    {
        const string text = "await r.SearchHistoryAsync(q, 1, ct); await r.ClearHistoryAsync(ct); " +
                            "HistoryRead a; HistoryRow b; HistoryQuery c; HistoryMatch.Of(d);";

        Assert.Equal(["SearchHistoryAsync", "ClearHistoryAsync", "HistoryRead", "HistoryRow", "HistoryQuery", "HistoryMatch"],
                     HistoryReadBreaches("src/PoorMansFeed/Pages/Skip.cs", text).Select(breach => breach.Split(' ')[^1]));
        Assert.Equal(["src/PoorMansFeed.Core/FeedRecordsX.cs names HistoryRow"],
                     HistoryReadBreaches("src/PoorMansFeed.Core/FeedRecordsX.cs", "HistoryRow b;"));
        Assert.Empty(HistoryReadBreaches("src/PoorMansFeed.Core/Other.cs", "HistoryRows b; MyHistoryQuery c;"));
        Assert.Empty(HistoryReadBreaches("src/PoorMansFeed.Core/FeedRecords.cs", text));
        Assert.Empty(HistoryReadBreaches("src/PoorMansFeed.Core/History/Any.cs", text));
    }

    [Fact]
    public void Only_the_allow_list_names_what_reads_the_history()
    {
        string root = RepositoryRoot();
        var files = SourceFiles(Path.Combine(root, "src")).ToList();
        Assert.NotEmpty(files);

        Assert.Empty(files.SelectMany(file => HistoryReadBreaches(Relative(root, file), File.ReadAllText(file))));
    }

    // A save or retire made through the records alone would skip the feed lock and leave the files as they were.
    // The call is matched as well as the type, because a target-typed new never names it
    private static readonly string[] RecordRequesters =
        ["src/PoorMansFeed.Core/FeedRecords.cs", "src/PoorMansFeed.Core/IpInfoService.cs",
         "src/PoorMansFeed.Core/History/FeedHistory.cs"];

    private static readonly Regex RecordRequest = new(@"\bFeedRecordRequest\b|\bRecord\s*\(", RegexOptions.CultureInvariant);

    internal static IEnumerable<string> RecordRequestBreaches(string path, string text)
    {
        if (!RecordRequesters.Contains(path) && RecordRequest.IsMatch(text))
            yield return $"{path} makes a record request";
    }

    [Fact]
    public void The_allow_list_catches_a_record_request_outside_it()
    {
        Assert.Equal(["src/PoorMansFeed/SavedFeedsViewModel.cs makes a record request"],
                     RecordRequestBreaches("src/PoorMansFeed/SavedFeedsViewModel.cs",
                                           "records.Record(new FeedRecordRequest(key, kind, null, now, intent));"));
        Assert.Equal(["src/PoorMansFeed.Core/Other.cs makes a record request"],
                     RecordRequestBreaches("src/PoorMansFeed.Core/Other.cs",
                                           "ipinfo.Records.Record (new(\"AS15169\", FeedKind.Asn, null, now, intent));"));
        Assert.Empty(RecordRequestBreaches("src/PoorMansFeed.Core/Other.cs", "MyFeedRecordRequests r; RecordFeedWrite(s, w);"));
        Assert.Empty(RecordRequestBreaches("src/PoorMansFeed.Core/IpInfoService.cs", "Records.Record(new(k, x, c, t))"));
    }

    [Fact]
    public void A_record_request_cant_be_made_outside_Core()
    {
        Assert.False(typeof(FeedRecordRequest).IsPublic);
        MethodInfo? record = typeof(IFeedRecords).GetMethod(nameof(IFeedRecords.Record),
                                                            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(record);
        Assert.True(record.IsAssembly);
    }

    [Fact]
    public void Only_the_recording_point_makes_a_record_request()
    {
        string root = RepositoryRoot();
        var files = SourceFiles(Path.Combine(root, "src")).ToList();
        Assert.NotEmpty(files);

        Assert.Empty(files.SelectMany(file => RecordRequestBreaches(Relative(root, file), File.ReadAllText(file))));
    }

    private static IEnumerable<string> SourceFiles(string folder)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(separator + "obj" + separator, StringComparison.Ordinal)
                && !file.Contains(separator + "bin" + separator, StringComparison.Ordinal));
    }

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

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
