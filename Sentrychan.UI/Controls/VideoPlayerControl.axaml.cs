using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LibVLCSharp.Avalonia;

namespace Sentrychan.UI.Controls;

public partial class VideoPlayerControl : UserControl
{
    public VideoView View => this.FindControl<VideoView>("VideoView") ?? throw new System.Exception("VideoView not found");

    public VideoPlayerControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
