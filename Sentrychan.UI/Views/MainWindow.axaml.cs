using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Sentrychan.UI.ViewModels;
using Sentrychan.Core.Models;
using Sentrychan.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Reactive.Linq;
using Avalonia.Interactivity;

namespace Sentrychan.UI.Views;

public partial class MainWindow : Window
{
    private int _logoClickCount = 0;
    private DateTime _lastLogoClick = DateTime.MinValue;
    private readonly IThemeService? _themeService;
    private IDisposable? _welcomeSub;
    private IDisposable? _toastSub;
    private IDisposable? _undoToastSub;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        
        _themeService = App.Services?.GetService<IThemeService>();
        if (_themeService != null)
        {
            _themeService.ThemeChanged += OnThemeChanged;
        }

        // Initialize WelcomeOverlay
        var quoteService = App.Services?.GetService<QuoteService>();
        if (quoteService != null)
        {
            WelcomeOverlay.SetQuoteService(quoteService);
        }

        this.DataContextChanged += (s, e) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Dispose previous subscriptions — DataContextChanged can fire more
                // than once, and stacked subscriptions double-play the welcome
                // overlay and duplicate every toast.
                _welcomeSub?.Dispose();
                _toastSub?.Dispose();
                _undoToastSub?.Dispose();

                _welcomeSub = vm.PlayWelcomeCommand.Subscribe(_ => WelcomeOverlay.Play());

                // Transient feedback toasts (download started / added to library / errors).
                // Both subjects already emit on the UI thread (posted via Dispatcher).
                _toastSub = vm.ToastRequest.Subscribe(t => Toast.Show(t.Title, t.Body));

                // Episode-advance undo toast.
                _undoToastSub = vm.UndoToastRequest.Subscribe(t => Toast.Show(t.Title, t.Body));
            }
        };
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }

    private void OnThemeChanged(bool isSecret)
    {
        // Both modes show artwork now — just swap which character.
        var logoAsset = isSecret
            ? "avares://Sentrychan.UI/Assets/sentrykun_logo.png"
            : "avares://Sentrychan.UI/Assets/logo.png";

        try
        {
            using var logoStream = Avalonia.Platform.AssetLoader.Open(new Uri(logoAsset));
            LogoImage.Source = new Avalonia.Media.Imaging.Bitmap(logoStream);
        }
        catch { /* keep previous logo on load failure */ }

        LogoText.Text = isSecret ? "Sentrykun" : "Sentrychan";
        Title = LogoText.Text;

        // Taskbar icon follows the mode
        try
        {
            using var iconStream = Avalonia.Platform.AssetLoader.Open(new Uri(logoAsset));
            Icon = new WindowIcon(iconStream);
        }
        catch { /* ignore */ }
    }

    private void LogoMark_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var now = DateTime.Now;
        if ((now - _lastLogoClick).TotalMilliseconds > 600)
            _logoClickCount = 0;
        _logoClickCount++;
        _lastLogoClick = now;

        if (_logoClickCount >= 3)
        {
            _logoClickCount = 0;
            var themeService = App.Services.GetRequiredService<IThemeService>();
            if (themeService.HasUnlockedThisSession)
            {
                themeService.ToggleSecretMode();
            }
            else
            {
                ShowSecretPasswordPrompt();
            }
        }
    }

    private void ShowSecretPasswordPrompt()
    {
        var overlay = new Border
        {
            Background = Brushes.Transparent,
            ZIndex = 999,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(60, 6, 0, 0)
        };
        var pwBox = new TextBox
        {
            Watermark = "unlock code",
            PasswordChar = (char)0x25CF,
            Width = 140,
            Height = 28,
            FontSize = 11
        };
        overlay.Child = pwBox;
        RootGrid.Children.Add(overlay);
        pwBox.Focus();

        pwBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                var themeService = App.Services!.GetRequiredService<IThemeService>();
                bool success = themeService.TryUnlock(pwBox.Text ?? string.Empty);
                pwBox.Text = string.Empty;
                RootGrid.Children.Remove(overlay);
                if (success) themeService.ActivateSecretMode();
            }
            else if (e.Key == Key.Escape)
            {
                pwBox.Text = string.Empty;
                RootGrid.Children.Remove(overlay);
            }
        };
        pwBox.LostFocus += (s, e) =>
        {
            pwBox.Text = string.Empty;
            RootGrid.Children.Remove(overlay);
        };
    }

    private void OnSeriesDetailRequested(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.DataContext is Series series && DataContext is MainWindowViewModel vm)
        {
            vm.OpenSeriesDetailsCommand.Execute(series);
        }
    }

    private void OnDownloadAllPending(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            // Snapshot — each Download fires an event that removes the card from the list.
            foreach (var card in System.Linq.Enumerable.ToList(vm.PendingConfirms))
                card.DownloadCommand.Execute().Subscribe();
        }
    }

    private void OnTogglePendingInbox(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsPendingInboxExpanded = !vm.IsPendingInboxExpanded;
    }

    private void OnHideInbox(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.IsInboxHidden = true;
    }

    private void OnShowInbox(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.IsInboxHidden = false;
    }

    private void OnHideSidebar(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.IsSidebarHidden = true;
    }

    private void OnRevealSidebar(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.IsSidebarHidden = false;
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }
}