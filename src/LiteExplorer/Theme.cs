using Microsoft.Win32;

namespace LiteExplorer;

internal enum ThemeMode { System, Light, Dark }

internal sealed class Theme
{
    public required bool Dark { get; init; }
    public required Color Back { get; init; }    // fond des listes
    public required Color Panel { get; init; }   // barres, onglets inactifs
    public required Color Input { get; init; }   // zones de texte
    public required Color Header { get; init; }  // en-têtes de colonnes
    public required Color Fore { get; init; }
    public required Color Gray { get; init; }    // texte secondaire, fichiers cachés
    public required Color Border { get; init; }
    public required Color Accent { get; init; }
    public required Color Hover { get; init; }
    public required Color Pressed { get; init; }

    public static readonly Theme Light = new()
    {
        Dark = false,
        Back = SystemColors.Window,
        Panel = SystemColors.Control,
        Input = SystemColors.Window,
        Header = SystemColors.Window,
        Fore = SystemColors.WindowText,
        Gray = SystemColors.GrayText,
        Border = SystemColors.ControlDark,
        Accent = SystemColors.Highlight,
        Hover = Color.FromArgb(229, 243, 255),
        Pressed = Color.FromArgb(204, 232, 255),
    };

    public static readonly Theme DarkTheme = new()
    {
        Dark = true,
        Back = Color.FromArgb(32, 32, 32),
        Panel = Color.FromArgb(43, 43, 43),
        Input = Color.FromArgb(52, 52, 52),
        Header = Color.FromArgb(32, 32, 32),
        Fore = Color.FromArgb(232, 232, 232),
        Gray = Color.FromArgb(150, 150, 150),
        Border = Color.FromArgb(70, 70, 70),
        Accent = Color.FromArgb(0, 120, 215),
        Hover = Color.FromArgb(62, 62, 62),
        Pressed = Color.FromArgb(80, 80, 80),
    };
}

/// <summary>Contrôle qui doit faire un travail particulier quand le thème change.</summary>
internal interface IThemed
{
    void OnThemeChanged(Theme theme);
}

internal static class ThemeManager
{
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static Theme Current { get; private set; } = Theme.Light;
    public static event EventHandler? Changed;

    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        Refresh();
    }

    /// <summary>Recalcule le thème (utile en mode « Selon Windows » quand Windows change).</summary>
    public static void Refresh()
    {
        Current = Mode switch
        {
            ThemeMode.Light => Theme.Light,
            ThemeMode.Dark => Theme.DarkTheme,
            _ => WindowsPrefersDark() ? Theme.DarkTheme : Theme.Light,
        };
        ToolStripManager.Renderer = Current.Dark
            ? new DarkToolStripRenderer(Current)
            : new ToolStripProfessionalRenderer(new ProfessionalColorTable { UseSystemColors = true }) { RoundedEdges = false };
        Changed?.Invoke(null, EventArgs.Empty);
    }

    static bool WindowsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>Applique le thème courant à un contrôle et à tous ses enfants.</summary>
    public static void Apply(Control root)
    {
        var t = Current;
        switch (root)
        {
            case ToolStrip:
                break; // dessinée par ToolStripManager.Renderer
            case TextBox tb:
                tb.BackColor = t.Input;
                tb.ForeColor = t.Fore;
                break;
            case ListView lv:
                lv.BackColor = t.Back;
                lv.ForeColor = t.Fore;
                break;
            case Button b:
                b.BackColor = t.Panel;
                b.ForeColor = t.Fore;
                b.FlatAppearance.BorderColor = t.Border;
                b.FlatAppearance.MouseOverBackColor = t.Hover;
                b.FlatAppearance.MouseDownBackColor = t.Pressed;
                break;
            default:
                root.BackColor = t.Panel;
                root.ForeColor = t.Fore;
                break;
        }
        foreach (Control child in root.Controls) Apply(child);
        if (root is IThemed themed) themed.OnThemeChanged(t);
        root.Invalidate();
    }
}

sealed class DarkColorTable : ProfessionalColorTable
{
    readonly Theme t;
    public DarkColorTable(Theme theme) { t = theme; UseSystemColors = false; }

    public override Color ToolStripGradientBegin => t.Panel;
    public override Color ToolStripGradientMiddle => t.Panel;
    public override Color ToolStripGradientEnd => t.Panel;
    public override Color ToolStripBorder => t.Panel;
    public override Color ToolStripDropDownBackground => t.Panel;
    public override Color MenuStripGradientBegin => t.Panel;
    public override Color MenuStripGradientEnd => t.Panel;
    public override Color MenuBorder => t.Border;
    public override Color MenuItemBorder => t.Border;
    public override Color MenuItemSelected => t.Hover;
    public override Color MenuItemSelectedGradientBegin => t.Hover;
    public override Color MenuItemSelectedGradientEnd => t.Hover;
    public override Color MenuItemPressedGradientBegin => t.Pressed;
    public override Color MenuItemPressedGradientMiddle => t.Pressed;
    public override Color MenuItemPressedGradientEnd => t.Pressed;
    public override Color ImageMarginGradientBegin => t.Panel;
    public override Color ImageMarginGradientMiddle => t.Panel;
    public override Color ImageMarginGradientEnd => t.Panel;
    public override Color ButtonSelectedHighlight => t.Hover;
    public override Color ButtonSelectedGradientBegin => t.Hover;
    public override Color ButtonSelectedGradientMiddle => t.Hover;
    public override Color ButtonSelectedGradientEnd => t.Hover;
    public override Color ButtonSelectedBorder => t.Border;
    public override Color ButtonPressedHighlight => t.Pressed;
    public override Color ButtonPressedGradientBegin => t.Pressed;
    public override Color ButtonPressedGradientMiddle => t.Pressed;
    public override Color ButtonPressedGradientEnd => t.Pressed;
    public override Color ButtonPressedBorder => t.Border;
    public override Color ButtonCheckedHighlight => t.Hover;
    public override Color ButtonCheckedGradientBegin => t.Hover;
    public override Color ButtonCheckedGradientMiddle => t.Hover;
    public override Color ButtonCheckedGradientEnd => t.Hover;
    public override Color CheckBackground => t.Hover;
    public override Color CheckSelectedBackground => t.Pressed;
    public override Color CheckPressedBackground => t.Pressed;
    public override Color SeparatorDark => t.Border;
    public override Color SeparatorLight => t.Panel;
    public override Color OverflowButtonGradientBegin => t.Panel;
    public override Color OverflowButtonGradientMiddle => t.Panel;
    public override Color OverflowButtonGradientEnd => t.Panel;
    public override Color GripDark => t.Border;
    public override Color GripLight => t.Panel;
}

sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
{
    readonly Theme t;
    public DarkToolStripRenderer(Theme theme) : base(new DarkColorTable(theme)) { t = theme; RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? t.Fore : t.Gray;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = t.Fore;
        base.OnRenderArrow(e);
    }

    // La coche par défaut est noire, donc invisible sur fond sombre.
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        using (var b = new SolidBrush(t.Pressed)) e.Graphics.FillRectangle(b, r);
        TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, r, t.Fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
