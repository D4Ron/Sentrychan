using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using Sentrychan.UI.Services;
using Sentrychan.UI.ViewModels;
using System;

namespace Sentrychan.UI.Views;

public partial class VideoPlayerWindow : Window
{
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _seeking;
    private IDisposable? _positionSub;

    public VideoPlayerWindow()
    {
        InitializeComponent();

        _hideTimer.Tick += (_, _) => SetChrome(false);

        // The overlay lives in its own floating window, so input reaching it doesn't route
        // through this one — hook both.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Overlay.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Overlay.PointerMoved += (_, _) => SetChrome(true);
        Overlay.PointerPressed += (_, _) => Overlay.Focus();
        Overlay.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual v && IsInBar(v)) return;
            ToggleFullscreen();
        };
        FullscreenButton.Click += (_, _) => ToggleFullscreen();

        // The seek bar follows playback except while the user is dragging it.
        Seek.AddHandler(PointerPressedEvent, (_, _) => _seeking = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        Seek.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _seeking = false;
            (DataContext as VideoPlayerViewModel)?.SeekTo(Seek.Value);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        PanicKey.Triggered += OnPanic;
    }

    private bool IsInBar(Visual v)
    {
        for (var p = v; p != null; p = p.GetVisualParent())
            if (p == BottomBar || p == TopBar) return true;
        return false;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not VideoPlayerViewModel vm) return;

        Overlay.DataContext = vm;
        _positionSub = vm.WhenAnyValue(x => x.Position).Subscribe(p => { if (!_seeking) Seek.Value = p; });
        SetChrome(true);
        await vm.InitializeAsync();
        Overlay.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        PanicKey.Triggered -= OnPanic;
        _hideTimer.Stop();
        _positionSub?.Dispose();
        (DataContext as VideoPlayerViewModel)?.Dispose();
        base.OnClosed(e);
    }

    private void OnPanic() => Dispatcher.UIThread.Post(Close);

    private void SetChrome(bool visible)
    {
        TopBar.Opacity = BottomBar.Opacity = visible ? 1 : 0;
        BottomBar.IsHitTestVisible = visible;
        Overlay.Cursor = visible ? Cursor.Default : new Cursor(StandardCursorType.None);
        _hideTimer.Stop();
        if (visible) _hideTimer.Start();
    }

    private void ToggleFullscreen()
    {
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        SetChrome(true);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (PanicKey.Matches(e)) { e.Handled = true; PanicKey.Trigger(); return; }
        if (DataContext is not VideoPlayerViewModel vm) return;
        if (e.Source is ComboBox or ComboBoxItem) return; // let the track pickers use arrow keys

        var handled = true;
        switch (e.Key)
        {
            case Key.Space: case Key.K: vm.PlayPause(); break;
            case Key.Left:  vm.SeekBy(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -60 : -10); break;
            case Key.Right: vm.SeekBy(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 60 : 10); break;
            case Key.J:     vm.SeekBy(-10); break;
            case Key.L:     vm.SeekBy(10); break;
            case Key.Up:    vm.Volume += 5; break;
            case Key.Down:  vm.Volume -= 5; break;
            case Key.M:     vm.IsMuted = !vm.IsMuted; break;
            case Key.S:     vm.CycleSubtitles(); break;
            case Key.A:     vm.CycleAudio(); break;
            case Key.N:     vm.NextCommand.Execute().Subscribe(); break;
            case Key.P:     vm.PreviousCommand.Execute().Subscribe(); break;
            case Key.F: case Key.Enter: ToggleFullscreen(); break;
            case Key.Escape:
                if (WindowState == WindowState.FullScreen) ToggleFullscreen();
                else Close();
                break;
            default: handled = false; break;
        }
        if (handled) { e.Handled = true; SetChrome(true); }
    }
}
