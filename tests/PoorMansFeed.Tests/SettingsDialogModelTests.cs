using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>The settings dialog's rules, without a window: validity, the default-folder mapping and the record it saves.</summary>
public sealed class SettingsDialogModelTests
{
    private const string DefaultFolder = "/exports/default";

    [Fact]
    public void DefaultSettingsAreValid()
    {
        var model = new SettingsDialogModel(new UserSettings(), DefaultFolder);

        Assert.True(model.IsValid);
        Assert.Equal("", model.ValidationText);
    }

    [Fact]
    public void ABlankPrefixMakesTheSettingsInvalid()
    {
        var model = new SettingsDialogModel(new UserSettings(), DefaultFolder);

        model.IPv6MinPrefix = null;

        Assert.False(model.IsValid);
        Assert.Equal(AppText.EnterBothPrefixes, model.ValidationText);

        model.IPv6MinPrefix = 32;
        Assert.True(model.IsValid);
        Assert.Equal("", model.ValidationText);
    }

    [Fact]
    public void ABlankPrefixIsAllowedWhileTheLargeRangeCheckIsOffAndSavesTheValueItWasOpenedWith()
    {
        var saved = new UserSettings { LargeRangeCheckEnabled = true, IPv4MinPrefix = 12, IPv6MinPrefix = 40 };
        var model = new SettingsDialogModel(saved, DefaultFolder);

        model.LargeRangeCheckEnabled = false;
        model.IPv4MinPrefix = null;
        model.IPv6MinPrefix = null;

        Assert.True(model.IsValid);
        Assert.Equal("", model.ValidationText);
        UserSettings result = model.ToSettings();
        Assert.False(result.LargeRangeCheckEnabled);
        Assert.Equal(12, result.IPv4MinPrefix);
        Assert.Equal(40, result.IPv6MinPrefix);
    }

    [Fact]
    public void TurningTheCheckBackOnWithABlankPrefixMakesTheSettingsInvalidAgain()
    {
        var model = new SettingsDialogModel(new UserSettings { LargeRangeCheckEnabled = false }, DefaultFolder);
        model.IPv4MinPrefix = null;
        Assert.True(model.IsValid);

        model.LargeRangeCheckEnabled = true;

        Assert.False(model.IsValid);
        Assert.Equal(AppText.EnterBothPrefixes, model.ValidationText);
    }

    [Fact]
    public void FortigateWithNoAddressFamilyIsInvalid()
    {
        var model = new SettingsDialogModel(new UserSettings(), DefaultFolder);

        model.FortigateIncludeIPv4 = false;
        model.FortigateIncludeIPv6 = false;

        Assert.False(model.IsValid);
        Assert.Equal(AppText.ChooseAddressFamily, model.ValidationText);
    }

    [Fact]
    public void NoAddressFamilyIsFineWhenFortigateIsOff()
    {
        var model = new SettingsDialogModel(new UserSettings(), DefaultFolder);

        model.FortigateEnabled = false;
        model.FortigateIncludeIPv4 = false;
        model.FortigateIncludeIPv6 = false;

        Assert.True(model.IsValid);
        Assert.Equal("", model.ValidationText);
    }

    [Fact]
    public void ChangingAnInputRaisesValidityNotifications()
    {
        var model = new SettingsDialogModel(new UserSettings(), DefaultFolder);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.FortigateIncludeIPv4 = false;
        model.FortigateIncludeIPv6 = false;

        Assert.Contains(nameof(SettingsDialogModel.IsValid), raised);
        Assert.Contains(nameof(SettingsDialogModel.ValidationText), raised);
    }

    [Fact]
    public void ToSettingsRoundTripsAnUneditedRecord()
    {
        var current = new UserSettings
        {
            ExportFolder = "/exports/custom",
            FortigateEnabled = false,
            FortigateIncludeIPv4 = true,
            FortigateIncludeIPv6 = false,
            FortigateSplitByFamily = false,
            LargeRangeCheckEnabled = false,
            IPv4MinPrefix = 12,
            IPv6MinPrefix = 40,
        };

        var model = new SettingsDialogModel(current, DefaultFolder);

        Assert.Equal(current, model.ToSettings());
    }

    [Fact]
    public void ToSettingsSavesAnEditedValue()
    {
        var model = new SettingsDialogModel(new UserSettings(), DefaultFolder);

        model.IPv4MinPrefix = 20;
        model.FortigateSplitByFamily = false;

        UserSettings saved = model.ToSettings();
        Assert.Equal(20, saved.IPv4MinPrefix);
        Assert.False(saved.FortigateSplitByFamily);
    }

    [Fact]
    public void AnEmptySavedFolderShowsTheDefaultAndSavesEmpty()
    {
        var model = new SettingsDialogModel(new UserSettings { ExportFolder = "" }, DefaultFolder);

        Assert.Equal(DefaultFolder, model.ExportFolder);
        Assert.Equal("", model.ToSettings().ExportFolder);
    }

    [Fact]
    public void ACustomFolderIsSavedAsItself()
    {
        var model = new SettingsDialogModel(new UserSettings { ExportFolder = "/exports/custom" }, DefaultFolder);

        Assert.Equal("/exports/custom", model.ExportFolder);
        Assert.Equal("/exports/custom", model.ToSettings().ExportFolder);
    }

    [Fact]
    public void UseDefaultFolderGoesBackToTheDefaultAndSavesEmpty()
    {
        var model = new SettingsDialogModel(new UserSettings { ExportFolder = "/exports/custom" }, DefaultFolder);

        model.ExportFolder = "/exports/elsewhere";
        model.UseDefaultFolder();

        Assert.Equal(DefaultFolder, model.ExportFolder);
        Assert.Equal("", model.ToSettings().ExportFolder);
    }

    [Fact]
    public void IPv6OffIgnoresABlankIPv6PrefixWhileTheCheckIsOn()
    {
        var model = new SettingsDialogModel(new UserSettings(), DefaultFolder);

        model.IPv6Enabled = false;
        model.IPv6MinPrefix = null;

        Assert.True(model.IsValid);
        Assert.Equal("", model.ValidationText);
    }

    [Fact]
    public void IPv6OnWithABlankIPv6PrefixIsStillInvalid()
    {
        var model = new SettingsDialogModel(new UserSettings { IPv6Enabled = false }, DefaultFolder);
        model.IPv6MinPrefix = null;
        Assert.True(model.IsValid);

        model.IPv6Enabled = true;

        Assert.False(model.IsValid);
        Assert.Equal(AppText.EnterBothPrefixes, model.ValidationText);
    }

    [Fact]
    public void IPv6OffWithOnlyTheIPv6BoxTickedIsNotAValidFamily()
    {
        var model = new SettingsDialogModel(new UserSettings(), DefaultFolder);

        model.FortigateIncludeIPv4 = false;
        Assert.True(model.IsValid);
        model.IPv6Enabled = false;

        Assert.False(model.IsValid);
        Assert.Equal(AppText.ChooseAddressFamily, model.ValidationText);
    }

    [Fact]
    public void IPv6OffWithNoFortigateIPv6BoxIsStillValidWithIPv4()
    {
        var model = new SettingsDialogModel(new UserSettings(), DefaultFolder);

        model.IPv6Enabled = false;
        model.FortigateIncludeIPv6 = false;

        Assert.True(model.IsValid);
    }

    [Fact]
    public void CanEditFortigateIPv6NeedsFortigateAndIPv6On()
    {
        var model = new SettingsDialogModel(new UserSettings(), DefaultFolder);
        Assert.True(model.CanEditFortigateIPv6);

        model.IPv6Enabled = false;
        Assert.False(model.CanEditFortigateIPv6);

        model.IPv6Enabled = true;
        Assert.True(model.CanEditFortigateIPv6);

        model.FortigateEnabled = false;
        Assert.False(model.CanEditFortigateIPv6);
    }

    [Fact]
    public void CanEditIPv6PrefixNeedsTheCheckAndIPv6On()
    {
        var model = new SettingsDialogModel(new UserSettings { LargeRangeCheckEnabled = true }, DefaultFolder);
        Assert.True(model.CanEditIPv6Prefix);

        model.IPv6Enabled = false;
        Assert.False(model.CanEditIPv6Prefix);

        model.IPv6Enabled = true;
        model.LargeRangeCheckEnabled = false;
        Assert.False(model.CanEditIPv6Prefix);
    }

    [Fact]
    public void ChangingIPv6EnabledRaisesTheEditabilityNotifications()
    {
        var model = new SettingsDialogModel(new UserSettings(), DefaultFolder);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.IPv6Enabled = false;

        Assert.Contains(nameof(SettingsDialogModel.CanEditFortigateIPv6), raised);
        Assert.Contains(nameof(SettingsDialogModel.CanEditIPv6Prefix), raised);
        Assert.Contains(nameof(SettingsDialogModel.IsValid), raised);
    }

    [Fact]
    public void ToSettingsSavesIPv6EnabledOffAndKeepsTheIPv6Values()
    {
        var current = new UserSettings { IPv6Enabled = true, IPv6MinPrefix = 40, FortigateIncludeIPv6 = true };
        var model = new SettingsDialogModel(current, DefaultFolder);

        model.IPv6Enabled = false;
        UserSettings saved = model.ToSettings();

        Assert.False(saved.IPv6Enabled);
        Assert.Equal(40, saved.IPv6MinPrefix);
        Assert.True(saved.FortigateIncludeIPv6);
    }

    [Fact]
    public void ToSettingsRoundTripsIPv6EnabledOff()
    {
        var current = new UserSettings { IPv6Enabled = false };

        var model = new SettingsDialogModel(current, DefaultFolder);

        Assert.Equal(current, model.ToSettings());
    }
}
