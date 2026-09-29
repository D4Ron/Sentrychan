using Sentrychan.Core;

namespace Sentrychan.Tests;

public class ShowWindowSignalTests
{
    [Fact]
    public void Names_differ_per_flavour_and_windows_keeps_the_old_event_names()
    {
        Assert.Equal(@"Global\Sentrychan_ShowWindow", ShowWindowSignal.EventName(isPreview: false));
        Assert.Equal(@"Global\SentrychanPreview_ShowWindow", ShowWindowSignal.EventName(isPreview: true));
        Assert.NotEqual(ShowWindowSignal.PipeName(false), ShowWindowSignal.PipeName(true));
        Assert.DoesNotContain('/', ShowWindowSignal.PipeName(false));
    }

    [Fact]
    public async Task A_second_launch_reaches_the_running_one_over_the_pipe()
    {
        if (OperatingSystem.IsWindows()) return; // the event path is Windows-only and covered there
        var name = "SentrychanTest_" + Guid.NewGuid().ToString("N");
        var got = new SemaphoreSlim(0);
        using (ShowWindowSignal.ListenPipe(name, () => got.Release()))
        {
            await Task.Delay(100);
            // Several launches back to back — none may fall between two listener instances.
            for (var i = 0; i < 5; i++) Assert.True(ShowWindowSignal.SendPipe(name));
            for (var i = 0; i < 5; i++) Assert.True(await got.WaitAsync(TimeSpan.FromSeconds(5)), $"signal {i + 1} was lost");
        }
    }

    [Fact]
    public void Nobody_listening_is_a_quiet_false()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.False(ShowWindowSignal.SendPipe("SentrychanTest_nobody_" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task A_socket_file_left_by_a_crash_doesnt_stop_the_next_run_listening()
    {
        if (OperatingSystem.IsWindows()) return;
        var name = "SentrychanTest_" + Guid.NewGuid().ToString("N");
        var stale = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name);
        // What a killed process leaves behind: the socket's file, with nobody bound to it. (A
        // socket closed by .NET removes its file, so leave one the way a crash would: raw.)
        File.WriteAllBytes(stale, []);
        Assert.True(File.Exists(stale));

        var got = new SemaphoreSlim(0);
        using (ShowWindowSignal.ListenPipe(name, () => got.Release()))
        {
            await Task.Delay(200);
            var sent = false;
            for (var i = 0; i < 10 && !(sent = ShowWindowSignal.SendPipe(name)); i++) await Task.Delay(200);
            Assert.True(sent);
            Assert.True(await got.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        try { File.Delete(stale); } catch { }
    }
}
