using System.Runtime.InteropServices;
using System.Text;

namespace LiteExplorer;

internal sealed record DriveItem(string Root, string Label, string ToolTip, bool IsNetwork);

internal static class Drives
{
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    /// <summary>Chemin UNC d'un lecteur mappé (ex. Z: → \\serveur\partage), ou null.</summary>
    public static string? UncPath(string root)
    {
        var sb = new StringBuilder(1024);
        int len = sb.Capacity;
        return WNetGetConnection(root.TrimEnd('\\'), sb, ref len) == 0 ? sb.ToString() : null;
    }

    public static List<DriveItem> List(bool networkOnly)
    {
        var result = new List<DriveItem>();
        foreach (var d in DriveInfo.GetDrives())
        {
            bool network = d.DriveType == DriveType.Network;
            if (networkOnly && !network) continue;
            var letter = d.Name.TrimEnd('\\');
            if (network)
            {
                var unc = UncPath(d.Name);
                var share = unc?.TrimEnd('\\').Split('\\').LastOrDefault(s => s.Length > 0) ?? "Réseau";
                result.Add(new DriveItem(d.Name, $"{letter} {share}", unc ?? "Lecteur réseau", true));
                continue;
            }
            string name = d.DriveType switch
            {
                DriveType.Removable => "Amovible",
                DriveType.CDRom => "CD/DVD",
                DriveType.Ram => "RAM",
                _ => "Disque local",
            };
            try
            {
                // IsReady est rapide sur un disque fixe ; on l'évite sur CD/amovible.
                if (d.DriveType == DriveType.Fixed && d.IsReady && d.VolumeLabel.Length > 0) name = d.VolumeLabel;
            }
            catch { }
            result.Add(new DriveItem(d.Name, $"{letter} {name}", d.Name, false));
        }
        return result;
    }

    /// <summary>Signature bon marché de la liste des lecteurs, pour détecter un ajout ou un retrait.</summary>
    public static string Signature() => string.Join("|", Environment.GetLogicalDrives());
}

/// <summary>Barre générée automatiquement : un bouton par lecteur (tous, ou seulement les lecteurs réseau).</summary>
internal sealed class DrivesBar : ToolStrip
{
    readonly FavoritesArea area;
    readonly ContextMenuStrip barMenu = new();

    public DrivesBar(FavoritesArea area, DrivesBarData data)
    {
        this.area = area;
        GripStyle = ToolStripGripStyle.Hidden;
        ImageScalingSize = ShellIcons.List.ImageSize;
        ShowItemToolTips = true;
        Padding = new Padding(4, 2, 4, 2);

        var networkOnly = new ToolStripMenuItem("Lecteurs réseau seulement") { Checked = data.NetworkOnly };
        networkOnly.Click += (_, _) => { data.NetworkOnly = !data.NetworkOnly; area.Rebuild(); };
        barMenu.Items.Add(networkOnly);
        var refresh = new ToolStripMenuItem("Actualiser");
        refresh.Click += (_, _) => area.Rebuild();
        barMenu.Items.Add(refresh);
        barMenu.Items.Add(new ToolStripSeparator());
        barMenu.Items.Add(AddToBarMenu("Ajouter les lecteurs réseau à la barre",
            bar => area.AddNetworkDrives(bar)));
        barMenu.Items.Add(new ToolStripSeparator());
        var hide = new ToolStripMenuItem("Masquer la barre des lecteurs");
        hide.Click += (_, _) => { data.Visible = false; area.Rebuild(); };
        barMenu.Items.Add(hide);

        int pcIcon = ShellIcons.ForPath("");
        Items.Add(new ToolStripDropDownButton(data.NetworkOnly ? "Lecteurs réseau" : "Lecteurs")
        {
            Image = pcIcon >= 0 ? ShellIcons.List.Images[pcIcon] : null,
            DropDown = barMenu,
            Font = new Font(Font, FontStyle.Bold),
            ToolTipText = "Lecteurs détectés automatiquement (mis à jour quand un lecteur apparaît ou disparaît)",
        });
        Items.Add(new ToolStripSeparator());

        var drives = Drives.List(data.NetworkOnly);
        if (drives.Count == 0)
            Items.Add(new ToolStripLabel(data.NetworkOnly ? "(aucun lecteur réseau mappé)" : "(aucun lecteur)") { Enabled = false });

        foreach (var drive in drives)
        {
            var button = new ToolStripButton(drive.Label) { ToolTipText = drive.ToolTip };
            int icon = ShellIcons.ForPath(drive.Root);
            if (icon >= 0) button.Image = ShellIcons.List.Images[icon];
            button.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) area.Open(drive.Root, ModifierKeys.HasFlag(Keys.Control));
                else if (e.Button == MouseButtons.Middle) area.Open(drive.Root, true);
                else if (e.Button == MouseButtons.Right) ShowDriveMenu(drive, button);
            };
            Items.Add(button);
        }
    }

    /// <summary>Sous-menu « … ▸ » listant les barres de favoris.</summary>
    ToolStripMenuItem AddToBarMenu(string text, Action<FavoriteBarData> action)
    {
        var menu = new ToolStripMenuItem(text);
        foreach (var bar in area.Bars)
        {
            var target = bar;
            var it = new ToolStripMenuItem(bar.Name);
            it.Click += (_, _) => action(target);
            menu.DropDownItems.Add(it);
        }
        return menu;
    }

    protected override void OnMouseUp(MouseEventArgs mea)
    {
        base.OnMouseUp(mea);
        if (mea.Button == MouseButtons.Right && GetItemAt(mea.Location) == null)
            barMenu.Show(this, mea.Location);
    }

    void ShowDriveMenu(DriveItem drive, ToolStripItem anchor)
    {
        var menu = new ContextMenuStrip();
        void Item(string text, Action a)
        {
            var it = new ToolStripMenuItem(text);
            it.Click += (_, _) => a();
            menu.Items.Add(it);
        }
        Item("Ouvrir", () => area.Open(drive.Root, false));
        Item("Ouvrir dans un nouvel onglet", () => area.Open(drive.Root, true));
        Item("Ouvrir dans l'autre panneau", () => area.OpenInOther(drive.Root));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(AddToBarMenu("Ajouter à la barre", bar => area.AddToBar(bar, drive.Root, drive.Label)));
        if (drive.IsNetwork)
        {
            Item("Copier le chemin réseau (UNC)", () =>
            {
                try { Clipboard.SetText(drive.ToolTip); } catch (ExternalException) { }
            });
        }
        Item("Propriétés", () => NativeMethods.ShellExec(Handle, drive.Root, "properties"));
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(this, new Point(anchor.Bounds.Left, anchor.Bounds.Bottom));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) barMenu.Dispose();
        base.Dispose(disposing);
    }
}
