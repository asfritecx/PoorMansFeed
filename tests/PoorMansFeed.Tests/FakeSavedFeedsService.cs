using PoorMansFeed.Core;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

/// <summary>Scripted saved feeds door. Records every call; touches no disk.</summary>
internal sealed class FakeSavedFeedsService : ISavedFeedsService
{
    public SavedFeedsRead Saved { get; set; } = new(true, []);
    public HistoryRead History { get; set; } = HistoryRead.Empty(HistoryState.Usable);
    public RetireResult RetireResult { get; set; } = new(RetireFailure.None, "/exports", [], null, [], null);
    public SavedFeedsFailure RemoveResult { get; set; } = SavedFeedsFailure.None;
    public HistoryState ClearResult { get; set; } = HistoryState.Missing;

    public Exception? SavedException { get; set; }
    public Exception? HistoryException { get; set; }
    public Exception? RetireException { get; set; }
    public Exception? RemoveException { get; set; }
    public Exception? ClearException { get; set; }

    /// <summary>Each query takes the next one, if any, and answers with its result once it completes.</summary>
    public Queue<TaskCompletionSource<HistoryRead>> HistoryGates { get; } = new();

    /// <summary>A gated query answers even after its token is cancelled, like a service that misses it.</summary>
    public bool HistoryIgnoresCancel { get; set; }

    /// <summary>When set, the next call of its kind stays with the service until it completes.</summary>
    public TaskCompletionSource? RetireGate { get; set; }
    public TaskCompletionSource? RemoveGate { get; set; }
    public TaskCompletionSource? ClearGate { get; set; }

    /// <summary>Runs as each retire, remove or clear starts, before any gate.</summary>
    public Action? OnCall { get; set; }

    public int SavedReads { get; private set; }
    public List<(HistoryQuery Query, int Limit)> Queries { get; } = [];
    public List<(FeedKind Kind, string Key)> Retires { get; } = [];
    public List<CancellationToken> RetireTokens { get; } = [];
    public List<string> Removes { get; } = [];
    public int Clears { get; private set; }

    public SavedFeedsRead ReadSavedFeeds()
    {
        SavedReads++;
        if (SavedException is { } failure)
            throw failure;
        return Saved;
    }

    public async Task<HistoryRead> SearchHistoryAsync(HistoryQuery query, int limit, CancellationToken ct)
    {
        Queries.Add((query, limit));
        if (HistoryGates.TryDequeue(out TaskCompletionSource<HistoryRead>? gate))
            return HistoryIgnoresCancel ? await gate.Task : await gate.Task.WaitAsync(ct);
        if (HistoryException is { } failure)
            throw failure;
        return History;
    }

    public async Task<RetireResult> RetireAsync(FeedKind kind, string key, CancellationToken ct)
    {
        Retires.Add((kind, key));
        RetireTokens.Add(ct);
        OnCall?.Invoke();
        await WaitAsync(RetireGate, ct);
        RetireGate = null;
        if (RetireException is { } failure)
            throw failure;
        return RetireResult;
    }

    public async Task<SavedFeedsFailure> RemoveAsync(string key, CancellationToken ct)
    {
        Removes.Add(key);
        OnCall?.Invoke();
        await WaitAsync(RemoveGate, ct);
        RemoveGate = null;
        if (RemoveException is { } failure)
            throw failure;
        return RemoveResult;
    }

    public async Task<HistoryState> ClearHistoryAsync(CancellationToken ct)
    {
        Clears++;
        OnCall?.Invoke();
        await WaitAsync(ClearGate, ct);
        ClearGate = null;
        if (ClearException is { } failure)
            throw failure;
        return ClearResult;
    }

    public Dictionary<uint, string> AsNames { get; } = [];
    public Exception? AsNamesException { get; set; }
    public List<uint[]> AsNameReads { get; } = [];

    /// <summary>Each name read takes the next one, if any, and answers once it completes, with the names as they
    /// were when it was asked.</summary>
    public Queue<TaskCompletionSource> AsNameGates { get; } = new();

    public async Task<IReadOnlyDictionary<uint, string>> FindAsNamesAsync(IReadOnlyCollection<uint> asns, CancellationToken ct)
    {
        AsNameReads.Add([.. asns]);
        Dictionary<uint, string> names = asns.Where(AsNames.ContainsKey).ToDictionary(asn => asn, asn => AsNames[asn]);
        if (AsNameGates.TryDequeue(out TaskCompletionSource? gate))
            await gate.Task;
        if (AsNamesException is { } failure)
            throw failure;
        return names;
    }

    private static async Task WaitAsync(TaskCompletionSource? gate, CancellationToken ct)
    {
        if (gate is not null)
            await gate.Task.WaitAsync(ct);
    }
}
