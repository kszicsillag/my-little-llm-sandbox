using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Sandman.Tray.Windows;

/// <summary>
/// The tray icon, drawn at runtime with GDI+ (same design as Sandman.Tray's TrayIconArt): a crescent moon, grey
/// when everything sleeps, amber when sandboxes are running. The running count and open egress are in the tooltip
/// and the menu. HICONs are cached per state and freed on Dispose.
/// </summary>
internal sealed partial class IconArt : IDisposable
{
    private const int Size = 64;
    private readonly Dictionary<bool, nint> _icons = [];

    public nint Get(bool running)
    {
        if (!_icons.TryGetValue(running, out var icon))
        {
            var png = RenderPng(running);
            // PNG-compressed icon images are supported since Vista; size 0/0 = the image's own size.
            icon = CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, 0, 0, 0);
            if (icon == 0) throw new InvalidOperationException($"CreateIconFromResourceEx failed ({Marshal.GetLastPInvokeError()})");
            _icons[running] = icon;
        }
        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values) DestroyIcon(icon);
        _icons.Clear();
    }

    private static byte[] RenderPng(bool running)
    {
        using var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        var moon = running ? Color.FromArgb(0xF5, 0xB7, 0x31) : Color.FromArgb(0x9A, 0xA0, 0xA6);

        // Crescent: disc (center 32,32 r28) minus disc (center 46,22 r26), 4x4 supersampled for smooth edges
        // (GDI+ region subtraction isn't antialiased).
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var hits = 0;
                for (var sy = 0; sy < 4; sy++)
                    for (var sx = 0; sx < 4; sx++)
                    {
                        double px = x + (sx + 0.5) / 4, py = y + (sy + 0.5) / 4;
                        if (Inside(px, py, 32, 32, 28) && !Inside(px, py, 46, 22, 26)) hits++;
                    }
                if (hits > 0) bmp.SetPixel(x, y, Color.FromArgb(hits * 255 / 16, moon));
            }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static bool Inside(double x, double y, double cx, double cy, double r) =>
        (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateIconFromResourceEx(byte[] presbits, uint dwResSize,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon, uint dwVer, int cxDesired, int cyDesired, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);
}
