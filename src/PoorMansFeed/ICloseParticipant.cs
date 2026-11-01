namespace PoorMansFeed;

/// <summary>A page or task the main window's close asks, holds and stops, through <see cref="CloseChain"/>.</summary>
public interface ICloseParticipant
{
    /// <summary>Busy or unsaved: the close must ask or wait.</summary>
    bool HoldsClose { get; }

    /// <summary>Read before the participant's own question; true skips the feed-write question.</summary>
    bool AsksBeforeClose { get; }

    /// <summary>False keeps the window open.</summary>
    Task<bool> AskBeforeCloseAsync();

    /// <summary>Taken right after the participant's own yes, and released when the close doesn't happen.</summary>
    IDisposable? HoldRunsForClose();

    Task StopForCloseAsync();

    /// <summary>True: stopped right after its own yes, before a later participant is asked about the same work.
    /// A later no can't undo that stop, so it suits only work no later participant can hold while it runs.</summary>
    bool StopsWhenAgreed => false;
}
