using H.NotifyIcon.Core;
using Sandman.Tray.Core;

namespace Sandman.Tray.Windows;

internal static class Program
{
    public static void Main()
    {
        var ui = new SerialContext();
        using var icons = new IconArt();
        using var controller = new TrayController(ui);

        // The GUID identifies the icon to Windows (and its "show on taskbar" setting). Windows binds it to the exe
        // path, so derive it from the path: a dev build and an installed copy don't collide.
        using var tray = new TrayIconWithContextMenu(TrayIcon.CreateUniqueGuidForProcessPath("Sandman"))
        {
            ContextMenu = new PopupMenu(),
        };
        tray.UpdateIcon(icons.Get(0, false));
        tray.Create();
        tray.UpdateToolTip("Sandman");

        // Rebuilt on every change; replacing it doesn't affect a menu that's open (the native menu is built
        // when it opens).
        void Update()
        {
            tray.UpdateIcon(icons.Get(controller.RunningCount, controller.AnyEgressOpen));
            var tip = controller.ToolTip;
            tray.UpdateToolTip(tip.Length <= 127 ? tip : tip[..126] + "…");   // NOTIFYICONDATA.szTip: 128 chars
            var menu = new PopupMenu();
            Fill(menu.Items, controller.BuildMenu(nested: true, ui.Complete));
            tray.ContextMenu = menu;
        }

        controller.Changed += Update;
        ui.Post(_ => Update(), null);
        controller.Start();
        ui.Run();
    }

    private static void Fill(ICollection<PopupItem> items, IEnumerable<MenuEntry> entries)
    {
        foreach (var e in entries)
        {
            if (e.IsSeparator) { items.Add(new PopupMenuSeparator()); continue; }
            var label = e.Label.Replace("&", "&&");   // '&' marks a mnemonic in Win32 menus
            if (e.Children is { } children)
            {
                var sub = new PopupSubMenu(label);
                Fill(sub.Items, children);
                items.Add(sub);
                continue;
            }
            var click = e.Click;
            items.Add(new PopupMenuItem(label, (_, _) => click?.Invoke()) { Enabled = e.Enabled });
        }
    }
}
