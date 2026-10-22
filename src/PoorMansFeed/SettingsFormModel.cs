using CommunityToolkit.Mvvm.ComponentModel;
using PoorMansFeed.Core;

namespace PoorMansFeed;

/// <summary>
/// The editable copy of <see cref="UserSettings"/> behind the Settings page's options. It holds no Avalonia types, so its rules
/// are tested without a window. <see cref="ToSettings"/> returns the record with the edits applied. The settings are
/// invalid while a prefix the large-range check uses is blank, while Fortigate is on with no address family, while the
/// AbuseIPDB keep days are blank, or while a number box in use holds a fraction. <see cref="Load"/> takes saved settings again,
/// and <see cref="HasChanges"/> says whether the form differs from them.
/// </summary>
public sealed class SettingsFormModel : ObservableObject
{
    private UserSettings _current;
    private UserSettings _baseline;
    private readonly string _defaultFolder;

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

    public SettingsFormModel(UserSettings current, string defaultExportFolder)
    {
        _defaultFolder = defaultExportFolder;
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
        // What an unedited form saves, so a saved folder that is the default's path doesn't count as a change
        _baseline = ToSettings();
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

    /// <summary>The Fortigate IPv6 box can be changed only while Fortigate is on and IPv6 is on.</summary>
    public bool CanEditFortigateIPv6 => _fortigateEnabled && _ipv6Enabled;

    /// <summary>The IPv6 prefix can be changed only while the large-range check is on and IPv6 is on.</summary>
    public bool CanEditIPv6Prefix => _largeRangeCheckEnabled && _ipv6Enabled;

    /// <summary>False when Save must be disabled: a blank prefix while the large-range check is on, Fortigate on with no usable address family, or blank keep days.</summary>
    public bool IsValid => ValidationText.Length == 0;

    /// <summary>
    /// True when Save would change the saved settings. An invalid form always counts, because a saved value is never
    /// blank or a fraction.
    /// </summary>
    public bool HasChanges => !IsValid || ToSettings() != _baseline;

    /// <summary>
    /// The message for the first problem found, or empty when the settings are valid. A blank or a fraction counts only
    /// in a box in use, and the whole-number rule comes after every other rule.
    /// </summary>
    public string ValidationText
    {
        get
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

            // A box shows a fraction rounded, so the value saved would not be the one shown. Only a box in use counts:
            // the IPv4 prefix while the check is on, the IPv6 prefix while the check and IPv6 are on, and the keep days
            // always. A box not in use keeps its saved value instead (ToSettings).
            if ((_largeRangeCheckEnabled && (IsFraction(_ipv4MinPrefix) || (_ipv6Enabled && IsFraction(_ipv6MinPrefix))))
                || IsFraction(_abuseIpDbKeepDays))
            {
                return AppText.WholeNumberRequired;
            }

            return "";
        }
    }

    /// <summary>Goes back to the default folder, which is saved as an empty value.</summary>
    public void UseDefaultFolder() => ExportFolder = _defaultFolder;

    /// <summary>
    /// The edited settings. The export folder is saved as empty when it is the default, so a later change to the
    /// default still applies. A number box that is blank or holds a fraction keeps the saved value. That can happen only
    /// in a box not in use (the IPv4 prefix while the large-range check is off, the IPv6 prefix while the check or IPv6
    /// is off), because ValidationText refuses a blank or a fraction in a box in use. A whole number is saved whether
    /// the box is in use or not. The store clamps the result to its range.
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
    };

    // A blank box or a fraction is never saved, not even truncated: the saved value stays
    private static int WholeOrSaved(decimal? value, int saved) => value is { } v && !IsFraction(v) ? (int)v : saved;

    private static bool IsFraction(decimal? value) => value is { } v && v != decimal.Truncate(v);

    // The Fortigate IPv6 box counts only while IPv6 is on
    private bool EffectiveFortigateIPv6 => _ipv6Enabled && _fortigateIncludeIPv6;

    private void RevalidateInputs()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationText));
        OnPropertyChanged(nameof(CanEditFortigateIPv6));
        OnPropertyChanged(nameof(CanEditIPv6Prefix));
        OnPropertyChanged(nameof(HasChanges));
    }
}
