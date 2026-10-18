using System.Text;

namespace PoorMansFeed.Core;

/// <summary>
/// Single-instance lock. The file is held open with FileShare.None until disposed.
/// The OS releases the lock if the process dies, so no stale-lock handling is needed.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private InstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Attempts to take the lock. Returns null if another instance holds it.
    /// </summary>
    public static InstanceLock? TryAcquire(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            stream.SetLength(0);
            byte[] pid = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            stream.Write(pid, 0, pid.Length);
            stream.Flush();
        }
        catch (IOException)
        {
            // The PID is diagnostic only; the lock itself is held.
        }

        return new InstanceLock(stream);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stream.Dispose();
    }
}
