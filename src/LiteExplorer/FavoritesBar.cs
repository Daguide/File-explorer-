namespace LiteExplorer;

/// <summary>
/// Barre de favoris façon fureteur : clic = ouvrir, clic milieu (ou Ctrl+clic) = nouvel onglet,
/// clic droit = options. On peut y glisser des dossiers pour les ajouter.
/// </summary>
internal sealed class FavoritesBar : ToolStrip
{
    public List<Favorite> Favorites { get; }

    /// <summary>(chemin, nouvel onglet)</summary>
    public event Action<string, bool>? OpenRequested;
    public event Action<string>? OpenInOtherRequested;
    public Func<string?>? CurrentPathProvider;

    public FavoritesBar(List<Favorite> favorites)
    {
        Favorites = favorites;
        GripStyle = ToolStripGripStyle.Hidden;
        ImageScalingSize = ShellIcons.List.ImageSize;
        ShowItemToolTips = true;
        AllowDrop = true;
        Padding = new Padding(4, 2, 4, 2);
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragDrop += OnDragDrop;
        Rebuild();
    }

    public void Rebuild()
    {
        SuspendLayout();
        Items.Clear();
        var add = new ToolStripButton("☆ Ajouter") { ToolTipText = "Ajouter le dossier courant aux favoris (Ctrl+D)" };
        add.Click += (_, _) => { if (CurrentPathProvider?.Invoke() is { } p) Add(p); };
        Items.Add(add);
        Items.Add(new ToolStripSeparator());

        for (int i = 0; i < Favorites.Count; i++)
        {
            var fav = Favorites[i];
            var button = new ToolStripButton(fav.Name)
            {
                ToolTipText = (fav.Path.Length == 0 ? "Ce PC" : fav.Path) + (i < 9 ? $"   (Ctrl+{i + 1})" : ""),
            };
            int icon = ShellIcons.ForPath(fav.Path);
            if (icon >= 0) button.Image = ShellIcons.List.Images[icon];
            button.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) OpenRequested?.Invoke(fav.Path, ModifierKeys.HasFlag(Keys.Control));
                else if (e.Button == MouseButtons.Middle) OpenRequested?.Invoke(fav.Path, true);
                else if (e.Button == MouseButtons.Right) ShowMenu(fav, button);
            };
            Items.Add(button);
        }
        ResumeLayout();
    }

    public void Add(string path, string? name = null)
    {
        if (Favorites.Any(f => FileEntry.PathEquals(f.Path, path) && f.Path.Length == path.Length)) return;
        if (name == null)
        {
            if (path.Length == 0) name = "Ce PC";
            else
            {
                var n = Path.GetFileName(path.TrimEnd('\\'));
                name = n.Length > 0 ? n : path;
            }
        }
        Favorites.Add(new Favorite { Name = name, Path = path });
        Rebuild();
    }

    public void OpenAt(int index, bool newTab)
    {
        if (index >= 0 && index < Favorites.Count) OpenRequested?.Invoke(Favorites[index].Path, newTab);
    }

    void ShowMenu(Favorite fav, ToolStripItem anchor)
    {
        var menu = new ContextMenuStrip();
        void Item(string text, Action a, bool enabled = true)
        {
            var it = new ToolStripMenuItem(text) { Enabled = enabled };
            it.Click += (_, _) => a();
            menu.Items.Add(it);
        }
        int i = Favorites.IndexOf(fav);
        Item("Ouvrir", () => OpenRequested?.Invoke(fav.Path, false));
        Item("Ouvrir dans un nouvel onglet", () => OpenRequested?.Invoke(fav.Path, true));
        Item("Ouvrir dans l'autre panneau", () => OpenInOtherRequested?.Invoke(fav.Path));
        menu.Items.Add(new ToolStripSeparator());
        Item("Renommer…", () =>
        {
            var name = Dialogs.InputBox(FindForm()!, "Renommer le favori", "Nom :", fav.Name);
            if (!string.IsNullOrWhiteSpace(name)) { fav.Name = name.Trim(); Rebuild(); }
        });
        Item("Déplacer vers la gauche", () => Swap(i, i - 1), i > 0);
        Item("Déplacer vers la droite", () => Swap(i, i + 1), i < Favorites.Count - 1);
        Item("Supprimer", () => { Favorites.Remove(fav); Rebuild(); });
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(this, new Point(anchor.Bounds.Left, anchor.Bounds.Bottom));
    }

    void Swap(int a, int b)
    {
        (Favorites[a], Favorites[b]) = (Favorites[b], Favorites[a]);
        Rebuild();
    }

    static string[] DroppedFolders(DragEventArgs e) =>
        (e.Data?.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>()).Where(Directory.Exists).ToArray();

    void OnDragOver(object? sender, DragEventArgs e) =>
        e.Effect = DroppedFolders(e).Length > 0 ? DragDropEffects.Link : DragDropEffects.None;

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        foreach (var dir in DroppedFolders(e)) Add(dir);
    }
}
