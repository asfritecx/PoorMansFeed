using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed;

/// <summary>
/// Why the AbuseIPDB key is asked for: a check found none saved, AbuseIPDB refused the saved one, or the user chose
/// to set a key.
/// </summary>
public enum AbuseKeyAsk { Missing, Rejected, Replace }

/// <summary>What became of one ask. The key itself is never part of it.</summary>
internal enum AbuseKeyOutcome { Saved, Cancelled, Busy, NotAKey, SaveFailed, DialogFailed }

/// <summary>
/// Asks for the AbuseIPDB key and saves it, for Check AbuseIPDB and for the Settings page alike, so only one prompt is
/// ever open. The gate is held while the dialog is open and the save runs. The typed key passes straight from the
/// dialog to the service: it is kept in a local variable only, and never returned, stored or raised.
/// </summary>
internal sealed class AbuseKeyPrompt(IUiServices ui, IAbuseIpDbService abuse, IWorkGate gate)
{
    private bool _isOpen;

    /// <summary>True while the dialog is open or its save runs.</summary>
    public bool IsOpen => _isOpen;

    /// <summary>The gate the prompt holds, so a view model can refuse a prompt built on another one.</summary>
    internal IWorkGate Gate => gate;

    public event EventHandler? IsOpenChanged;

    public async Task<AbuseKeyOutcome> AskAndSaveAsync(AbuseKeyAsk why)
    {
        if (_isOpen || !gate.CanStart)
        {
            return AbuseKeyOutcome.Busy;
        }

        SetOpen(true);
        try
        {
            using IDisposable hold = gate.Hold();
            string? key;
            try
            {
                key = await ui.PromptForAbuseIpDbKeyAsync(why);
            }
            catch (Exception)
            {
                return AbuseKeyOutcome.DialogFailed;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                return AbuseKeyOutcome.Cancelled;
            }

            try
            {
                return await abuse.SaveKeyAsync(key) switch
                {
                    AbuseKeySave.Saved => AbuseKeyOutcome.Saved,
                    AbuseKeySave.NotAKey => AbuseKeyOutcome.NotAKey,
                    _ => AbuseKeyOutcome.SaveFailed,
                };
            }
            catch (Exception)
            {
                return AbuseKeyOutcome.SaveFailed;
            }
        }
        finally
        {
            SetOpen(false);
        }
    }

    private void SetOpen(bool open)
    {
        _isOpen = open;
        IsOpenChanged?.Invoke(this, EventArgs.Empty);
    }
}
