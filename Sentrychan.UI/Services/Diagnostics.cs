using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentrychan.Core;
using Sentrychan.Core.Data;
using Sentrychan.Core.Diagnostics;
using Sentrychan.Core.Sources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Sentrychan.UI.Services;

/// <summary>
/// Test builds watch their own windows so a tester's report says what was on screen: every window
/// opened or closed is logged with its size, place and screen, and a screenshot of it is kept
/// (logs/screens). A report bundles all of that with <see cref="DiagnosticReport"/>.
/// </summary>
public static class Diagnostics
{
    private const int KeepScreenshots = 40;
    private static ILogger? _log;

    public static string ScreensFolder => Path.Combine(AppPaths.Logs, "screens");

    /// <summary>Starts watching windows. Test builds only.</summary>
    public static void WatchWindows()
    {
        if (!BuildInfo.IsTestBuild) return;
        _log = App.Services?.GetService<ILoggerFactory>()?.CreateLogger("Windows");
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => OnOpened(w));
        Window.WindowClosedEvent.AddClassHandler<Window>((w, _) =>
            _log?.LogInformation("[Window] closed \"{Title}\"", w.Title));
    }

    private static void OnOpened(Window w)
    {
        _log?.LogInformation("[Window] opened {Window}", Describe(w));
        // Once it has laid out — and again a little later, when the welcome animation and any
        // first-start dialogs have settled.
        DispatcherTimer.RunOnce(() => Snapshot(w, "open"), TimeSpan.FromSeconds(1.5));
        if (w == MainWindow) DispatcherTimer.RunOnce(() => Snapshot(w, "settled"), TimeSpan.FromSeconds(8));
        w.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
                _log?.LogInformation("[Window] \"{Title}\" is now {State}", w.Title, w.WindowState);
        };
        w.Resized += (_, e) => _log?.LogDebug("[Window] \"{Title}\" resized to {W}x{H}", w.Title, (int)e.ClientSize.Width, (int)e.ClientSize.Height);
    }

    private static Window? MainWindow =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;

    private static IEnumerable<Window> OpenWindows =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.Windows.Where(w => w.IsVisible) : [];

    private static string Describe(Window w)
    {
        var screen = w.Screens.ScreenFromWindow(w);
        return $"\"{w.Title}\" {w.GetType().Name} client {w.ClientSize.Width:0}x{w.ClientSize.Height:0} at {w.Position.X},{w.Position.Y} " +
               $"state {w.WindowState} scaling {w.RenderScaling:0.##} owner \"{(w.Owner as Window)?.Title}\" " +
               $"screen {(screen == null ? "?" : $"{screen.Bounds.Width}x{screen.Bounds.Height} work {screen.WorkingArea.Width}x{screen.WorkingArea.Height} @{screen.Scaling:0.##}")}";
    }

    /// <summary>Screens and open windows, for the report.</summary>
    public static string DescribeUi()
    {
        var sb = new StringBuilder();
        var main = MainWindow;
        if (main != null)
            foreach (var s in main.Screens.All)
                sb.AppendLine($"Screen {(s.IsPrimary ? "(primary) " : "")}{s.Bounds.Width}x{s.Bounds.Height} at {s.Bounds.X},{s.Bounds.Y} · work area {s.WorkingArea.Width}x{s.WorkingArea.Height} · scaling {s.Scaling:0.##}");
        foreach (var w in OpenWindows) sb.AppendLine("Window " + Describe(w));
        return sb.ToString();
    }

    /// <summary>Saves a picture of a window. Native video surfaces show black; everything else is as drawn.</summary>
    public static string? Snapshot(Window w, string label)
    {
        try
        {
            if (!w.IsVisible || w.ClientSize.Width < 1) return null;
            Directory.CreateDirectory(ScreensFolder);
            var scale = w.RenderScaling;
            var size = new PixelSize((int)(w.ClientSize.Width * scale), (int)(w.ClientSize.Height * scale));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(w);
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Safe(w.Title)}-{label}.png";
            var path = Path.Combine(ScreensFolder, name);
            bitmap.Save(path);
            foreach (var old in new DirectoryInfo(ScreensFolder).GetFiles("*.png").OrderByDescending(f => f.Name).Skip(KeepScreenshots))
                old.Delete();
            return path;
        }
        catch (Exception ex)
        {
            _log?.LogDebug("[Window] screenshot of \"{Title}\" failed: {Message}", w.Title, ex.Message);
            return null;
        }
    }

    private static string Safe(string? s) =>
        new string((s ?? "window").Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-') is { Length: > 0 } x ? x : "window";

    /// <summary>
    /// Builds the problem report (with a fresh sources check and a picture of every open window),
    /// saves it to the Desktop and returns its path.
    /// </summary>
    public static async Task<string> WriteReportAsync(string? reason)
    {
        var services = App.Services ?? throw new InvalidOperationException("The app isn't ready yet.");
        var db = services.GetRequiredService<IDbContextFactory<AppDbContext>>();

        var shots = OpenWindows.Select(w => Snapshot(w, "report")).OfType<string>().ToList();
        if (Directory.Exists(ScreensFolder))
            shots.AddRange(Directory.GetFiles(ScreensFolder, "*.png").Except(shots));

        IReadOnlyList<SourcesCheckItem>? check = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"{BuildInfo.AppName.Replace(' ', '-')}/1.0");
            if (services.GetService<SourcesChecker>() is { } checker) check = await checker.RunAsync(http, live: true);
        }
        catch (Exception ex) { check = [new SourcesCheckItem("Sources check", CheckState.Problem, "failed: " + ex.Message)]; }

        var ui = DescribeUi();
        return await Task.Run(() => DiagnosticReport.WriteAsync(db, ui, shots, check, reason));
    }
}
