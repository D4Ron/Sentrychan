using System;
using System.Runtime;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Sentrychan.UI.Services;

/// <summary>
/// Gives memory back while the main window is out of sight. The app spends most of its life in
/// the tray, monitoring, yet kept every decoded cover and the heap it grew while browsing.
/// A short delay keeps a quick hide-and-show from throwing away images that are about to be
/// drawn again.
/// </summary>
public static class BackgroundTrim
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(20);
    private static DispatcherTimer? _timer;

    public static void WindowHidden()
    {
        _timer ??= new DispatcherTimer(Delay, DispatcherPriority.Background, (_, _) => { _timer!.Stop(); Trim(); });
        _timer.Stop();
        _timer.Start();
    }

    public static void WindowShown() => _timer?.Stop();

    private static void Trim()
    {
        // Covers still on a page keep their own bitmap; this only drops the cache's hold on them.
        Controls.AsyncImage.ClearMemoryCache();

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        // Aggressive collection returns freed segments to the OS; trimming the working set then
        // moves the pages nothing is touching out of RAM. They page back in if the window returns.
        if (OperatingSystem.IsWindows())
        {
            try { SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1); } catch { /* best effort */ }
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
