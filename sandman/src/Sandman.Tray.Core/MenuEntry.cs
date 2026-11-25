namespace Sandman.Tray.Core;

/// <summary>
/// One line of the tray menu, as plain data: each head turns these into its own menu type.
/// <see cref="Children"/> set = a submenu; <see cref="Separator"/> = a separator line.
/// </summary>
public sealed record MenuEntry(string Label, bool Enabled = true, Action? Click = null, IReadOnlyList<MenuEntry>? Children = null)
{
    public static MenuEntry Separator { get; } = new("-", Enabled: false);

    public bool IsSeparator => ReferenceEquals(this, Separator);
}
