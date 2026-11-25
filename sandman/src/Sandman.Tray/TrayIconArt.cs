using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Sandman.Tray;

/// <summary>
/// Draws the tray icon at runtime (no binary assets): a crescent moon, grey when everything
/// sleeps, amber when sandboxes are running. The running count and open egress are in the
/// tooltip and the menu.
/// </summary>
internal static class TrayIconArt
{
    private const int Size = 64;

    public static WindowIcon Render(bool running)
    {
        using var bmp = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext())
        {
            var moon = running ? Color.FromRgb(0xF5, 0xB7, 0x31) : Color.FromRgb(0x9A, 0xA0, 0xA6);

            // Crescent: full disc minus an offset disc.
            var full = new EllipseGeometry(new Rect(4, 4, 56, 56));
            var bite = new EllipseGeometry(new Rect(20, -4, 52, 52));
            var crescent = new CombinedGeometry(GeometryCombineMode.Exclude, full, bite);
            ctx.DrawGeometry(new SolidColorBrush(moon), null, crescent);

        }

        using var ms = new MemoryStream();
        bmp.Save(ms, new PngBitmapEncoderOptions());
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
