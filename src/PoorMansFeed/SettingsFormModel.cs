using CommunityToolkit.Mvvm.ComponentModel;
using PoorMansFeed.Core;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed;

/// <summary>
/// The editable copy of <see cref="UserSettings"/> behind the Settings page's options. It holds no Avalonia types,
/// so its rules are tested without a window.
/// </summary>
public sealed class SettingsFormModel : ObservableObject
{
    private UserSettings _current;
    private UserSettings _baseline;
    private readonly string _defaultFolder;
    private readonly TimeProvider _time;

    private string _exportFolder;
    private bool _ipv6Enabled;
    private bool _fortigateEnabled;
    private bool _fortigateIncludeIPv4;
    private bool _fortigateIncludeIPv6;
    private bool _fortigateSplitByFamily;
    private bool _largeRangeCheckEnabled;
    private decimal? _ipv4MinPrefix;
    private decimal? _ipv6MinPrefix;
    private decimal? _abuseIpDbKeepDays;
    private bool _writeFortigateFeeds;
    private bool _writeModSecurityLists;
    private ModSecurityAction _modSecurityAction;
    private decimal? _firstRuleId;
    private string _modSecurityLocation = "";

    public SettingsFormModel(UserSettings current, string defaultExportFolder, TimeProvider? time = null)
    {
        _defaultFolder = defaultExportFolder;
        _time = time ?? TimeProvider.System;
        _current = current;
        _baseline = current;
        _exportFolder = "";
        Load(current);
    }

    /// <summary>Replaces every value with the saved settings', so the form has no changes afterwards.</summary>
    public void Load(UserSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        _current = current;
        _exportFolder = string.IsNullOrWhiteSpace(current.ExportFolder) ? _defaultFolder : current.ExportFolder;
        _ipv6Enabled = current.IPv6Enabled;
        _fortigateEnabled = current.FortigateEnabled;
        _fortigateIncludeIPv4 = current.FortigateIncludeIPv4;
        _fortigateIncludeIPv6 = current.FortigateIncludeIPv6;
        _fortigateSplitByFamily = current.FortigateSplitByFamily;
        _largeRangeCheckEnabled = current.LargeRangeCheckEnabled;
        _ipv4MinPrefix = current.IPv4MinPrefix;
        _ipv6MinPrefix = current.IPv6MinPrefix;
        _abuseIpDbKeepDays = current.AbuseIpDbKeepDays;
        _writeFortigateFeeds = current.WriteFortigateFeeds;
        _writeModSecurityLists = current.WriteModSecurityLists;
        _modSecurityAction = current.ModSecurityAction;
        _firstRuleId = current.ModSecurityFirstRuleId;
        _modSecurityLocation = current.ModSecurityLocation ?? "";
        // What an unedited form saves, so a saved folder that is the default's path doesn't count as a change
        _baseline = ToSettings();
        UpdateLocationNote();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>The export folder shown: the saved folder, or the default when none is saved.</summary>
    public string ExportFolder
    {
        get => _exportFolder;
        set
        {
            if (SetProperty(ref _exportFolder, value))
            {
                OnPropertyChanged(nameof(HasChanges));
            }
        }
    }

    /// <summary>False leaves IPv6 networks out of the dataset, searches and exports. The kept values stay as they are.</summary>
    public bool IPv6Enabled
    {
        get => _ipv6Enabled;
        set
        {
            if (SetProperty(ref _ipv6Enabled, value))
            {
                RevalidateInputs();
            }
        }
    }

    public bool FortigateEnabled
    {
        get => _fortigateEnabled;
        set
        {
            if (SetProperty(ref _fortigateEnabled, value))
            {
                RevalidateInputs();
            }
        }
    }

    public bool FortigateIncludeIPv4
    {
        get => _fortigateIncludeIPv4;
        set
        {
            if (SetProperty(ref _fortigateIncludeIPv4, value))
            {
                RevalidateInputs();
            }
        }
    }

    public bool FortigateIncludeIPv6
    {
        get => _fortigateIncludeIPv6;
        set
        {
            if (SetProperty(ref _fortigateIncludeIPv6, value))
            {
                RevalidateInputs();
            }
        }
    }

    public bool FortigateSplitByFamily
    {
        get => _fortigateSplitByFamily;
        set
        {
            if (SetProperty(ref _fortigateSplitByFamily, value))
            {
                OnPropertyChanged(nameof(HasChanges));
            }
        }
    }

    public bool LargeRangeCheckEnabled
    {
        get => _largeRangeCheckEnabled;
        set
        {
            if (SetProperty(ref _largeRangeCheckEnabled, value))
            {
                RevalidateInputs();
            }
        }
    }

    /// <summary>The IPv4 prefix below which a range is large. Null while the field is blank.</summary>
    public decimal? IPv4MinPrefix
    {
        get => _ipv4MinPrefix;
        set
        {
            if (SetProperty(ref _ipv4MinPrefix, value))
            {
                RevalidateInputs();
            }
        }
    }

    /// <summary>The IPv6 prefix below which a range is large. Null while the field is blank.</summary>
    public decimal? IPv6MinPrefix
    {
        get => _ipv6MinPrefix;
        set
        {
            if (SetProperty(ref _ipv6MinPrefix, value))
            {
                RevalidateInputs();
            }
        }
    }

    /// <summary>The days an address stays in the AbuseIPDB rolling list after the last download that listed it. Null while the field is blank.</summary>
    public decimal? AbuseIpDbKeepDays
    {
        get => _abuseIpDbKeepDays;
        set
        {
            if (SetProperty(ref _abuseIpDbKeepDays, value))
            {
                RevalidateInputs();
            }
        }
    }

    public bool WriteFortigateFeeds
    {
        get => _writeFortigateFeeds;
        set
        {
            if (SetProperty(ref _writeFortigateFeeds, value))
            {
                OnPropertyChanged(nameof(HasChanges));
            }
        }
    }

    public bool WriteModSecurityLists
    {
        get => _writeModSecurityLists;
        set
        {
            if (SetProperty(ref _writeModSecurityLists, value))
            {
                UpdateLocationNote();
                RevalidateInputs();
            }
        }
    }

    /// <summary>The actions are radio buttons, so only a press that ticks one acts; unticking does nothing.</summary>
    public bool IsLogOnly
    {
        get => _modSecurityAction == ModSecurityAction.LogOnly;
        set
        {
            if (value)
            {
                ChooseAction(ModSecurityAction.LogOnly);
            }
        }
    }

    public bool IsDeny
    {
        get => _modSecurityAction == ModSecurityAction.Deny;
        set
        {
            if (value)
            {
                ChooseAction(ModSecurityAction.Deny);
            }
        }
    }

    public bool IsClose
    {
        get => _modSecurityAction == ModSecurityAction.Close;
        set
        {
            if (value)
            {
                ChooseAction(ModSecurityAction.Close);
            }
        }
    }

    /// <summary>The rule id new ModSecurity lists start from. Null while the field is blank.</summary>
    public decimal? ModSecurityFirstRuleId
    {
        get => _firstRuleId;
        set
        {
            if (SetProperty(ref _firstRuleId, value))
            {
                RevalidateInputs();
            }
        }
    }

    /// <summary>The ModSecurity location as typed; it is trimmed only when checked and saved.</summary>
    public string ModSecurityLocation
    {
        get => _modSecurityLocation;
        set
        {
            if (SetProperty(ref _modSecurityLocation, value ?? ""))
            {
                UpdateLocationNote();
                RevalidateInputs();
            }
        }
    }

    public bool CanEditModSecurity => _writeModSecurityLists;

    /// <summary>The note under the location box, its own object, so a clock change is never a form edit.</summary>
    public LocationNote LocationNote { get; } = new();

    /// <summary>Reads the clock again for the expiry; the note raises only what changed.</summary>
    public void RefreshExpiry() => UpdateLocationNote();

    /// <summary>The Fortigate IPv6 box can be changed only while Fortigate is on and IPv6 is on.</summary>
    public bool CanEditFortigateIPv6 => _fortigateEnabled && _ipv6Enabled;

    /// <summary>The IPv6 prefix can be changed only while the large-range check is on and IPv6 is on.</summary>
    public bool CanEditIPv6Prefix => _largeRangeCheckEnabled && _ipv6Enabled;

    /// <summary>False when Save must be disabled: <see cref="ValidationText"/> names a problem.</summary>
    public bool IsValid => ValidationText.Length == 0;

    /// <summary>
    /// True when Save would change the saved settings, or the form has a problem: a saved value is never blank or a
    /// fraction. A saved location this build refuses counts only once the box no longer holds it.
    /// </summary>
    public bool HasChanges => Problem(exemptSavedLocation: true).Length > 0 || ToSettings() != _baseline;

    /// <summary>
    /// The message for the first problem found, or empty when the settings are valid. A blank or a fraction counts only
    /// in a box in use, and the whole-number rule comes after every other rule.
    /// </summary>
    public string ValidationText => Problem(exemptSavedLocation: false);

    private string Problem(bool exemptSavedLocation)
    {
        // A blank prefix matters only while the check uses it; with the check off the saved prefixes are kept.
        // The IPv6 prefix is not used while IPv6 is off, so a blank one is not a problem then.
        if (_largeRangeCheckEnabled && (_ipv4MinPrefix is null || (_ipv6Enabled && _ipv6MinPrefix is null)))
        {
            return AppText.EnterBothPrefixes;
        }

        // An IPv6 box that is ticked but switched off by IPv6 is not an address family
        if (_fortigateEnabled && !_fortigateIncludeIPv4 && !EffectiveFortigateIPv6)
        {
            return AppText.ChooseAddressFamily;
        }

        if (_abuseIpDbKeepDays is null)
        {
            return AppText.EnterKeepDays;
        }

        if (_writeModSecurityLists && _firstRuleId is null)
        {
            return AppText.EnterFirstRuleId;
        }

        string location = _modSecurityLocation.Trim();
        bool savedLocation = string.Equals(location, SavedLocation, StringComparison.Ordinal);
        if (_writeModSecurityLists && AppText.ModSecurityLocationIsRefused(location)
            && !(exemptSavedLocation && savedLocation))
        {
            return AppText.FixModSecurityLocation;
        }

        // A box shows a fraction rounded, so the value saved would not be the one shown. Only a box in use counts;
        // a box not in use keeps its saved value instead (ToSettings).
        if ((_largeRangeCheckEnabled && (IsFraction(_ipv4MinPrefix) || (_ipv6Enabled && IsFraction(_ipv6MinPrefix))))
            || IsFraction(_abuseIpDbKeepDays)
            || (_writeModSecurityLists && IsFraction(_firstRuleId)))
        {
            return AppText.WholeNumberRequired;
        }

        return "";
    }

    /// <summary>Goes back to the default folder, which is saved as an empty value.</summary>
    public void UseDefaultFolder() => ExportFolder = _defaultFolder;

    /// <summary>
    /// The default export folder is saved as empty, so a later change to the default still applies. A blank or a
    /// fraction keeps the saved value; ValidationText allows one only in a box not in use.
    /// </summary>
    public UserSettings ToSettings() => _current with
    {
        ExportFolder = string.Equals(_exportFolder, _defaultFolder, StringComparison.Ordinal) ? "" : _exportFolder,
        IPv6Enabled = _ipv6Enabled,
        FortigateEnabled = _fortigateEnabled,
        FortigateIncludeIPv4 = _fortigateIncludeIPv4,
        FortigateIncludeIPv6 = _fortigateIncludeIPv6,
        FortigateSplitByFamily = _fortigateSplitByFamily,
        LargeRangeCheckEnabled = _largeRangeCheckEnabled,
        IPv4MinPrefix = WholeOrSaved(_ipv4MinPrefix, _current.IPv4MinPrefix),
        IPv6MinPrefix = WholeOrSaved(_ipv6MinPrefix, _current.IPv6MinPrefix),
        AbuseIpDbKeepDays = WholeOrSaved(_abuseIpDbKeepDays, _current.AbuseIpDbKeepDays),
        WriteFortigateFeeds = _writeFortigateFeeds,
        WriteModSecurityLists = _writeModSecurityLists,
        ModSecurityAction = _modSecurityAction,
        ModSecurityFirstRuleId = WholeOrSaved(_firstRuleId, _current.ModSecurityFirstRuleId),
        ModSecurityLocation = AppText.ModSecurityLocationIsRefused(_modSecurityLocation)
            ? SavedLocation
            : _modSecurityLocation.Trim(),
    };

    private string SavedLocation => (_current.ModSecurityLocation ?? "").Trim();

    // A blank box or a fraction is never saved, not even truncated: the saved value stays
    private static int WholeOrSaved(decimal? value, int saved) => value is { } v && !IsFraction(v) ? (int)v : saved;

    private static bool IsFraction(decimal? value) => value is { } v && v != decimal.Truncate(v);

    // The Fortigate IPv6 box counts only while IPv6 is on
    private bool EffectiveFortigateIPv6 => _ipv6Enabled && _fortigateIncludeIPv6;

    private void ChooseAction(ModSecurityAction action)
    {
        if (_modSecurityAction == action)
        {
            return;
        }

        _modSecurityAction = action;
        OnPropertyChanged(nameof(IsLogOnly));
        OnPropertyChanged(nameof(IsDeny));
        OnPropertyChanged(nameof(IsClose));
        OnPropertyChanged(nameof(HasChanges));
    }

    private void UpdateLocationNote()
    {
        ModSecurityLocationNote note = AppText.ModSecurityLocationNoteFor(_modSecurityLocation, _time.GetUtcNow());
        bool error = note.IsRefused || note.Nearness == SasExpiryNearness.Passed;
        bool caution = note.Nearness == SasExpiryNearness.Soon;
        // An unticked location is never used, so its note is shown without a tone
        LocationNote.Set(note.Text, error && _writeModSecurityLists, caution && _writeModSecurityLists);
    }

    private void RevalidateInputs()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationText));
        OnPropertyChanged(nameof(CanEditFortigateIPv6));
        OnPropertyChanged(nameof(CanEditIPv6Prefix));
        OnPropertyChanged(nameof(CanEditModSecurity));
        OnPropertyChanged(nameof(HasChanges));
    }
}

/// <summary>The line under the ModSecurity location box: why it is refused, or when its SAS expires.</summary>
public sealed class LocationNote : ObservableObject
{
    private string _text = "";
    private bool _isError;
    private bool _isCaution;

    /// <summary>Empty when there is no note.</summary>
    public string Text => _text;

    public bool HasNote => _text.Length > 0;

    /// <summary>A refused location, or a SAS already expired, while ModSecurity is ticked.</summary>
    public bool IsError => _isError;

    /// <summary>A SAS that expires within the warning days, while ModSecurity is ticked.</summary>
    public bool IsCaution => _isCaution;

    /// <summary>Takes the note's new state whole, then raises each property that changed.</summary>
    internal void Set(string text, bool isError, bool isCaution)
    {
        bool hadNote = HasNote;
        bool textChanged = _text != text;
        bool errorChanged = _isError != isError;
        bool cautionChanged = _isCaution != isCaution;
        _text = text;
        _isError = isError;
        _isCaution = isCaution;

        if (textChanged)
        {
            OnPropertyChanged(nameof(Text));
        }

        if (hadNote != HasNote)
        {
            OnPropertyChanged(nameof(HasNote));
        }

        if (errorChanged)
        {
            OnPropertyChanged(nameof(IsError));
        }

        if (cautionChanged)
        {
            OnPropertyChanged(nameof(IsCaution));
        }
    }
}
