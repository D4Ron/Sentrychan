using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;
using Sentrychan.Core.Interfaces;
using System;

namespace Sentrychan.UI.Services;

public static class TrayService
{
    private static TrayIcon? _trayIcon;
    private static WindowNotificationManager? _notificationManager;
    private static string _baseTooltip = "Sentrychan";

    public static void Initialize()
    {
        if (_trayIcon != null) return;

        using var iconStream = AssetLoader.Open(new Uri("avares://Sentrychan.UI/Assets/logo.png"));
        
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "Sentrychan"
        };

        var menu = new NativeMenu();

        var showItem = new NativeMenuItem("Show Window");
        showItem.Click += (s, e) =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                desktop.MainWindow.Show();
                desktop.MainWindow.WindowState = WindowState.Normal;
                desktop.MainWindow.Activate();
            }
        };

        var checkItem = new NativeMenuItem("Check Now");
        checkItem.Click += async (s, e) =>
        {
            var monitor = App.Services?.GetService<IRssMonitorService>();
            if (monitor != null) await monitor.ManualCheckAsync();
        };

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (s, e) =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        };

        menu.Items.Add(showItem);
        menu.Items.Add(checkItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon.Menu = menu;

        // Subscribe to theme changes to swap the tray icon
        var themeService = App.Services?.GetService<IThemeService>();
        if (themeService != null)
        {
            themeService.ThemeChanged += (isSecret) =>
            {
                try
                {
                    var assetPath = isSecret
                        ? "avares://Sentrychan.UI/Assets/sentrykun_logo.png"
                        : "avares://Sentrychan.UI/Assets/logo.png";
                    using var stream = AssetLoader.Open(new Uri(assetPath));
                    _trayIcon.Icon = new WindowIcon(stream);
                    _baseTooltip = isSecret ? "Sentrykun" : "Sentrychan";
                    _trayIcon.ToolTipText = _baseTooltip;
                }
                catch { /* Ignore icon swap failures */ }
            };
        }
    }

    /// <summary>
    /// Reflects live download activity in the tray tooltip so the user can
    /// see what's happening even while the window is hidden.
    /// </summary>
    public static void SetDownloadStatus(int activeCount, double avgProgress)
    {
        if (_trayIcon == null) return;
        _trayIcon.ToolTipText = activeCount > 0
            ? $"{_baseTooltip} — {activeCount} downloading ({avgProgress:F0}%)"
            : _baseTooltip;
    }

    public static void ShowNotification(string title, string message)
    {
        // Prefer a real Windows Action Center toast (persists, works when hidden).
        var os = App.Services?.GetService<Sentrychan.Core.Interfaces.INotificationService>();
        if (os != null)
        {
            os.Notify(title, message);
            return;
        }

        // Fallback: in-window banner (e.g. if the OS toast service isn't available).
        if (_notificationManager == null)
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                _notificationManager = new WindowNotificationManager(TopLevel.GetTopLevel(desktop.MainWindow))
                {
                    Position = NotificationPosition.BottomRight,
                    MaxItems = 3
                };
            }
        }

        _notificationManager?.Show(new Notification(title, message, NotificationType.Information));
    }
}
