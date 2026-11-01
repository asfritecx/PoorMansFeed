using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace PoorMansFeed.Core.GoogleCloud;

internal sealed record CachedGoogleRanges(GoogleRangeFileSource Source, byte[] Json);

/// <summary>
/// The last good cloud.json and its source record. A file whose length or SHA-256 differs from the record counts as
/// missing and is left for the next fetch to overwrite. With no AppPaths nothing is read or written.
/// </summary>
internal sealed class GoogleRangeCache
{
    private static readonly JsonSerializerOptions SourceOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly AppPaths? _paths;
    private readonly long _maxBytes;

    public GoogleRangeCache(AppPaths? paths, long maxBytes)
    {
        _paths = paths;
        _maxBytes = maxBytes;
    }

    public CachedGoogleRanges? TryRead()
    {
        if (_paths is null || !File.Exists(_paths.GoogleCloudRangesPath) || !File.Exists(_paths.GoogleCloudRangesSourcePath))
            return null;

        try
        {
            GoogleRangeFileSource? source = JsonSerializer.Deserialize<GoogleRangeFileSource>(
                File.ReadAllBytes(_paths.GoogleCloudRangesSourcePath), SourceOptions);
            if (source is null || !IsReadableIdentity(source)
                || source.Sha256 is null || source.Length <= 0 || source.Length > _maxBytes
                || new FileInfo(_paths.GoogleCloudRangesPath).Length != source.Length)
            {
                Trace.TraceWarning("The saved Google Cloud ranges do not match their record, so they are not used.");
                return null;
            }

            byte[] json = File.ReadAllBytes(_paths.GoogleCloudRangesPath);
            if (json.Length != source.Length
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(json)), source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Trace.TraceWarning("The saved Google Cloud ranges do not match their record, so they are not used.");
                return null;
            }

            return new CachedGoogleRanges(source, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Trace.TraceWarning("The saved Google Cloud ranges could not be read.");
            return null;
        }
    }

    /// <summary>Whether either file of the pair is there, to tell nothing saved from a damaged pair.</summary>
    internal bool AnyFileExists() =>
        _paths is not null && (File.Exists(_paths.GoogleCloudRangesPath) || File.Exists(_paths.GoogleCloudRangesSourcePath));

    /// <summary>The file first, then its record. A failure is traced and returned, never thrown.</summary>
    public bool TryWrite(byte[] json, GoogleRangeFileSource source)
    {
        if (_paths is null)
            return false;

        if (!IsReadableIdentity(source))
        {
            Trace.TraceWarning("The Google Cloud ranges were not saved, because their source is not one this build reads back.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(_paths.DataFolder);
            WriteAtomic(_paths.GoogleCloudRangesPath, json);
            WriteAtomic(_paths.GoogleCloudRangesSourcePath, JsonSerializer.SerializeToUtf8Bytes(source, SourceOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Trace.TraceWarning("The Google Cloud ranges could not be saved.");
            return false;
        }
    }

    // The writer shares the reader's check, so no pair is saved that the next read would refuse. Scheme throws on
    // a relative Uri, which System.Text.Json makes from a bare file name, so IsAbsoluteUri is tested first.
    private static bool IsReadableIdentity(GoogleRangeFileSource source) =>
        source.Url is not null && source.Url.IsAbsoluteUri && source.Url.Scheme == Uri.UriSchemeHttps
        && source.SyncToken is not null && source.CreationTime is not null;

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
            Trace.TraceWarning("Could not delete a temporary Google Cloud cache file.");
        }
    }
}
