using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Sandman.Tray.Core;

namespace Sandman.Tray;

/// <summary>
/// Avalonia head (Linux, macOS): tray icon + NativeMenu over <see cref="TrayController"/>.
/// On Windows use Sandman.Tray.Windows instead (native Win32 menu, no Avalonia).
///
/// The menu is flat: Avalonia draws the Windows tray menu itself, in a popup that closes when it loses focus,
/// and a submenu opens as its own window and takes the focus. It's only replaced when its content changes,
/// so the periodic refresh doesn't close a menu you're looking at.
/// </summary>
public sealed class App : Application
{
    private TrayIcon _tray = null!;
    private TrayController _controller = null!;
    private bool? _lastIcon;
    private string? _menuSignature;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        _tray = new TrayIcon { Icon = TrayIconArt.Render(false), ToolTipText = "Sandman", IsVisible = true };
        TrayIcon.SetIcons(this, [_tray]);

        _controller = new TrayController(SynchronizationContext.Current ?? new AvaloniaSynchronizationContext());
        _controller.Changed += UpdateUi;
        UpdateUi();
        _controller.Start();
        base.OnFrameworkInitializationCompleted();
    }

    private void UpdateUi()
    {
        var icon = _controller.RunningCount > 0;
        if (_lastIcon != icon)
        {
            _tray.Icon = TrayIconArt.Render(icon);
            _lastIcon = icon;
        }
        _tray.ToolTipText = _controller.ToolTip;

        var entries = _controller.BuildMenu(nested: false, Quit);
        var signature = string.Join("\n", entries.Select(e => $"{e.Enabled}|{e.Label}"));
        if (signature == _menuSignature) return;
        _menuSignature = signature;

        var menu = new NativeMenu();
        foreach (var e in entries)
        {
            if (e.IsSeparator) { menu.Add(new NativeMenuItemSeparator()); continue; }
            var item = new NativeMenuItem(e.Label) { IsEnabled = e.Enabled };
            if (e.Click is { } click) item.Click += (_, _) => click();
            menu.Add(item);
        }
        _tray.Menu = menu;
    }

    private void Quit()
    {
        _controller.Dispose();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
