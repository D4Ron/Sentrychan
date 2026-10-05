using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.Views;
using System;
using System.Linq;

namespace Sentrychan.UI;

public partial class App : Application
{
    public static IServiceProvider? Services { get; private set; }

    public static void SetServiceProvider(IServiceProvider services)
        => Services = services;

    /// <summary>
    /// Optional callback invoked AFTER the MainWindow is created and the
    /// Avalonia Win32 dispatcher is installed on the UI thread. Used to
    /// start the Generic Host without tripping the Dispatcher.UIThread
    /// init race that produces PlatformNotSupportedException in MainLoop.
    /// </summary>
    public static Action? PostInitAction { get; set; }

    /// <summary>
    /// Relaunches the app and shuts this instance down. Set by the composition root, which knows
    /// how this process was started; null where restarting isn't possible (design time).
    /// </summary>
    public static Action? Restart { get; set; }

    /// <summary>
    /// Loads source packs installed while the app runs and wires what they bring (image settings,
    /// default feeds and groups). Set by the composition root, which owns the plugin loader.
    /// </summary>
    public static Func<System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Sentrychan.Core.Sources.SourcePackStatus>>>? LoadNewPacks { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // macOS titles its application menu with this ("About …", "Hide …", "Quit …"); unset it
        // read "Avalonia Application".
        Name = Sentrychan.Core.BuildInfo.AppName;

        // ReactiveUI crashes the whole app on any unobserved ReactiveCommand error (a
        // `command.Execute().Subscribe()` re-entered by rapid input is enough). Swallow +
        // log instead of crashing. Must be set before any command executes.
        ReactiveUI.RxApp.DefaultExceptionHandler =
            System.Reactive.Observer.Create<Exception>(ex =>
            {
                System.Diagnostics.Debug.WriteLine($"[RxApp] unobserved command error: {ex.Message}");
                // Reaches the log in test builds (the console is pointed there).
                if (Sentrychan.Core.BuildInfo.IsTestBuild) Console.WriteLine($"[RxApp] unobserved command error: {ex}");
            });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // The player explains what's missing (system libvlc on Linux) instead of failing later.
        Sentrychan.UI.Services.VideoSupport.Initialize();
        MainWindowViewModel? mainVm = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            mainVm = Services?.GetService<MainWindowViewModel>()
                  ?? new MainWindowViewModel();

            desktop.MainWindow = new MainWindow { DataContext = mainVm };
            // Started at login: stay out of the way until the user opens it.
            if (desktop.Args?.Contains(Sentrychan.UI.Services.LoginItem.BackgroundArg) == true)
                desktop.MainWindow.WindowState = Avalonia.Controls.WindowState.Minimized;
            Sentrychan.UI.Services.TrayService.Initialize();
            if (OperatingSystem.IsMacOS()) SetMacAppMenu(mainVm);
            Sentrychan.UI.Services.WindowFit.Install();
            Sentrychan.UI.Services.Diagnostics.WatchWindows();

            // macOS: clicking the Dock icon of a running app whose window is hidden brings it back.
            if (OperatingSystem.IsMacOS()
                && TryGetFeature(typeof(Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime))
                    is Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind != Avalonia.Controls.ApplicationLifetimes.ActivationKind.Reopen
                        || desktop.MainWindow is not { } w) return;
                    w.Show();
                    w.WindowState = Avalonia.Controls.WindowState.Normal;
                    w.Activate();
                };
            }
        }

        base.OnFrameworkInitializationCompleted();

        // Gate-open the VM's MediatR handlers. Without this call, every
        // INotificationHandler in MainWindowViewModel early-returns on
        // !_uiReady — blocking popups, monitor status, file banners, etc.
        mainVm?.MarkUiReady();

        // Start the Generic Host AFTER the dispatcher is in place and the main
        // window has been assigned — safe for hosted services to resolve UI VMs.
        try
        {
            PostInitAction?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PostInitAction failed: {ex}");
        }
    }

    /// <summary>
    /// The application menu on macOS. Without one Avalonia shows its own, whose "About" opens an
    /// Avalonia dialog. The system adds Hide/Quit itself, named after <see cref="Application.Name"/>.
    /// </summary>
    private void SetMacAppMenu(MainWindowViewModel vm)
    {
        void Show(int tab)
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } w })
            {
                w.Show();
                w.Activate();
            }
            _ = vm.OpenSettingsAtAsync(tab);
        }

        var about = new Avalonia.Controls.NativeMenuItem("About " + Sentrychan.Core.BuildInfo.AppName);
        about.Click += (_, _) => Show(SettingsViewModel.AboutTab);
        var settings = new Avalonia.Controls.NativeMenuItem("Settings…")
        {
            Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.OemComma, Avalonia.Input.KeyModifiers.Meta),
        };
        settings.Click += (_, _) => Show(0);

        var menu = new Avalonia.Controls.NativeMenu();
        menu.Add(about);
        menu.Add(new Avalonia.Controls.NativeMenuItemSeparator());
        menu.Add(settings);
        if (Sentrychan.Core.BuildInfo.IsTestBuild)
        {
            var report = new Avalonia.Controls.NativeMenuItem("Send a problem report…");
            report.Click += (_, _) => _ = vm.ShowProblemReportAsync();
            menu.Add(report);
        }
        Avalonia.Controls.NativeMenu.SetMenu(this, menu);
    }
}