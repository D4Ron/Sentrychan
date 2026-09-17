using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Sentrychan.UI.Controls;
using Sentrychan.UI.ViewModels;

namespace Sentrychan.UI.Views;

public partial class BattleRoyaleView : UserControl
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private BattleRoyaleViewModel? _vm;
    private string? _pendingUrl;
    private bool _initStarted;
    private volatile bool _playerReady;

    public BattleRoyaleView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Teardown();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= VmPropertyChanged;
            _vm.StopPlaybackRequested -= OnStopRequested;
        }
        _vm = DataContext as BattleRoyaleViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += VmPropertyChanged;
            _vm.StopPlaybackRequested += OnStopRequested;
        }
        EnsurePlayer();
    }

    private void EnsurePlayer()
    {
        if (_mediaPlayer != null || _initStarted) return;
        _initStarted = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var libVlc = new LibVLC("--network-caching=800", "--no-video-title-show", "--quiet");
                var mp = new MediaPlayer(libVlc);
                Dispatcher.UIThread.Post(() =>
                {
                    _libVlc = libVlc;
                    _mediaPlayer = mp;
                    _playerReady = true;
                    if (this.FindControl<VideoPlayerControl>("Player") is { } host)
                        host.View.MediaPlayer = mp;
                    if (_pendingUrl is { } p) { _pendingUrl = null; PlayUrl(p); }
                });
            }
            catch { /* no VLC → bracket still works without sound */ }
        });
    }

    private void VmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BattleRoyaleViewModel.CurrentMediaUrl))
            Dispatcher.UIThread.Post(PlayCurrent);
    }

    private void PlayCurrent()
    {
        var url = _vm?.CurrentMediaUrl;
        if (string.IsNullOrEmpty(url)) { try { _mediaPlayer?.Stop(); } catch { } _pendingUrl = null; return; }
        PlayUrl(url);
    }

    private void PlayUrl(string url)
    {
        if (!_playerReady || _mediaPlayer == null || _libVlc == null) { _pendingUrl = url; return; }
        try
        {
            _mediaPlayer.Stop();
            using var media = new Media(_libVlc, new Uri(url));
            _mediaPlayer.Play(media);
        }
        catch { /* streaming hiccup */ }
    }

    private void OnStopRequested() => Dispatcher.UIThread.Post(() =>
    {
        try { _mediaPlayer?.Stop(); } catch { }
        _pendingUrl = null;
    });

    private void Teardown()
    {
        try { _mediaPlayer?.Stop(); } catch { }
        try { _mediaPlayer?.Dispose(); } catch { }
        try { _libVlc?.Dispose(); } catch { }
        _mediaPlayer = null;
        _libVlc = null;
        _playerReady = false;
        _initStarted = false;
    }
}
