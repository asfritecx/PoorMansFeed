using PoorMansFeed.Core;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed;

/// <summary>
/// The ModSecurity rules press, shared by every page that writes ModSecurity lists: it keeps a rule id for each list
/// file and opens the rules dialog under the work gate. It gives the line to show, or null when there is none.
/// </summary>
internal sealed class ModSecurityRulesPress
{
    private readonly IIpInfoService _ipinfo;
    private readonly IUiServices _ui;
    private readonly IWorkGate _gate;

    public ModSecurityRulesPress(IIpInfoService ipinfo, IUiServices ui, IWorkGate gate)
    {
        ArgumentNullException.ThrowIfNull(ipinfo);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(gate);
        _ipinfo = ipinfo;
        _ui = ui;
        _gate = gate;
    }

    public async Task<string?> RunAsync(IReadOnlyList<string> modSecurityFiles, Func<bool> mayShow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(modSecurityFiles);
        ArgumentNullException.ThrowIfNull(mayShow);

        if (_gate.IsHeld)
            return AppText.ModSecurityRulesBusy;

        ModSecurityRulesPrompt prompt;
        try
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in modSecurityFiles)
            {
                string name = Path.GetFileName(file);
                if (seen.Add(name))
                    names.Add(name);
            }

            // Holding no gate: the ids are kept even if the dialog then can't open, and are the same at the next press
            ModSecurityRuleIdsResult result = await _ipinfo.AssignModSecurityRuleIdsAsync(names, ct);
            if (result.Failure != ModSecurityRuleIdsFailure.None)
                return AppText.ModSecurityRuleIdsLine(result.Failure);

            // Read after the assignment, so a save that commits while it waits shows its location and action
            UserSettings settings = _ipinfo.Settings;

            prompt = new ModSecurityRulesPrompt(names, result.RuleIds, AppText.ModSecurityRuleLabel,
                settings.ModSecurityLocation ?? "", settings.ModSecurityAction);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            return AppText.Generic;
        }

        // Tested at the moment of showing; nothing is awaited between the test and the hold, so no other dialog opens
        if (_gate.IsHeld || !mayShow())
            return AppText.ModSecurityRulesBusy;

        using IDisposable hold = _gate.Hold();
        try
        {
            await _ui.ShowModSecurityRulesAsync(prompt);
            return null;
        }
        catch (Exception)
        {
            return AppText.Generic;
        }
    }
}

/// <summary>The modsecurity/ files of a page's last successful write, which its rules button offers. UI thread only.</summary>
internal sealed class ModSecurityRulesFiles
{
    private IReadOnlyList<string>? _files;
    private int _mark;

    /// <summary>Raised when <see cref="Shows"/> flips.</summary>
    public event EventHandler? ShowsChanged;

    public bool Shows => _files is { Count: > 0 };

    public IReadOnlyList<string>? Files => _files;

    /// <summary>Drops the files. The mark it gives goes back to <see cref="Set"/> with the write's result.</summary>
    public int Clear()
    {
        Replace(null);
        return ++_mark;
    }

    /// <summary>Takes a write's files, unless they were cleared again after the write's own clear.</summary>
    public void Set(int mark, IReadOnlyList<string>? files)
    {
        if (mark == _mark)
            Replace(files);
    }

    private void Replace(IReadOnlyList<string>? files)
    {
        bool before = Shows;
        _files = files;
        if (Shows != before)
            ShowsChanged?.Invoke(this, EventArgs.Empty);
    }
}
