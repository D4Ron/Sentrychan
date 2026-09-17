using ReactiveUI;
using System;

namespace Sentrychan.UI.ViewModels;

public class ChatMessageVm : ReactiveObject
{
    private string _sender = string.Empty;
    public string Sender
    {
        get => _sender;
        set => this.RaiseAndSetIfChanged(ref _sender, value);
    }

    private string _content = string.Empty;
    public string Content
    {
        get => _content;
        set => this.RaiseAndSetIfChanged(ref _content, value);
    }

    private DateTime _timestamp = DateTime.Now;
    public DateTime Timestamp
    {
        get => _timestamp;
        set => this.RaiseAndSetIfChanged(ref _timestamp, value);
    }
}
