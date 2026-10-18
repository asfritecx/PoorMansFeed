using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class InstanceLockTests
{
    [Fact]
    public void Second_TryAcquire_returns_null_while_first_is_held_and_succeeds_after_dispose()
    {
        using var temp = new TempFolder();
        string path = temp.File("ipinfo.lock");

        InstanceLock? first = InstanceLock.TryAcquire(path);
        Assert.NotNull(first);

        Assert.Null(InstanceLock.TryAcquire(path));

        first.Dispose();

        InstanceLock? third = InstanceLock.TryAcquire(path);
        Assert.NotNull(third);
        third.Dispose();
    }
}
