using Avalonia.Input;
using Avalonia.Controls;
using Sentrychan.UI.ViewModels;
using Sentrychan.Core.Services;
using Sentrychan.UI.Interfaces;
using Sentrychan.UI.Services;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Specialized;

namespace Sentrychan.UI.Views;

public partial class WatchPartyView : UserControl
{
    public WatchPartyView()
    {
        InitializeComponent();
        
        this.KeyDown += OnKeyDown;
        
        this.DataContextChanged += (s, e) =>
        {
            if (DataContext is WatchPartyViewModel vm)
            {
                var playerHost = this.FindControl<Sentrychan.UI.Controls.VideoPlayerControl>("PlayerHost");
                var reactionCanvas = this.FindControl<Canvas>("ReactionCanvas");
                
                if (playerHost != null && vm.Player is PlayerBridgeService router)
                {
                    System.Threading.Tasks.Task.Run(() => 
                    {
                        var libVlc = new LibVLCSharp.Shared.LibVLC();
                        var mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(libVlc);
                        Dispatcher.UIThread.Post(() => 
                        {
                            var internalPlayer = new InternalPlayerBridge(playerHost, libVlc, mediaPlayer);
                            router.SetActivePlayer(internalPlayer);
                        });
                    });
                }

                if (reactionCanvas != null && vm.ReactionService is ReactionOverlayService reactionSvc)
                {
                    reactionSvc.SetCanvas(reactionCanvas);
                }

                vm.ChatMessages.CollectionChanged += (s2, e2) => 
                {
                    if (e2.Action == NotifyCollectionChangedAction.Add)
                    {
                        Dispatcher.UIThread.Post(() => ScrollChatToBottom(), DispatcherPriority.Background);
                    }
                };
            }
        };
    }

    private void ScrollChatToBottom()
    {
        var scroll = this.FindControl<ScrollViewer>("ChatScroll");
        if (scroll != null)
        {
            scroll.Offset = new Avalonia.Vector(0, double.MaxValue);
        }
    }

    private DispatcherTimer? _hideControlsTimer;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Focus();
        
        _hideControlsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _hideControlsTimer.Tick += (_, _) =>
        {
            if (DataContext is WatchPartyViewModel vm)
                vm.AreControlsVisible = false;
            _hideControlsTimer!.Stop();
        };
    }

    private void OnVideoAreaPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is WatchPartyViewModel vm)
            vm.AreControlsVisible = true;
        _hideControlsTimer?.Stop();
        _hideControlsTimer?.Start();
    }

    private void OnControlsPointerMoved(object? sender, PointerEventArgs e)
    {
        OnVideoAreaPointerMoved(sender, e);
    }

    private void OnSeekBarReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && DataContext is WatchPartyViewModel vm && vm.IsHost)
        {
            vm.SeekCommand.Execute(slider.Value).Subscribe();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not WatchPartyViewModel vm) return;

        // Show controls on any key press
        vm.AreControlsVisible = true;
        _hideControlsTimer?.Stop();
        _hideControlsTimer?.Start();

        switch (e.Key)
        {
            case Key.Space:
                if (vm.IsHost)
                    vm.PlayPauseCommand.Execute().Subscribe();
                e.Handled = true;
                break;
            case Key.Right:
                if (vm.IsHost)
                    vm.SkipForwardCommand.Execute().Subscribe();
                e.Handled = true;
                break;
            case Key.Left:
                if (vm.IsHost)
                    vm.SkipBackCommand.Execute().Subscribe();
                e.Handled = true;
                break;
            case Key.M:
                vm.ToggleMuteCommand.Execute().Subscribe();
                e.Handled = true;
                break;
            case Key.F:
                vm.ToggleFullscreenCommand.Execute().Subscribe();
                e.Handled = true;
                break;
            case Key.Up:
                vm.Volume = Math.Min(100, vm.Volume + 5);
                vm.SetVolumeCommand.Execute(vm.Volume).Subscribe();
                e.Handled = true;
                break;
            case Key.Down:
                vm.Volume = Math.Max(0, vm.Volume - 5);
                vm.SetVolumeCommand.Execute(vm.Volume).Subscribe();
                e.Handled = true;
                break;
        }
    }
 
}
