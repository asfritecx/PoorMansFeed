using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PoorMansFeed.Core;

public static class DatasetImporter
{
    private const int SkipCheckInterval = 10_000;
    private const int MinSkipsBeforeFailing = 1000;
    private const double MaxSkipFraction = 0.01;
    private const int IPv6AddressLength = 16;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Does not call builder.Complete. When the builder leaves out IPv6, IPv6 rows are neither added nor counted as
    /// skipped or corrupt. Returns the number of rows added.
    /// </summary>
    public static Task<long> ImportAsync(string gzPath, IpDatabase.Builder builder, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gzPath);
        ArgumentNullException.ThrowIfNull(builder);

        // The token is deliberately not passed to Task.Run: a pre-cancelled token then surfaces as the
        // OperationCanceledException thrown by the body, not as a TaskCanceledException.
        return Task.Run(() => Import(gzPath, builder, progress, ct));
    }

    private static long Import(string gzPath, IpDatabase.Builder builder, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var file = new FileStream(gzPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        long compressedLength = file.Length;
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 65536);

        var clock = Stopwatch.StartNew();
        long lines = 0;
        long skipped = 0;
        long added = 0;
        bool includeIPv6 = builder.IncludesIPv6;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            lines++;

            if (!string.IsNullOrWhiteSpace(line))
            {
                if (TryParseLine(line, lines, out IpInfoEntry? entry))
                {
                    // A valid row left out by the setting is neither added nor counted as skipped. The test uses the
                    // parsed address length, so it does not depend on how the network text is written.
                    bool leftOut = !includeIPv6 && entry.StartIp!.Length == IPv6AddressLength;
                    if (!leftOut)
                    {
                        builder.Add(entry);
                        added++;
                    }
                }
                else
                {
                    skipped++;
                }
            }

            if (lines % SkipCheckInterval == 0)
                CheckSkipped(skipped, lines);

            if (clock.Elapsed >= ProgressInterval)
            {
                progress?.Report(new UpdateProgress(UpdatePhase.Importing, file.Position, compressedLength));
                clock.Restart();
            }
        }

        CheckSkipped(skipped, lines);
        progress?.Report(new UpdateProgress(UpdatePhase.Importing, compressedLength, compressedLength));

        return added;
    }

    private static bool TryParseLine(string line, long lineNumber, [NotNullWhen(true)] out IpInfoEntry? entry)
    {
        entry = null;

        IpInfoEntry? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<IpInfoEntry>(line);
        }
        catch (JsonException ex)
        {
            Trace.TraceWarning(FormattableString.Invariant($"Skipping malformed JSON at line {lineNumber}: {ex.Message}"));
            return false;
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Network))
        {
            Trace.TraceWarning(FormattableString.Invariant($"Skipping entry without a network at line {lineNumber}"));
            return false;
        }

        try
        {
            (parsed.StartIp, parsed.EndIp) = Cidr.Parse(parsed.Network);
        }
        catch (FormatException ex)
        {
            Trace.TraceWarning(FormattableString.Invariant($"Skipping invalid CIDR at line {lineNumber}: {ex.Message}"));
            return false;
        }

        entry = parsed;
        return true;
    }

    private static void CheckSkipped(long skipped, long lines)
    {
        if (skipped > MinSkipsBeforeFailing && skipped > lines * MaxSkipFraction)
        {
            throw new InvalidDataException(FormattableString.Invariant(
                $"Too many malformed records ({skipped:N0} of {lines:N0} lines read). The dataset looks corrupt."));
        }
    }
}
