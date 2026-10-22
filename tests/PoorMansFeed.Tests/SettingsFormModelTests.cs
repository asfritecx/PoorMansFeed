using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>The rules of the Settings page's options, without a window: validity, the default-folder mapping and the record it saves.</summary>
public sealed class SettingsFormModelTests
{
    private const string DefaultFolder = "/exports/default";

    [Fact]
    public void DefaultSettingsAreValid()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

        Assert.True(model.IsValid);
        Assert.Equal("", model.ValidationText);
    }

    [Fact]
    public void ABlankPrefixMakesTheSettingsInvalid()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

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
        var model = new SettingsFormModel(saved, DefaultFolder);

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
        var model = new SettingsFormModel(new UserSettings { LargeRangeCheckEnabled = false }, DefaultFolder);
        model.IPv4MinPrefix = null;
        Assert.True(model.IsValid);

        model.LargeRangeCheckEnabled = true;

        Assert.False(model.IsValid);
        Assert.Equal(AppText.EnterBothPrefixes, model.ValidationText);
    }

    [Fact]
    public void FortigateWithNoAddressFamilyIsInvalid()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

        model.FortigateIncludeIPv4 = false;
        model.FortigateIncludeIPv6 = false;

        Assert.False(model.IsValid);
        Assert.Equal(AppText.ChooseAddressFamily, model.ValidationText);
    }

    [Fact]
    public void NoAddressFamilyIsFineWhenFortigateIsOff()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

        model.FortigateEnabled = false;
        model.FortigateIncludeIPv4 = false;
        model.FortigateIncludeIPv6 = false;

        Assert.True(model.IsValid);
        Assert.Equal("", model.ValidationText);
    }

    [Fact]
    public void ChangingAnInputRaisesValidityNotifications()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.FortigateIncludeIPv4 = false;
        model.FortigateIncludeIPv6 = false;

        Assert.Contains(nameof(SettingsFormModel.IsValid), raised);
        Assert.Contains(nameof(SettingsFormModel.ValidationText), raised);
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
            AbuseIpDbKeepDays = 90,
        };

        var model = new SettingsFormModel(current, DefaultFolder);

        Assert.Equal((decimal?)90, model.AbuseIpDbKeepDays);
        Assert.Equal(current, model.ToSettings());
    }

    [Fact]
    public void ToSettingsSavesAnEditedKeepDays()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

        model.AbuseIpDbKeepDays = 120;

        Assert.True(model.IsValid);
        Assert.Equal(120, model.ToSettings().AbuseIpDbKeepDays);
    }

    [Fact]
    public void ABlankKeepDaysMakesTheSettingsInvalid()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

        model.AbuseIpDbKeepDays = null;

        Assert.False(model.IsValid);
        Assert.Equal(AppText.EnterKeepDays, model.ValidationText);

        model.AbuseIpDbKeepDays = 7;
        Assert.True(model.IsValid);
        Assert.Equal("", model.ValidationText);
    }

    [Fact]
    public void ABlankPrefixIsReportedBeforeABlankKeepDays()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

        model.AbuseIpDbKeepDays = null;
        model.IPv4MinPrefix = null;

        Assert.Equal(AppText.EnterBothPrefixes, model.ValidationText);
    }

    [Fact]
    public void ChangingTheKeepDaysRaisesValidityNotifications()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.AbuseIpDbKeepDays = null;

        Assert.Contains(nameof(SettingsFormModel.AbuseIpDbKeepDays), raised);
        Assert.Contains(nameof(SettingsFormModel.IsValid), raised);
        Assert.Contains(nameof(SettingsFormModel.ValidationText), raised);
    }

    [Fact]
    public void ToSettingsSavesAnEditedValue()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

        model.IPv4MinPrefix = 20;
        model.FortigateSplitByFamily = false;

        UserSettings saved = model.ToSettings();
        Assert.Equal(20, saved.IPv4MinPrefix);
        Assert.False(saved.FortigateSplitByFamily);
    }

    [Fact]
    public void AnEmptySavedFolderShowsTheDefaultAndSavesEmpty()
    {
        var model = new SettingsFormModel(new UserSettings { ExportFolder = "" }, DefaultFolder);

        Assert.Equal(DefaultFolder, model.ExportFolder);
        Assert.Equal("", model.ToSettings().ExportFolder);
    }

    [Fact]
    public void ACustomFolderIsSavedAsItself()
    {
        var model = new SettingsFormModel(new UserSettings { ExportFolder = "/exports/custom" }, DefaultFolder);

        Assert.Equal("/exports/custom", model.ExportFolder);
        Assert.Equal("/exports/custom", model.ToSettings().ExportFolder);
    }

    [Fact]
    public void UseDefaultFolderGoesBackToTheDefaultAndSavesEmpty()
    {
        var model = new SettingsFormModel(new UserSettings { ExportFolder = "/exports/custom" }, DefaultFolder);

        model.ExportFolder = "/exports/elsewhere";
        model.UseDefaultFolder();

        Assert.Equal(DefaultFolder, model.ExportFolder);
        Assert.Equal("", model.ToSettings().ExportFolder);
    }

    [Fact]
    public void IPv6OffIgnoresABlankIPv6PrefixWhileTheCheckIsOn()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

        model.IPv6Enabled = false;
        model.IPv6MinPrefix = null;

        Assert.True(model.IsValid);
        Assert.Equal("", model.ValidationText);
    }

    [Fact]
    public void IPv6OnWithABlankIPv6PrefixIsStillInvalid()
    {
        var model = new SettingsFormModel(new UserSettings { IPv6Enabled = false }, DefaultFolder);
        model.IPv6MinPrefix = null;
        Assert.True(model.IsValid);

        model.IPv6Enabled = true;

        Assert.False(model.IsValid);
        Assert.Equal(AppText.EnterBothPrefixes, model.ValidationText);
    }

    [Fact]
    public void IPv6OffWithOnlyTheIPv6BoxTickedIsNotAValidFamily()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

        model.FortigateIncludeIPv4 = false;
        Assert.True(model.IsValid);
        model.IPv6Enabled = false;

        Assert.False(model.IsValid);
        Assert.Equal(AppText.ChooseAddressFamily, model.ValidationText);
    }

    [Fact]
    public void IPv6OffWithNoFortigateIPv6BoxIsStillValidWithIPv4()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

        model.IPv6Enabled = false;
        model.FortigateIncludeIPv6 = false;

        Assert.True(model.IsValid);
    }

    [Fact]
    public void CanEditFortigateIPv6NeedsFortigateAndIPv6On()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);
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
        var model = new SettingsFormModel(new UserSettings { LargeRangeCheckEnabled = true }, DefaultFolder);
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
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.IPv6Enabled = false;

        Assert.Contains(nameof(SettingsFormModel.CanEditFortigateIPv6), raised);
        Assert.Contains(nameof(SettingsFormModel.CanEditIPv6Prefix), raised);
        Assert.Contains(nameof(SettingsFormModel.IsValid), raised);
    }

    [Fact]
    public void ToSettingsSavesIPv6EnabledOffAndKeepsTheIPv6Values()
    {
        var current = new UserSettings { IPv6Enabled = true, IPv6MinPrefix = 40, FortigateIncludeIPv6 = true };
        var model = new SettingsFormModel(current, DefaultFolder);

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

        var model = new SettingsFormModel(current, DefaultFolder);

        Assert.Equal(current, model.ToSettings());
    }
    [Fact]
    public void LoadReplacesEveryValueAndClearsHasChanges()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);
        model.IPv4MinPrefix = null;
        model.ExportFolder = "/exports/edited";
        Assert.True(model.HasChanges);
        var saved = new UserSettings
        {
            ExportFolder = "/exports/saved",
            IPv6Enabled = false,
            FortigateEnabled = false,
            FortigateIncludeIPv4 = false,
            FortigateIncludeIPv6 = true,
            FortigateSplitByFamily = false,
            LargeRangeCheckEnabled = false,
            IPv4MinPrefix = 20,
            IPv6MinPrefix = 40,
            AbuseIpDbKeepDays = 90,
        };
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.Load(saved);

        Assert.Equal("/exports/saved", model.ExportFolder);
        Assert.False(model.IPv6Enabled);
        Assert.False(model.FortigateEnabled);
        Assert.False(model.FortigateIncludeIPv4);
        Assert.True(model.FortigateIncludeIPv6);
        Assert.False(model.FortigateSplitByFamily);
        Assert.False(model.LargeRangeCheckEnabled);
        Assert.Equal(20m, model.IPv4MinPrefix);
        Assert.Equal(40m, model.IPv6MinPrefix);
        Assert.Equal(90m, model.AbuseIpDbKeepDays);
        Assert.Equal(saved, model.ToSettings());
        Assert.False(model.HasChanges);
        Assert.True(model.IsValid);
        Assert.Contains(string.Empty, raised);

        // A blank keep days after Load still saves the value Load gave
        model.AbuseIpDbKeepDays = null;
        model.LargeRangeCheckEnabled = false;
        Assert.Equal(90, model.ToSettings().AbuseIpDbKeepDays);
    }

    [Fact]
    public void HasChangesIsFalseUntilAValueDiffersAndTrueWhileInvalid()
    {
        var model = new SettingsFormModel(new UserSettings { AbuseIpDbKeepDays = 30 }, DefaultFolder);
        Assert.False(model.HasChanges);

        model.AbuseIpDbKeepDays = 31;
        Assert.True(model.HasChanges);
        model.AbuseIpDbKeepDays = 30;
        Assert.False(model.HasChanges);

        // Blank saves the value it was opened with, so only the invalid term makes it a change
        model.AbuseIpDbKeepDays = null;
        Assert.Equal(new UserSettings { AbuseIpDbKeepDays = 30 }, model.ToSettings());
        Assert.False(model.IsValid);
        Assert.True(model.HasChanges);
        model.AbuseIpDbKeepDays = 30;

        model.FortigateSplitByFamily = !model.FortigateSplitByFamily;
        Assert.True(model.HasChanges);
        model.FortigateSplitByFamily = !model.FortigateSplitByFamily;
        Assert.False(model.HasChanges);

        model.ExportFolder = "/exports/other";
        Assert.True(model.HasChanges);
        model.UseDefaultFolder();
        Assert.False(model.HasChanges);
    }

    [Fact]
    public void AFolderSavedAsTheDefaultsPathIsNoChange()
    {
        var model = new SettingsFormModel(new UserSettings { ExportFolder = DefaultFolder }, DefaultFolder);

        Assert.False(model.HasChanges);
    }

    [Theory]
    [InlineData("ipv4")]
    [InlineData("ipv6")]
    [InlineData("keep")]
    public void AFractionInAnyNumberBoxIsInvalid(string box)
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

        switch (box)
        {
            case "ipv4": model.IPv4MinPrefix = 16.5m; break;
            case "ipv6": model.IPv6MinPrefix = 32.5m; break;
            default: model.AbuseIpDbKeepDays = 2.7m; break;
        }

        Assert.False(model.IsValid);
        Assert.Equal(AppText.WholeNumberRequired, model.ValidationText);
        Assert.Equal("Enter whole numbers only.", model.ValidationText);
        Assert.True(model.HasChanges);
    }

    [Fact]
    public void AWholeNumberWrittenWithDecimalsIsValid()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);

        model.AbuseIpDbKeepDays = 45.00m;

        Assert.True(model.IsValid);
        Assert.Equal(45, model.ToSettings().AbuseIpDbKeepDays);
    }

    [Fact]
    public void AFractionInTheIPv6PrefixIsIgnoredWhileIPv6IsOff()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);
        model.IPv6MinPrefix = 32.5m;
        Assert.False(model.IsValid);

        model.IPv6Enabled = false;

        Assert.True(model.IsValid);
        Assert.Equal("", model.ValidationText);
    }

    [Fact]
    public void AFractionInTheIPv6PrefixWhileIPv6IsOffSavesTheSavedValueNotItsTruncation()
    {
        var model = new SettingsFormModel(new UserSettings { IPv6MinPrefix = 48 }, DefaultFolder);
        model.IPv6Enabled = false;

        model.IPv6MinPrefix = 32.5m;

        Assert.True(model.IsValid);
        Assert.Equal(48, model.ToSettings().IPv6MinPrefix);
    }

    [Fact]
    public void AFractionInTheIPv6PrefixWhileTheCheckIsOffSavesTheSavedValue()
    {
        var model = new SettingsFormModel(new UserSettings { IPv6MinPrefix = 48 }, DefaultFolder);
        model.LargeRangeCheckEnabled = false;

        model.IPv6MinPrefix = 32.5m;

        Assert.True(model.IsValid);
        Assert.Equal(48, model.ToSettings().IPv6MinPrefix);
    }

    [Fact]
    public void AFractionInTheIPv4PrefixWhileTheCheckIsOffIsValidAndSavesTheSavedValue()
    {
        var model = new SettingsFormModel(new UserSettings { IPv4MinPrefix = 20 }, DefaultFolder);
        model.IPv4MinPrefix = 16.5m;
        Assert.Equal(AppText.WholeNumberRequired, model.ValidationText);

        model.LargeRangeCheckEnabled = false;

        Assert.True(model.IsValid);
        Assert.Equal("", model.ValidationText);
        Assert.Equal(20, model.ToSettings().IPv4MinPrefix);

        // Back in use, the fraction is refused again
        model.LargeRangeCheckEnabled = true;
        Assert.Equal(AppText.WholeNumberRequired, model.ValidationText);
    }

    [Fact]
    public void AWholeNumberInABoxNotInUseIsStillSaved()
    {
        var model = new SettingsFormModel(new UserSettings { IPv4MinPrefix = 20, IPv6MinPrefix = 48 }, DefaultFolder);
        model.LargeRangeCheckEnabled = false;

        model.IPv4MinPrefix = 18;
        model.IPv6MinPrefix = 40;

        Assert.True(model.IsValid);
        UserSettings saved = model.ToSettings();
        Assert.Equal(18, saved.IPv4MinPrefix);
        Assert.Equal(40, saved.IPv6MinPrefix);
    }

    [Fact]
    public void AFractionInTheKeepDaysIsRefusedWhateverTheOtherSwitches()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);
        model.LargeRangeCheckEnabled = false;
        model.IPv6Enabled = false;

        model.AbuseIpDbKeepDays = 2.7m;

        Assert.Equal(AppText.WholeNumberRequired, model.ValidationText);
    }

    [Fact]
    public void TheWholeNumberRuleComesAfterTheBlankRules()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);
        model.AbuseIpDbKeepDays = 2.7m;
        model.IPv4MinPrefix = null;
        Assert.Equal(AppText.EnterBothPrefixes, model.ValidationText);

        model.IPv4MinPrefix = 16;
        model.FortigateEnabled = true;
        model.FortigateIncludeIPv4 = false;
        model.FortigateIncludeIPv6 = false;
        Assert.Equal(AppText.ChooseAddressFamily, model.ValidationText);

        model.FortigateIncludeIPv4 = true;
        model.AbuseIpDbKeepDays = null;
        Assert.Equal(AppText.EnterKeepDays, model.ValidationText);

        model.AbuseIpDbKeepDays = 2.7m;
        Assert.Equal(AppText.WholeNumberRequired, model.ValidationText);
    }

    [Fact]
    public void ChangingAValueRaisesHasChanges()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.ExportFolder = "/exports/other";
        model.IPv6Enabled = false;
        model.FortigateEnabled = !model.FortigateEnabled;
        model.FortigateIncludeIPv4 = !model.FortigateIncludeIPv4;
        model.FortigateIncludeIPv6 = !model.FortigateIncludeIPv6;
        model.FortigateSplitByFamily = !model.FortigateSplitByFamily;
        model.LargeRangeCheckEnabled = !model.LargeRangeCheckEnabled;
        model.IPv4MinPrefix = 20;
        model.IPv6MinPrefix = 40;
        model.AbuseIpDbKeepDays = 60;

        Assert.Equal(10, raised.Count(name => name == nameof(SettingsFormModel.HasChanges)));
    }
}
