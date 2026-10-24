using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace PoorMansFeed.Core.Aws;

internal sealed record CachedAwsRanges(AwsFileSource Source, byte[] Json);

/// <summary>
/// The last good ip-ranges.json and its source record. A file whose length or SHA-256 differs from the record
/// counts as missing and is left for the next update to overwrite. With no AppPaths nothing is read or written.
/// </summary>
internal sealed class AwsRangeCache
{
    private static readonly JsonSerializerOptions SourceOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly AppPaths? _paths;
    private readonly long _maxBytes;

    public AwsRangeCache(AppPaths? paths, long maxBytes)
    {
        _paths = paths;
        _maxBytes = maxBytes;
    }

    public CachedAwsRanges? TryRead()
    {
        if (_paths is null || !File.Exists(_paths.AwsIpRangesPath) || !File.Exists(_paths.AwsIpRangesSourcePath))
            return null;

        try
        {
            AwsFileSource? source = JsonSerializer.Deserialize<AwsFileSource>(
                File.ReadAllBytes(_paths.AwsIpRangesSourcePath), SourceOptions);
            if (source is null || !AwsRangesParser.IsValidSyncToken(source.SyncToken) || source.Sha256 is null
                || source.Length <= 0 || source.Length > _maxBytes
                || new FileInfo(_paths.AwsIpRangesPath).Length != source.Length)
            {
                Trace.TraceWarning("The saved AWS ranges do not match their record, so they are not used.");
                return null;
            }

            byte[] json = File.ReadAllBytes(_paths.AwsIpRangesPath);
            if (json.Length != source.Length
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(json)), source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Trace.TraceWarning("The saved AWS ranges do not match their record, so they are not used.");
                return null;
            }

            return new CachedAwsRanges(source, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Trace.TraceWarning("The saved AWS ranges could not be read.");
            return null;
        }
    }

    /// <summary>The file first, then its record. A failure is traced and returned, never thrown.</summary>
    public bool TryWrite(byte[] json, AwsFileSource source)
    {
        if (_paths is null)
            return false;

        try
        {
            Directory.CreateDirectory(_paths.DataFolder);
            WriteAtomic(_paths.AwsIpRangesPath, json);
            WriteAtomic(_paths.AwsIpRangesSourcePath, JsonSerializer.SerializeToUtf8Bytes(source, SourceOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Trace.TraceWarning("The AWS ranges could not be saved.");
            return false;
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            DeleteQuietly(tmp);
            throw;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not delete a temporary AWS cache file.");
        }
    }
}
