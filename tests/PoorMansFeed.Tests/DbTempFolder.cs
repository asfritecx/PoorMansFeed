namespace PoorMansFeed.Tests;

/// <summary>A unique temp folder for SQLite test files, deleted on Dispose.</summary>
internal sealed class DbTempFolder : IDisposable
{
    public DbTempFolder()
    {
        Folder = Path.Combine(Path.GetTempPath(), "PoorMansFeed.DbTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public string FilePath(string name) => Path.Combine(Folder, name);

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
