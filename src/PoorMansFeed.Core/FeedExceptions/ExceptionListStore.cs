using System.Diagnostics;
using System.Text.Json;

namespace PoorMansFeed.Core.FeedExceptions;

/// <summary>
/// Reads the legacy exceptions.json once, for the import into config.db, and turns the exceptions section to and from
/// JSON. Nothing writes the legacy file: the list is saved only in config.db. A missing file is an empty list. A file
/// that can't be read, is larger than MaxFileBytes, isn't the expected shape, comes from a newer version or holds an
/// entry that isn't a network is Unreadable, and the import then stops start-up.
/// </summary>
internal sealed class ExceptionListStore(string path)
{
    private const int SchemaVersion = 1;

    /// <summary>
    /// The largest file Load reads. The largest list ToJson can give (MaxEntries IPv6 networks, each with a full note
    /// escaped as \uXXXX) is about 6.5 MB, so a larger file was not written by this application.
    /// </summary>
    internal const long MaxFileBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

    public ExceptionList Load()
    {
        if (!File.Exists(_path))
            return ExceptionList.Empty;

        try
        {
            byte[] bytes;
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaxFileBytes)
                {
                    TraceTooLarge();
                    return ExceptionList.Unreadable;
                }

                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            return Read(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"The exceptions file couldn't be read ({ex.GetType().Name}: {ex.Message}).");
            return ExceptionList.Unreadable;
        }
    }

    /// <summary>
    /// Reads a list from its JSON with <see cref="Load"/>'s rules: longer than <see cref="MaxFileBytes"/>, not the
    /// expected shape, another schema version, more than <see cref="ExceptionList.MaxEntries"/> entries or an entry that
    /// isn't a network is Unreadable. One UTF-8 byte order mark is skipped.
    /// </summary>
    internal static ExceptionList Read(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxFileBytes)
        {
            TraceTooLarge();
            return ExceptionList.Unreadable;
        }

        try
        {
            // Notepad and other editors save UTF-8 with a byte order mark, which the JSON reader refuses
            ReadOnlySpan<byte> json = utf8Json;
            if (json.StartsWith(Utf8ByteOrderMark))
                json = json[Utf8ByteOrderMark.Length..];

            ExceptionsFile? file = JsonSerializer.Deserialize<ExceptionsFile>(json, Options);
            if (file is null || file.SchemaVersion != SchemaVersion || file.Exceptions is null
                || file.Exceptions.Count > ExceptionList.MaxEntries)
            {
                Trace.TraceWarning("The exceptions file is not in the expected form, so no feed can be written until it is saved again.");
                return ExceptionList.Unreadable;
            }

            var entries = new List<ExceptionEntry>(file.Exceptions.Count);
            foreach (ExceptionsFileEntry? saved in file.Exceptions)
            {
                if (saved is null || !ExceptionEntry.TryParseNetwork(saved.Network, out CidrBlock? network, out _))
                {
                    Trace.TraceWarning("The exceptions file holds an entry that isn't a network, so no feed can be written until it is saved again.");
                    return ExceptionList.Unreadable;
                }

                entries.Add(new ExceptionEntry(network, saved.Note));
            }

            return ExceptionList.Create(entries);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            Trace.TraceWarning($"The exceptions file couldn't be read ({ex.GetType().Name}: {ex.Message}).");
            return ExceptionList.Unreadable;
        }
    }

    /// <summary>The list as exceptions.json holds it: schema version 1 and one object per entry, in order. The list must be readable.</summary>
    internal static byte[] ToJson(ExceptionList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var file = new ExceptionsFile
        {
            SchemaVersion = SchemaVersion,
            Exceptions = list.Entries.Select(entry => (ExceptionsFileEntry?)new ExceptionsFileEntry { Network = entry.Network.Text, Note = entry.Note }).ToList(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(file, Options);
    }

    private static void TraceTooLarge() =>
        Trace.TraceWarning("The exceptions file is larger than any list the application saves, so no feed can be written until it is saved again.");

    private sealed class ExceptionsFile
    {
        public int SchemaVersion { get; set; }

        public List<ExceptionsFileEntry?>? Exceptions { get; set; }
    }

    private sealed class ExceptionsFileEntry
    {
        public string? Network { get; set; }

        public string? Note { get; set; }
    }
}
