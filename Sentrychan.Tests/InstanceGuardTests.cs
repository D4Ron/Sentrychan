using Sentrychan.Core;

namespace Sentrychan.Tests;

public class InstanceGuardTests
{
    // Local\ keeps test mutexes per-session on Windows; the unique suffix keeps runs apart.
    private static string UniqueName() => @"Local\SentrychanTest_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void A_mutex_nobody_created_is_not_held()
    {
        Assert.False(InstanceGuard.IsHeldElsewhere(UniqueName()));
    }

    [Fact]
    public void A_mutex_owned_by_another_thread_is_held()
    {
        var name = UniqueName();
        using var owned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        // Mutexes are owned per thread, and a thread may re-enter its own, so the owner has to be
        // a different thread to look like another process.
        var owner = new Thread(() =>
        {
            using var m = new Mutex(initiallyOwned: true, name, out _);
            owned.Set();
            release.Wait();
            m.ReleaseMutex();
        });
        owner.Start();
        owned.Wait();
        try
        {
            Assert.True(InstanceGuard.IsHeldElsewhere(name));
        }
        finally
        {
            release.Set();
            owner.Join();
        }
    }

    [Fact]
    public void A_mutex_that_exists_but_is_released_is_not_held()
    {
        var name = UniqueName();
        using var m = new Mutex(initiallyOwned: false, name, out _);
        Assert.False(InstanceGuard.IsHeldElsewhere(name));
        // Probing must leave it free for the real owner.
        Assert.True(m.WaitOne(0));
        m.ReleaseMutex();
    }

    [Fact]
    public void An_abandoned_mutex_is_not_held()
    {
        var name = UniqueName();
        using var keepAlive = new Mutex(initiallyOwned: false, name, out _);
        var owner = new Thread(() =>
        {
            using var m = Mutex.OpenExisting(name);
            m.WaitOne(); // then exit without releasing
        });
        owner.Start();
        owner.Join();

        Assert.False(InstanceGuard.IsHeldElsewhere(name));
    }

    [Fact]
    public void Each_flavour_watches_the_other()
    {
        Assert.NotEqual(InstanceGuard.OwnInstanceMutex, InstanceGuard.OtherInstanceMutex);
        Assert.Contains(InstanceGuard.StableInstanceMutex,
            new[] { InstanceGuard.OwnInstanceMutex, InstanceGuard.OtherInstanceMutex });
        // The name every stable build before the preview existed used — must not change.
        Assert.Equal(@"Global\Sentrychan_SingleInstance", InstanceGuard.StableInstanceMutex);
    }
}
