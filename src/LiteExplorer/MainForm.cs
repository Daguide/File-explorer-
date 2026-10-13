using Microsoft.Win32;

namespace LiteExplorer;

internal enum TreeMode { None, Shared, PerPane }

internal sealed class MainForm : Form, IExplorerHost, IThemed
{
    readonly AppSettings settings;
    readonly string[] args;
    readonly FavoritesArea favorites;
    readonly SplitContainer split;
    // [arbre commun (optionnel)] | [panneau gauche | panneau droit]
    readonly SplitContainer treeSplit = new()
    {
        Dock = DockStyle.Fill,
        FixedPanel = FixedPanel.Panel1,
        Panel1Collapsed = true,
        SplitterWidth = 5,
    };
    FolderTree? sharedTree;
    TreeMode treeMode = TreeMode.None;
    readonly FilePane left, right;
    FilePane active;

    public MainForm(string[] args)
    {
        this.args = args;
        settings = AppSettings.Load();
        AppState.ShowHidden = settings.ShowHidden;
        ThemeManager.SetMode(Enum.TryParse<ThemeMode>(settings.Theme, out var mode) ? mode : ThemeMode.System);

        Text = Dialogs.AppName;
        Font = SystemFonts.MessageBoxFont ?? Font;
        MinimumSize = new Size(640, 400);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;

        left = new FilePane(this) { Dock = DockStyle.Fill };
        right = new FilePane(this) { Dock = DockStyle.Fill };
        active = left;

        favorites = new FavoritesArea(settings.FavoriteBars);
        favorites.OpenRequested += (path, newTab) =>
        {
            if (newTab) active.AddTab(path, select: !ModifierKeys.HasFlag(Keys.Shift));
            else active.Current?.Navigate(path);
            active.Current?.FocusList();
        };
        favorites.OpenInOtherRequested += path => OtherPane(active).Current?.Navigate(path);
        favorites.CurrentPathProvider = () => active.Current?.CurrentPath;

        split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        split.DoubleClick += (_, _) => split.SplitterDistance = split.Width / 2; // double-clic = 50/50

        treeSplit.Panel2.Controls.Add(split);
        Controls.Add(treeSplit);
        Controls.Add(BuildFunctionBar());
        Controls.Add(favorites);
        Controls.Add(BuildMenu());

        ThemeManager.Apply(this);
        ThemeManager.Changed += OnThemeManagerChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        RestoreWindow();
    }

    FolderView? V => active.Current;

    Control BuildFunctionBar()
    {
        (string text, Action action)[] defs =
        {
            ("F2 Renommer", () => V?.BeginRename()),
            ("F5 Copier →", () => V?.TransferToOther(false)),
            ("F6 Déplacer →", () => V?.TransferToOther(true)),
            ("F7 Nouveau dossier", () => V?.NewFolder()),
            ("F8 Supprimer", () => V?.DeleteSelected(true)),
            ("Ctrl+T Onglet", () => active.AddTab(V?.CurrentPath ?? "").FocusList()),
            ("Ctrl+H Cachés", ToggleHidden),
            ("Terminal", () => V?.OpenTerminal()),
        };
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = Font.Height + 14,
            ColumnCount = defs.Length,
            RowCount = 1,
            Padding = new Padding(2),
        };
        foreach (var (text, action) in defs)
        {
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / defs.Length));
            var b = new Button { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, TabStop = false, Margin = new Padding(1) };
            b.Click += (_, _) => { V?.FocusList(); action(); };
            bar.Controls.Add(b);
        }
        return bar;
    }

    MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { Dock = DockStyle.Top, Padding = new Padding(4, 2, 0, 2) };
        MainMenuStrip = menu;

        static ToolStripMenuItem Item(string text, string? keys, Action action)
        {
            var it = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys };
            it.Click += (_, _) => action();
            return it;
        }

        var file = new ToolStripMenuItem("&Fichier");
        file.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("Nouvel onglet", "Ctrl+T", () => active.AddTab(V?.CurrentPath ?? "").FocusList()),
            Item("Rouvrir l'onglet fermé", "Ctrl+Maj+T", () => active.ReopenClosed()),
            Item("Fermer l'onglet", "Ctrl+W", () => active.CloseCurrent()),
            new ToolStripSeparator(),
            Item("Nouveau dossier", "F7", () => V?.NewFolder()),
            Item("Ouvrir un terminal ici", null, () => V?.OpenTerminal()),
            new ToolStripSeparator(),
            Item("Quitter", "Alt+F4", Close),
        });

        var themeMenu = new ToolStripMenuItem("&Thème");
        foreach (var (label, m) in new[] { ("Selon Windows", ThemeMode.System), ("Clair", ThemeMode.Light), ("Sombre", ThemeMode.Dark) })
        {
            var it = new ToolStripMenuItem(label) { Tag = m };
            it.Click += (_, _) => ThemeManager.SetMode(m);
            themeMenu.DropDownItems.Add(it);
        }
        themeMenu.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem it in themeMenu.DropDownItems)
                it.Checked = (ThemeMode)it.Tag! == ThemeManager.Mode;
        };

        var hidden = Item("Fichiers cachés", "Ctrl+H", ToggleHidden);
        var treeMenu = new ToolStripMenuItem("&Arborescence");
        foreach (var (label, m) in new[]
                 {
                     ("Masquée", TreeMode.None),
                     ("Une seule, à gauche des panneaux", TreeMode.Shared),
                     ("Une par panneau", TreeMode.PerPane),
                 })
        {
            var it = new ToolStripMenuItem(label) { Tag = m };
            it.Click += (_, _) => SetTreeMode(m);
            treeMenu.DropDownItems.Add(it);
        }
        treeMenu.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem it in treeMenu.DropDownItems)
                it.Checked = (TreeMode)it.Tag! == treeMode;
        };

        var view = new ToolStripMenuItem("&Affichage");
        view.DropDownItems.AddRange(new ToolStripItem[]
        {
            themeMenu,
            treeMenu,
            new ToolStripSeparator(),
            hidden,
            Item("Échanger les panneaux", "Ctrl+U", SwapPanes),
            Item("Actualiser", "Ctrl+R", () => V?.ReloadView()),
        });
        view.DropDownOpening += (_, _) => hidden.Checked = AppState.ShowHidden;

        var favMenu = new ToolStripMenuItem("F&avoris");
        favMenu.DropDownOpening += (_, _) =>
        {
            favMenu.DropDownItems.Clear();
            favMenu.DropDownItems.Add(Item("Ajouter le dossier courant", "Ctrl+D", () => { if (V != null) AddFavorite(V.CurrentPath); }));
            favMenu.DropDownItems.Add(Item("Nouvelle barre de favoris…", null, favorites.NewBar));
            favMenu.DropDownItems.Add(new ToolStripSeparator());
            favMenu.DropDownItems.Add(new ToolStripMenuItem("Barres affichées :") { Enabled = false });
            foreach (var bar in favorites.Bars)
            {
                var b = bar;
                var it = new ToolStripMenuItem(bar.Name) { Checked = bar.Visible };
                it.Click += (_, _) => favorites.SetBarVisible(b, !b.Visible);
                favMenu.DropDownItems.Add(it);
            }
        };
        favMenu.DropDownItems.Add(new ToolStripMenuItem("…")); // sinon le menu vide ne s'ouvre pas

        menu.Items.AddRange(new ToolStripItem[] { file, view, favMenu });
        return menu;
    }

    // --- Thème ----------------------------------------------------------------

    void OnThemeManagerChanged(object? sender, EventArgs e) => ThemeManager.Apply(this);

    public void OnThemeChanged(Theme theme)
    {
        split.BackColor = theme.Border; // couleur du séparateur
        treeSplit.BackColor = theme.Border;
        if (IsHandleCreated) NativeMethods.SetDarkTitleBar(Handle, theme.Dark);
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (ThemeManager.Mode != ThemeMode.System || e.Category != UserPreferenceCategory.General) return;
        if (IsHandleCreated) BeginInvoke(new Action(ThemeManager.Refresh));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetDarkTitleBar(Handle, ThemeManager.Current.Dark);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        ThemeManager.Changed -= OnThemeManagerChanged;
        base.OnFormClosed(e);
    }

    // --- IExplorerHost -------------------------------------------------------

    public FilePane ActivePane => active;
    public FilePane OtherPane(FilePane pane) => pane == left ? right : left;

    public void SetActivePane(FilePane pane)
    {
        if (active == pane && pane.IsActive) return;
        active = pane;
        left.IsActive = pane == left;
        right.IsActive = pane == right;
        if (pane.Current is { } v) ViewPathChanged(v);
    }

    public void AddFavorite(string path) => favorites.AddFavorite(path);

    public void ToggleHidden()
    {
        AppState.ShowHidden = !AppState.ShowHidden;
        foreach (var v in left.Views.Concat(right.Views)) v.ReloadView();
        sharedTree?.Build();
        left.RebuildTree();
        right.RebuildTree();
        if (V != null) sharedTree?.SyncTo(V.CurrentPath);
    }

    public void ViewPathChanged(FolderView view)
    {
        if (view != active.Current) return;
        Text = $"{view.DisplayPath} — {Dialogs.AppName}";
        if (treeMode == TreeMode.Shared) sharedTree?.SyncTo(view.CurrentPath);
    }

    // --- Arborescence ----------------------------------------------------------

    void SetTreeMode(TreeMode mode)
    {
        // On mémorise les largeurs actuelles avant de changer de disposition.
        if (treeMode == TreeMode.Shared) settings.SharedTreeWidth = treeSplit.SplitterDistance;
        if (treeMode == TreeMode.PerPane) settings.PaneTreeWidth = left.TreeWidth;

        treeMode = mode;
        if (mode == TreeMode.Shared && sharedTree == null)
        {
            sharedTree = new FolderTree(this, null) { Dock = DockStyle.Fill };
            treeSplit.Panel1.Controls.Add(sharedTree);
            ThemeManager.Apply(sharedTree);
        }
        treeSplit.Panel1Collapsed = mode != TreeMode.Shared;
        if (mode == TreeMode.Shared)
        {
            Dialogs.SetSplitter(treeSplit, settings.SharedTreeWidth);
            if (V != null) sharedTree!.SyncTo(V.CurrentPath);
        }
        left.ShowTree(mode == TreeMode.PerPane, settings.PaneTreeWidth);
        right.ShowTree(mode == TreeMode.PerPane, settings.PaneTreeWidth);
    }

    // --- Raccourcis globaux ---------------------------------------------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var v = V;
        if (v?.IsEditing == true) return base.ProcessCmdKey(ref msg, keyData);

        // Ctrl+1..9 : ouvrir le n-ième favori (Ctrl+Maj = nouvel onglet)
        var key = keyData & Keys.KeyCode;
        var mods = keyData & Keys.Modifiers;
        if (key is >= Keys.D1 and <= Keys.D9 && (mods == Keys.Control || mods == (Keys.Control | Keys.Shift)))
        {
            favorites.OpenAt(key - Keys.D1, mods.HasFlag(Keys.Shift));
            return true;
        }

        switch (keyData)
        {
            case Keys.Control | Keys.T: active.AddTab(v?.CurrentPath ?? "").FocusList(); return true;
            case Keys.Control | Keys.W: active.CloseCurrent(); return true;
            case Keys.Control | Keys.Shift | Keys.T: active.ReopenClosed(); return true;
            case Keys.Control | Keys.Tab: active.CycleTab(1); return true;
            case Keys.Control | Keys.Shift | Keys.Tab: active.CycleTab(-1); return true;
            case Keys.Tab when v?.ListFocused == true:
                OtherPane(active).Current?.FocusList();
                return true;
            case Keys.Control | Keys.L:
            case Keys.Alt | Keys.D:
            case Keys.F4: v?.FocusAddress(); return true;
            case Keys.Control | Keys.F: v?.FocusFilter(); return true;
            case Keys.Alt | Keys.Left: v?.GoBack(); return true;
            case Keys.Alt | Keys.Right: v?.GoForward(); return true;
            case Keys.Alt | Keys.Up: v?.GoUp(); return true;
            case Keys.Control | Keys.R: v?.ReloadView(); return true;
            case Keys.F5: v?.TransferToOther(false); return true;
            case Keys.F6: v?.TransferToOther(true); return true;
            case Keys.F7:
            case Keys.Control | Keys.Shift | Keys.N: v?.NewFolder(); return true;
            case Keys.F8: v?.DeleteSelected(true); return true;
            case Keys.Control | Keys.D: if (v != null) AddFavorite(v.CurrentPath); return true;
            case Keys.Control | Keys.H: ToggleHidden(); return true;
            case Keys.Control | Keys.U: SwapPanes(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void SwapPanes()
    {
        var a = left.Current;
        var b = right.Current;
        if (a == null || b == null) return;
        string pa = a.CurrentPath, pb = b.CurrentPath;
        a.Navigate(pb, quiet: true);
        b.Navigate(pa, quiet: true);
    }

    // --- Démarrage / fermeture -------------------------------------------------

    void RestoreWindow()
    {
        if (settings.WinW < 300 || settings.WinH < 200) return;
        var rect = new Rectangle(settings.WinX, settings.WinY, settings.WinW, settings.WinH);
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = rect;
        if (settings.Maximized) WindowState = FormWindowState.Maximized;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        double ratio = settings.SplitRatio is > 0.1 and < 0.9 ? settings.SplitRatio : 0.5;
        split.SplitterDistance = (int)(split.Width * ratio);

        RestoreTabs(left, settings.LeftTabs, settings.LeftIndex,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        RestoreTabs(right, settings.RightTabs, settings.RightIndex, "");
        SetTreeMode(Enum.TryParse<TreeMode>(settings.TreeMode, out var tm) ? tm : TreeMode.None);

        // Chemins passés en ligne de commande (ex. LiteExplorer.exe "D:\Projets" ou /select,C:\fichier.txt)
        foreach (var raw in args)
        {
            var arg = raw.StartsWith("/select,", StringComparison.OrdinalIgnoreCase) ? raw[8..] : raw;
            if (arg.StartsWith('/')) continue;
            if (Directory.Exists(arg) || File.Exists(arg)) left.AddTab(arg, quiet: true);
        }

        SetActivePane(left);
        BeginInvoke(new Action(() => left.Current?.FocusList()));
    }

    static void RestoreTabs(FilePane pane, List<string> paths, int index, string fallback)
    {
        if (paths.Count == 0) paths = new List<string> { fallback };
        foreach (var p in paths) pane.AddTab(p, select: false, quiet: true);
        pane.SelectedIndex = index;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        settings.LeftTabs = left.TabPaths().ToList();
        settings.LeftIndex = left.SelectedIndex;
        settings.RightTabs = right.TabPaths().ToList();
        settings.RightIndex = right.SelectedIndex;
        settings.ShowHidden = AppState.ShowHidden;
        settings.Theme = ThemeManager.Mode.ToString();
        settings.TreeMode = treeMode.ToString();
        if (treeMode == TreeMode.Shared) settings.SharedTreeWidth = treeSplit.SplitterDistance;
        if (treeMode == TreeMode.PerPane) settings.PaneTreeWidth = left.TreeWidth;
        settings.FavoriteBars = favorites.Bars;
        settings.SplitRatio = split.Width > 0 ? (double)split.SplitterDistance / split.Width : 0.5;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        (settings.WinX, settings.WinY, settings.WinW, settings.WinH) = (b.X, b.Y, b.Width, b.Height);
        settings.Maximized = WindowState == FormWindowState.Maximized;
        settings.Save();
        base.OnFormClosing(e);
    }
}
