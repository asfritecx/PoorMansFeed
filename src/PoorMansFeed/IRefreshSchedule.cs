namespace PoorMansFeed;

/// <summary>When the next timed Refresh all is due, as the Saved feeds page shows it.</summary>
public interface IRefreshSchedule
{
    /// <summary>Null while the timer is off or stopped, and while a run is out.</summary>
    DateTimeOffset? NextDueUtc { get; }

    /// <summary>A run is due but held back.</summary>
    bool IsWaiting { get; }

    /// <summary>Raised on the UI thread, also as a UTC day starts, so tomorrow's time then reads as today's.</summary>
    event EventHandler? Changed;
}
