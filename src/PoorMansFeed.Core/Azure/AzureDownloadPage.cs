using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoorMansFeed.Core.Azure;

/// <summary>
/// Finds the current service tags file on Microsoft's download page. The page is untrusted input: a link is only
/// accepted when it points at the Public cloud file on download.microsoft.com over https.
/// </summary>
internal static partial class AzureDownloadPage
{
    private const string FileHost = "download.microsoft.com";
    private const int MaxUrlLength = 2048;

    private static readonly byte[] DetailsMarker = "window.__DLCDetails__="u8.ToArray();
    private static readonly byte[] HrefMarker = "href="u8.ToArray();

    [GeneratedRegex(@"^ServiceTags_Public_([0-9]{8})\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    /// <summary>The embedded page data first, then the download button's link. Null when neither gives a valid file.</summary>
    public static AzurePublishedFile? TryFind(ReadOnlySpan<byte> html) => FromDetails(html) ?? FromLinks(html);

    private static AzurePublishedFile? FromDetails(ReadOnlySpan<byte> html)
    {
        int at = html.IndexOf(DetailsMarker);
        if (at < 0)
            return null;

        ReadOnlySpan<byte> rest = html[(at + DetailsMarker.Length)..];
        int start = 0;
        while (start < rest.Length && rest[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            start++;
        if (start >= rest.Length || rest[start] != (byte)'{')
            return null;

        // The object is followed by more script, so the reader has to tolerate content after the first value.
        var reader = new Utf8JsonReader(rest[start..], new JsonReaderOptions { AllowMultipleValues = true });
        try
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            if (!document.RootElement.TryGetProperty("dlcDetailsView", out JsonElement view) || view.ValueKind != JsonValueKind.Object
                || !view.TryGetProperty("downloadFile", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            AzurePublishedFile? newest = null;
            foreach (JsonElement entry in files.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("url", out JsonElement url) || url.ValueKind != JsonValueKind.String
                    || !TryValidate(url.GetString(), out AzurePublishedFile? file))
                {
                    continue;
                }

                if (newest is null || file.FileDate > newest.FileDate)
                    newest = file;
            }

            return newest;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AzurePublishedFile? FromLinks(ReadOnlySpan<byte> html)
    {
        int offset = 0;
        while (offset < html.Length)
        {
            int at = html[offset..].IndexOf(HrefMarker);
            if (at < 0)
                return null;

            int valueStart = offset + at + HrefMarker.Length;
            offset = valueStart;
            if (valueStart >= html.Length || html[valueStart] is not ((byte)'"' or (byte)'\''))
                continue;

            byte quote = html[valueStart];
            ReadOnlySpan<byte> value = html[(valueStart + 1)..];
            int end = value.IndexOf(quote);
            if (end < 0 || end > MaxUrlLength)
                continue;

            if (TryValidate(Encoding.UTF8.GetString(value[..end]), out AzurePublishedFile? file))
                return file;
        }

        return null;
    }

    private static bool TryValidate(string? url, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AzurePublishedFile? file)
    {
        file = null;
        if (string.IsNullOrEmpty(url) || url.Length > MaxUrlLength)
            return false;

        foreach (char character in url)
        {
            if (character <= ' ' || character is '\\' or '%' or '?' or '#')
                return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, FileHost, StringComparison.Ordinal)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
        {
            return false;
        }

        string name = uri.Segments[^1];
        Match match = FileNamePattern().Match(name);
        if (!match.Success
            || !DateOnly.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            return false;
        }

        file = new AzurePublishedFile(uri, name, date);
        return true;
    }
}
