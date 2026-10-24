using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.ModSecurity;

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

    // ----- feed types -----

    private const string SasBase = "https://acct.blob.core.windows.net/feeds?";

    private static readonly UserSettings ModSecurityTicked = new() { WriteModSecurityLists = true };

    [Fact]
    public void LoadAndToSettingsRoundTripTheFeedTypeFields()
    {
        var saved = new UserSettings
        {
            WriteFortigateFeeds = true,
            WriteModSecurityLists = true,
            ModSecurityAction = ModSecurityAction.Deny,
            ModSecurityFirstRuleId = 20_000,
            ModSecurityLocation = "/etc/modsecurity/lists",
        };
        var model = new SettingsFormModel(saved, DefaultFolder);

        Assert.True(model.WriteFortigateFeeds);
        Assert.True(model.WriteModSecurityLists);
        Assert.True(model.IsDeny);
        Assert.False(model.IsLogOnly);
        Assert.Equal(20_000m, model.ModSecurityFirstRuleId);
        Assert.Equal("/etc/modsecurity/lists", model.ModSecurityLocation);
        Assert.True(model.CanEditModSecurity);
        Assert.Equal(saved, model.ToSettings());
        Assert.False(model.HasChanges);

        model.WriteFortigateFeeds = false;
        model.WriteModSecurityLists = false;
        model.IsLogOnly = true;
        model.ModSecurityFirstRuleId = 30_000;
        model.ModSecurityLocation = "https://h.example/lists";
        UserSettings result = model.ToSettings();

        Assert.False(result.WriteFortigateFeeds);
        Assert.False(result.WriteModSecurityLists);
        Assert.Equal(ModSecurityAction.LogOnly, result.ModSecurityAction);
        Assert.Equal(30_000, result.ModSecurityFirstRuleId);
        Assert.Equal("https://h.example/lists", result.ModSecurityLocation);
        Assert.False(model.CanEditModSecurity);
    }

    [Fact]
    public void TheLocationIsSavedTrimmed()
    {
        var model = new SettingsFormModel(ModSecurityTicked, DefaultFolder);

        model.ModSecurityLocation = "  /etc/modsecurity/lists\n";

        Assert.Equal("  /etc/modsecurity/lists\n", model.ModSecurityLocation);
        Assert.Equal("", model.ValidationText);
        Assert.Equal("/etc/modsecurity/lists", model.ToSettings().ModSecurityLocation);

        model.ModSecurityLocation = null!;
        Assert.Equal("", model.ModSecurityLocation);
        Assert.Equal("", model.ToSettings().ModSecurityLocation);
    }

    [Fact]
    public void WhileModSecurityIsUntickedItsBoxesKeepTheirSavedValues()
    {
        var saved = new UserSettings { ModSecurityFirstRuleId = 12_000, ModSecurityLocation = "/srv/lists" };
        var model = new SettingsFormModel(saved, DefaultFolder);

        model.ModSecurityFirstRuleId = null;
        Assert.Equal("", model.ValidationText);
        Assert.Equal(12_000, model.ToSettings().ModSecurityFirstRuleId);

        model.ModSecurityFirstRuleId = 10_000.5m;
        Assert.Equal("", model.ValidationText);
        Assert.Equal(12_000, model.ToSettings().ModSecurityFirstRuleId);

        model.ModSecurityLocation = "http://example.com/lists";
        Assert.Equal("", model.ValidationText);
        Assert.True(model.IsValid);
        Assert.Equal("/srv/lists", model.ToSettings().ModSecurityLocation);
    }

    [Fact]
    public void TheModSecurityRulesComeBetweenTheKeepDaysAndTheWholeNumberRule()
    {
        var model = new SettingsFormModel(ModSecurityTicked, DefaultFolder);
        model.AbuseIpDbKeepDays = null;
        model.ModSecurityFirstRuleId = null;
        model.ModSecurityLocation = "http://example.com/lists";
        Assert.Equal(AppText.EnterKeepDays, model.ValidationText);

        model.AbuseIpDbKeepDays = 2.7m;
        Assert.Equal(AppText.EnterFirstRuleId, model.ValidationText);

        model.ModSecurityFirstRuleId = 10_000.5m;
        Assert.Equal(AppText.FixModSecurityLocation, model.ValidationText);

        model.ModSecurityLocation = "";
        Assert.Equal(AppText.WholeNumberRequired, model.ValidationText);

        model.AbuseIpDbKeepDays = 30;
        Assert.Equal(AppText.WholeNumberRequired, model.ValidationText);

        model.ModSecurityFirstRuleId = 10_000;
        Assert.Equal("", model.ValidationText);
    }

    [Theory]
    [InlineData("http://example.com/lists", ModSecurityRuleFailure.NotHttps)]
    [InlineData("/etc/modsecurity/../lists", ModSecurityRuleFailure.LocationInvalid)]
    [InlineData(SasBase + "sp=rw&sr=c&se=2030-01-01&sig=abc", ModSecurityRuleFailure.SasCanWrite)]
    [InlineData(SasBase + "si=policy&sr=c&sig=abc", ModSecurityRuleFailure.SasRightsUnknown)]
    [InlineData(SasBase + "sp=r&ss=b&srt=co&se=2030-01-01&sig=abc", ModSecurityRuleFailure.SasScopeTooWide)]
    public void EveryRefusedLocationAsksForItToBeCorrected(string location, ModSecurityRuleFailure failure)
    {
        Assert.Equal(failure, ModSecurityRules.CheckLocation(location));
        var model = new SettingsFormModel(ModSecurityTicked, DefaultFolder);

        model.ModSecurityLocation = " " + location + " ";

        Assert.False(model.IsValid);
        Assert.Equal(AppText.FixModSecurityLocation, model.ValidationText);
        Assert.True(model.HasChanges);
        Assert.Equal("", model.ToSettings().ModSecurityLocation);
    }

    [Fact]
    public void ASavedLocationThisBuildRefusesBlocksSaveButIsNoChange()
    {
        var saved = ModSecurityTicked with { ModSecurityLocation = "http://example.com/lists" };
        var model = new SettingsFormModel(saved, DefaultFolder);

        Assert.False(model.IsValid);
        Assert.Equal(AppText.FixModSecurityLocation, model.ValidationText);
        Assert.False(model.HasChanges);

        model.ModSecurityLocation = "/etc/modsecurity/../lists";
        Assert.True(model.HasChanges);

        model.ModSecurityLocation = "http://example.com/lists";
        Assert.False(model.HasChanges);
    }

    [Fact]
    public void BesideARefusedSavedLocationAnyOtherProblemIsAChange()
    {
        var saved = ModSecurityTicked with { ModSecurityLocation = "http://example.com/lists" };
        var model = new SettingsFormModel(saved, DefaultFolder);

        model.ModSecurityFirstRuleId = 10_000.5m;
        Assert.True(model.HasChanges);

        model.ModSecurityFirstRuleId = null;
        Assert.True(model.HasChanges);

        model.ModSecurityFirstRuleId = 10_000;
        Assert.False(model.HasChanges);

        model.WriteModSecurityLists = false;
        Assert.True(model.IsValid);
        Assert.True(model.HasChanges);
    }

    [Fact]
    public void ARefusedSavedLocationIsComparedTrimmed()
    {
        var saved = ModSecurityTicked with { ModSecurityLocation = "  http://example.com/lists  " };
        var model = new SettingsFormModel(saved, DefaultFolder);

        model.ModSecurityLocation = "http://example.com/lists\n";
        Assert.False(model.HasChanges);

        model.WriteModSecurityLists = false;
        Assert.Equal("http://example.com/lists", model.ToSettings().ModSecurityLocation);

        model.WriteModSecurityLists = true;
        Assert.False(model.HasChanges);
    }

    [Fact]
    public void ANullSavedLocationReadsAsEmpty()
    {
        var model = new SettingsFormModel(ModSecurityTicked with { ModSecurityLocation = null! }, DefaultFolder);

        Assert.Equal("", model.ValidationText);
        Assert.False(model.HasChanges);
        Assert.Equal("", model.ToSettings().ModSecurityLocation);
    }

    [Fact]
    public void UntickingEitherFeedTypeIsAChange()
    {
        var saved = new UserSettings() with { WriteFortigateFeeds = true, WriteModSecurityLists = true };

        var fortigate = new SettingsFormModel(saved, DefaultFolder) { WriteFortigateFeeds = false };
        var modSecurity = new SettingsFormModel(saved, DefaultFolder) { WriteModSecurityLists = false };

        Assert.True(fortigate.HasChanges);
        Assert.True(modSecurity.HasChanges);
    }

    public static TheoryData<string> FeedTypeEdits => ["Fortigate", "ModSecurity", "Deny", "Close", "FirstRuleId", "Location"];

    [Theory]
    [MemberData(nameof(FeedTypeEdits))]
    public void EachFeedTypeEditRaisesHasChanges(string edit)
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        switch (edit)
        {
            case "Fortigate": model.WriteFortigateFeeds = !model.WriteFortigateFeeds; break;
            case "ModSecurity": model.WriteModSecurityLists = !model.WriteModSecurityLists; break;
            case "Deny": model.IsDeny = true; break;
            case "Close": model.IsClose = true; break;
            case "FirstRuleId": model.ModSecurityFirstRuleId = 20_000; break;
            default: model.ModSecurityLocation = "/etc/modsecurity/lists"; break;
        }

        Assert.Contains(nameof(SettingsFormModel.HasChanges), raised);
        Assert.True(model.HasChanges);
    }

    [Fact]
    public void CloseIsPickedOnlyByTrueAndRoundTrips()
    {
        var saved = new UserSettings { ModSecurityAction = ModSecurityAction.Close };
        var model = new SettingsFormModel(saved, DefaultFolder);
        Assert.True(model.IsClose);
        Assert.False(model.IsDeny);
        Assert.False(model.IsLogOnly);
        Assert.Equal(saved, model.ToSettings());
        Assert.False(model.HasChanges);

        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        model.IsClose = false;
        Assert.Empty(raised);
        Assert.True(model.IsClose);

        model.IsDeny = true;
        Assert.False(model.IsClose);
        Assert.Contains(nameof(SettingsFormModel.IsClose), raised);
        Assert.True(model.HasChanges);
        Assert.Equal(ModSecurityAction.Deny, model.ToSettings().ModSecurityAction);

        raised.Clear();
        model.IsClose = true;
        Assert.True(model.IsClose);
        Assert.False(model.IsDeny);
        Assert.Contains(nameof(SettingsFormModel.IsDeny), raised);
        Assert.Contains(nameof(SettingsFormModel.IsLogOnly), raised);
        Assert.False(model.HasChanges);
        Assert.Equal(ModSecurityAction.Close, model.ToSettings().ModSecurityAction);
    }

    [Fact]
    public void UntickingTheChosenActionChangesNothing()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.IsLogOnly = false;

        Assert.Empty(raised);
        Assert.True(model.IsLogOnly);
        Assert.False(model.IsDeny);
        Assert.False(model.IsClose);
        Assert.False(model.HasChanges);
    }

    [Fact]
    public void TheModSecurityTickRaisesCanEditModSecurity()
    {
        var model = new SettingsFormModel(new UserSettings(), DefaultFolder);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.WriteModSecurityLists = true;

        Assert.Contains(nameof(SettingsFormModel.CanEditModSecurity), raised);
        Assert.True(model.CanEditModSecurity);
    }

    // ----- the location note -----

    private static readonly DateTimeOffset NoteNow = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private static string SasExpiring(DateTimeOffset expiry) =>
        SasBase + "sv=2026-01-01&sr=c&sp=rl&se=" + expiry.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ",
            System.Globalization.CultureInfo.InvariantCulture) + "&sig=SAMPLEONLY%2Bnot%3Dreal";

    private static SettingsFormModel NoteModel(string location, bool ticked, FakeTimeProvider time) =>
        new(new UserSettings { WriteModSecurityLists = ticked, ModSecurityLocation = location }, DefaultFolder, time);

    [Theory]
    [InlineData("")]
    [InlineData("/etc/modsecurity/lists")]
    [InlineData(SasBase + "sv=2026-01-01&sr=c&sp=rl")]
    public void ALocationWithNoSasExpiryGivesNoNote(string location)
    {
        var model = NoteModel(location, ticked: true, new FakeTimeProvider(NoteNow));

        Assert.Equal("", model.LocationNote.Text);
        Assert.False(model.LocationNote.HasNote);
        Assert.False(model.LocationNote.IsError);
        Assert.False(model.LocationNote.IsCaution);
    }

    [Theory]
    [InlineData(30, "Later", false, false)]
    [InlineData(10, "Soon", false, true)]
    [InlineData(-1, "Passed", true, false)]
    public void TheNoteShowsTheExpiryInItsToneWhileModSecurityIsTicked(int days, string nearness, bool error, bool caution)
    {
        DateTimeOffset expiry = NoteNow.AddDays(days);
        var model = NoteModel(SasExpiring(expiry), ticked: true, new FakeTimeProvider(NoteNow));

        Assert.Equal(Enum.Parse<SasExpiryNearness>(nearness), AppText.ModSecuritySasExpiryNearness(expiry, NoteNow));
        Assert.Equal(AppText.ModSecuritySasExpiry(expiry, true, NoteNow), model.LocationNote.Text);
        Assert.True(model.LocationNote.HasNote);
        Assert.Equal(error, model.LocationNote.IsError);
        Assert.Equal(caution, model.LocationNote.IsCaution);
    }

    [Fact]
    public void AnHttpLocationGivesTheNotHttpsLineAsAnError()
    {
        var model = NoteModel("http://h.example/lists", ticked: true, new FakeTimeProvider(NoteNow));

        Assert.Equal(AppText.ModSecurityLocationProblem(ModSecurityRuleFailure.NotHttps), model.LocationNote.Text);
        Assert.True(model.LocationNote.IsError);
        Assert.False(model.LocationNote.IsCaution);
    }

    [Fact]
    public void WhileModSecurityIsUntickedTheNoteKeepsItsTextWithNoTone()
    {
        DateTimeOffset expiry = NoteNow.AddDays(10);
        var model = NoteModel(SasExpiring(expiry), ticked: false, new FakeTimeProvider(NoteNow));

        Assert.Equal(AppText.ModSecuritySasExpiry(expiry, true, NoteNow), model.LocationNote.Text);
        Assert.False(model.LocationNote.IsError);
        Assert.False(model.LocationNote.IsCaution);

        var refused = NoteModel("http://h.example/lists", ticked: false, new FakeTimeProvider(NoteNow));
        Assert.Equal(AppText.ModSecurityLocationProblem(ModSecurityRuleFailure.NotHttps), refused.LocationNote.Text);
        Assert.False(refused.LocationNote.IsError);

        // Ticking it gives the tone back
        model.WriteModSecurityLists = true;
        Assert.True(model.LocationNote.IsCaution);
    }

    [Fact]
    public void ALinkTypedWithATrailingLineBreakStillShowsItsExpiry()
    {
        DateTimeOffset expiry = NoteNow.AddDays(30);
        var model = NoteModel("", ticked: true, new FakeTimeProvider(NoteNow));

        model.ModSecurityLocation = SasExpiring(expiry) + "\n";

        Assert.Equal(AppText.ModSecuritySasExpiry(expiry, true, NoteNow), model.LocationNote.Text);
        Assert.False(model.LocationNote.IsError);
    }

    [Fact]
    public void ALinkWithASignatureButNoExpiryGivesTheExpiryUnknownLineAsAnError()
    {
        const string location = SasBase + "sv=2026-01-01&sr=c&sp=rl&sig=SAMPLEONLY%2Bnot%3Dreal";
        Assert.Equal(ModSecurityRuleFailure.SasExpiryUnknown, ModSecurityRules.CheckLocation(location));

        var model = NoteModel(location, ticked: true, new FakeTimeProvider(NoteNow));

        Assert.Equal(AppText.ModSecurityLocationProblem(ModSecurityRuleFailure.SasExpiryUnknown), model.LocationNote.Text);
        Assert.True(model.LocationNote.IsError);
        // Saved by a build before the expiry rule: it blocks Save, but loading it is no change
        Assert.False(model.IsValid);
        Assert.Equal(AppText.FixModSecurityLocation, model.ValidationText);
        Assert.False(model.HasChanges);
    }

    // A refresh raises nothing on the form, because the page reads every form change as an edit
    [Fact]
    public void RefreshExpiryRaisesTheNoteOnlyWhenItChangedAndNeverTheForm()
    {
        var time = new FakeTimeProvider(NoteNow);
        var model = NoteModel(SasExpiring(NoteNow.AddDays(15)), ticked: true, time);
        var formRaised = new List<string?>();
        var noteRaised = new List<string?>();
        model.PropertyChanged += (_, e) => formRaised.Add(e.PropertyName);
        model.LocationNote.PropertyChanged += (_, e) => noteRaised.Add(e.PropertyName);

        model.RefreshExpiry();
        Assert.Empty(noteRaised);
        Assert.False(model.LocationNote.IsCaution);

        time.Advance(TimeSpan.FromDays(2));
        model.RefreshExpiry();

        Assert.Contains(nameof(LocationNote.Text), noteRaised);
        Assert.Contains(nameof(LocationNote.IsCaution), noteRaised);
        Assert.DoesNotContain(nameof(LocationNote.IsError), noteRaised);
        Assert.DoesNotContain(nameof(LocationNote.HasNote), noteRaised);
        Assert.True(model.LocationNote.IsCaution);

        noteRaised.Clear();
        model.RefreshExpiry();
        Assert.Empty(noteRaised);
        Assert.Empty(formRaised);
    }

    [Fact]
    public void EditingTheLocationRaisesTheNote()
    {
        var model = NoteModel("", ticked: true, new FakeTimeProvider(NoteNow));
        var raised = new List<string?>();
        model.LocationNote.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.ModSecurityLocation = "http://h.example/lists";

        Assert.Contains(nameof(LocationNote.Text), raised);
        Assert.Contains(nameof(LocationNote.HasNote), raised);
        Assert.Contains(nameof(LocationNote.IsError), raised);
    }

    [Fact]
    public void TickingAndUntickingModSecurityRaisesTheNotesTone()
    {
        var model = NoteModel(SasExpiring(NoteNow.AddDays(10)), ticked: false, new FakeTimeProvider(NoteNow));
        var raised = new List<string?>();
        model.LocationNote.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.WriteModSecurityLists = true;
        Assert.Equal([nameof(LocationNote.IsCaution)], raised);
        Assert.True(model.LocationNote.IsCaution);

        raised.Clear();
        model.ModSecurityLocation = "http://h.example/lists";
        raised.Clear();
        model.WriteModSecurityLists = false;
        Assert.Equal([nameof(LocationNote.IsError)], raised);
        Assert.False(model.LocationNote.IsError);
    }

    [Fact]
    public void ADateOnlyExpiryReadsAsTheStartOfThatDay()
    {
        const string location = SasBase + "sv=2026-01-01&sr=c&sp=rl&se=2026-11-30&sig=SAMPLEONLY%2Bnot%3Dreal";
        var expiry = new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.Zero);

        var model = NoteModel(location, ticked: true, new FakeTimeProvider(NoteNow));

        Assert.Equal(AppText.ModSecuritySasExpiry(expiry, false, NoteNow), model.LocationNote.Text);
        Assert.Contains("at the start of 30 November 2026 (UTC)", model.LocationNote.Text);
    }

    // A user delegation SAS stops at its key's expiry when that comes first, whatever its se says
    [Fact]
    public void AUserDelegationKeyExpiringFirstGivesTheNoteItsDateAndTone()
    {
        const string location = SasBase + "sv=2026-01-01&sr=c&sp=rl&skoid=o&ske=2026-10-30T12:00:00Z"
                                + "&se=2027-10-23T00:00:00Z&sig=SAMPLEONLY%2Bnot%3Dreal";
        var keyExpiry = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        var model = NoteModel(location, ticked: true, new FakeTimeProvider(NoteNow));

        Assert.Equal(AppText.ModSecuritySasExpiry(keyExpiry, true, NoteNow), model.LocationNote.Text);
        Assert.True(model.LocationNote.IsCaution);
        Assert.False(model.LocationNote.IsError);
        Assert.True(model.IsValid);
    }

    [Fact]
    public void LoadWorksTheNoteOutAgainFromTheSavedLocation()
    {
        DateTimeOffset expiry = NoteNow.AddDays(30);
        var saved = new UserSettings { WriteModSecurityLists = true, ModSecurityLocation = SasExpiring(expiry) };
        var model = new SettingsFormModel(saved, DefaultFolder, new FakeTimeProvider(NoteNow));
        model.ModSecurityLocation = "http://h.example/lists";
        Assert.True(model.LocationNote.IsError);

        model.Load(saved);

        Assert.Equal(AppText.ModSecuritySasExpiry(expiry, true, NoteNow), model.LocationNote.Text);
        Assert.False(model.LocationNote.IsError);
    }
}
