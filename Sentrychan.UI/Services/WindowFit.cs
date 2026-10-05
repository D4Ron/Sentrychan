using Avalonia;
using Avalonia.Controls;
using System;

namespace Sentrychan.UI.Services;

/// <summary>
/// Keeps every window inside the screen's usable area. Several windows have a fixed height that
/// is fine on a desktop monitor but taller than a small laptop screen once the menu bar and Dock
/// are taken off — the bottom (and its buttons) then ends up behind the Dock or off screen.
/// </summary>
public static class WindowFit
{
    private const double Margin = 24;

    public static void Install() =>
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => Fit(w));

    private static void Fit(Window w)
    {
        try
        {
            if (w.WindowState != WindowState.Normal) return;
            var screen = w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary;
            if (screen == null) return;
            var scale = screen.Scaling;
            var area = screen.WorkingArea;
            var maxW = area.Width / scale - Margin * 2;
            var maxH = area.Height / scale - Margin * 2 - 28; // the title bar sits outside the client area
            var size = w.ClientSize;
            if (size.Width <= maxW && size.Height <= maxH) return;

            if (size.Height > maxH) { w.SizeToContent &= ~SizeToContent.Height; w.MinHeight = Math.Min(w.MinHeight, maxH); w.Height = maxH; }
            if (size.Width > maxW) { w.SizeToContent &= ~SizeToContent.Width; w.MinWidth = Math.Min(w.MinWidth, maxW); w.Width = maxW; }
            var x = area.X + (int)((area.Width - w.Width * scale) / 2);
            var y = area.Y + (int)(Margin * scale);
            w.Position = new PixelPoint(x, y);
            Console.WriteLine($"[WindowFit] \"{w.Title}\" was {size.Width:0}x{size.Height:0}, fitted to {w.Width:0}x{w.Height:0} on a {area.Width / scale:0}x{area.Height / scale:0} work area");
        }
        catch { /* layout nicety only */ }
    }
}
