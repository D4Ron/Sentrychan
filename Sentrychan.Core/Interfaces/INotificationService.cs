namespace Sentrychan.Core.Interfaces;

/// <summary>Sends notifications to the OS (Windows Action Center toasts).</summary>
public interface INotificationService
{
    void Notify(string title, string message);
}
