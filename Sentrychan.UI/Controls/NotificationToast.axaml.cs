using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace Sentrychan.UI.Controls;

public partial class NotificationToast : UserControl
{
    private DispatcherTimer? _dismissTimer;

    public NotificationToast()
    {
        InitializeComponent();
    }

    public void Show(string title, string body)
    {
        TitleText.Text = title;
        BodyText.Text = body;
        TimeText.Text = DateTime.Now.ToString("HH:mm");
        
        ToastRoot.IsVisible = true;
        ToastRoot.Opacity = 1;
        ToastRoot.RenderTransform = new TranslateTransform(0, 0);

        _dismissTimer?.Stop();
        _dismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _dismissTimer.Tick += (s, e) => Dismiss();
        _dismissTimer.Start();
    }

    private async void Dismiss()
    {
        _dismissTimer?.Stop();
        ToastRoot.Opacity = 0;
        await Task.Delay(400);
        ToastRoot.IsVisible = false;
        ToastRoot.RenderTransform = new TranslateTransform(260, 0);
    }
}
