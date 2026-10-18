using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>A unique data folder for IpInfoService tests, with paths that stay inside it. Deleted on Dispose.</summary>
internal sealed class ServiceTempFolder : IDisposable
{
    public ServiceTempFolder()
    {
        Folder = Path.Combine(Path.GetTempPath(), "PoorMansFeed.ServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        Paths = new AppPaths(Folder, Path.Combine(Folder, "DefaultExports"));
    }

    public string Folder { get; }

    public AppPaths Paths { get; }

    /// <summary>An export folder used by the export tests; it is set through SaveSettingsAsync.</summary>
    public string ExportFolder => Path.Combine(Folder, "exports");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Folder))
                Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the OS temp folder is cleaned up eventually.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
