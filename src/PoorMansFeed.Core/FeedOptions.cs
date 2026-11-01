namespace PoorMansFeed.Core;

/// <summary>IPv6 is written only while IPv6Enabled (Settings' IPv6 switch) and IncludeIPv6 (the feed's own) are both on.</summary>
internal sealed record FeedOptions(IReadOnlyList<FeedType> Types, bool IncludeIPv4, bool IncludeIPv6,
                                   bool SplitByFamily, bool IPv6Enabled)
{
    public bool WritesIPv6 => IPv6Enabled && IncludeIPv6;

    // FortigateEnabled off gives no type; the ASN export's own family and split switches (still named Fortigate*)
    public static FeedOptions ForAsnExport(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(settings.FortigateEnabled ? FeedTypes.Ticked(settings) : [], settings.FortigateIncludeIPv4,
            settings.FortigateIncludeIPv6, settings.FortigateSplitByFamily, settings.IPv6Enabled);
    }

    // The cloud feeds and the rolling feed write one file per family and have no family switch of their own
    public static FeedOptions ForCloudFeed(UserSettings settings) =>
        new(FeedTypes.Ticked(settings), true, true, true, settings.IPv6Enabled);

    public static FeedOptions ForRollingFeed(UserSettings settings) =>
        new(FeedTypes.Ticked(settings), true, true, true, settings.IPv6Enabled);
}
