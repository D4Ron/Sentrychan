using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Sentrychan.UI.Interfaces;
using System;
using System.Threading.Tasks;

namespace Sentrychan.UI.Services;

public class ReactionOverlayService : IReactionOverlayService
{
    private Canvas? _canvas;

    public void SetCanvas(Canvas canvas)
    {
        _canvas = canvas;
    }

    public void ShowReaction(string? username, string reactionType)
    {
        if (_canvas == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            var emoji = reactionType switch
            {
                "heart" => "❤️",
                "fire" => "🔥",
                "wow" => "😮",
                _ => "✨"
            };

            var textBlock = new TextBlock
            {
                Text = emoji,
                FontSize = 32,
                RenderTransform = new TranslateTransform()
            };

            var random = new Random();
            double x = random.NextDouble() * (_canvas.Bounds.Width - 40);
            double y = _canvas.Bounds.Height - 40;

            Canvas.SetLeft(textBlock, x);
            Canvas.SetTop(textBlock, y);

            _canvas.Children.Add(textBlock);

            // Animate up and fade out
            AnimateReaction(textBlock);
        });
    }

    private async void AnimateReaction(TextBlock textBlock)
    {
        double y = Canvas.GetTop(textBlock);
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(50);
            y -= 4;
            textBlock.Opacity -= 0.025;
            Canvas.SetTop(textBlock, y);
            if (textBlock.Opacity <= 0) break;
        }

        _canvas?.Children.Remove(textBlock);
    }
}
