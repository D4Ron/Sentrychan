using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Sentrychan.Core;
using Sentrychan.Core.Updates;

namespace Sentrychan.UI.Views.Dialogs;

/// <summary>The update notes: once after an update with what's new, or all of them from Settings → About.</summary>
public partial class WhatsNewDialog : Window
{
    public IReadOnlyList<ChangelogEntry> Entries { get; }
    public string Subtitle { get; }

    public WhatsNewDialog() : this([], "") { }

    public WhatsNewDialog(IReadOnlyList<ChangelogEntry> entries, string subtitle)
    {
        Entries = entries;
        Subtitle = subtitle;
        DataContext = this;
        InitializeComponent();
    }

    /// <summary>This build's notes, newest first; empty when they can't be read.</summary>
    public static IReadOnlyList<ChangelogEntry> Notes()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Sentrychan.UI/Assets/changelog.json"));
            using var reader = new System.IO.StreamReader(stream);
            return Changelog.ForBuild(Changelog.Parse(reader.ReadToEnd()), BuildInfo.IsPreview);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WhatsNew] notes unreadable: {ex.Message}");
            return [];
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
