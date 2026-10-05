using Avalonia.Controls;
using Avalonia.Interactivity;
using Sentrychan.Core;
using Sentrychan.UI.Services;
using System;

namespace Sentrychan.UI.Views.Dialogs;

/// <summary>What the user chose on <see cref="InstallFinishedDialog"/>.</summary>
public sealed record InstallChoices(bool StartAtLogin, bool KeepOpen);

/// <summary>
/// An installer's last page, asked by the app itself: start at sign-in, and — right after a
/// Windows install, which opens the app on its own — whether to open it now at all. A tester
/// found an installer that silently started the app alarming. People who updated see only the
/// start-at-sign-in question, once.
/// </summary>
public partial class InstallFinishedDialog : Window
{
    public InstallFinishedDialog() : this(justInstalled: false) { }

    /// <param name="justInstalled">The installer started this run (Windows), so "Open now" is offered.</param>
    public InstallFinishedDialog(bool justInstalled)
    {
        InitializeComponent();
        var fresh = justInstalled || OperatingSystem.IsMacOS();
        Heading.Text = fresh ? $"{BuildInfo.AppName} is installed"
            : OperatingSystem.IsWindows() ? "New: start with Windows" : "New: open at login";
        Intro.Text = fresh
            ? "One choice before you start — you can change it any time in Settings → General."
            : "Sentrychan can start when you sign in, so new episodes keep arriving. You can change this any time in Settings → General.";
        OpenAtLoginLabel.Text = OperatingSystem.IsWindows()
            ? $"Start {BuildInfo.AppName} with Windows"
            : $"Open {BuildInfo.AppName} when I log in";
        OpenNow.IsVisible = justInstalled;
        OpenNowLabel.Text = $"Open {BuildInfo.AppName} now";
        FinishButton.Content = justInstalled ? "Finish" : "Continue";
    }

    private void OnContinue(object? sender, RoutedEventArgs e) =>
        Close(new InstallChoices(OpenAtLogin.IsChecked == true, !OpenNow.IsVisible || OpenNow.IsChecked == true));
}
