using Avalonia.Controls;
using Avalonia.Interactivity;
using Sentrychan.UI.Services;
using System;
using System.IO;

namespace Sentrychan.UI.Views.Dialogs;

/// <summary>Makes a problem report on request — or shows one the app already made (its first-start diagnosis).</summary>
public partial class ProblemReportDialog : Window
{
    private string? _file;

    public ProblemReportDialog() => InitializeComponent();

    /// <summary>For a report that was already written: says where it is.</summary>
    public static ProblemReportDialog ForExisting(string file)
    {
        var d = new ProblemReportDialog();
        d.Heading.Text = "A report was made for the developer";
        d.Intro.Text = "This test version checked itself the first time it started and saved what it found in one file. Please send that file to the developer — it shows them what happened on your computer. Nothing was sent anywhere.";
        d.AskPanel.IsVisible = false;
        d.CreateButton.IsVisible = false;
        d.ShowDone(file);
        return d;
    }

    private void ShowDone(string file)
    {
        _file = file;
        Result.Text = $"Saved to {Path.GetDirectoryName(file)}:\n{Path.GetFileName(file)}\n\nSend this file to the developer (any chat or e-mail). It contains your folder names and series titles, nothing else personal.";
        Result.IsVisible = true;
        ShowFileButton.IsVisible = true;
    }

    private async void OnCreate(object? sender, RoutedEventArgs e)
    {
        CreateButton.IsEnabled = false;
        Busy.IsVisible = true;
        Result.IsVisible = false;
        try
        {
            ShowDone(await Diagnostics.WriteReportAsync(string.IsNullOrWhiteSpace(WhatHappened.Text) ? null : WhatHappened.Text.Trim()));
            CreateButton.IsVisible = false;
            AskPanel.IsVisible = false;
        }
        catch (Exception ex)
        {
            Result.Text = "The report couldn't be made: " + ex.Message;
            Result.IsVisible = true;
            CreateButton.IsEnabled = true;
        }
        finally { Busy.IsVisible = false; }
    }

    private void OnShowFile(object? sender, RoutedEventArgs e) => ShellLauncher.Reveal(_file);
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
