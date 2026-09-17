using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Sentrychan.UI.Controls;
using Sentrychan.UI.ViewModels;

namespace Sentrychan.UI.Views;

public partial class AnimeQuizView : UserControl
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private AnimeQuizViewModel? _vm;
    private string? _pendingUrl;
    private bool _initStarted;
    private volatile bool _playerReady;

    public AnimeQuizView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => Focus();
        DetachedFromVisualTree += (_, _) => Teardown();
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= VmPropertyChanged;
            _vm.StopPlaybackRequested -= OnStopRequested;
            _vm.ReplayRequested -= OnReplayRequested;
        }
        _vm = DataContext as AnimeQuizViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += VmPropertyChanged;
            _vm.StopPlaybackRequested += OnStopRequested;
            _vm.ReplayRequested += OnReplayRequested;
        }
        EnsurePlayer();
    }

    // LibVLC construction is slow — do it off the UI thread so clicking Quiz never freezes.
    private void EnsurePlayer()
    {
        if (_mediaPlayer != null || _initStarted) return;
        _initStarted = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var libVlc = new LibVLC("--network-caching=700", "--avcodec-hw=any",
                                        "--no-video-title-show", "--quiet");
                var mp = new MediaPlayer(libVlc);
                // The countdown only starts once the audio truly begins — never during buffering.
                mp.Playing += (_, _) => Dispatcher.UIThread.Post(() => _vm?.OnMediaStarted());
                Dispatcher.UIThread.Post(() =>
                {
                    _libVlc = libVlc;
                    _mediaPlayer = mp;
                    _playerReady = true;
                    if (this.FindControl<VideoPlayerControl>("Player") is { } host)
                        host.View.MediaPlayer = mp;
                    if (_pendingUrl is not null) { _pendingUrl = null; PlayCurrent(); }
                });
            }
            catch { /* no VLC → quiz still works minus playback */ }
        });
    }

    private void VmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnimeQuizViewModel.CurrentMediaUrl))
            Dispatcher.UIThread.Post(PlayCurrent);
    }

    private void PlayCurrent()
    {
        // Never start playback unless we're actually in a round — guards against a load that
        // finished after the user quit trying to play audio they can't stop.
        if (_vm is not { IsPlaying: true }) { try { _mediaPlayer?.Stop(); } catch { } _pendingUrl = null; return; }
        var url = _vm.CurrentMediaUrl;
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
            // Only seek a snippet start during GUESSING (local audio → instant seek). On reveal the
            // video is often streamed, and a remote seek is very slow — so play it from the start.
            var start = _vm is { HasAnswered: false } ? _vm.CurrentStartSeconds : 0;
            if (start > 0) media.AddOption($":start-time={start}");
            _mediaPlayer.Play(media);
        }
        catch { /* streaming hiccup — user can hit Replay/Next */ }
    }

    private void OnStopRequested() => Dispatcher.UIThread.Post(() =>
    {
        try { _mediaPlayer?.Stop(); } catch { }
        _pendingUrl = null;
    });

    private void OnReplayRequested() => Dispatcher.UIThread.Post(PlayCurrent);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = _vm;
        if (vm == null || !vm.IsPlaying) return;

        switch (e.Key)
        {
            case Key.D1: case Key.NumPad1: vm.AnswerByIndex(1); e.Handled = true; break;
            case Key.D2: case Key.NumPad2: vm.AnswerByIndex(2); e.Handled = true; break;
            case Key.D3: case Key.NumPad3: vm.AnswerByIndex(3); e.Handled = true; break;
            case Key.D4: case Key.NumPad4: vm.AnswerByIndex(4); e.Handled = true; break;
            case Key.Enter:
            case Key.Space:
                if (vm.HasAnswered) { vm.NextCommand.Execute().Subscribe(_ => { }, _ => { }); e.Handled = true; }
                break;
        }
    }

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
