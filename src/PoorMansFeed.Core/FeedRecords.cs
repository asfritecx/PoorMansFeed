using System.Collections.Concurrent;
using System.Diagnostics;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Core;

public enum FeedSaveOutcome { NotAsked, Saved, TooMany, Unavailable, StorageError }

public enum HistoryOutcome { NothingToRecord, Recorded, Failed }

public sealed record FeedRecordResult(FeedSaveOutcome Save, HistoryOutcome History);

internal sealed record FeedRecordRequest(string FeedKey, FeedKind Kind, FeedChangeRecord? Change, DateTimeOffset AtUtc,
                                       SavedFeedIntent? Intent = null);

/// <summary>A feed write's planned copies, its per-file changes in write order, and why each entry went.</summary>
internal sealed record FeedWriteRecord(string FeedKey, FeedKind Kind, FeedChangeCause Cause, DateTimeOffset AtUtc,
                                       IReadOnlyCollection<FeedType> Types,
                                       IReadOnlyList<FeedFileChange>? Changes,
                                       RemovalReasons? Reasons,
                                       DateOnly? DatasetDate, DateTimeOffset? ListDownloadedUtc,
                                       SavedFeedIntent? Intent = null);

public interface IFeedRecords
{
    /// <summary>Internal, so no page can save or retire a feed without writing it.</summary>
    internal FeedRecordResult Record(FeedRecordRequest request);

    /// <summary>Blocks until no other write or Remove of the same feed key (ignoring case) holds it.</summary>
    IDisposable EnterWrite(string feedKey);

    /// <summary>Writes and Removes from asking for the lock until it is released, waiters included.</summary>
    int WritesInFlight { get; }

    /// <summary>Admits no new write, then completes when none is in flight; a later EnterWrite throws.</summary>
    Task StopWritesAsync(CancellationToken ct);
    Task<HistoryRead> SearchHistoryAsync(HistoryQuery query, int limit, CancellationToken ct);
    Task<HistoryState> ClearHistoryAsync(CancellationToken ct);

    /// <summary>The saved feeds as config.db last committed them; it never touches the disk.</summary>
    SavedFeedsRead ReadSavedFeeds();
    Task<SavedFeedsFailure> RemoveSavedFeedAsync(string key, CancellationToken ct);

    /// <summary>Saves a refresh's stamps at once, an outcome per stamp; no feed lock, no history.</summary>
    internal IReadOnlyList<SavedFeedStampOutcome> CommitRefresh(IReadOnlyList<SavedFeedStamp> stamps);

    /// <summary>True while this session's last page save or retire of the feed was refused.</summary>
    internal bool SaveRefusedThisSession(string feedKey);
}

internal interface ISavedFeedStore
{
    SavedFeedsRead Read();
    SavedFeedsFailure Apply(string key, FeedKind kind, SavedFeedIntent intent, DateTimeOffset atUtc);
    SavedFeedsFailure Remove(string key);
    IReadOnlyList<SavedFeedStampOutcome> ApplyStamps(IReadOnlyList<SavedFeedStamp> stamps);
}

public sealed class FeedRecords : IFeedRecords
{
    private readonly FeedHistory? _history;
    private readonly ISavedFeedStore? _savedFeeds;
    private readonly FeedFileLocks _locks = new();
    private readonly ConcurrentDictionary<string, bool> _refusedThisSession = new(StringComparer.OrdinalIgnoreCase);

    internal FeedRecords(AppPaths paths, TimeProvider time, ISavedFeedStore savedFeeds)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(savedFeeds);
        _history = new FeedHistory(paths.HistoryDatabasePath, time);
        _savedFeeds = savedFeeds;
    }

    private FeedRecords()
    {
    }

    /// <summary>No history or saved feeds; each read is a new instance, so no two holders share its locks.</summary>
    public static IFeedRecords None => new FeedRecords();

    public int WritesInFlight => _locks.InFlight;

    public IDisposable EnterWrite(string feedKey) => _locks.Enter(feedKey);

    public Task StopWritesAsync(CancellationToken ct)
    {
        _locks.StopAdmitting();
        return _locks.WhenNoneAsync(ct);
    }

    // The history and the save are independent: neither one's failure stops the other
    FeedRecordResult IFeedRecords.Record(FeedRecordRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_locks.HeldByThisFlow(request.FeedKey))
        {
            Trace.TraceError("A feed record was refused: its feed's lock isn't held.");
            return new FeedRecordResult(request.Intent is null ? FeedSaveOutcome.NotAsked : FeedSaveOutcome.StorageError,
                                        request.Change is null ? HistoryOutcome.NothingToRecord : HistoryOutcome.Failed);
        }

        HistoryOutcome history = RecordHistory(request.Change);
        FeedSaveOutcome save = Save(request);
        // A refused retire counts too: its files are header-only while the store still holds the live feed
        if (save is FeedSaveOutcome.TooMany or FeedSaveOutcome.StorageError)
            _refusedThisSession[request.FeedKey] = true;
        else if (save == FeedSaveOutcome.Saved)
            _refusedThisSession.TryRemove(request.FeedKey, out _);
        return new FeedRecordResult(save, history);
    }

    private HistoryOutcome RecordHistory(FeedChangeRecord? change)
    {
        if (change is null)
            return HistoryOutcome.NothingToRecord;
        if (_history is null)
            return HistoryOutcome.Failed;

        try
        {
            return _history.Record(change) == HistoryState.Usable ? HistoryOutcome.Recorded : HistoryOutcome.Failed;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"A feed change couldn't be recorded ({ex.GetType().FullName}).");
            return HistoryOutcome.Failed;
        }
    }

    private FeedSaveOutcome Save(FeedRecordRequest request)
    {
        if (request.Intent is null)
            return FeedSaveOutcome.NotAsked;
        if (_savedFeeds is null)
            return FeedSaveOutcome.Unavailable;

        try
        {
            return _savedFeeds.Apply(request.FeedKey, request.Kind, request.Intent, request.AtUtc) switch
            {
                SavedFeedsFailure.None => FeedSaveOutcome.Saved,
                SavedFeedsFailure.NotFound => FeedSaveOutcome.NotAsked,
                SavedFeedsFailure.TooMany => FeedSaveOutcome.TooMany,
                SavedFeedsFailure.Unavailable => FeedSaveOutcome.Unavailable,
                _ => FeedSaveOutcome.StorageError,
            };
        }
        catch (Exception ex)
        {
            // By type alone: an invalid key or pick's message could quote what the caller passed
            Trace.TraceError($"A feed couldn't be saved ({ex.GetType().FullName}).");
            return FeedSaveOutcome.StorageError;
        }
    }

    IReadOnlyList<SavedFeedStampOutcome> IFeedRecords.CommitRefresh(IReadOnlyList<SavedFeedStamp> stamps)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        if (_savedFeeds is null)
            return stamps.Select(_ => SavedFeedStampOutcome.Unavailable).ToList();

        try
        {
            return _savedFeeds.ApplyStamps(stamps);
        }
        catch (Exception ex)
        {
            // By type alone: an invalid stamp's message could quote what the caller passed
            Trace.TraceError($"A refresh's saved feeds couldn't be saved ({ex.GetType().FullName}).");
            return stamps.Select(_ => SavedFeedStampOutcome.StorageError).ToList();
        }
    }

    bool IFeedRecords.SaveRefusedThisSession(string feedKey) =>
        feedKey is not null && _refusedThisSession.ContainsKey(feedKey);

    public SavedFeedsRead ReadSavedFeeds() => _savedFeeds?.Read() ?? new SavedFeedsRead(false, []);

    public Task<SavedFeedsFailure> RemoveSavedFeedAsync(string key, CancellationToken ct)
    {
        ISavedFeedStore? savedFeeds = _savedFeeds;
        if (savedFeeds is null)
            return ct.IsCancellationRequested ? Task.FromCanceled<SavedFeedsFailure>(ct) : Task.FromResult(SavedFeedsFailure.Unavailable);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Under the feed's lock, so a write of it already in flight saves first and the Remove wins
                using IDisposable hold = _locks.Enter(key);
                return savedFeeds.Remove(key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Trace.TraceError($"A saved feed couldn't be removed ({ex.GetType().FullName}).");
                return SavedFeedsFailure.StorageError;
            }
        }, ct);
    }

    public Task<HistoryRead> SearchHistoryAsync(HistoryQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        FeedHistory? history = _history;
        if (history is null)
            return ct.IsCancellationRequested ? Task.FromCanceled<HistoryRead>(ct) : Task.FromResult(HistoryRead.Empty(HistoryState.Missing));
        return Task.Run(() => history.Search(query, limit, ct), ct);
    }

    public Task<HistoryState> ClearHistoryAsync(CancellationToken ct)
    {
        FeedHistory? history = _history;
        if (history is null)
            return ct.IsCancellationRequested ? Task.FromCanceled<HistoryState>(ct) : Task.FromResult(HistoryState.Missing);
        return Task.Run(history.Clear, ct);
    }

    /// <summary>The write's changes as one history record, or null when no family holds an entry.</summary>
    internal static FeedChangeRecord? ChangeOf(FeedWriteRecord write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Types);
        if (write.Changes is null)
            return null;
        var planned = write.Types.ToHashSet();
        if (write.Changes.Any(file => !planned.Contains(file.Type)))
            throw new ArgumentException("A changed file's copy wasn't planned by the write.", nameof(write));

        bool traced = false;
        FeedRemovalReason ReasonOf(string text)
        {
            if (write.Reasons is null)
                return FeedRemovalReason.NotTold;
            try
            {
                return write.Reasons.For(text);
            }
            catch (Exception ex)
            {
                if (!traced)
                {
                    traced = true;
                    Trace.TraceWarning($"A feed change's removal reasons couldn't be told ({ex.GetType().FullName}).");
                }

                return FeedRemovalReason.NotTold;
            }
        }

        FeedFamilyChange FamilyOf(bool isIPv6, FeedType? copy, FeedFileState oldFile, FeedEntryChanges entries) =>
            new(isIPv6, copy, oldFile, entries.Added,
                entries.Removed.Select(text => new FeedRemoval(text, ReasonOf(text))).ToList());

        var families = new List<FeedFamilyChange>();
        foreach (bool isIPv6 in new[] { false, true })
        {
            FeedFamilies family = isIPv6 ? FeedFamilies.IPv6 : FeedFamilies.IPv4;
            var files = write.Changes
                .Where(file => (file.Families & family) != 0)
                .Select(file => (file.Type, Entries: isIPv6 ? file.IPv6 : file.IPv4, file.OldFile))
                .ToList();
            if (files.Count == 0)
                continue;

            // A write that failed part way moved only some of the planned copies, so its changes name each copy
            var first = files[0];
            bool alike = files.Count == planned.Count && planned.SetEquals(files.Select(file => file.Type))
                && files.All(file => file.OldFile == first.OldFile
                && file.Entries.Added.SequenceEqual(first.Entries.Added, StringComparer.Ordinal)
                && file.Entries.Removed.SequenceEqual(first.Entries.Removed, StringComparer.Ordinal));
            if (alike)
            {
                if (!first.Entries.IsEmpty)
                    families.Add(FamilyOf(isIPv6, null, first.OldFile, first.Entries));
                continue;
            }

            foreach (var file in files.Where(file => !file.Entries.IsEmpty))
                families.Add(FamilyOf(isIPv6, file.Type, file.OldFile, file.Entries));
        }

        return families.Count == 0
            ? null
            : new FeedChangeRecord(write.AtUtc, write.FeedKey, write.Kind, write.Cause, write.DatasetDate,
                                   write.ListDownloadedUtc, families);
    }
}
