using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.Views;
using System;

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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // ReactiveUI crashes the whole app on any unobserved ReactiveCommand error (a
        // `command.Execute().Subscribe()` re-entered by rapid input is enough). Swallow +
        // log instead of crashing. Must be set before any command executes.
        ReactiveUI.RxApp.DefaultExceptionHandler =
            System.Reactive.Observer.Create<Exception>(ex =>
                System.Diagnostics.Debug.WriteLine($"[RxApp] unobserved command error: {ex.Message}"));
    }

    private static bool _vlcInitialized = false;

    public override void OnFrameworkInitializationCompleted()
    {
        try 
        {
            if (!_vlcInitialized) 
            {
                LibVLCSharp.Shared.Core.Initialize();
                _vlcInitialized = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LibVLC Core.Initialize failed: {ex}");
        }
        MainWindowViewModel? mainVm = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            mainVm = Services?.GetService<MainWindowViewModel>()
                  ?? new MainWindowViewModel();

            desktop.MainWindow = new MainWindow { DataContext = mainVm };
            Sentrychan.UI.Services.TrayService.Initialize();
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
}