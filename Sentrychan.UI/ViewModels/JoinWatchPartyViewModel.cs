using ReactiveUI;
using Sentrychan.Core.Interfaces;
using System;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

public class JoinWatchPartyViewModel : ViewModelBase, IDisposable
{
    private readonly IWatchPartyClientService? _clientService;
    private readonly ILanDiscoveryService? _lanService;
    private readonly System.Collections.ObjectModel.ObservableCollection<LanPartyRowVm> _nearbyParties = new();

    public System.Collections.ObjectModel.ObservableCollection<LanPartyRowVm> NearbyParties => _nearbyParties;
    public bool HasNearbyParties => NearbyParties.Count > 0;

    // Output
    public bool Joined { get; private set; }

    private bool _isTailscaleDetected;
    public bool IsTailscaleDetected
    {
        get => _isTailscaleDetected;
        set => this.RaiseAndSetIfChanged(ref _isTailscaleDetected, value);
    }

    private string _hostIp = "localhost";
    public string HostIp
    {
        get => _hostIp;
        set => this.RaiseAndSetIfChanged(ref _hostIp, value);
    }

    private string _port = "7742";
    public string Port
    {
        get => _port;
        set => this.RaiseAndSetIfChanged(ref _port, value);
    }

    private string _username = string.Empty;
    public string Username
    {
        get => _username;
        set => this.RaiseAndSetIfChanged(ref _username, value);
    }

    private string _password = string.Empty;
    public string Password
    {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _hasError;
    public bool HasError
    {
        get => _hasError;
        set => this.RaiseAndSetIfChanged(ref _hasError, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public ReactiveCommand<Unit, Unit> JoinCommand { get; }
    public ReactiveCommand<LanPartyRowVm, Unit> JoinNearbyCommand { get; }

    public JoinWatchPartyViewModel()
    {
        JoinCommand = ReactiveCommand.CreateFromTask(async ct => await JoinPartyAsync(ct));
        JoinNearbyCommand = ReactiveCommand.Create<LanPartyRowVm>(row => JoinNearby(row));
    }

    public JoinWatchPartyViewModel(IWatchPartyClientService clientService, ILanDiscoveryService lanService) : this()
    {
        _clientService = clientService;
        _lanService = lanService;

        _lanService.PartyDiscovered += OnPartyDiscovered;
        _lanService.PartyLost += OnPartyLost;

        IsTailscaleDetected = Sentrychan.Core.Services.TailscaleDetector.IsTailscaleInstalled();

        _ = _lanService.StartListeningAsync(CancellationToken.None);
    }

    private void OnPartyDiscovered(LanParty party)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var existing = System.Linq.Enumerable.FirstOrDefault(NearbyParties, p => p.Data.RoomCode == party.RoomCode);
            if (existing == null)
            {
                NearbyParties.Add(new LanPartyRowVm(party));
                this.RaisePropertyChanged(nameof(HasNearbyParties));
            }
        });
    }

    private void OnPartyLost(string roomCode)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var existing = System.Linq.Enumerable.FirstOrDefault(NearbyParties, p => p.Data.RoomCode == roomCode);
            if (existing != null)
            {
                NearbyParties.Remove(existing);
                this.RaisePropertyChanged(nameof(HasNearbyParties));
            }
        });
    }

    private string _roomCode = string.Empty;
    public string RoomCode
    {
        get => _roomCode;
        set => this.RaiseAndSetIfChanged(ref _roomCode, value);
    }

    public void JoinNearby(LanPartyRowVm row)
    {
        var parts = row.Address.Split(':');
        HostIp = parts[0];
        if (parts.Length > 1) Port = parts[1];
        RoomCode = row.Data.RoomCode;
    }

    public void Dispose()
    {
        if (_lanService != null)
        {
            _lanService.PartyDiscovered -= OnPartyDiscovered;
            _lanService.PartyLost -= OnPartyLost;
            _ = _lanService.StopListeningAsync();
        }
    }

    private async Task JoinPartyAsync(CancellationToken ct)
    {
        if (_clientService == null) return;

        if (string.IsNullOrWhiteSpace(HostIp) || string.IsNullOrWhiteSpace(Port) || string.IsNullOrWhiteSpace(Username))
        {
            HasError = true;
            StatusMessage = "IP, Port, and Username are required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(RoomCode))
        {
            HasError = true;
            StatusMessage = "Room Code (Session ID) is required.";
            return;
        }

        if (!int.TryParse(Port, out var portNumber))
        {
            HasError = true;
            StatusMessage = "Port must be a number.";
            return;
        }

        IsBusy = true;
        HasError = false;
        StatusMessage = "Connecting...";

        var success = await _clientService.ConnectAsync(HostIp, portNumber, Username, Password, RoomCode, ct);

        if (success)
        {
            StatusMessage = "Joined Watch Party!";
            Joined = true;
            this.RaisePropertyChanged(nameof(Joined));
        }
        else
        {
            HasError = true;
            StatusMessage = "Failed to connect. Check IP, Port, and Password.";
        }

        IsBusy = false;
    }
}
