using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed;

/// <summary>
/// Behind the ModSecurity rules dialog: the location and action as edited, and the rules built from them. It holds no
/// Avalonia types, and the clock is read again at each edit, so a SAS expiry is judged as of that edit.
/// </summary>
public sealed class ModSecurityRulesModel : ObservableObject
{
    private readonly ModSecurityRulesPrompt _prompt;
    private readonly TimeProvider _time;
    private readonly Func<string, Task> _copyText;

    private string _location;
    private ModSecurityAction _action;
    private string _rulesText = "";
    private int _ruleCount;
    private string _previewText = "";
    private bool _hasRules;
    private string _expiryText = "";
    private bool _isExpiryCaution;
    private bool _isExpiryCritical;
    private string _copyStatusText = "";

    public ModSecurityRulesModel(ModSecurityRulesPrompt prompt, TimeProvider time, Func<string, Task> copyText)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(copyText);
        _prompt = prompt;
        _time = time;
        _copyText = copyText;
        _location = prompt.Location;
        _action = prompt.Action;
        IdsText = AppText.ModSecurityRulesIds(prompt.FileNames, prompt.RuleIds);
        CopyCommand = new AsyncRelayCommand(CopyAsync, () => HasRules);
        Recompute();
    }

    /// <summary>As typed; the rules and the note are built from it trimmed.</summary>
    public string Location
    {
        get => _location;
        set
        {
            if (SetProperty(ref _location, value ?? ""))
            {
                Recompute();
            }
        }
    }

    public bool IsLogOnly
    {
        get => _action == ModSecurityAction.LogOnly;
        set => PickAction(value, ModSecurityAction.LogOnly);
    }

    public bool IsDeny
    {
        get => _action == ModSecurityAction.Deny;
        set => PickAction(value, ModSecurityAction.Deny);
    }

    public bool IsClose
    {
        get => _action == ModSecurityAction.Close;
        set => PickAction(value, ModSecurityAction.Close);
    }

    public string IdsText { get; }

    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    public bool HasRules
    {
        get => _hasRules;
        private set
        {
            if (SetProperty(ref _hasRules, value))
            {
                OnPropertyChanged(nameof(IsProblem));
                CopyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsProblem => !HasRules;

    public string ExpiryText
    {
        get => _expiryText;
        private set
        {
            if (SetProperty(ref _expiryText, value))
            {
                OnPropertyChanged(nameof(HasExpiry));
            }
        }
    }

    public bool HasExpiry => ExpiryText.Length > 0;

    public bool IsExpiryCaution
    {
        get => _isExpiryCaution;
        private set => SetProperty(ref _isExpiryCaution, value);
    }

    public bool IsExpiryCritical
    {
        get => _isExpiryCritical;
        private set => SetProperty(ref _isExpiryCritical, value);
    }

    public string CopyStatusText
    {
        get => _copyStatusText;
        private set
        {
            if (SetProperty(ref _copyStatusText, value))
            {
                OnPropertyChanged(nameof(HasCopyStatus));
            }
        }
    }

    public bool HasCopyStatus => CopyStatusText.Length > 0;

    public IAsyncRelayCommand CopyCommand { get; }

    // A radio group writes false to the button it unchecks, so only true picks an action
    private void PickAction(bool value, ModSecurityAction action)
    {
        if (!value || _action == action)
        {
            return;
        }

        _action = action;
        OnPropertyChanged(nameof(IsLogOnly));
        OnPropertyChanged(nameof(IsDeny));
        OnPropertyChanged(nameof(IsClose));
        Recompute();
    }

    // The request, the rule set and the note stay locals: a record's ToString would print the location
    private void Recompute()
    {
        string trimmed = _location.Trim();
        ModSecurityRuleSet set = ModSecurityRules.Build(new ModSecurityRuleRequest(
            _prompt.RuleIds, _action, trimmed, _prompt.FileNames, _prompt.FeedLabel));
        ModSecurityLocationNote note = AppText.ModSecurityLocationNoteFor(trimmed, _time.GetUtcNow());

        string rulesText = set.Failure == ModSecurityRuleFailure.None ? set.Text : "";
        if (rulesText != _rulesText)
        {
            CopyStatusText = "";
        }

        _rulesText = rulesText;
        _ruleCount = set.Failure == ModSecurityRuleFailure.None ? set.Rules.Count : 0;
        PreviewText = set.Failure switch
        {
            ModSecurityRuleFailure.None => set.Text,
            ModSecurityRuleFailure.NoLocation => AppText.ModSecurityRulesEnterLocation,
            // Build judges the files and ids first, but a refusal is the one the user can fix, so it shows first
            _ when note.IsRefused => note.Text,
            ModSecurityRuleFailure.NoFiles or ModSecurityRuleFailure.FileNameInvalid
                or ModSecurityRuleFailure.RuleIdOutOfRange or ModSecurityRuleFailure.RuleIdRepeated => AppText.Generic,
            _ => AppText.ModSecurityLocationProblem(set.Failure) ?? AppText.ModSecurityLocationInvalid,
        };
        HasRules = set.Failure == ModSecurityRuleFailure.None;

        // A refusal always shows in the preview, so it isn't repeated under the box
        ExpiryText = note.IsRefused ? "" : note.Text;
        IsExpiryCritical = !note.IsRefused && note.Nearness == SasExpiryNearness.Passed;
        IsExpiryCaution = !note.IsRefused && note.Nearness == SasExpiryNearness.Soon;
    }

    private async Task CopyAsync()
    {
        string text = _rulesText;
        int count = _ruleCount;
        string line;
        try
        {
            await _copyText(text);
            line = AppText.ModSecurityRulesCopied(count);
        }
        catch (Exception)
        {
            // Whatever the clipboard threw, the dialog says the copy failed and stays open
            line = AppText.ModSecurityRulesCopyFailed;
        }

        // An edit during the copy cleared the line; it stays clear unless the rules are again the ones copied
        if (_rulesText == text)
        {
            CopyStatusText = line;
        }
    }
}
