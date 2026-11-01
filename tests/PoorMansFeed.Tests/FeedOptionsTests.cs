using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class FeedOptionsTests
{
    private static UserSettings Settings(bool fortigateFeeds, bool modSecurityLists) =>
        new() { WriteFortigateFeeds = fortigateFeeds, WriteModSecurityLists = modSecurityLists };

    [Fact]
    public void The_asn_export_takes_the_ticked_types_in_write_order()
    {
        Assert.Equal([FeedType.Fortigate, FeedType.ModSecurity], FeedOptions.ForAsnExport(Settings(true, true)).Types);
        Assert.Equal([FeedType.Fortigate], FeedOptions.ForAsnExport(Settings(true, false)).Types);
        Assert.Equal([FeedType.ModSecurity], FeedOptions.ForAsnExport(Settings(false, true)).Types);
        Assert.Empty(FeedOptions.ForAsnExport(Settings(false, false)).Types);
    }

    [Fact]
    public void The_asn_export_with_fortigate_off_has_no_type()
    {
        UserSettings settings = Settings(true, true) with { FortigateEnabled = false };

        Assert.Empty(FeedOptions.ForAsnExport(settings).Types);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    public void The_asn_export_takes_its_own_family_and_split_switches(
        bool includeIPv4, bool includeIPv6, bool split, bool ipv6Enabled)
    {
        UserSettings settings = Settings(true, false) with
        {
            FortigateIncludeIPv4 = includeIPv4,
            FortigateIncludeIPv6 = includeIPv6,
            FortigateSplitByFamily = split,
            IPv6Enabled = ipv6Enabled,
        };

        FeedOptions options = FeedOptions.ForAsnExport(settings);

        Assert.Equal(includeIPv4, options.IncludeIPv4);
        Assert.Equal(includeIPv6, options.IncludeIPv6);
        Assert.Equal(split, options.SplitByFamily);
        Assert.Equal(ipv6Enabled, options.IPv6Enabled);
        Assert.Equal(includeIPv6 && ipv6Enabled, options.WritesIPv6);
    }

    [Fact]
    public void The_asn_export_keeps_ipv6_enabled_apart_from_its_own_ipv6_switch()
    {
        UserSettings settings = Settings(true, false) with { IPv6Enabled = true, FortigateIncludeIPv6 = false };

        FeedOptions options = FeedOptions.ForAsnExport(settings);

        Assert.True(options.IPv6Enabled);
        Assert.False(options.IncludeIPv6);
        Assert.False(options.WritesIPv6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_cloud_and_rolling_feeds_write_both_families_split_and_ignore_the_fortigate_switches(bool ipv6Enabled)
    {
        UserSettings settings = Settings(true, true) with
        {
            FortigateEnabled = false,
            FortigateIncludeIPv4 = false,
            FortigateIncludeIPv6 = false,
            FortigateSplitByFamily = false,
            IPv6Enabled = ipv6Enabled,
        };

        foreach (FeedOptions options in new[] { FeedOptions.ForCloudFeed(settings), FeedOptions.ForRollingFeed(settings) })
        {
            Assert.Equal([FeedType.Fortigate, FeedType.ModSecurity], options.Types);
            Assert.True(options.IncludeIPv4);
            Assert.True(options.IncludeIPv6);
            Assert.True(options.SplitByFamily);
            Assert.Equal(ipv6Enabled, options.IPv6Enabled);
            Assert.Equal(ipv6Enabled, options.WritesIPv6);
        }
    }

    [Fact]
    public void The_cloud_and_rolling_feeds_take_the_ticked_types()
    {
        Assert.Equal([FeedType.ModSecurity], FeedOptions.ForCloudFeed(Settings(false, true)).Types);
        Assert.Equal([FeedType.Fortigate], FeedOptions.ForRollingFeed(Settings(true, false)).Types);
        Assert.Empty(FeedOptions.ForCloudFeed(Settings(false, false)).Types);
        Assert.Empty(FeedOptions.ForRollingFeed(Settings(false, false)).Types);
    }
}
