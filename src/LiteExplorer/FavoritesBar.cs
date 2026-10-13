namespace LiteExplorer;

/// <summary>
/// Zone contenant une ou plusieurs barres de favoris empilées.
/// La première barre visible est la barre « par défaut » (Ctrl+D, Ctrl+1..9).
/// </summary>
internal sealed class FavoritesArea : Panel
{
    public List<FavoriteBarData> Bars { get; }

    /// <summary>(chemin, nouvel onglet)</summary>
    public event Action<string, bool>? OpenRequested;
    public event Action<string>? OpenInOtherRequested;
    public Func<string?>? CurrentPathProvider;

    public DrivesBarData DrivesBar { get; }
    readonly System.Windows.Forms.Timer driveWatch = new() { Interval = 3000 };
    string driveSignature = "";

    public FavoritesArea(List<FavoriteBarData> bars, DrivesBarData drives)
    {
        Bars = bars;
        DrivesBar = drives;
        Dock = DockStyle.Top;
        // Détecte l'arrivée d'une clé USB ou d'un lecteur mappé (vérification très légère).
        driveWatch.Tick += (_, _) =>
        {
            var sig = Drives.Signature();
            if (sig != driveSignature) Rebuild();
        };
        RebuildNow();
    }

    bool rebuildPending;

    public FavoriteBarData? DefaultBar => Bars.FirstOrDefault(b => b.Visible) ?? Bars.FirstOrDefault();

    /// <summary>
    /// Reconstruit les barres. Différé : on est souvent appelé depuis un clic sur
    /// une barre qu'on va justement détruire.
    /// </summary>
    public void Rebuild()
    {
        if (!IsHandleCreated) { RebuildNow(); return; }
        if (rebuildPending) return;
        rebuildPending = true;
        BeginInvoke(new Action(() => { rebuildPending = false; RebuildNow(); }));
    }

    void RebuildNow()
    {
        SuspendLayout();
        var old = Controls.Cast<Control>().ToList();
        Controls.Clear();
        foreach (var c in old) c.Dispose();

        var visible = Bars.Where(b => b.Visible).ToList();
        int height = 0;
        // Barre des lecteurs : ajoutée en premier, elle se retrouve en bas de la pile.
        if (DrivesBar.Visible)
        {
            driveSignature = Drives.Signature();
            var drives = new DrivesBar(this, DrivesBar) { Dock = DockStyle.Top };
            Controls.Add(drives);
            height += drives.GetPreferredSize(Size.Empty).Height;
        }
        driveWatch.Enabled = DrivesBar.Visible;
        // Les contrôles ancrés en haut s'empilent à l'envers de l'ordre d'ajout.
        for (int i = visible.Count - 1; i >= 0; i--)
        {
            var bar = new FavoritesBar(this, visible[i], isDefault: i == 0) { Dock = DockStyle.Top };
            Controls.Add(bar);
            height += bar.GetPreferredSize(Size.Empty).Height;
        }
        Height = height;
        Visible = height > 0;
        ResumeLayout();
    }

    internal void Open(string path, bool newTab) => OpenRequested?.Invoke(path, newTab);
    internal void OpenInOther(string path) => OpenInOtherRequested?.Invoke(path);
    internal string? CurrentPath => CurrentPathProvider?.Invoke();

    public void AddFavorite(string path)
    {
        var bar = DefaultBar;
        if (bar == null) { bar = new FavoriteBarData(); Bars.Add(bar); }
        AddToBar(bar, path);
    }

    public void AddToBar(FavoriteBarData bar, string path, string? name = null)
    {
        if (bar.Items.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        if (name == null)
        {
            if (path.Length == 0) name = "Ce PC";
            else
            {
                var n = Path.GetFileName(path.TrimEnd('\\'));
                name = n.Length > 0 ? n : path;
            }
        }
        bar.Items.Add(new Favorite { Name = name, Path = path });
        bar.Visible = true;
        Rebuild();
    }

    public void SetDrivesBarVisible(bool visible)
    {
        DrivesBar.Visible = visible;
        Rebuild();
    }

    /// <summary>Ajoute à une barre de favoris tous les lecteurs réseau mappés qui n'y sont pas déjà.</summary>
    public void AddNetworkDrives(FavoriteBarData bar)
    {
        var drives = Drives.List(networkOnly: true);
        if (drives.Count == 0)
        {
            MessageBox.Show(FindForm(), "Aucun lecteur réseau mappé.", Dialogs.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (var d in drives)
            if (!bar.Items.Any(f => FileEntry.PathEquals(f.Path, d.Root)))
                bar.Items.Add(new Favorite { Name = d.Label, Path = d.Root });
        bar.Visible = true;
        Rebuild();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) driveWatch.Dispose();
        base.Dispose(disposing);
    }

    public void OpenAt(int index, bool newTab)
    {
        var items = DefaultBar?.Items;
        if (items != null && index >= 0 && index < items.Count) Open(items[index].Path, newTab);
    }

    public void NewBar()
    {
        var name = Dialogs.InputBox(FindForm()!, "Nouvelle barre de favoris", "Nom de la barre :", $"Favoris {Bars.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        Bars.Add(new FavoriteBarData { Name = name.Trim() });
        Rebuild();
    }

    public void RenameBar(FavoriteBarData bar)
    {
        var name = Dialogs.InputBox(FindForm()!, "Renommer la barre", "Nom de la barre :", bar.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        bar.Name = name.Trim();
        Rebuild();
    }

    public void DeleteBar(FavoriteBarData bar)
    {
        if (Bars.Count <= 1) return;
        if (bar.Items.Count > 0 &&
            MessageBox.Show(FindForm(), $"Supprimer la barre « {bar.Name} » et ses {bar.Items.Count} favori(s) ?",
                Dialogs.AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        Bars.Remove(bar);
        Rebuild();
    }

    public void SetBarVisible(FavoriteBarData bar, bool visible)
    {
        bar.Visible = visible;
        Rebuild();
    }

    public void MoveBar(FavoriteBarData bar, int delta)
    {
        int i = Bars.IndexOf(bar), j = i + delta;
        if (i < 0 || j < 0 || j >= Bars.Count) return;
        (Bars[i], Bars[j]) = (Bars[j], Bars[i]);
        Rebuild();
    }

    public void MoveFavorite(Favorite fav, FavoriteBarData from, FavoriteBarData to)
    {
        from.Items.Remove(fav);
        to.Items.Add(fav);
        to.Visible = true;
        Rebuild();
    }
}

/// <summary>
/// Une barre de favoris : clic = ouvrir, clic milieu (ou Ctrl+clic) = nouvel onglet,
/// clic droit = options. On peut y glisser des dossiers pour les ajouter.
/// </summary>
internal sealed class FavoritesBar : ToolStrip
{
    readonly FavoritesArea area;
    readonly FavoriteBarData data;
    readonly ContextMenuStrip barMenu = new();
    readonly ToolStripMenuItem deleteItem, upItem, downItem;

    public FavoritesBar(FavoritesArea area, FavoriteBarData data, bool isDefault)
    {
        this.area = area;
        this.data = data;
        GripStyle = ToolStripGripStyle.Hidden;
        ImageScalingSize = ShellIcons.List.ImageSize;
        ShowItemToolTips = true;
        AllowDrop = true;
        Padding = new Padding(4, 2, 4, 2);
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragDrop += OnDragDrop;

        // Menu de la barre (bouton à gauche, ou clic droit dans le vide).
        AddBarMenu("Ajouter le dossier courant ici", () => { if (area.CurrentPath is { } p) area.AddToBar(data, p); });
        AddBarMenu("Ajouter les lecteurs réseau mappés ici", () => area.AddNetworkDrives(data));
        barMenu.Items.Add(new ToolStripSeparator());
        AddBarMenu("Nouvelle barre de favoris…", area.NewBar);
        AddBarMenu("Renommer cette barre…", () => area.RenameBar(data));
        upItem = AddBarMenu("Monter cette barre", () => area.MoveBar(data, -1));
        downItem = AddBarMenu("Descendre cette barre", () => area.MoveBar(data, 1));
        AddBarMenu("Masquer cette barre", () => area.SetBarVisible(data, false));
        deleteItem = AddBarMenu("Supprimer cette barre", () => area.DeleteBar(data));
        barMenu.Opening += (_, _) =>
        {
            int i = area.Bars.IndexOf(data);
            deleteItem.Enabled = area.Bars.Count > 1;
            upItem.Enabled = i > 0;
            downItem.Enabled = i >= 0 && i < area.Bars.Count - 1;
        };

        var head = new ToolStripDropDownButton("☆ " + data.Name)
        {
            DropDown = barMenu,
            ToolTipText = isDefault ? "Barre par défaut (Ctrl+D pour y ajouter, Ctrl+1…9 pour ouvrir)" : data.Name,
            Font = new Font(Font, FontStyle.Bold),
        };
        Items.Add(head);
        Items.Add(new ToolStripSeparator());

        for (int i = 0; i < data.Items.Count; i++)
        {
            var fav = data.Items[i];
            var button = new ToolStripButton(fav.Name)
            {
                ToolTipText = (fav.Path.Length == 0 ? "Ce PC" : fav.Path) + (isDefault && i < 9 ? $"   (Ctrl+{i + 1})" : ""),
            };
            int icon = ShellIcons.ForPath(fav.Path);
            if (icon >= 0) button.Image = ShellIcons.List.Images[icon];
            button.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) area.Open(fav.Path, ModifierKeys.HasFlag(Keys.Control));
                else if (e.Button == MouseButtons.Middle) area.Open(fav.Path, true);
                else if (e.Button == MouseButtons.Right) ShowFavoriteMenu(fav, button);
            };
            Items.Add(button);
        }
    }

    ToolStripMenuItem AddBarMenu(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        barMenu.Items.Add(item);
        return item;
    }

    protected override void OnMouseUp(MouseEventArgs mea)
    {
        base.OnMouseUp(mea);
        if (mea.Button == MouseButtons.Right && GetItemAt(mea.Location) == null)
            barMenu.Show(this, mea.Location);
    }

    void ShowFavoriteMenu(Favorite fav, ToolStripItem anchor)
    {
        var menu = new ContextMenuStrip();
        ToolStripMenuItem Item(string text, Action a, bool enabled = true)
        {
            var it = new ToolStripMenuItem(text) { Enabled = enabled };
            it.Click += (_, _) => a();
            menu.Items.Add(it);
            return it;
        }
        int i = data.Items.IndexOf(fav);
        Item("Ouvrir", () => area.Open(fav.Path, false));
        Item("Ouvrir dans un nouvel onglet", () => area.Open(fav.Path, true));
        Item("Ouvrir dans l'autre panneau", () => area.OpenInOther(fav.Path));
        menu.Items.Add(new ToolStripSeparator());
        Item("Renommer…", () =>
        {
            var name = Dialogs.InputBox(FindForm()!, "Renommer le favori", "Nom :", fav.Name);
            if (!string.IsNullOrWhiteSpace(name)) { fav.Name = name.Trim(); area.Rebuild(); }
        });
        Item("Déplacer vers la gauche", () => Swap(i, i - 1), i > 0);
        Item("Déplacer vers la droite", () => Swap(i, i + 1), i < data.Items.Count - 1);
        var moveTo = new ToolStripMenuItem("Déplacer vers la barre");
        foreach (var other in area.Bars.Where(b => b != data))
        {
            var target = other;
            var it = new ToolStripMenuItem(other.Name);
            it.Click += (_, _) => area.MoveFavorite(fav, data, target);
            moveTo.DropDownItems.Add(it);
        }
        moveTo.Enabled = moveTo.DropDownItems.Count > 0;
        menu.Items.Add(moveTo);
        Item("Supprimer", () => { data.Items.Remove(fav); area.Rebuild(); });
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(this, new Point(anchor.Bounds.Left, anchor.Bounds.Bottom));
    }

    void Swap(int a, int b)
    {
        (data.Items[a], data.Items[b]) = (data.Items[b], data.Items[a]);
        area.Rebuild();
    }

    static string[] DroppedFolders(DragEventArgs e) =>
        (e.Data?.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>()).Where(Directory.Exists).ToArray();

    void OnDragOver(object? sender, DragEventArgs e) =>
        e.Effect = DroppedFolders(e).Length > 0 ? DragDropEffects.Link : DragDropEffects.None;

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        foreach (var dir in DroppedFolders(e)) area.AddToBar(data, dir);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) barMenu.Dispose();
        base.Dispose(disposing);
    }
}
