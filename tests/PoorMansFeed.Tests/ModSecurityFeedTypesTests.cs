using System.Reflection;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class ModSecurityFeedTypesTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Ticked_is_empty_exactly_when_no_feed_type_is_ticked(bool fortigate, bool modSecurity)
    {
        var settings = new UserSettings { WriteFortigateFeeds = fortigate, WriteModSecurityLists = modSecurity };

        Assert.Equal(!settings.TicksAnyFeedType(), FeedTypes.Ticked(settings).Count == 0);
    }

    [Fact]
    public void Ticked_lists_the_ticked_types_FortiGate_first()
    {
        Assert.Equal(new[] { FeedType.Fortigate, FeedType.ModSecurity },
            FeedTypes.Ticked(new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = true }));
        Assert.Equal(new[] { FeedType.Fortigate }, FeedTypes.Ticked(new UserSettings { WriteFortigateFeeds = true }));
        Assert.Equal(new[] { FeedType.ModSecurity }, FeedTypes.Ticked(new UserSettings { WriteModSecurityLists = true }));
        Assert.Empty(FeedTypes.Ticked(new UserSettings()));
    }

    // A feed type added to the settings but not to FeedTypes, or the reverse, would let a write pass the type check and
    // write nothing, so every Write* switch must tick exactly one type of its own
    [Fact]
    public void Every_Write_switch_alone_counts_as_ticked_and_ticks_one_type_of_its_own()
    {
        PropertyInfo[] switches = typeof(UserSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(bool) && property.Name.StartsWith("Write", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(FeedTypes.All.Count, switches.Length);

        var seen = new HashSet<FeedType>();
        foreach (PropertyInfo property in switches)
        {
            var settings = new UserSettings();
            foreach (PropertyInfo other in switches)
                other.SetValue(settings, other == property);

            Assert.True(settings.TicksAnyFeedType(), property.Name);
            FeedType type = Assert.Single(FeedTypes.Ticked(settings));
            Assert.True(seen.Add(type), property.Name);
        }
    }

    [Fact]
    public void Each_type_has_its_own_folder_and_first_line()
    {
        Assert.Equal("fortigate", FeedTypes.FolderName(FeedType.Fortigate));
        Assert.Equal("modsecurity", FeedTypes.FolderName(FeedType.ModSecurity));
        Assert.Equal("# PoorMansFeed - Fortigate Threat Feed", FeedTypes.FirstLine(FeedType.Fortigate));
        Assert.Equal("# PoorMansFeed - ModSecurity IP list", FeedTypes.FirstLine(FeedType.ModSecurity));

        Assert.Equal(FeedTypes.All.Count, FeedTypes.All.Select(FeedTypes.FolderName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(FeedTypes.All.Count, FeedTypes.All.Select(FeedTypes.FirstLine).Distinct(StringComparer.Ordinal).Count());
        Assert.All(FeedTypes.All, type =>
        {
            string line = FeedTypes.FirstLine(type);
            Assert.StartsWith("# ", line);
            Assert.DoesNotContain(line, c => char.IsControl(c));
        });
    }
}
