using System.Text;

namespace PoorMansFeed.Core;

internal static class TemporaryFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes the text as UTF-8 with no byte order mark to a new file at <paramref name="tmp"/>, the <c>.tmp</c> path a
    /// feed is moved from. Whatever is already there is deleted first (a link itself, never what it points at), and
    /// the file is made with <see cref="FileMode.CreateNew"/>, which follows no link: so a link or a stale file at the
    /// <c>.tmp</c> path is never written through, and one put back in between makes the write fail. IOException and
    /// UnauthorizedAccessException escape.
    /// </summary>
    public static void WriteNew(string tmp, string text)
    {
        File.Delete(tmp);
        using var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Utf8NoBom.GetBytes(text));
    }
}
