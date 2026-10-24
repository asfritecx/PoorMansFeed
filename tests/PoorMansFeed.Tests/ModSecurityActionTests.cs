using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>The settings JSON stores the action by number, so none moves and a new action takes the next.</summary>
public sealed class ModSecurityActionTests
{
    [Fact]
    public void Log_only_is_0_deny_is_1_and_close_is_2()
    {
        Assert.Equal(0, (int)ModSecurityAction.LogOnly);
        Assert.Equal(1, (int)ModSecurityAction.Deny);
        Assert.Equal(2, (int)ModSecurityAction.Close);
    }

    [Fact]
    public void The_actions_are_numbered_from_0_with_no_gap_or_repeat()
    {
        int[] numbers = Enum.GetValues<ModSecurityAction>().Select(action => (int)action).ToArray();

        Assert.Equal(Enumerable.Range(0, numbers.Length), numbers);
    }
}
