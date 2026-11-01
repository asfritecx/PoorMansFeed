using System.Diagnostics;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Core;

/// <summary>The saved feeds page's one door; only the caller's own cancel escapes a method.</summary>
public interface ISavedFeedsService
{
    SavedFeedsRead ReadSavedFeeds();
    Task<HistoryRead> SearchHistoryAsync(HistoryQuery query, int limit, CancellationToken ct);
    Task<RetireResult> RetireAsync(FeedKind kind, string key, CancellationToken ct);
    Task<SavedFeedsFailure> RemoveAsync(string key, CancellationToken ct);
    Task<HistoryState> ClearHistoryAsync(CancellationToken ct);

    /// <summary>The dataset's AS names for the numbers; none for a number it doesn't name or a read that fails.</summary>
    Task<IReadOnlyDictionary<uint, string>> FindAsNamesAsync(IReadOnlyCollection<uint> asns, CancellationToken ct);
}

public sealed class SavedFeedsService : ISavedFeedsService
{
    private readonly IIpInfoService _ipinfo;
    private readonly IAbuseIpDbService _abuse;
    private readonly TimeProvider _time;
    private readonly string _defaultExportFolder;

    public SavedFeedsService(IIpInfoService ipinfo, IAbuseIpDbService abuse, TimeProvider time, string defaultExportFolder)
    {
        _ipinfo = ipinfo ?? throw new ArgumentNullException(nameof(ipinfo));
        _abuse = abuse ?? throw new ArgumentNullException(nameof(abuse));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _defaultExportFolder = defaultExportFolder ?? throw new ArgumentNullException(nameof(defaultExportFolder));
    }

    public SavedFeedsRead ReadSavedFeeds()
    {
        try
        {
            return _ipinfo.Records.ReadSavedFeeds();
        }
        catch (Exception ex)
        {
            TraceUnexpected(nameof(ReadSavedFeeds), ex);
            return new SavedFeedsRead(false, []);
        }
    }

    public async Task<IReadOnlyDictionary<uint, string>> FindAsNamesAsync(IReadOnlyCollection<uint> asns, CancellationToken ct)
    {
        try
        {
            return await _ipinfo.FindAsNamesAsync(asns, ct);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(FindAsNamesAsync), ex);
            return new Dictionary<uint, string>();
        }
    }

    public async Task<HistoryRead> SearchHistoryAsync(HistoryQuery query, int limit, CancellationToken ct)
    {
        try
        {
            return await _ipinfo.Records.SearchHistoryAsync(query, limit, ct);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(SearchHistoryAsync), ex);
            return HistoryRead.Empty(HistoryState.StorageError);
        }
    }

    public async Task<RetireResult> RetireAsync(FeedKind kind, string key, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            // The writers check only the key's form, so a kind that isn't the saved feed's would retire another
            // feed's files under the wrong header
            if (SavedFeedOf(_ipinfo.Records.ReadSavedFeeds(), kind, key) is not { } saved)
                return RetireResult.Of(RetireFailure.NotSaved);

            if (kind == FeedKind.AbuseIpDbRolling)
            {
                return string.Equals(saved.Key, AbuseIpDbFeedWriter.FileStem, StringComparison.OrdinalIgnoreCase)
                    ? await RetireRollingAsync(ct)
                    : RetireResult.Of(RetireFailure.NotRetirable);
            }

            // The saved feed's own key, so the history, the save and the file names agree whatever case was asked
            return await Task.Run(() => RetiredFeedWriter.Retire(_ipinfo, kind, saved.Key, _time, _defaultExportFolder),
                                  ct);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(RetireAsync), ex);
            return RetireResult.Of(RetireFailure.WriteFailed);
        }
    }

    private static SavedFeed? SavedFeedOf(SavedFeedsRead saved, FeedKind kind, string key) =>
        saved.Available
            ? saved.Feeds.FirstOrDefault(feed => feed.Kind == kind
                                                 && string.Equals(feed.Key, key, StringComparison.OrdinalIgnoreCase))
            : null;

    // The rolling retire resolves the folder again inside its own lock; a folder changed in between is only named wrongly
    private async Task<RetireResult> RetireRollingAsync(CancellationToken ct)
    {
        string folder = ExportFolders.Resolve(_ipinfo.Settings, _defaultExportFolder);
        AbuseFeedWriteResult result = await _abuse.EmptyFeedAsync(ct);
        RetireFailure failure = result.Failure switch
        {
            AbuseFeedWriteFailure.None => RetireFailure.None,
            AbuseFeedWriteFailure.NothingToWrite => RetireFailure.NothingToRetire,
            _ => RetireFailure.WriteFailed,
        };
        return new RetireResult(failure, folder, result.Files, result.FailedFile, result.OldFilesLeft ?? [], result.Record);
    }

    public async Task<SavedFeedsFailure> RemoveAsync(string key, CancellationToken ct)
    {
        try
        {
            return await _ipinfo.Records.RemoveSavedFeedAsync(key, ct);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(RemoveAsync), ex);
            return SavedFeedsFailure.StorageError;
        }
    }

    public async Task<HistoryState> ClearHistoryAsync(CancellationToken ct)
    {
        try
        {
            return await _ipinfo.Records.ClearHistoryAsync(ct);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(ClearHistoryAsync), ex);
            return HistoryState.StorageError;
        }
    }

    private static bool IsCallerCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    // The type alone: a message here can hold a path from the export folder
    private static void TraceUnexpected(string operation, Exception ex) =>
        Trace.TraceError($"The saved feeds {operation} failed unexpectedly ({ex.GetType().FullName}).");
}
