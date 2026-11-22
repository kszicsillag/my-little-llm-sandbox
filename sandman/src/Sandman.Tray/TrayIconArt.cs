using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Sandman.Tray;

/// <summary>
/// Draws the tray icon at runtime (no binary assets): a crescent moon, grey when everything
/// sleeps, amber when sandboxes are running, with the running count as a badge. Egress open
/// (allow-all) turns the badge red, and shows a red dot when nothing is running.
/// </summary>
internal static class TrayIconArt
{
    private const int Size = 64;

    public static WindowIcon Render(int running, bool egressOpen)
    {
        using var bmp = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext())
        {
            var moon = running > 0 ? Color.FromRgb(0xF5, 0xB7, 0x31) : Color.FromRgb(0x9A, 0xA0, 0xA6);

            // Crescent: full disc minus an offset disc.
            var full = new EllipseGeometry(new Rect(4, 4, 56, 56));
            var bite = new EllipseGeometry(new Rect(20, -4, 52, 52));
            var crescent = new CombinedGeometry(GeometryCombineMode.Exclude, full, bite);
            ctx.DrawGeometry(new SolidColorBrush(moon), null, crescent);

            if (running > 0)
            {
                var badge = egressOpen ? Color.FromRgb(0xD9, 0x30, 0x25) : Color.FromRgb(0x2E, 0xA0, 0x43);
                ctx.DrawEllipse(new SolidColorBrush(badge), null, new Point(46, 46), 17, 17);
                var text = new FormattedText(
                    running > 9 ? "9+" : running.ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                    running > 9 ? 18 : 24,
                    Brushes.White);
                ctx.DrawText(text, new Point(46 - text.Width / 2, 46 - text.Height / 2));
            }
            else if (egressOpen)
            {
                ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xD9, 0x30, 0x25)), null, new Point(48, 48), 12, 12);
            }
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, new PngBitmapEncoderOptions());
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
