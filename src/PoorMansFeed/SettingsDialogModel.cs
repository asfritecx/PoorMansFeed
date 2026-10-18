using CommunityToolkit.Mvvm.ComponentModel;
using PoorMansFeed.Core;

namespace PoorMansFeed;

/// <summary>
/// The editable copy of <see cref="UserSettings"/> behind the settings dialog. It holds no Avalonia types, so its rules
/// are tested without a window. <see cref="ToSettings"/> returns the record with the edits applied.
/// </summary>
public sealed class SettingsDialogModel : ObservableObject
{
    private readonly UserSettings _current;
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

    public SettingsDialogModel(UserSettings current, string defaultExportFolder)
    {
        _current = current;
        _defaultFolder = defaultExportFolder;
        _exportFolder = string.IsNullOrWhiteSpace(current.ExportFolder) ? defaultExportFolder : current.ExportFolder;
        _ipv6Enabled = current.IPv6Enabled;
        _fortigateEnabled = current.FortigateEnabled;
        _fortigateIncludeIPv4 = current.FortigateIncludeIPv4;
        _fortigateIncludeIPv6 = current.FortigateIncludeIPv6;
        _fortigateSplitByFamily = current.FortigateSplitByFamily;
        _largeRangeCheckEnabled = current.LargeRangeCheckEnabled;
        _ipv4MinPrefix = current.IPv4MinPrefix;
        _ipv6MinPrefix = current.IPv6MinPrefix;
    }

    /// <summary>The export folder shown: the saved folder, or the default when none is saved.</summary>
    public string ExportFolder
    {
        get => _exportFolder;
        set => SetProperty(ref _exportFolder, value);
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
        set => SetProperty(ref _fortigateSplitByFamily, value);
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

    /// <summary>The Fortigate IPv6 box can be changed only while Fortigate is on and IPv6 is on.</summary>
    public bool CanEditFortigateIPv6 => _fortigateEnabled && _ipv6Enabled;

    /// <summary>The IPv6 prefix can be changed only while the large-range check is on and IPv6 is on.</summary>
    public bool CanEditIPv6Prefix => _largeRangeCheckEnabled && _ipv6Enabled;

    /// <summary>False when Save must be disabled: a blank prefix while the large-range check is on, or Fortigate on with no usable address family.</summary>
    public bool IsValid => ValidationText.Length == 0;

    /// <summary>The message for the first problem found, or empty when the settings are valid.</summary>
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

            return "";
        }
    }

    /// <summary>Goes back to the default folder, which is saved as an empty value.</summary>
    public void UseDefaultFolder() => ExportFolder = _defaultFolder;

    /// <summary>
    /// The edited settings. The export folder is saved as empty when it is the default, so a later change to the
    /// default still applies. A blank prefix keeps the value the dialog was opened with (it can be blank only while
    /// the large-range check is off, because Save is disabled otherwise).
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
        IPv4MinPrefix = (int)(_ipv4MinPrefix ?? _current.IPv4MinPrefix),
        IPv6MinPrefix = (int)(_ipv6MinPrefix ?? _current.IPv6MinPrefix),
    };

    // The Fortigate IPv6 box counts only while IPv6 is on
    private bool EffectiveFortigateIPv6 => _ipv6Enabled && _fortigateIncludeIPv6;

    private void RevalidateInputs()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationText));
        OnPropertyChanged(nameof(CanEditFortigateIPv6));
        OnPropertyChanged(nameof(CanEditIPv6Prefix));
    }
}
