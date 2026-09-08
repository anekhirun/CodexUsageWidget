using System.Drawing;

namespace CodexUsageWidget;

internal static class WidgetPlacement
{
    internal static double MigrateLegacyOffset(double savedLeftDip, double screenLeftPixels, double dipPerPixel)
    {
        if (!double.IsFinite(savedLeftDip) || !double.IsFinite(dipPerPixel) || dipPerPixel <= 0) return 0;
        // Upstream 2.1.2 persisted absolute WPF DIPs, not native pixels.
        return Math.Max(0, savedLeftDip - screenLeftPixels * dipPerPixel);
    }

    // All rectangles use native screen pixels. Only the saved offset uses DIPs.
    internal static Rectangle Calculate(Rectangle screen, Rectangle work, Rectangle? taskbar,
        double offsetDip, double scale, double logicalWidth = 279)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        if (!double.IsFinite(offsetDip)) offsetDip = 0;
        int width = Math.Min(screen.Width, Math.Max(1, (int)Math.Round(logicalWidth * scale)));
        int height = Math.Min(screen.Height, Math.Max(1, (int)Math.Round(48 * scale)));
        int top = screen.Bottom - height;

        if (taskbar is Rectangle bar && bar.Width > bar.Height &&
            bar.Right > screen.Left && bar.Left < screen.Right)
        {
            height = Math.Min(screen.Height, Math.Max(height, bar.Height));
            top = Math.Clamp(bar.Top, screen.Top, screen.Bottom - height);
        }
        else if (work.Top - screen.Top >= height)
        {
            top = screen.Top;
        }

        double offset = Math.Clamp(offsetDip * scale, 0, Math.Max(0, screen.Width - width));
        return new Rectangle(screen.Left + (int)Math.Round(offset), top, width, height);
    }
}
