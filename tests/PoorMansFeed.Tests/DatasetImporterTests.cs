using System.IO.Compression;
using System.Text;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class DatasetImporterTests : IDisposable
{
    private static readonly DateOnly Downloaded = new(2026, 10, 7);
    private static readonly DateTimeOffset Imported = new(2026, 10, 7, 16, 5, 0, TimeSpan.Zero);

    private readonly DbTempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Line(string network, string asn = "AS15169") =>
        $$"""{"network":"{{network}}","country":"United States","country_code":"US","continent":"North America","continent_code":"NA","asn":"{{asn}}","as_name":"Google LLC","as_domain":"google.com"}""";

    private static void WriteGz(string path, IEnumerable<string> lines)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (string line in lines)
            writer.WriteLine(line);
    }

    private sealed class RecordingProgress : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Reports { get; } = [];

        public void Report(UpdateProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task Imports_Valid_Lines_And_Promotes_Queryable_Database()
    {
        string gz = _temp.FilePath("ipinfo_lite.json.gz");
        WriteGz(gz, [Line("8.8.8.0/24"), Line("8.8.4.0/24"), Line("2001:4860::/32"), Line("1.1.1.0/24", "AS13335")]);
        string next = _temp.FilePath("ipinfo.next.db");
        var live = new IpDatabase(_temp.FilePath("ipinfo.db"));

        long added;
        using (var builder = IpDatabase.CreateBuilder(next, 2))
        {
            added = await DatasetImporter.ImportAsync(gz, builder, null, CancellationToken.None);
            Assert.Equal(added, builder.Count);
            builder.Complete("sum", Downloaded, Imported);
        }
        live.Promote(next);

        Assert.Equal(4L, added);
        Assert.Equal(new[] { "8.8.4.0/24", "8.8.8.0/24", "2001:4860::/32" },
            live.QueryAsn(15169, null).Select(r => r.Network).ToArray());
        Assert.Equal(1, live.CountAsn(13335));
        Assert.Equal(4L, live.ReadInfo()?.RangeCount);
    }

    [Fact]
    public async Task Malformed_Lines_Are_Skipped_And_Not_Counted()
    {
        string gz = _temp.FilePath("ipinfo_lite.json.gz");
        WriteGz(gz,
        [
            Line("8.8.8.0/24"),
            "{not json",
            "",
            Line("8.8.4.0/24"),
            "{\"country\":\"no network here\"}",
            Line("1.2.3.4/99"),
            Line("1.1.1.0/24", "AS13335"),
        ]);

        using var builder = IpDatabase.CreateBuilder(_temp.FilePath("ipinfo.next.db"), 100);
        long added = await DatasetImporter.ImportAsync(gz, builder, null, CancellationToken.None);

        Assert.Equal(3L, added);
        Assert.Equal(3L, builder.Count);
    }

    [Fact]
    public async Task Too_Many_Malformed_Lines_Throws_InvalidDataException()
    {
        string gz = _temp.FilePath("ipinfo_lite.json.gz");
        var lines = Enumerable.Repeat("{broken", 1500).Concat(Enumerable.Repeat(Line("8.8.8.0/24"), 10));
        WriteGz(gz, lines);

        using var builder = IpDatabase.CreateBuilder(_temp.FilePath("ipinfo.next.db"), 100);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatasetImporter.ImportAsync(gz, builder, null, CancellationToken.None));
    }

    [Fact]
    public async Task Progress_Never_Decreases_And_Ends_At_Total()
    {
        string gz = _temp.FilePath("ipinfo_lite.json.gz");
        WriteGz(gz, Enumerable.Repeat(Line("8.8.8.0/24"), 20_000));
        var progress = new RecordingProgress();

        using (var builder = IpDatabase.CreateBuilder(_temp.FilePath("ipinfo.next.db"), 5_000))
        {
            await DatasetImporter.ImportAsync(gz, builder, progress, CancellationToken.None);
        }

        Assert.NotEmpty(progress.Reports);
        for (int i = 1; i < progress.Reports.Count; i++)
            Assert.True(progress.Reports[i].Done >= progress.Reports[i - 1].Done, $"Progress decreased at report {i}");

        UpdateProgress last = progress.Reports[^1];
        Assert.Equal(UpdatePhase.Importing, last.Phase);
        Assert.True(last.Total > 0);
        Assert.Equal(last.Total, last.Done);
    }

    [Fact]
    public async Task Pre_Cancelled_Token_Throws_OperationCanceledException()
    {
        string gz = _temp.FilePath("ipinfo_lite.json.gz");
        WriteGz(gz, [Line("8.8.8.0/24")]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var builder = IpDatabase.CreateBuilder(_temp.FilePath("ipinfo.next.db"), 100);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DatasetImporter.ImportAsync(gz, builder, null, cts.Token));
    }

    [Fact]
    public async Task IPv6_Off_Skips_IPv6_Rows_And_Records_Metadata()
    {
        string gz = _temp.FilePath("ipinfo_lite.json.gz");
        WriteGz(gz, [Line("8.8.8.0/24"), Line("2001:4860::/32"), Line("8.8.4.0/24"), Line("2a02:26f0::/29", "AS5606")]);
        string next = _temp.FilePath("ipinfo.next.db");
        var live = new IpDatabase(_temp.FilePath("ipinfo.db"));

        long added;
        using (var builder = IpDatabase.CreateBuilder(next, 2, includeIPv6: false))
        {
            added = await DatasetImporter.ImportAsync(gz, builder, null, CancellationToken.None);
            Assert.Equal(added, builder.Count);
            builder.Complete("sum", Downloaded, Imported);
        }
        live.Promote(next);

        Assert.Equal(2L, added);
        Assert.Equal(new[] { "8.8.4.0/24", "8.8.8.0/24" },
            live.QueryAsn(15169, null).Select(r => r.Network).ToArray());
        Assert.Equal(0, live.CountAsn(5606));
        DatasetInfo? info = live.ReadInfo();
        Assert.Equal(2L, info?.RangeCount);
        Assert.False(info?.IncludesIPv6 == true);
    }

    [Fact]
    public async Task IPv6_Off_Mostly_IPv6_File_Does_Not_Trip_Corrupt_Threshold()
    {
        string gz = _temp.FilePath("ipinfo_lite.json.gz");
        var lines = Enumerable.Repeat(Line("2001:4860::/32"), 1500).Concat([Line("8.8.8.0/24"), Line("8.8.4.0/24")]);
        WriteGz(gz, lines);

        using var builder = IpDatabase.CreateBuilder(_temp.FilePath("ipinfo.next.db"), 100, includeIPv6: false);
        long added = await DatasetImporter.ImportAsync(gz, builder, null, CancellationToken.None);

        Assert.Equal(2L, added);
        Assert.Equal(2L, builder.Count);
    }

    [Fact]
    public async Task IPv6_On_Imports_IPv6_Rows_And_Records_Metadata()
    {
        string gz = _temp.FilePath("ipinfo_lite.json.gz");
        WriteGz(gz, [Line("8.8.8.0/24"), Line("2001:4860::/32")]);
        string next = _temp.FilePath("ipinfo.next.db");
        var live = new IpDatabase(_temp.FilePath("ipinfo.db"));

        using (var builder = IpDatabase.CreateBuilder(next, 2))
        {
            await DatasetImporter.ImportAsync(gz, builder, null, CancellationToken.None);
            builder.Complete("sum", Downloaded, Imported);
        }
        live.Promote(next);

        Assert.Equal(1, live.CountAsn(15169, includeIPv6: false));
        Assert.Equal(2, live.CountAsn(15169));
        Assert.True(live.ReadInfo()?.IncludesIPv6 == true);
    }
}
