namespace LiteExplorer;

/// <summary>
/// Un panneau = une barre d'onglets façon fureteur + le contenu de l'onglet actif.
/// Les onglets sont dessinés à la main : largeur selon le nom du dossier,
/// « + » pour en ouvrir un, « × » ou clic milieu pour fermer, glisser pour réordonner.
/// </summary>
internal sealed class FilePane : UserControl, IThemed
{
    readonly IExplorerHost host;
    readonly TabStrip strip;
    readonly Panel content = new() { Dock = DockStyle.Fill };
    readonly List<FolderView> views = new();
    readonly ContextMenuStrip tabMenu = new();
    readonly Stack<string> closedTabs = new();
    FolderView? current, menuTarget;
    bool isActive;

    public FilePane(IExplorerHost host)
    {
        this.host = host;
        strip = new TabStrip(this) { Dock = DockStyle.Top };
        Controls.Add(content);
        Controls.Add(strip);

        AddMenu("Nouvel onglet", "Ctrl+T", NewTab);
        AddMenu("Dupliquer l'onglet", null, () =>
        {
            if (menuTarget != null) AddTab(menuTarget.CurrentPath, index: views.IndexOf(menuTarget) + 1).FocusList();
        });
        AddMenu("Déplacer vers l'autre panneau", null, () =>
        {
            if (menuTarget == null) return;
            host.OtherPane(this).AddTab(menuTarget.CurrentPath).FocusList();
            CloseTab(menuTarget);
        });
        tabMenu.Items.Add(new ToolStripSeparator());
        AddMenu("Fermer l'onglet", "Ctrl+W", () => { if (menuTarget != null) CloseTab(menuTarget); });
        AddMenu("Fermer les autres onglets", null, () =>
        {
            if (menuTarget == null) return;
            foreach (var v in views.Where(v => v != menuTarget).ToList()) CloseTab(v);
        });
        AddMenu("Rouvrir l'onglet fermé", "Ctrl+Maj+T", ReopenClosed);
    }

    void AddMenu(string text, string? keys, Action action)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys };
        item.Click += (_, _) => action();
        tabMenu.Items.Add(item);
    }

    public FolderView? Current => current;
    public IEnumerable<FolderView> Views => views;
    internal List<FolderView> ViewList => views;

    public int SelectedIndex
    {
        get => current == null ? 0 : Math.Max(0, views.IndexOf(current));
        set { if (views.Count > 0) Select(views[Math.Clamp(value, 0, views.Count - 1)]); }
    }

    public bool IsActive
    {
        get => isActive;
        set
        {
            isActive = value;
            foreach (var v in views) v.SetActive(value);
            strip.Invalidate();
        }
    }

    public void OnThemeChanged(Theme theme) => strip.Invalidate();

    internal void Activate() => host.SetActivePane(this);

    public void NewTab() => AddTab(current?.CurrentPath ?? "").FocusList();

    public FolderView AddTab(string path, bool select = true, int index = -1, bool quiet = false)
    {
        var view = new FolderView(host, this) { Dock = DockStyle.Fill, Visible = false };
        view.SetActive(isActive);
        view.PathChanged += (_, _) =>
        {
            strip.Relayout();
            if (view == current) host.ViewPathChanged(view);
        };
        views.Insert(index < 0 || index > views.Count ? views.Count : index, view);
        content.Controls.Add(view);
        ThemeManager.Apply(view);

        if (!view.Navigate(path, addHistory: false, quiet: quiet)) view.Navigate("", addHistory: false);
        if (select || current == null) Select(view);
        else strip.Relayout();
        return view;
    }

    public void Select(FolderView view)
    {
        if (view == current) return;
        var old = current;
        current = view;
        view.Visible = true;
        view.BringToFront();
        if (old != null) old.Visible = false;
        strip.Relayout();
        host.ViewPathChanged(view);
    }

    public void CloseCurrent()
    {
        if (current != null) CloseTab(current);
    }

    public void CloseTab(FolderView view)
    {
        if (views.Count <= 1) return; // on garde toujours un onglet
        int idx = views.IndexOf(view);
        if (idx < 0) return;
        if (view == current) Select(views[idx + 1 < views.Count ? idx + 1 : idx - 1]);
        views.RemoveAt(idx);
        content.Controls.Remove(view);
        closedTabs.Push(view.CurrentPath);
        view.Dispose();
        strip.Relayout();
        current?.FocusList();
    }

    public void ReopenClosed()
    {
        if (closedTabs.Count > 0) AddTab(closedTabs.Pop(), quiet: true).FocusList();
    }

    public void CycleTab(int direction)
    {
        if (views.Count < 2 || current == null) return;
        int i = views.IndexOf(current);
        Select(views[((i + direction) % views.Count + views.Count) % views.Count]);
        current?.FocusList();
    }

    internal void MoveTab(int from, int to)
    {
        var v = views[from];
        views.RemoveAt(from);
        views.Insert(to, v);
        strip.Relayout();
    }

    internal void ShowTabMenu(FolderView view, Point location)
    {
        menuTarget = view;
        tabMenu.Show(strip, location);
    }

    public IEnumerable<string> TabPaths() => views.Select(v => v.CurrentPath);

    protected override void Dispose(bool disposing)
    {
        if (disposing) tabMenu.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Barre d'onglets dessinée à la main.</summary>
internal sealed class TabStrip : Control
{
    const int MinTabWidth = 56;
    const int MaxTabWidth = 260;

    readonly FilePane pane;
    readonly List<Rectangle> rects = new();
    readonly ToolTip tip = new() { InitialDelay = 600 };
    Rectangle plusRect;
    int hover = -1, dragIndex = -1;
    bool hoverClose, hoverPlus;
    Point dragStart;
    string? tipText;

    public TabStrip(FilePane pane)
    {
        this.pane = pane;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Height = Font.Height + 14;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Height = Font.Height + 14;
        Relayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    int IconWidth => ShellIcons.List.ImageSize.Width;

    public void Relayout()
    {
        rects.Clear();
        var views = pane.ViewList;
        // Largeur naturelle : marge + icône + nom + bouton ×
        var widths = views.Select(v =>
            Math.Clamp(8 + IconWidth + 6 + TextRenderer.MeasureText(v.TabTitle, Font).Width + 26, MinTabWidth, MaxTabWidth)).ToList();
        int plusWidth = Height;
        int available = Width - plusWidth - 4;
        int total = widths.Sum();
        if (total > available && total > 0)
        {
            // Pas assez de place : on rétrécit tous les onglets, comme un fureteur.
            double k = (double)available / total;
            for (int i = 0; i < widths.Count; i++) widths[i] = Math.Max(MinTabWidth, (int)(widths[i] * k));
        }
        int x = 2;
        foreach (var w in widths)
        {
            rects.Add(new Rectangle(x, 2, w, Height - 2));
            x += w;
        }
        plusRect = new Rectangle(x + 2, 4, plusWidth - 4, Height - 7);
        Invalidate();
    }

    static Rectangle CloseRect(Rectangle r) => new(r.Right - 22, r.Top + (r.Height - 16) / 2, 16, 16);

    int HitTab(Point p) => rects.FindIndex(r => r.Contains(p));

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = ThemeManager.Current;
        var g = e.Graphics;
        g.Clear(t.Panel);
        var views = pane.ViewList;
        int selected = pane.Current == null ? -1 : views.IndexOf(pane.Current);

        using (var border = new Pen(t.Border))
            g.DrawLine(border, 0, Height - 1, Width, Height - 1);

        for (int i = 0; i < rects.Count && i < views.Count; i++)
        {
            var r = rects[i];
            var v = views[i];
            bool sel = i == selected;
            bool hot = i == hover;

            using (var back = new SolidBrush(sel ? t.Back : hot ? t.Hover : t.Panel))
                g.FillRectangle(back, r);
            if (sel)
            {
                using var accent = new SolidBrush(pane.IsActive ? t.Accent : t.Border);
                g.FillRectangle(accent, r.X, r.Y, r.Width, 3);
            }
            else if (!hot && i + 1 != selected && i + 1 != hover)
            {
                using var sep = new Pen(t.Border);
                g.DrawLine(sep, r.Right - 1, r.Top + 7, r.Right - 1, r.Bottom - 7);
            }

            int icon = ShellIcons.ForPath(v.CurrentPath);
            if (icon >= 0)
                ShellIcons.List.Draw(g, r.X + 8, r.Y + (r.Height - ShellIcons.List.ImageSize.Height) / 2, icon);

            int textX = r.X + 8 + IconWidth + 6;
            var textRect = new Rectangle(textX, r.Y, Math.Max(0, r.Right - 26 - textX), r.Height);
            TextRenderer.DrawText(g, v.TabTitle, Font, textRect, sel ? t.Fore : t.Gray,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            var c = CloseRect(r);
            if (hot && hoverClose)
                using (var hb = new SolidBrush(t.Pressed)) g.FillRectangle(hb, c);
            DrawCross(g, c, sel || hot ? t.Fore : t.Gray);
        }

        if (hoverPlus)
            using (var hb = new SolidBrush(t.Hover)) g.FillRectangle(hb, plusRect);
        DrawPlus(g, plusRect, t.Fore);
    }

    static void DrawCross(Graphics g, Rectangle c, Color color)
    {
        const int m = 4;
        var old = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.5f);
        g.DrawLine(pen, c.Left + m, c.Top + m, c.Right - m, c.Bottom - m);
        g.DrawLine(pen, c.Right - m, c.Top + m, c.Left + m, c.Bottom - m);
        g.SmoothingMode = old;
    }

    static void DrawPlus(Graphics g, Rectangle r, Color color)
    {
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, h = Math.Min(r.Width, r.Height) / 4;
        using var pen = new Pen(color, 1.5f);
        g.DrawLine(pen, cx - h, cy, cx + h, cy);
        g.DrawLine(pen, cx, cy - h, cx, cy + h);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        pane.Activate();
        if (plusRect.Contains(e.Location))
        {
            if (e.Button == MouseButtons.Left) pane.NewTab();
            return;
        }
        int i = HitTab(e.Location);
        if (i < 0 || i >= pane.ViewList.Count) return;
        var v = pane.ViewList[i];
        switch (e.Button)
        {
            case MouseButtons.Middle:
                pane.CloseTab(v);
                break;
            case MouseButtons.Right:
                pane.ShowTabMenu(v, e.Location);
                break;
            case MouseButtons.Left when CloseRect(rects[i]).Contains(e.Location):
                pane.CloseTab(v);
                break;
            case MouseButtons.Left:
                pane.Select(v);
                dragIndex = i;
                dragStart = e.Location;
                BeginInvoke(new Action(() => pane.Current?.FocusList()));
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = HitTab(e.Location);

        // Glisser un onglet pour le déplacer.
        if (dragIndex >= 0 && e.Button == MouseButtons.Left && i >= 0 && i != dragIndex
            && Math.Abs(e.X - dragStart.X) > 4)
        {
            pane.MoveTab(dragIndex, i);
            dragIndex = i;
        }

        bool close = i >= 0 && CloseRect(rects[i]).Contains(e.Location);
        bool plus = plusRect.Contains(e.Location);
        if (i != hover || close != hoverClose || plus != hoverPlus)
        {
            hover = i;
            hoverClose = close;
            hoverPlus = plus;
            Invalidate();
        }

        string? text = plus ? "Nouvel onglet (Ctrl+T)"
                     : i >= 0 && i < pane.ViewList.Count ? pane.ViewList[i].DisplayPath : null;
        if (text != tipText)
        {
            tipText = text;
            tip.SetToolTip(this, text);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragIndex = -1;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        hoverClose = hoverPlus = false;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (HitTab(e.Location) < 0 && !plusRect.Contains(e.Location)) pane.NewTab();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tip.Dispose();
        base.Dispose(disposing);
    }
}
