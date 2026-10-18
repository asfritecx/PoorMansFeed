using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace PoorMansFeed.Core.Azure;

internal sealed record CachedAzureTags(AzureTagFileSource Source, byte[] Json);

/// <summary>
/// The last good service tags file in the data folder, with a record of where it came from. A cache is only used
/// when the file's length and SHA-256 match that record. A cache that does not match counts as missing and is left
/// where it is: the next update overwrites it. With no AppPaths nothing is read or written.
/// </summary>
internal sealed class AzureTagCache
{
    private static readonly JsonSerializerOptions SourceOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly AppPaths? _paths;
    private readonly long _maxBytes;

    public AzureTagCache(AppPaths? paths, long maxBytes)
    {
        _paths = paths;
        _maxBytes = maxBytes;
    }

    public CachedAzureTags? TryRead()
    {
        if (_paths is null || !File.Exists(_paths.AzureServiceTagsPath) || !File.Exists(_paths.AzureServiceTagsSourcePath))
            return null;

        try
        {
            AzureTagFileSource? source = JsonSerializer.Deserialize<AzureTagFileSource>(
                File.ReadAllBytes(_paths.AzureServiceTagsSourcePath), SourceOptions);
            if (source is null || source.Url is null || source.FileName is null || source.Cloud is null || source.Sha256 is null
                || source.Length <= 0 || source.Length > _maxBytes
                || new FileInfo(_paths.AzureServiceTagsPath).Length != source.Length)
            {
                Trace.TraceWarning("The saved Azure service tags do not match their record, so they are not used.");
                return null;
            }

            byte[] json = File.ReadAllBytes(_paths.AzureServiceTagsPath);
            if (json.Length != source.Length
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(json)), source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Trace.TraceWarning("The saved Azure service tags do not match their record, so they are not used.");
                return null;
            }

            return new CachedAzureTags(source, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Trace.TraceWarning("The saved Azure service tags could not be read.");
            return null;
        }
    }

    /// <summary>The file first, then its record. A failure is traced and returned, never thrown.</summary>
    public bool TryWrite(byte[] json, AzureTagFileSource source)
    {
        if (_paths is null)
            return false;

        try
        {
            Directory.CreateDirectory(_paths.DataFolder);
            WriteAtomic(_paths.AzureServiceTagsPath, json);
            WriteAtomic(_paths.AzureServiceTagsSourcePath, JsonSerializer.SerializeToUtf8Bytes(source, SourceOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Trace.TraceWarning("The Azure service tags could not be saved.");
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
            Trace.TraceWarning("Could not delete a temporary Azure cache file.");
        }
    }
}
