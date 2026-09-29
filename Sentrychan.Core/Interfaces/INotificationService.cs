namespace Sentrychan.Core.Interfaces;

/// <summary>Sends notifications to the OS: Windows toasts, macOS Notification Center, Linux notify-send.</summary>
public interface INotificationService
{
    void Notify(string title, string message);
}
