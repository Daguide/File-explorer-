namespace LiteExplorer;

/// <summary>
/// Un panneau = une rangée d'onglets façon fureteur Web.
/// Bouton « + » pour ouvrir un onglet, « × » ou clic milieu pour le fermer.
/// </summary>
/// <summary>
/// TabControl entièrement dessiné par nous (fond + onglets), sinon Windows
/// peint la bande d'onglets en clair, même en thème sombre.
/// </summary>
internal sealed class ThemedTabControl : TabControl
{
    public Action<Graphics, int>? DrawTabItem;

    public ThemedTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var back = new SolidBrush(ThemeManager.Current.Panel))
            e.Graphics.FillRectangle(back, ClientRectangle);
        for (int i = 0; i < TabCount; i++) DrawTabItem?.Invoke(e.Graphics, i);
    }
}

internal sealed class FilePane : UserControl, IThemed
{
    readonly IExplorerHost host;
    readonly ThemedTabControl tabs = new();
    readonly TabPage plusPage = new("+") { ToolTipText = "Nouvel onglet (Ctrl+T)" };
    readonly ContextMenuStrip tabMenu = new();
    readonly Stack<string> closedTabs = new();
    TabPage? menuTarget;
    bool suppress, isActive;

    public FilePane(IExplorerHost host)
    {
        this.host = host;
        tabs.Dock = DockStyle.Fill;
        tabs.Padding = new Point(14, 4);
        tabs.ShowToolTips = true;
        Controls.Add(tabs);
        _ = tabs.Handle; // sinon TabPages.Insert est ignoré avant la création du handle
        tabs.TabPages.Add(plusPage);

        tabs.DrawTabItem = DrawTab;
        tabs.MouseDown += TabMouseDown;
        tabs.MouseDoubleClick += (_, e) => { if (HitTab(e.Location) < 0) AddTab(Current?.CurrentPath ?? "").FocusList(); };
        tabs.Selecting += (_, e) =>
        {
            if (e.TabPage != plusPage) return;
            e.Cancel = true;
            if (!suppress) BeginInvoke(new Action(() => AddTab(Current?.CurrentPath ?? "").FocusList()));
        };
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (Current is { } v) host.ViewPathChanged(v);
        };

        AddMenu("Nouvel onglet", "Ctrl+T", () => AddTab(Current?.CurrentPath ?? "").FocusList());
        AddMenu("Dupliquer l'onglet", null, () =>
        {
            if (menuTarget != null) AddTab(ViewOf(menuTarget).CurrentPath, index: tabs.TabPages.IndexOf(menuTarget) + 1).FocusList();
        });
        AddMenu("Déplacer vers l'autre panneau", null, () =>
        {
            if (menuTarget == null) return;
            host.OtherPane(this).AddTab(ViewOf(menuTarget).CurrentPath).FocusList();
            CloseTab(menuTarget);
        });
        tabMenu.Items.Add(new ToolStripSeparator());
        AddMenu("Fermer l'onglet", "Ctrl+W", () => { if (menuTarget != null) CloseTab(menuTarget); });
        AddMenu("Fermer les autres onglets", null, () =>
        {
            if (menuTarget == null) return;
            foreach (var p in RealPages().Where(p => p != menuTarget).ToList()) CloseTab(p);
        });
        AddMenu("Rouvrir l'onglet fermé", "Ctrl+Maj+T", ReopenClosed);
    }

    void AddMenu(string text, string? keys, Action action)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys };
        item.Click += (_, _) => action();
        tabMenu.Items.Add(item);
    }

    static FolderView ViewOf(TabPage page) => (FolderView)page.Controls[0];

    List<TabPage> RealPages() => tabs.TabPages.Cast<TabPage>().Where(p => p != plusPage).ToList();

    public FolderView? Current => tabs.SelectedTab is { } p && p != plusPage && p.Controls.Count > 0 ? ViewOf(p) : null;

    public IEnumerable<FolderView> Views => RealPages().Select(ViewOf);

    public int SelectedIndex
    {
        get => Math.Max(0, RealPages().IndexOf(tabs.SelectedTab!));
        set
        {
            var pages = RealPages();
            if (pages.Count > 0) tabs.SelectedTab = pages[Math.Clamp(value, 0, pages.Count - 1)];
        }
    }

    public bool IsActive
    {
        get => isActive;
        set
        {
            isActive = value;
            foreach (var v in Views) v.SetActive(value);
            tabs.Invalidate();
        }
    }

    public FolderView AddTab(string path, bool select = true, int index = -1, bool quiet = false)
    {
        var view = new FolderView(host, this) { Dock = DockStyle.Fill };
        var page = new TabPage("…") { Padding = Padding.Empty, UseVisualStyleBackColor = false };
        page.Controls.Add(view);
        ThemeManager.Apply(page);
        view.SetActive(isActive);
        view.PathChanged += (_, _) =>
        {
            page.Text = Truncate(view.TabTitle, 24);
            page.ToolTipText = view.DisplayPath;
            if (tabs.SelectedTab == page) host.ViewPathChanged(view);
        };

        int last = tabs.TabPages.Count - 1; // position du « + »
        int at = index < 0 || index > last ? last : index;
        suppress = true;
        tabs.TabPages.Insert(at, page);
        suppress = false;
        tabs.Invalidate();

        if (!view.Navigate(path, addHistory: false, quiet: quiet)) view.Navigate("", addHistory: false);
        if (select || Current == null) tabs.SelectedTab = page;
        return view;
    }

    public void CloseCurrent()
    {
        if (tabs.SelectedTab is { } p && p != plusPage) CloseTab(p);
    }

    public void CloseTab(TabPage page)
    {
        var pages = RealPages();
        if (pages.Count <= 1 || page == plusPage) return; // on garde toujours un onglet
        int idx = pages.IndexOf(page);
        suppress = true;
        if (tabs.SelectedTab == page)
            tabs.SelectedTab = idx + 1 < pages.Count ? pages[idx + 1] : pages[idx - 1];
        tabs.TabPages.Remove(page);
        suppress = false;
        closedTabs.Push(ViewOf(page).CurrentPath);
        page.Dispose();
        Current?.FocusList();
    }

    public void ReopenClosed()
    {
        if (closedTabs.Count > 0) AddTab(closedTabs.Pop(), quiet: true).FocusList();
    }

    public void CycleTab(int direction)
    {
        var pages = RealPages();
        if (pages.Count < 2) return;
        int i = pages.IndexOf(tabs.SelectedTab!);
        tabs.SelectedTab = pages[((i + direction) % pages.Count + pages.Count) % pages.Count];
        Current?.FocusList();
    }

    public IEnumerable<string> TabPaths() => Views.Select(v => v.CurrentPath);

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // --- Dessin et souris --------------------------------------------------

    int HitTab(Point pt)
    {
        for (int i = 0; i < tabs.TabCount; i++)
            if (tabs.GetTabRect(i).Contains(pt)) return i;
        return -1;
    }

    static Rectangle CloseRect(Rectangle r) => new(r.Right - 18, r.Top + (r.Height - 14) / 2, 14, 14);

    void TabMouseDown(object? sender, MouseEventArgs e)
    {
        host.SetActivePane(this);
        int i = HitTab(e.Location);
        if (i < 0) return;
        var page = tabs.TabPages[i];
        if (page == plusPage) return;

        if (e.Button == MouseButtons.Middle ||
            (e.Button == MouseButtons.Left && CloseRect(tabs.GetTabRect(i)).Contains(e.Location)))
        {
            CloseTab(page);
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            menuTarget = page;
            tabMenu.Show(tabs, e.Location);
            return;
        }
        BeginInvoke(new Action(() => Current?.FocusList()));
    }

    public void OnThemeChanged(Theme theme) => tabs.Invalidate();

    void DrawTab(Graphics g, int index)
    {
        if (index < 0 || index >= tabs.TabPages.Count) return;
        var t = ThemeManager.Current;
        var page = tabs.TabPages[index];
        var r = tabs.GetTabRect(index);
        bool selected = index == tabs.SelectedIndex;

        using (var back = new SolidBrush(selected ? t.Back : t.Panel))
            g.FillRectangle(back, r);
        if (!selected)
            using (var sep = new Pen(t.Border))
                g.DrawLine(sep, r.Right - 1, r.Top + 6, r.Right - 1, r.Bottom - 6);

        if (page == plusPage)
        {
            using var bold = new Font(tabs.Font.FontFamily, tabs.Font.Size + 2, FontStyle.Bold);
            TextRenderer.DrawText(g, "+", bold, r, t.Fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        if (selected)
            using (var accent = new SolidBrush(isActive ? t.Accent : t.Border))
                g.FillRectangle(accent, r.X, r.Y, r.Width, 3);

        var textRect = new Rectangle(r.X + 6, r.Y, r.Width - 26, r.Height);
        TextRenderer.DrawText(g, page.Text, tabs.Font, textRect, selected ? t.Fore : t.Gray,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

        var c = CloseRect(r);
        const int m = 4;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var pen = new Pen(selected ? t.Fore : t.Gray, 1.5f))
        {
            g.DrawLine(pen, c.Left + m, c.Top + m, c.Right - m, c.Bottom - m);
            g.DrawLine(pen, c.Right - m, c.Top + m, c.Left + m, c.Bottom - m);
        }
        g.SmoothingMode = smoothing;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tabMenu.Dispose();
        base.Dispose(disposing);
    }
}
