using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sentrychan.UI.Views.Dialogs;

public partial class TutorialDialog : Window
{
    private record Step(string IconKey, string Title, string Body);

    // The baked-in how-to guide. Keep in sync with the website's tutorial section.
    private static readonly Step[] Steps =
    [
        new("LibraryRegular", "Welcome to Sentrychan",
            "Your self-driving library for anime and manga. Tell it what you're into — it finds new episodes and chapters, downloads them, names them, and files them away. This quick tour shows you around. You can skip it anytime."),
        new("LibraryRegular", "Your Library",
            "The Library is home base. Add the shows you're watching and Sentrychan keeps them organised — sections re-sort themselves as shows finish airing, and each card shows your progress at a glance."),
        new("DownloadRegular", "Adding a series",
            "Click “Add Series”, search for a show, and pick it. Sentrychan grabs the poster, starts tracking it, and from then on new episodes arrive on their own. Turn on auto-download to skip even the confirmation."),
        new("SparkleRegular", "Latest releases",
            "The Latest page is a live feed of what just came out from your sources. Browse it, open Details to see seeds and size, and hit Download on anything you want — added to your library or saved standalone."),
        new("MangaRegular", "Manga",
            "Track your reading and read chapters right here — paged or webtoon scroll, with resume. Download chapters for offline reading, point it at a local folder you already have, and get notified when a series you follow updates. Online sources come from source packs you import in Settings."),
        new("DownloadRegular", "Downloads",
            "A torrent client is built in: pause, resume, set speed limits, and cap how many run at once. Prefer your own? Point Sentrychan at qBittorrent instead. Right-click any download to open its folder."),
        new("CalendarRegular", "Always up to date",
            "A background monitor quietly checks your sources for new episodes and chapters and sends a Windows notification when they land — so you never have to go looking. The Airing Today rail shows what's dropping."),
        new("SettingsRegular", "Make it yours",
            "Four themes, a sidebar or top-bar layout, release-group preferences, and more all live in Settings. That's the tour — Sentrychan is free and open source, so if something's missing, the issue tracker is open."),
    ];

    private int _index;

    public TutorialDialog()
    {
        InitializeComponent();
        BuildDots();
        Render();
    }

    private void BuildDots()
    {
        DotsPanel.Children.Clear();
        for (int i = 0; i < Steps.Length; i++)
            DotsPanel.Children.Add(new Ellipse { Width = 7, Height = 7 });
    }

    private void Render()
    {
        var step = Steps[_index];

        if (this.TryFindResource(step.IconKey, out var geo) && geo is Geometry g)
            StepIcon.Data = g;
        StepTitle.Text = step.Title;
        StepBody.Text = step.Body;

        var accent = (this.TryFindResource("AccentBrush", out var a) ? a as IBrush : null) ?? Brushes.MediumPurple;
        var dim = (this.TryFindResource("BorderBrush", out var b) ? b as IBrush : null) ?? Brushes.Gray;
        for (int i = 0; i < DotsPanel.Children.Count; i++)
            if (DotsPanel.Children[i] is Ellipse dot)
                dot.Fill = i == _index ? accent : dim;

        BackButton.IsVisible = _index > 0;
        NextButton.Content = _index == Steps.Length - 1 ? "Done" : "Next";
        SkipButton.IsVisible = _index < Steps.Length - 1;
    }

    private void OnBack(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_index > 0) { _index--; Render(); }
    }

    private void OnNext(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_index < Steps.Length - 1) { _index++; Render(); }
        else Close();
    }

    private void OnSkip(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
