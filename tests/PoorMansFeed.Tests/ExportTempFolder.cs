namespace PoorMansFeed.Tests;

/// <summary>A private temp folder for export tests, deleted on dispose.</summary>
public sealed class ExportTempFolder : IDisposable
{
    public ExportTempFolder()
    {
        Folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PoorMansFeed.ExportTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Folder))
                Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a locked temp folder is left for the OS to clean up
        }
    }
}
