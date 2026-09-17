using Sentrychan.Core.Interfaces;
using System.Diagnostics;
using System.Security;

namespace Sentrychan.App;

/// <summary>
/// Real Windows Action Center toasts without the WinUI toolkit (which pulls
/// legacy RIDs incompatible with .NET 9). Drives the WinRT ToastNotificationManager
/// through a hidden PowerShell call — works on unpackaged apps, no extra TFM/package.
/// Best-effort; never throws into the caller.
/// </summary>
public class WindowsNotificationService : INotificationService
{
    public void Notify(string title, string message)
    {
        try
        {
            string t = Escape(title);
            string m = Escape(message);

            // ToastText02: one bold heading + one body line.
            string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "[void][Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime];" +
                "[void][Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom.XmlDocument,ContentType=WindowsRuntime];" +
                "$x=[Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);" +
                "$n=$x.GetElementsByTagName('text');" +
                $"$n.Item(0).AppendChild($x.CreateTextNode('{t}'))|Out-Null;" +
                $"$n.Item(1).AppendChild($x.CreateTextNode('{m}'))|Out-Null;" +
                "$toast=[Windows.UI.Notifications.ToastNotification]::new($x);" +
                "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Sentrychan').Show($toast);";

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
        }
        catch
        {
            // Toasts require Windows 10+/PowerShell; ignore where unavailable.
        }
    }

    // Neutralize characters that would break the single-quoted PS string / XML.
    private static string Escape(string s) =>
        SecurityElement.Escape(s ?? string.Empty)?.Replace("'", "''") ?? string.Empty;
}
