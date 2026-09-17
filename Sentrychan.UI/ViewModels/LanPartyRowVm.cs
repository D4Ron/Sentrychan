using ReactiveUI;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.UI.ViewModels;

public class LanPartyRowVm : ViewModelBase
{
    public LanParty Data { get; }
    
    public string DisplayText => $"{Data.HostDisplayName} ({Data.RoomCode})";
    public string Address => Data.HostAddress;

    public LanPartyRowVm(LanParty data)
    {
        Data = data;
    }
}
