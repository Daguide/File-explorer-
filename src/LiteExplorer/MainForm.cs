namespace LiteExplorer;

internal sealed class MainForm : Form, IExplorerHost
{
    readonly AppSettings settings;
    readonly string[] args;
    readonly FavoritesBar favorites;
    readonly SplitContainer split;
    readonly FilePane left, right;
    FilePane active;

    public MainForm(string[] args)
    {
        this.args = args;
        settings = AppSettings.Load();
        AppState.ShowHidden = settings.ShowHidden;

        Text = Dialogs.AppName;
        Font = SystemFonts.MessageBoxFont ?? Font;
        MinimumSize = new Size(640, 400);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;

        left = new FilePane(this) { Dock = DockStyle.Fill };
        right = new FilePane(this) { Dock = DockStyle.Fill };
        active = left;

        favorites = new FavoritesBar(settings.Favorites) { Dock = DockStyle.Top };
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

        Controls.Add(split);
        Controls.Add(BuildFunctionBar());
        Controls.Add(favorites);

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
            b.FlatAppearance.BorderColor = SystemColors.ControlDark;
            b.Click += (_, _) => { V?.FocusList(); action(); };
            bar.Controls.Add(b);
        }
        return bar;
    }

    // --- IExplorerHost -------------------------------------------------------

    public FilePane OtherPane(FilePane pane) => pane == left ? right : left;

    public void SetActivePane(FilePane pane)
    {
        if (active == pane && pane.IsActive) return;
        active = pane;
        left.IsActive = pane == left;
        right.IsActive = pane == right;
        if (pane.Current is { } v) ViewPathChanged(v);
    }

    public void AddFavorite(string path) => favorites.Add(path);

    public void ToggleHidden()
    {
        AppState.ShowHidden = !AppState.ShowHidden;
        foreach (var v in left.Views.Concat(right.Views)) v.ReloadView();
    }

    public void ViewPathChanged(FolderView view)
    {
        if (view == active.Current) Text = $"{view.DisplayPath} — {Dialogs.AppName}";
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
        settings.Favorites = favorites.Favorites;
        settings.SplitRatio = split.Width > 0 ? (double)split.SplitterDistance / split.Width : 0.5;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        (settings.WinX, settings.WinY, settings.WinW, settings.WinH) = (b.X, b.Y, b.Width, b.Height);
        settings.Maximized = WindowState == FormWindowState.Maximized;
        settings.Save();
        base.OnFormClosing(e);
    }
}
