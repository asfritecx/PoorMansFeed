namespace PoorMansFeed;

/// <summary>What a feed write asks the Settings page: whether it holds edits not saved yet.</summary>
public interface IUnsavedSettings
{
    bool HasUnsavedChanges { get; }
}

/// <summary>Bound to the Settings page once it is built; reads as nothing unsaved until then.</summary>
internal sealed class LateUnsavedSettings : IUnsavedSettings
{
    public IUnsavedSettings? Source { get; set; }
    public bool HasUnsavedChanges => Source?.HasUnsavedChanges == true;
}
