using ReactiveUI;

namespace Sentrychan.UI.ViewModels;

public class ParticipantRowVm : ReactiveObject
{
    private string _guid = string.Empty;
    public string Guid
    {
        get => _guid;
        set => this.RaiseAndSetIfChanged(ref _guid, value);
    }

    private string _username = string.Empty;
    public string Username
    {
        get => _username;
        set => this.RaiseAndSetIfChanged(ref _username, value);
    }

    private bool _isReady;
    public bool IsReady
    {
        get => _isReady;
        set => this.RaiseAndSetIfChanged(ref _isReady, value);
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set => this.RaiseAndSetIfChanged(ref _isMuted, value);
    }

    private bool _isHost;
    public bool IsHost
    {
        get => _isHost;
        set => this.RaiseAndSetIfChanged(ref _isHost, value);
    }
}
