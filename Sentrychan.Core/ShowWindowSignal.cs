using System.IO.Pipes;

namespace Sentrychan.Core;

/// <summary>
/// Lets a second launch ask the running app to bring its window forward, then exit.
///
/// <para>Windows uses a named event, under the names older builds used, so an updated app and a
/// not-yet-updated one still reach each other. Named events don't exist on macOS or Linux, so
/// there the running app listens on a named pipe (a Unix domain socket) and a second launch
/// connects to it. The pipe's name includes the user, since /tmp is shared on Linux.</para>
/// </summary>
public static class ShowWindowSignal
{
    public static string EventName(bool isPreview) =>
        isPreview ? @"Global\SentrychanPreview_ShowWindow" : @"Global\Sentrychan_ShowWindow";

    public static string PipeName(bool isPreview) =>
        (isPreview ? "SentrychanPreview_ShowWindow_" : "Sentrychan_ShowWindow_") + SafeUser();

    private static string SafeUser() =>
        new(Environment.UserName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    /// <summary>Asks the running instance to show itself. False when none answered (it may be mid-startup).</summary>
    public static bool Send(bool isPreview) =>
        OperatingSystem.IsWindows() ? SendEvent(EventName(isPreview)) : SendPipe(PipeName(isPreview));

    /// <summary>
    /// Calls <paramref name="onSignal"/> (on a background thread) each time another launch asks.
    /// Never throws: without a listener a second launch simply finds nobody and exits.
    /// </summary>
    public static IDisposable Listen(bool isPreview, Action onSignal) =>
        OperatingSystem.IsWindows() ? ListenEvent(EventName(isPreview), onSignal) : ListenPipe(PipeName(isPreview), onSignal);

    // ── Windows: named event ────────────────────────────────────────

    private static bool SendEvent(string name)
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(name);
            ev.Set();
            return true;
        }
        catch { return false; }
    }

    private static IDisposable ListenEvent(string name, Action onSignal)
    {
        var stop = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            try
            {
                using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
                var handles = new[] { ev, stop.Token.WaitHandle };
                while (WaitHandle.WaitAny(handles) == 0)
                    try { onSignal(); } catch { /* the app may be shutting down */ }
            }
            catch { /* no listener then */ }
        })
        { IsBackground = true, Name = "ShowWindowListener" };
        thread.Start();
        return new Stopper(stop);
    }

    // ── macOS / Linux: named pipe ───────────────────────────────────

    internal static bool SendPipe(string name)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
            client.Connect(1000);
            client.WriteByte(1);
            client.Flush();
            return true;
        }
        catch { return false; }
    }

    internal static IDisposable ListenPipe(string name, Action onSignal)
    {
        var stop = new CancellationTokenSource();
        NamedPipeServerStream Create() => new(name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        _ = Task.Run(async () =>
        {
            NamedPipeServerStream? server = null;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    server ??= Create();
                    await server.WaitForConnectionAsync(stop.Token);
                    // The next instance first: it keeps the listening socket open, so a launch that
                    // connects while this one is being handled isn't dropped.
                    var connected = server;
                    server = Create();
                    _ = Task.Run(async () =>
                    {
                        await using (connected)
                        {
                            try
                            {
                                var buffer = new byte[1];
                                _ = await connected.ReadAsync(buffer, stop.Token);
                                onSignal();
                            }
                            catch { /* hung up early, or the app is shutting down */ }
                        }
                    });
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // A stale socket file from a crashed run, or a broken instance: start over shortly.
                    try { server?.Dispose(); } catch { }
                    server = null;
                    try { await Task.Delay(500, stop.Token); } catch { break; }
                }
            }
            try { server?.Dispose(); } catch { }
        });
        return new Stopper(stop);
    }

    private sealed class Stopper(CancellationTokenSource stop) : IDisposable
    {
        public void Dispose()
        {
            try { stop.Cancel(); } catch { /* already disposed */ }
        }
    }
}
