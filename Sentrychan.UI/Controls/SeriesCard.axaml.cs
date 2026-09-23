using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Sentrychan.Core.Models;
using System;
using System.Reactive;
using System.Windows.Input;
using Sentrychan.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Sentrychan.UI.Controls;

public partial class SeriesCard : UserControl
{
    public static readonly StyledProperty<ICommand> OpenDetailCommandProperty =
        AvaloniaProperty.Register<SeriesCard, ICommand>(nameof(OpenDetailCommand));

    public static readonly StyledProperty<ICommand> OpenPosterOptionsCommandProperty =
        AvaloniaProperty.Register<SeriesCard, ICommand>(nameof(OpenPosterOptionsCommand));

    public static readonly StyledProperty<ICommand> DownloadPosterCommandProperty =
        AvaloniaProperty.Register<SeriesCard, ICommand>(nameof(DownloadPosterCommand));

    public static readonly StyledProperty<ICommand> LookForEpisodeCommandProperty =
        AvaloniaProperty.Register<SeriesCard, ICommand>(nameof(LookForEpisodeCommand));

    public static readonly StyledProperty<ICommand> RemoveSeriesCommandProperty =
        AvaloniaProperty.Register<SeriesCard, ICommand>(nameof(RemoveSeriesCommand));

    public static readonly StyledProperty<ICommand> DownloadAllCommandProperty =
        AvaloniaProperty.Register<SeriesCard, ICommand>(nameof(DownloadAllCommand));

    public ICommand OpenDetailCommand
    {
        get => GetValue(OpenDetailCommandProperty);
        set => SetValue(OpenDetailCommandProperty, value);
    }

    public ICommand OpenPosterOptionsCommand
    {
        get => GetValue(OpenPosterOptionsCommandProperty);
        set => SetValue(OpenPosterOptionsCommandProperty, value);
    }

    public ICommand DownloadPosterCommand
    {
        get => GetValue(DownloadPosterCommandProperty);
        set => SetValue(DownloadPosterCommandProperty, value);
    }

    public ICommand LookForEpisodeCommand
    {
        get => GetValue(LookForEpisodeCommandProperty);
        set => SetValue(LookForEpisodeCommandProperty, value);
    }

    public ICommand RemoveSeriesCommand
    {
        get => GetValue(RemoveSeriesCommandProperty);
        set => SetValue(RemoveSeriesCommandProperty, value);
    }

    public ICommand DownloadAllCommand
    {
        get => GetValue(DownloadAllCommandProperty);
        set => SetValue(DownloadAllCommandProperty, value);
    }

    private readonly IThemeService? _themeService;

    public SeriesCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        _themeService = App.Services?.GetService<IThemeService>();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_themeService != null)
        {
            _themeService.ThemeChanged += OnThemeModeChanged;
            ApplyCensorState();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_themeService != null)
            _themeService.ThemeChanged -= OnThemeModeChanged;
    }

    private void OnThemeModeChanged(bool isSecret) => ApplyCensorState();

    private void ApplyCensorState()
    {
        if (DataContext is Series series)
            SetCensorMode((_themeService?.IsSecretMode ?? false) && series.IsCensored);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is Series series)
        {
            ApplyPlaceholderColor(series);
            UpdateCensorSplit(series);
            UpdateAdultBadge(series);
            ApplyCensorState();
        }
    }

    private void ApplyPlaceholderColor(Series series)
    {
        string[] placeholderKeys = { "PosterPlaceholderA", "PosterPlaceholderB", "PosterPlaceholderC", "PosterPlaceholderD", "PosterPlaceholderE", "PosterPlaceholderF" };
        int index = Math.Abs(series.Title.GetHashCode()) % placeholderKeys.Length;
        if (Application.Current!.Resources.TryGetResource(placeholderKeys[index], null, out var brush))
        {
            PosterPlaceholder.Background = brush as IBrush;
        }
    }

    private void UpdateCensorSplit(Series series)
    {
        var title = series.Title ?? string.Empty;
        if (title.Length > 2)
        {
            TitleFirstHalf.Text = title.Substring(0, title.Length / 2);
            TitleSecondHalf.Text = title.Substring(title.Length / 2);
        }
        else
        {
            TitleFirstHalf.Text = title;
            TitleSecondHalf.Text = string.Empty;
        }
    }

    private void UpdateAdultBadge(Series series)
    {
        // Placeholder: nothing marks a Series as adult yet, so AdultBadge stays hidden.
    }

    public void SetCensorMode(bool active)
    {
        CensorBar.IsVisible = active;
        TitleSecondHalf.IsVisible = active;
        
        if (DataContext is Series series)
        {
            UpdateAdultBadge(series);
        }
    }

    public static readonly RoutedEvent<RoutedEventArgs> OpenDetailRequestedEvent =
        RoutedEvent.Register<SeriesCard, RoutedEventArgs>(nameof(OpenDetailRequested), RoutingStrategies.Bubble);

    public event EventHandler<RoutedEventArgs> OpenDetailRequested
    {
        add => AddHandler(OpenDetailRequestedEvent, value);
        remove => RemoveHandler(OpenDetailRequestedEvent, value);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(OpenDetailRequestedEvent));
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        PosterOverlay.Opacity = 1;
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        PosterOverlay.Opacity = 0;
    }

    private void OnPosterButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu != null)
        {
            button.ContextMenu.Open(button);
        }
    }

    // Context-menu click handlers. These bypass the MenuItem-inside-Popup
    // binding-scope problem ($parent[controls:SeriesCard] does not resolve
    // across popup namescopes) by reading the StyledProperty directly from
    // the owning SeriesCard and executing against the current Series.
    private void InvokeCardCommand(ICommand? command)
    {
        if (command == null) return;
        if (DataContext is Series series && command.CanExecute(series))
        {
            command.Execute(series);
        }
    }

    private void OnLookForEpisodeClick(object? sender, RoutedEventArgs e)
        => InvokeCardCommand(LookForEpisodeCommand);

    private void OnDownloadAllClick(object? sender, RoutedEventArgs e)
        => InvokeCardCommand(DownloadAllCommand);

    private void OnRemoveSeriesClick(object? sender, RoutedEventArgs e)
        => InvokeCardCommand(RemoveSeriesCommand);

    private void OnOpenPosterOptionsClick(object? sender, RoutedEventArgs e)
        => InvokeCardCommand(OpenPosterOptionsCommand);

    private void OnDownloadPosterClick(object? sender, RoutedEventArgs e)
        => InvokeCardCommand(DownloadPosterCommand);
}
