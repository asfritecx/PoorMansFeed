namespace PoorMansFeed;

/// <summary>What became of a Reset token, so the caller can say it in its own line.</summary>
internal enum TokenResetOutcome { Saved, Declined, Busy, ClearFailed, NoNewToken, SaveFailed, DialogFailed }
