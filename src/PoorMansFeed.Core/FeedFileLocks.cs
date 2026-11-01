namespace PoorMansFeed.Core;

/// <summary>One writer per feed at a time, keyed by the feed key (its file stem), ignoring case.</summary>
internal sealed class FeedFileLocks
{
    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    // A task started inside a holder's flow inherits the key, so it is refused, not left waiting on its parent
    private readonly AsyncLocal<string?> _heldByThisFlow = new();
    private int _inFlight;
    private bool _stopped;
    private TaskCompletionSource? _none;

    /// <summary>Callers from <see cref="Enter"/> until their handle is disposed, waiters included.</summary>
    public int InFlight
    {
        get
        {
            lock (_gate)
                return _inFlight;
        }
    }

    /// <summary>Throws InvalidOperationException on a flow holding one of these locks, or once stopped.</summary>
    public IDisposable Enter(string feedKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedKey);
        string key = feedKey;
        if (_heldByThisFlow.Value is not null)
            throw new InvalidOperationException("This flow already holds a feed lock.");

        Entry entry;
        lock (_gate)
        {
            if (_stopped)
                throw new InvalidOperationException("Feed writes are no longer admitted.");
            _inFlight++;
            if (!_entries.TryGetValue(key, out Entry? found))
            {
                found = new Entry();
                _entries[key] = found;
            }

            entry = found;
            entry.Users++;
        }

        entry.Semaphore.Wait();
        _heldByThisFlow.Value = key;
        return new Hold(this, key, entry);
    }

    /// <summary>True only while this flow holds exactly this feed key, ignoring case.</summary>
    public bool HeldByThisFlow(string feedKey) =>
        _heldByThisFlow.Value is { } held && string.Equals(held, feedKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>One way: holders and waiters already in finish, and every later <see cref="Enter"/> throws.</summary>
    public void StopAdmitting()
    {
        lock (_gate)
            _stopped = true;
    }

    public Task WhenNoneAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_inFlight == 0)
                return Task.CompletedTask;
            _none ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _none.Task.WaitAsync(ct);
        }
    }

    private void Leave(string key, Entry entry)
    {
        entry.Semaphore.Release();
        TaskCompletionSource? none = null;
        lock (_gate)
        {
            if (--entry.Users == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }

            if (--_inFlight == 0)
            {
                none = _none;
                _none = null;
            }
        }

        none?.TrySetResult();
    }

    private sealed class Hold(FeedFileLocks owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner._heldByThisFlow.Value = null;
                owner.Leave(key, entry);
            }
        }
    }
}
