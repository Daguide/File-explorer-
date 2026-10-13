namespace LiteExplorer;

/// <summary>
/// Arborescence des dossiers, chargée à la demande (un niveau à la fois).
/// Elle pilote soit le panneau qui la contient, soit le panneau actif (arbre commun).
/// </summary>
internal sealed class FolderTree : TreeView, IThemed
{
    readonly IExplorerHost host;
    readonly FilePane? ownerPane;
    readonly ContextMenuStrip menu = new();
    TreeNode? pcNode, menuNode;
    bool syncing;

    /// <param name="ownerPane">Panneau piloté ; null = arbre commun qui pilote le panneau actif.</param>
    public FolderTree(IExplorerHost host, FilePane? ownerPane)
    {
        this.host = host;
        this.ownerPane = ownerPane;
        ImageList = ShellIcons.List;
        HideSelection = false;
        ShowLines = false;
        HotTracking = true;
        FullRowSelect = true;
        BorderStyle = BorderStyle.None;
        AllowDrop = true;
        ItemHeight = Math.Max(ItemHeight, ShellIcons.List.ImageSize.Height + 6);
        HandleCreated += (_, _) => ApplyNativeTheme();
        Enter += (_, _) => { if (ownerPane != null) host.SetActivePane(ownerPane); };

        AddMenu("Ouvrir", () => Open(menuNode));
        AddMenu("Ouvrir dans un nouvel onglet", () => { if (PathOf(menuNode) is { } p) Target.AddTab(p).FocusList(); });
        AddMenu("Ouvrir dans l'autre panneau", () => { if (PathOf(menuNode) is { } p) host.OtherPane(Target).Current?.Navigate(p); });
        AddMenu("Ajouter aux favoris", () => { if (PathOf(menuNode) is { } p) host.AddFavorite(p); });
        menu.Items.Add(new ToolStripSeparator());
        AddMenu("Actualiser ce dossier", () => { if (menuNode != null) ReloadNode(menuNode); });
        AddMenu("Propriétés", () =>
        {
            if (PathOf(menuNode) is { Length: > 0 } p) NativeMethods.ShellExec(Handle, p, "properties");
        });

        Build();
    }

    FilePane Target => ownerPane ?? host.ActivePane;

    void AddMenu(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    static string? PathOf(TreeNode? node) => node?.Tag as string;

    // --- Construction ----------------------------------------------------------

    public void Build()
    {
        BeginUpdate();
        Nodes.Clear();

        void Quick(string name, string path)
        {
            if (Directory.Exists(path)) Nodes.Add(MakeNode(name, path, ShellIcons.ForPath(path)));
        }
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Quick("Bureau", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Quick("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Quick("Téléchargements", Path.Combine(user, "Downloads"));
        Quick("Images", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

        int pcIcon = ShellIcons.ForPath("");
        pcNode = new TreeNode("Ce PC") { Tag = "" };
        SetIcon(pcNode, pcIcon);
        foreach (var d in DriveInfo.GetDrives())
            pcNode.Nodes.Add(MakeNode(DriveLabel(d), d.Name, ShellIcons.ForPath(d.Name)));
        Nodes.Add(pcNode);
        pcNode.Expand();
        EndUpdate();
    }

    static string DriveLabel(DriveInfo d)
    {
        var letter = d.Name.TrimEnd('\\');
        try
        {
            // IsReady est rapide sur un disque fixe, mais peut bloquer sur un lecteur CD ou réseau.
            if (d.DriveType == DriveType.Fixed && d.IsReady && d.VolumeLabel.Length > 0)
                return $"{d.VolumeLabel} ({letter})";
        }
        catch { }
        return d.DriveType switch
        {
            DriveType.Fixed => "Disque local",
            DriveType.Removable => "Disque amovible",
            DriveType.Network => "Lecteur réseau",
            DriveType.CDRom => "Lecteur CD/DVD",
            _ => "Lecteur",
        } + $" ({letter})";
    }

    static void SetIcon(TreeNode node, int icon)
    {
        if (icon < 0) icon = ShellIcons.FolderIndex;
        node.ImageIndex = icon;
        node.SelectedImageIndex = icon;
    }

    static TreeNode MakeNode(string name, string path, int icon)
    {
        var node = new TreeNode(name) { Tag = path };
        SetIcon(node, icon);
        node.Nodes.Add(new TreeNode()); // nœud factice : affiche la flèche, remplacé au premier dépliage
        return node;
    }

    static bool IsPlaceholder(TreeNode node) => node.Nodes.Count == 1 && node.Nodes[0].Tag == null;

    void LoadChildren(TreeNode node)
    {
        if (!IsPlaceholder(node) || PathOf(node) is not { Length: > 0 } path) return;
        var dirs = new List<string>();
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = AppState.ShowHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            };
            dirs.AddRange(Directory.EnumerateDirectories(path, "*", options));
        }
        catch { }
        dirs.Sort((a, b) => NativeMethods.StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));

        BeginUpdate();
        node.Nodes.Clear();
        int folder = ShellIcons.FolderIndex;
        foreach (var dir in dirs) node.Nodes.Add(MakeNode(Path.GetFileName(dir), dir, folder));
        EndUpdate();
    }

    void ReloadNode(TreeNode node)
    {
        if (PathOf(node) is not { Length: > 0 }) { Build(); return; }
        bool expanded = node.IsExpanded;
        node.Collapse();
        node.Nodes.Clear();
        node.Nodes.Add(new TreeNode());
        if (expanded) node.Expand();
    }

    protected override void OnBeforeExpand(TreeViewCancelEventArgs e)
    {
        base.OnBeforeExpand(e);
        if (e.Node != null) LoadChildren(e.Node);
    }

    // --- Navigation --------------------------------------------------------------

    void Open(TreeNode? node)
    {
        if (PathOf(node) is { } path) Target.Current?.Navigate(path);
    }

    protected override void OnAfterSelect(TreeViewEventArgs e)
    {
        base.OnAfterSelect(e);
        if (syncing || e.Action == TreeViewAction.Unknown) return; // sélection faite par SyncTo
        Open(e.Node);
    }

    protected override void OnNodeMouseClick(TreeNodeMouseClickEventArgs e)
    {
        base.OnNodeMouseClick(e);
        if (e.Button == MouseButtons.Middle && PathOf(e.Node) is { } path)
        {
            Target.AddTab(path, select: false); // onglet en arrière-plan, comme un fureteur
        }
        else if (e.Button == MouseButtons.Right)
        {
            menuNode = e.Node;
            menu.Show(this, e.Location);
        }
    }

    /// <summary>Déplie l'arbre jusqu'au dossier affiché et le sélectionne.</summary>
    public void SyncTo(string path)
    {
        if (pcNode == null) return;
        syncing = true;
        BeginUpdate();
        try
        {
            TreeNode? node = pcNode;
            if (path.Length > 0)
            {
                var root = Path.GetPathRoot(path) ?? "";
                node = pcNode.Nodes.Cast<TreeNode>().FirstOrDefault(n => FileEntry.PathEquals(PathOf(n), root));
                if (node != null)
                {
                    foreach (var part in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                    {
                        LoadChildren(node);
                        var next = node.Nodes.Cast<TreeNode>()
                            .FirstOrDefault(n => string.Equals(n.Text, part, StringComparison.OrdinalIgnoreCase));
                        if (next == null) break; // dossier caché, réseau, créé depuis… : on s'arrête au parent connu
                        node = next;
                    }
                }
            }
            if (node != null)
            {
                SelectedNode = node;
                node.EnsureVisible();
            }
        }
        finally
        {
            EndUpdate();
            syncing = false;
        }
    }

    // --- Glisser-déposer : déposer des fichiers sur un dossier de l'arbre -------------

    string? DropTarget(DragEventArgs e) =>
        PathOf(GetNodeAt(PointToClient(new Point(e.X, e.Y)))) is { Length: > 0 } p ? p : null;

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        var files = e.Data?.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        var target = DropTarget(e);
        e.Effect = target == null ? DragDropEffects.None : FileOps.DropEffect(files, target, e.KeyState, e.AllowedEffect);
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        OnDragOver(e);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        var files = e.Data?.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        var target = DropTarget(e);
        if (target == null) return;
        var effect = FileOps.DropEffect(files, target, e.KeyState, e.AllowedEffect);
        if (effect != DragDropEffects.None) FileOps.Transfer(FindForm()!, files, target, effect == DragDropEffects.Move);
    }

    // --- Thème -------------------------------------------------------------------

    void ApplyNativeTheme()
    {
        if (IsHandleCreated)
            NativeMethods.SetWindowTheme(Handle, ThemeManager.Current.Dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    public void OnThemeChanged(Theme theme)
    {
        BackColor = theme.Back;
        ForeColor = theme.ListFore;
        ApplyNativeTheme();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) menu.Dispose();
        base.Dispose(disposing);
    }
}
