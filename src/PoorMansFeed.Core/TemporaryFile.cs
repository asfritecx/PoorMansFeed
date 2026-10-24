using System.Text;

namespace PoorMansFeed.Core;

internal static class TemporaryFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Deletes whatever is at <paramref name="tmp"/> (a link itself, never its target), then makes the file with
    /// <see cref="FileMode.CreateNew"/>, which follows no link: a link put back in between makes the write fail.
    /// </summary>
    public static void WriteNew(string tmp, string text)
    {
        File.Delete(tmp);
        using var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Utf8NoBom.GetBytes(text));
    }
}
