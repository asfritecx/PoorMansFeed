using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

public sealed class FeedExceptionsRefusalTests
{
    private const string ReadableExceptionsFile = "src/PoorMansFeed.Core/FeedExceptions/ReadableExceptions.cs";
    private const string IpInfoServiceFile = "src/PoorMansFeed.Core/IpInfoService.cs";

    private static ExceptionList ListOf(params string[] networks) =>
        ExceptionList.Create(networks.Select(network =>
        {
            Assert.True(ExceptionEntry.TryParseNetwork(network, out CidrBlock? block, out _));
            return new ExceptionEntry(block, null);
        }));

    private static (byte[] Start, byte[] End) Range(string cidr) => Cidr.Parse(cidr);

    [Fact]
    public void An_unreadable_list_gives_no_exceptions_for_a_feed()
    {
        var service = new FakeIpInfoService { Exceptions = ExceptionList.Unreadable };

        Assert.Null(IpInfoService.ExceptionsForFeed(service));
    }

    [Fact]
    public void An_empty_list_gives_exceptions_that_cut_and_cover_nothing()
    {
        var service = new FakeIpInfoService { Exceptions = ExceptionList.Empty };

        ReadableExceptions? exceptions = IpInfoService.ExceptionsForFeed(service);

        Assert.NotNull(exceptions);
        (byte[] start, byte[] end) = Range("10.0.0.0/8");
        Assert.Null(exceptions.Cut(start, end));
        Assert.False(exceptions.Covers(Range("10.0.0.1").Start));
    }

    [Fact]
    public void A_list_with_entries_cuts_and_covers_as_the_list_does()
    {
        ExceptionList list = ListOf("10.1.0.0/16", "2001:4860::/32");
        var service = new FakeIpInfoService { Exceptions = list };

        ReadableExceptions? exceptions = IpInfoService.ExceptionsForFeed(service);

        Assert.NotNull(exceptions);
        foreach (string cidr in new[] { "10.0.0.0/8", "10.1.0.0/16", "8.8.8.0/24", "2001::/16", "2001:4860::/32" })
        {
            (byte[] start, byte[] end) = Range(cidr);
            Assert.Equal(list.Cut(start, end), exceptions.Cut(start, end));
            Assert.Equal(list.Classify(start, end), exceptions.Classify(start, end));
        }

        Assert.Equal(ExceptionCover.Part, exceptions.Classify(Range("10.0.0.0/8").Start, Range("10.0.0.0/8").End));
        Assert.Equal(ExceptionCover.Whole, exceptions.Classify(Range("10.1.0.0/16").Start, Range("10.1.0.0/16").End));
        Assert.Equal(ExceptionCover.None, exceptions.Classify(Range("8.8.8.0/24").Start, Range("8.8.8.0/24").End));

        foreach (string address in new[] { "10.0.255.255", "10.1.0.0", "10.1.255.255", "10.2.0.0", "2001:4860::8888", "8.8.8.8" })
        {
            byte[] bytes = Range(address).Start;
            Assert.Equal(list.Covers(bytes), exceptions.Covers(bytes));
        }

        Assert.True(exceptions.Covers(Range("10.1.0.0").Start));
        Assert.False(exceptions.Covers(Range("10.2.0.0").Start));
        Assert.Equal(["10.0.0.0/16", "10.2.0.0/15"], exceptions.Cut(Range("10.0.0.0/14").Start, Range("10.0.0.0/14").End));
    }

    [Fact]
    public void The_list_is_read_once_per_call()
    {
        var service = new FakeIpInfoService { Exceptions = ExceptionList.Unreadable };
        Assert.Null(IpInfoService.ExceptionsForFeed(service));

        service.Exceptions = ExceptionList.Empty;
        Assert.NotNull(IpInfoService.ExceptionsForFeed(service));
    }

    [Fact]
    public void An_unreadable_list_is_never_wrapped()
    {
        Assert.Null(ReadableExceptions.WrapForFeed(ExceptionList.Unreadable));
        Assert.NotNull(ReadableExceptions.WrapForFeed(ExceptionList.Empty));
        Assert.Throws<ArgumentNullException>(() => ReadableExceptions.WrapForFeed(null!));
    }

    [Fact]
    public void Only_the_refusal_point_wraps_the_list()
    {
        // With every constructor private, a wrapper is had only by naming the one factory, so each name is counted
        ConstructorInfo[] constructors = typeof(ReadableExceptions).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.All(constructors, constructor => Assert.True(constructor.IsPrivate));

        var factory = new Regex(@"\bWrapForFeed\b");
        string root = RepositoryRoot();
        List<string> callers = SourceFiles(Path.Combine(root, "src"))
            .SelectMany(file => Enumerable.Repeat(Relative(root, file), factory.Matches(File.ReadAllText(file)).Count))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal([ReadableExceptionsFile, IpInfoServiceFile], callers);
    }

    [Fact]
    public void No_feed_writer_reads_the_list_itself()
    {
        // A writer that reached the list itself could write while it is unreadable, so it holds only the wrapper. A
        // writer names the wrapper, the refusal point or a feed write; a name in a comment counts too, on purpose
        var writes = new Regex(
            @"\bReadableExceptions\b|\bExceptionsForFeed\b|\bExportWriter\.WriteFeeds\b|\bCloudFeedWriter\.|\bAbuseIpDbFeedWriter\.");
        var list = new Regex(@"\bExceptionList\b|\bIsReadable\b|\.Exceptions\b");
        string root = RepositoryRoot();
        List<(string Path, string Text)> writers = SourceFiles(Path.Combine(root, "src", "PoorMansFeed.Core"))
            .Select(file => (Path: Relative(root, file), Text: File.ReadAllText(file)))
            .Where(file => file.Path is not (ReadableExceptionsFile or IpInfoServiceFile)
                && (file.Path.EndsWith("FeedWriter.cs", StringComparison.Ordinal) || writes.IsMatch(file.Text)))
            .ToList();

        Assert.Superset(
            new HashSet<string>
            {
                "src/PoorMansFeed.Core/ExportWriter.cs",
                "src/PoorMansFeed.Core/CloudFeeds/CloudFeedEngine.cs",
                "src/PoorMansFeed.Core/CloudFeeds/CloudFeedWriter.cs",
                "src/PoorMansFeed.Core/AbuseIpDb/AbuseIpDbService.cs",
                "src/PoorMansFeed.Core/AbuseIpDb/AbuseIpDbFeedWriter.cs",
            },
            writers.Select(file => file.Path).ToHashSet());
        Assert.Empty(writers.Where(file => list.IsMatch(file.Text)).Select(file => file.Path));
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
