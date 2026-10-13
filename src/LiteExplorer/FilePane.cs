namespace LiteExplorer;

/// <summary>
/// Un panneau = une rangée d'onglets façon fureteur Web.
/// Bouton « + » pour ouvrir un onglet, « × » ou clic milieu pour le fermer.
/// </summary>
internal sealed class FilePane : UserControl
{
    readonly IExplorerHost host;
    readonly TabControl tabs = new();
    readonly TabPage plusPage = new("+") { ToolTipText = "Nouvel onglet (Ctrl+T)" };
    readonly ContextMenuStrip tabMenu = new();
    readonly Stack<string> closedTabs = new();
    TabPage? menuTarget;
    bool suppress, isActive;

    public FilePane(IExplorerHost host)
    {
        this.host = host;
        tabs.Dock = DockStyle.Fill;
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.Padding = new Point(14, 4);
        tabs.ShowToolTips = true;
        Controls.Add(tabs);
        _ = tabs.Handle; // sinon TabPages.Insert est ignoré avant la création du handle
        tabs.TabPages.Add(plusPage);

        tabs.DrawItem += DrawTab;
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
        var page = new TabPage("…") { Padding = Padding.Empty };
        page.Controls.Add(view);
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

    void DrawTab(object? sender, DrawItemEventArgs e)
    {
        var page = tabs.TabPages[e.Index];
        var r = tabs.GetTabRect(e.Index);
        bool selected = e.Index == tabs.SelectedIndex;
        var g = e.Graphics;

        using (var back = new SolidBrush(selected ? SystemColors.Window : SystemColors.Control))
            g.FillRectangle(back, r);

        if (page == plusPage)
        {
            using var bold = new Font(tabs.Font.FontFamily, tabs.Font.Size + 2, FontStyle.Bold);
            TextRenderer.DrawText(g, "+", bold, r, SystemColors.ControlText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        if (selected && isActive)
            using (var accent = new SolidBrush(SystemColors.Highlight))
                g.FillRectangle(accent, r.X, r.Y, r.Width, 3);

        var textRect = new Rectangle(r.X + 6, r.Y, r.Width - 26, r.Height);
        TextRenderer.DrawText(g, page.Text, tabs.Font, textRect, SystemColors.ControlText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

        var c = CloseRect(r);
        const int m = 4;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(selected ? SystemColors.ControlDarkDark : SystemColors.GrayText, 1.5f);
        g.DrawLine(pen, c.Left + m, c.Top + m, c.Right - m, c.Bottom - m);
        g.DrawLine(pen, c.Right - m, c.Top + m, c.Left + m, c.Bottom - m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tabMenu.Dispose();
        base.Dispose(disposing);
    }
}
