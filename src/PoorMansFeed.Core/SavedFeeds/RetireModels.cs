namespace PoorMansFeed.Core;

/// <summary>NotSaved: no saved feed has that key and kind, so the page asked for one that is gone.</summary>
public enum RetireFailure { None, NothingToRetire, NotRetirable, NotSaved, WriteFailed }

/// <summary>
/// Folder is the export folder the retire looked in, empty when it looked in none; Files are the files rewritten, in
/// write order.
/// </summary>
public sealed record RetireResult(RetireFailure Failure, string Folder, IReadOnlyList<string> Files, string? FailedFile,
                                  IReadOnlyList<string> OldFilesLeft, FeedRecordResult? Record)
{
    internal static RetireResult Of(RetireFailure failure, string folder = "") => new(failure, folder, [], null, [], null);
}
