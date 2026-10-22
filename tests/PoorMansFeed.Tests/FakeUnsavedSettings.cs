namespace PoorMansFeed.Tests;

/// <summary>The Settings page's unsaved edits as a switch the test sets; reads are counted.</summary>
internal sealed class FakeUnsavedSettings : IUnsavedSettings
{
    private bool _hasUnsavedChanges;

    public FakeUnsavedSettings(bool hasUnsavedChanges = false)
    {
        _hasUnsavedChanges = hasUnsavedChanges;
    }

    public int Reads { get; private set; }

    public bool HasUnsavedChanges
    {
        get
        {
            Reads++;
            return _hasUnsavedChanges;
        }
        set => _hasUnsavedChanges = value;
    }
}
