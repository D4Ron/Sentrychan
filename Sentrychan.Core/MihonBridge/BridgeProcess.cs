using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Sentrychan.Core.MihonBridge;

/// <summary>Everything needed to start the server once.</summary>
/// <param name="WebView">
/// Lets the server set up its embedded browser, which some sources need to pass a web check.
/// It downloads a browser runtime the first time, so it's off unless the user turns it on.
/// </param>
public sealed record BridgeLaunch(string JavaPath, string JarPath, string DataDir, int Port,
    string? PreloadLibrary = null, bool WebView = false);

/// <summary>A started server process.</summary>
public interface IBridgeProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }

    /// <summary>The last lines the server wrote — shown when it fails to come up.</summary>
    string RecentOutput { get; }

    /// <summary>Stops the server and everything it started.</summary>
    void Kill();
}

/// <summary>
/// Starts the server. Behind an interface so the service's lifecycle (start on demand, health
/// check, stop with the app) is testable without a Java runtime.
/// </summary>
public interface IBridgeProcessLauncher
{
    IBridgeProcess Start(BridgeLaunch launch);

    /// <summary>Stops a server a previous run left behind (after a crash). No-op when it's gone.</summary>
    void KillOrphan(int processId);
}

public sealed class JavaBridgeProcessLauncher : IBridgeProcessLauncher
{
    private const string Prefix = "-Dsuwayomi.tachidesk.config.server.";

    /// <summary>
    /// The JVM arguments. Server settings are overridable with -D properties (read from the
    /// server's config module), which beat anything in its own config file — so whatever the
    /// server's saved settings say, it listens on loopback only, opens no browser or tray icon,
    /// doesn't fetch its web UI, and makes no backups of its own.
    /// </summary>
    public static IReadOnlyList<string> Arguments(BridgeLaunch l) =>
    [
        Prefix + "rootDir=" + l.DataDir,
        Prefix + "ip=127.0.0.1",
        Prefix + "port=" + l.Port,
        Prefix + "webUIEnabled=false",
        Prefix + "initialOpenInBrowserEnabled=false",
        Prefix + "systemTrayEnabled=false",
        Prefix + "backupInterval=0",
        Prefix + "kcefEnabled=" + (l.WebView ? "true" : "false"),
        "-Djava.awt.headless=true",
        "-jar", l.JarPath,
    ];

    public IBridgeProcess Start(BridgeLaunch launch)
    {
        var psi = new ProcessStartInfo(launch.JavaPath)
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(launch.JarPath))!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in Arguments(launch)) psi.ArgumentList.Add(a);
        if (launch.PreloadLibrary != null) psi.Environment["LD_PRELOAD"] = launch.PreloadLibrary;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("The server process didn't start.");
        return new JavaBridgeProcess(process);
    }

    public void KillOrphan(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            // Only ever a Java process: after a reboot the id may belong to something else.
            if (!p.ProcessName.StartsWith("java", StringComparison.OrdinalIgnoreCase)) return;
            p.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
    }

    private sealed class JavaBridgeProcess : IBridgeProcess
    {
        private readonly Process _process;
        private readonly Queue<string> _tail = new();
        private readonly WindowsJob? _job;

        public JavaBridgeProcess(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, e) => Keep(e.Data);
            _process.ErrorDataReceived += (_, e) => Keep(e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            // Windows: tie the server to a job object that dies with this process, so a crash
            // (or a kill from Task Manager) never leaves it running. Other systems rely on the
            // shutdown path and the orphan check at the next start.
            if (OperatingSystem.IsWindows()) _job = WindowsJob.TryCreate(process);
        }

        public int Id => _process.Id;
        public bool HasExited => _process.HasExited;

        public string RecentOutput
        {
            get { lock (_tail) return string.Join(Environment.NewLine, _tail); }
        }

        private void Keep(string? line)
        {
            if (line == null) return;
            lock (_tail)
            {
                _tail.Enqueue(line);
                while (_tail.Count > 40) _tail.Dequeue();
            }
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            catch { /* exited on its own meanwhile */ }
        }

        public void Dispose()
        {
            Kill();
            _job?.Dispose();
            _process.Dispose();
        }
    }
}

/// <summary>Picks a free loopback port for the server.</summary>
public static class BridgePorts
{
    /// <summary>
    /// <paramref name="preferred"/> when it's free (so the server keeps its address between
    /// runs), else any free port the OS hands out.
    /// </summary>
    public static int Pick(int preferred = 0)
    {
        if (preferred is > 0 and < 65536 && IsFree(preferred)) return preferred;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public static bool IsFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }
}

/// <summary>
/// A Windows job object set to kill its processes when its last handle closes — that is, when
/// Sentrychan exits for any reason. Needs checking on Windows.
/// </summary>
internal sealed class WindowsJob : IDisposable
{
    private IntPtr _handle;

    private WindowsJob(IntPtr handle) => _handle = handle;

    public static WindowsJob? TryCreate(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return null;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)size)
                || !AssignProcessToJobObject(handle, process.Handle))
            {
                CloseHandle(handle);
                return null;
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return new WindowsJob(handle);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
