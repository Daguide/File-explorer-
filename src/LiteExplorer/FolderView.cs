using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LiteExplorer;

internal sealed class FileListView : ListView
{
    const int WM_LBUTTONDBLCLK = 0x203;

    /// <summary>Double-clic hors de tout élément (la ListView ne lève pas MouseDoubleClick dans ce cas).</summary>
    public event EventHandler? EmptyAreaDoubleClick;

    public FileListView()
    {
        DoubleBuffered = true;
        // On dessine nous-mêmes les en-têtes (pour le thème sombre), le reste reste natif.
        OwnerDraw = true;
        DrawItem += (_, e) => e.DrawDefault = true;
        DrawSubItem += (_, e) => e.DrawDefault = true;
        DrawColumnHeader += OnDrawHeader;
        HandleCreated += (_, _) => ApplyNativeTheme();
    }

    public void ApplyNativeTheme()
    {
        if (!IsHandleCreated) return;
        bool dark = ThemeManager.Current.Dark;
        // DarkMode_Explorer : barres de défilement et sélection sombres (Windows 10 1809+).
        NativeMethods.SetWindowTheme(Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        var header = NativeMethods.SendMessage(Handle, NativeMethods.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header != IntPtr.Zero)
            NativeMethods.SetWindowTheme(header, dark ? "DarkMode_ItemsView" : "Explorer", null);
        Invalidate();
    }

    void OnDrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var t = ThemeManager.Current;
        var g = e.Graphics;
        using (var back = new SolidBrush(t.Header)) g.FillRectangle(back, e.Bounds);
        using (var pen = new Pen(t.Border))
        {
            g.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
            g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }
        var align = e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
        TextRenderer.DrawText(g, e.Header?.Text, Font, Rectangle.Inflate(e.Bounds, -6, 0), t.Fore,
            align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_LBUTTONDBLCLK)
        {
            int lp = (int)(long)m.LParam;
            var pt = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            if (HitTest(pt).Item == null)
            {
                EmptyAreaDoubleClick?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        base.WndProc(ref m);
    }
}

/// <summary>
/// Contenu d'un onglet : barre d'adresse, liste virtuelle des fichiers,
/// historique Précédent/Suivant et surveillance du dossier.
/// </summary>
internal sealed class FolderView : UserControl, IThemed
{
    readonly IExplorerHost host;
    public FilePane Pane { get; }

    readonly FileListView list = new();
    readonly TextBox address = new();
    readonly TextBox filter = new();
    readonly ToolStrip nav = new();
    readonly ToolStripButton btnBack, btnForward, btnUp;
    readonly Label status = new();
    readonly Panel accent = new();
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 400 };

    static readonly string[] ColumnNames = { "Nom", "Taille", "Type", "Modifié le" };

    List<FileEntry> all = new();
    List<FileEntry> view = new();
    readonly Stack<string> backStack = new();
    readonly Stack<string> forwardStack = new();
    FileSystemWatcher? watcher;
    CancellationTokenSource? loadCts;
    string? freeInfo, loadError;
    int sortColumn;
    bool sortAsc = true;
    bool initialized, editing, statusPending, suppressFilter, dirty, isActive;
    string filterText = "";
    Regex? filterRegex;

    public string CurrentPath { get; private set; } = "";
    public string DisplayPath => CurrentPath.Length == 0 ? "Ce PC" : CurrentPath;
    public string TabTitle
    {
        get
        {
            if (CurrentPath.Length == 0) return "Ce PC";
            var name = Path.GetFileName(CurrentPath);
            return name.Length == 0 ? CurrentPath : name;
        }
    }
    public bool ListFocused => list.Focused;
    public bool IsEditing => editing;

    public event EventHandler? PathChanged;

    public FolderView(IExplorerHost host, FilePane pane)
    {
        this.host = host;
        Pane = pane;

        // --- Barre de navigation ---
        var glyphs = new Font("Segoe MDL2 Assets", 10f);
        btnBack = NavButton("", "Précédent (Alt+Gauche)", GoBack, glyphs);
        btnForward = NavButton("", "Suivant (Alt+Droite)", GoForward, glyphs);
        btnUp = NavButton("", "Dossier parent (Retour arrière)", GoUp, glyphs);
        var btnRefresh = NavButton("", "Actualiser (Ctrl+R)", ReloadView, glyphs);
        nav.Items.AddRange(new ToolStripItem[] { btnBack, btnForward, btnUp, btnRefresh });
        nav.GripStyle = ToolStripGripStyle.Hidden;
        nav.CanOverflow = false;
        nav.Dock = DockStyle.Left;
        nav.Padding = Padding.Empty;

        address.Dock = DockStyle.Fill;
        address.BorderStyle = BorderStyle.FixedSingle;
        address.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        address.AutoCompleteSource = AutoCompleteSource.FileSystemDirectories;
        address.KeyDown += AddressKeyDown;
        address.Enter += (_, _) => { host.SetActivePane(Pane); BeginInvoke(new Action(address.SelectAll)); };

        filter.Dock = DockStyle.Right;
        filter.BorderStyle = BorderStyle.FixedSingle;
        filter.Width = 150;
        filter.PlaceholderText = "Filtrer (ex. *.pdf)";
        filter.TextChanged += (_, _) => OnFilterChanged();
        filter.KeyDown += FilterKeyDown;
        filter.Enter += (_, _) => host.SetActivePane(Pane);

        var bar = new Panel { Dock = DockStyle.Top, Height = address.PreferredHeight + 8, Padding = new Padding(2, 4, 4, 2) };
        bar.Controls.Add(address);
        bar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 4 });
        bar.Controls.Add(filter);
        bar.Controls.Add(nav);

        accent.Dock = DockStyle.Top;
        accent.Height = 2;

        status.Dock = DockStyle.Bottom;
        status.AutoSize = false;
        status.Height = Font.Height + 6;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.Padding = new Padding(4, 0, 0, 0);
        status.AutoEllipsis = true;

        // --- Liste ---
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.VirtualMode = true;
        list.HideSelection = false;
        list.LabelEdit = true;
        list.AllowDrop = true;
        list.MultiSelect = true;
        list.BorderStyle = BorderStyle.None;
        list.HeaderStyle = ColumnHeaderStyle.Clickable;
        list.SmallImageList = ShellIcons.List;
        list.Columns.Add(ColumnNames[0], 300);
        list.Columns.Add(ColumnNames[1], 90, HorizontalAlignment.Right);
        list.Columns.Add(ColumnNames[2], 150);
        list.Columns.Add(ColumnNames[3], 140);
        list.ContextMenuStrip = menu;
        UpdateHeaders();

        list.EmptyAreaDoubleClick += (_, _) => GoUp(); // double-clic dans le vide = dossier parent
        list.RetrieveVirtualItem += OnRetrieveItem;
        list.SearchForVirtualItem += OnSearchItem;
        list.ColumnClick += OnColumnClick;
        list.KeyDown += ListKeyDown;
        list.MouseDoubleClick += ListDoubleClick;
        list.MouseDown += ListMouseDown;
        list.Enter += (_, _) => host.SetActivePane(Pane);
        list.SelectedIndexChanged += (_, _) => QueueStatus();
        list.VirtualItemsSelectionRangeChanged += (_, _) => QueueStatus();
        list.BeforeLabelEdit += OnBeforeLabelEdit;
        list.AfterLabelEdit += OnAfterLabelEdit;
        list.ItemDrag += OnItemDrag;
        list.DragEnter += OnDragOver;
        list.DragOver += OnDragOver;
        list.DragDrop += OnDragDrop;
        menu.Opening += OnMenuOpening;

        refreshTimer.Tick += (_, _) =>
        {
            if (editing) return; // on réessaiera au prochain tick
            refreshTimer.Stop();
            _ = LoadAsync();
        };

        Controls.Add(list);
        Controls.Add(status);
        Controls.Add(bar);
        Controls.Add(accent);
    }

    static ToolStripButton NavButton(string glyph, string tip, Action action, Font font)
    {
        var b = new ToolStripButton(glyph) { ToolTipText = tip, Font = font, AutoSize = false, Width = 28, Height = 24 };
        b.Click += (_, _) => action();
        return b;
    }

    // =====================================================================
    // Navigation
    // =====================================================================

    public bool Navigate(string input, bool addHistory = true, string? select = null, bool quiet = false)
    {
        var path = Normalize(input);
        if (path == null)
        {
            if (!quiet) Dialogs.Error(FindForm(), $"Chemin invalide : « {input} »");
            address.Text = DisplayPath;
            return false;
        }
        if (path.Length > 0 && !Directory.Exists(path))
        {
            if (File.Exists(path))
                return Navigate(Path.GetDirectoryName(path)!, addHistory, path, quiet);
            if (!quiet) Dialogs.Error(FindForm(), $"Impossible de trouver « {path} ».");
            address.Text = DisplayPath;
            return false;
        }

        if (initialized && string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadAsync(select == null ? null : new[] { select });
            return true;
        }
        if (initialized && addHistory)
        {
            backStack.Push(CurrentPath);
            forwardStack.Clear();
        }
        initialized = true;
        NavigateCore(path, select);
        return true;
    }

    void NavigateCore(string path, string? select)
    {
        CurrentPath = path;
        suppressFilter = true;
        filter.Text = "";
        suppressFilter = false;
        filterText = "";
        filterRegex = null;
        address.Text = DisplayPath;
        btnBack.Enabled = backStack.Count > 0;
        btnForward.Enabled = forwardStack.Count > 0;
        btnUp.Enabled = path.Length > 0;
        SetupWatcher();
        _ = LoadAsync(select == null ? Array.Empty<string>() : new[] { select }, resetScroll: true);
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    string? Normalize(string input)
    {
        var p = input.Trim().Trim('"');
        if (p.Length == 0 || p.Equals("Ce PC", StringComparison.OrdinalIgnoreCase)) return "";
        p = Environment.ExpandEnvironmentVariables(p);
        if (p.Length == 2 && p[1] == ':' && char.IsLetter(p[0])) p += "\\";
        try
        {
            if (!Path.IsPathRooted(p) && CurrentPath.Length > 0) p = Path.Combine(CurrentPath, p);
            p = Path.GetFullPath(p);
        }
        catch { return null; }
        var root = Path.GetPathRoot(p) ?? "";
        if (p.Length > root.Length) p = p.TrimEnd('\\', '/');
        return p;
    }

    public void GoBack()
    {
        if (backStack.Count == 0) return;
        var from = CurrentPath;
        forwardStack.Push(CurrentPath);
        NavigateCore(backStack.Pop(), from);
    }

    public void GoForward()
    {
        if (forwardStack.Count == 0) return;
        var from = CurrentPath;
        backStack.Push(CurrentPath);
        NavigateCore(forwardStack.Pop(), from);
    }

    public void GoUp()
    {
        if (CurrentPath.Length == 0) return;
        var parent = Directory.GetParent(CurrentPath)?.FullName ?? "";
        Navigate(parent, select: CurrentPath);
    }

    public void ReloadView() => _ = LoadAsync();

    public void FocusList()
    {
        list.Focus();
        if (list.FocusedItem == null && view.Count > 0) list.FocusedItem = list.Items[0];
    }

    public void FocusAddress() { address.Focus(); address.SelectAll(); }
    public void FocusFilter() { filter.Focus(); filter.SelectAll(); }

    public void SetActive(bool active)
    {
        isActive = active;
        accent.BackColor = active ? ThemeManager.Current.Accent : ThemeManager.Current.Panel;
    }

    public void OnThemeChanged(Theme theme)
    {
        SetActive(isActive);
        status.ForeColor = theme.Gray;
        list.ApplyNativeTheme();
    }

    // =====================================================================
    // Chargement
    // =====================================================================

    async Task LoadAsync(ICollection<string>? select = null, bool resetScroll = false)
    {
        loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        loadCts = cts;
        string path = CurrentPath;
        bool hidden = AppState.ShowHidden;
        select ??= SelectedEntries().Select(e => e.FullPath).ToList();
        int top = resetScroll ? -1 : TopIndex();
        if (resetScroll) status.Text = "Chargement…";

        List<FileEntry> items;
        string? info = null, error = null;
        try
        {
            (items, info) = await Task.Run(() => Enumerate(path, hidden, cts.Token));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            items = path.Length > 0 ? new List<FileEntry> { ParentEntry(path) } : new List<FileEntry>();
            error = ex.Message;
        }
        if (cts.IsCancellationRequested || IsDisposed || path != CurrentPath) return;

        all = items;
        freeInfo = info;
        loadError = error;
        ApplyView(select, top);
    }

    static FileEntry ParentEntry(string path) => new()
    {
        Name = "..",
        FullPath = Directory.GetParent(path)?.FullName ?? "",
        Kind = EntryKind.Parent,
    };

    static (List<FileEntry>, string?) Enumerate(string path, bool showHidden, CancellationToken ct)
    {
        var result = new List<FileEntry>();
        if (path.Length == 0)
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                ct.ThrowIfCancellationRequested();
                string label = "";
                long total = -1, free = -1;
                try
                {
                    if (d.IsReady) { label = d.VolumeLabel; total = d.TotalSize; free = d.AvailableFreeSpace; }
                }
                catch { }
                if (label.Length == 0)
                {
                    label = d.DriveType switch
                    {
                        DriveType.Fixed => "Disque local",
                        DriveType.Removable => "Disque amovible",
                        DriveType.Network => "Lecteur réseau",
                        DriveType.CDRom => "Lecteur CD/DVD",
                        DriveType.Ram => "Disque RAM",
                        _ => "Lecteur",
                    };
                }
                result.Add(new FileEntry
                {
                    Name = $"{label} ({d.Name.TrimEnd('\\')})",
                    FullPath = d.Name,
                    Kind = EntryKind.Drive,
                    Size = total,
                    Info = free >= 0 ? FileEntry.FormatSize(free) + " libres" : "",
                });
            }
            return (result, null);
        }

        result.Add(ParentEntry(path));
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };
        int n = 0;
        foreach (var fsi in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            if ((++n & 255) == 0) ct.ThrowIfCancellationRequested();
            bool dir = (fsi.Attributes & FileAttributes.Directory) != 0;
            result.Add(new FileEntry
            {
                Name = fsi.Name,
                FullPath = fsi.FullName,
                Kind = dir ? EntryKind.Folder : EntryKind.File,
                Size = dir ? -1 : ((FileInfo)fsi).Length,
                Modified = fsi.LastWriteTime,
                Attributes = fsi.Attributes,
                Extension = dir ? "" : fsi.Extension,
            });
        }

        string? info = null;
        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\"))
                info = FileEntry.FormatSize(new DriveInfo(root).AvailableFreeSpace) + " libres";
        }
        catch { }
        return (result, info);
    }

    void ApplyView(ICollection<string>? select, int top)
    {
        select ??= SelectedEntries().Select(e => e.FullPath).ToList();
        view = all.Where(Matches).ToList();
        view.Sort(Compare);

        list.BeginUpdate();
        list.SelectedIndices.Clear();
        list.VirtualListSize = view.Count;
        int first = -1;
        if (select.Count > 0)
        {
            var wanted = new HashSet<string>(select, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < view.Count; i++)
            {
                if (!wanted.Contains(view[i].FullPath)) continue;
                list.SelectedIndices.Add(i);
                if (first < 0) first = i;
            }
        }
        list.EndUpdate();

        if (view.Count > 0)
        {
            if (first >= 0)
            {
                list.FocusedItem = list.Items[first];
            }
            else if (top < 0)
            {
                list.FocusedItem = list.Items[0];
                if (view[0].Kind == EntryKind.Parent) list.SelectedIndices.Add(0);
            }

            if (top >= 0 && top < view.Count)
            {
                if (TopIndex() != top)
                {
                    list.EnsureVisible(view.Count - 1);
                    list.EnsureVisible(top);
                }
            }
            else
            {
                list.EnsureVisible(first >= 0 ? first : 0);
            }
        }
        list.Invalidate();
        UpdateStatus();
    }

    int TopIndex()
    {
        try { return list.VirtualListSize > 0 ? list.TopItem?.Index ?? -1 : -1; }
        catch { return -1; }
    }

    bool Matches(FileEntry e)
    {
        if (e.Kind == EntryKind.Parent) return true;
        if (filterRegex != null) return filterRegex.IsMatch(e.Name);
        return filterText.Length == 0 || e.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase);
    }

    int Compare(FileEntry a, FileEntry b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a.Kind == EntryKind.Parent) return -1;
        if (b.Kind == EntryKind.Parent) return 1;
        bool ad = a.Kind != EntryKind.File, bd = b.Kind != EntryKind.File;
        if (ad != bd) return ad ? -1 : 1; // dossiers toujours en premier
        int r = sortColumn switch
        {
            1 => a.Size.CompareTo(b.Size),
            2 => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            3 => Nullable.Compare(a.Modified, b.Modified),
            _ => 0,
        };
        if (r == 0) r = NativeMethods.StrCmpLogicalW(a.Name, b.Name);
        return sortAsc ? r : -r;
    }

    void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (sortColumn == e.Column) sortAsc = !sortAsc;
        else { sortColumn = e.Column; sortAsc = e.Column != 3; } // date : plus récent d'abord
        UpdateHeaders();
        ApplyView(null, -1);
    }

    void UpdateHeaders()
    {
        for (int i = 0; i < list.Columns.Count; i++)
            list.Columns[i].Text = ColumnNames[i] + (i == sortColumn ? (sortAsc ? "  ▲" : "  ▼") : "");
    }

    void OnFilterChanged()
    {
        if (suppressFilter) return;
        filterText = filter.Text.Trim();
        filterRegex = filterText.IndexOfAny(new[] { '*', '?' }) >= 0
            ? new Regex("^" + Regex.Escape(filterText).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : null;
        ApplyView(null, -1);
    }

    // =====================================================================
    // Surveillance du dossier (actualisation automatique)
    // =====================================================================

    void SetupWatcher()
    {
        watcher?.Dispose();
        watcher = null;
        if (CurrentPath.Length == 0) return;
        try
        {
            var w = new FileSystemWatcher(CurrentPath)
            {
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                             | NotifyFilters.LastWrite | NotifyFilters.Attributes,
            };
            w.Created += OnFsEvent;
            w.Deleted += OnFsEvent;
            w.Changed += OnFsEvent;
            w.Renamed += OnFsEvent;
            w.Error += (_, _) => OnFsEvent(null, null);
            w.EnableRaisingEvents = true;
            watcher = w;
        }
        catch { /* lecteur réseau, droits… : pas d'actualisation auto */ }
    }

    void OnFsEvent(object? sender, FileSystemEventArgs? e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(KickRefresh)); } catch { }
    }

    void KickRefresh()
    {
        if (!Visible) { dirty = true; return; } // onglet caché : on actualisera à l'affichage
        if (!refreshTimer.Enabled) refreshTimer.Start();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && dirty) { dirty = false; _ = LoadAsync(); }
    }

    // =====================================================================
    // Liste virtuelle
    // =====================================================================

    void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex >= view.Count)
        {
            e.Item = new ListViewItem(new[] { "", "", "", "" });
            return;
        }
        var en = view[e.ItemIndex];
        var item = new ListViewItem(en.Name, ShellIcons.GetIndex(en));
        item.SubItems.Add(en.Kind is EntryKind.File or EntryKind.Drive ? FileEntry.FormatSize(en.Size) : "");
        item.SubItems.Add(ShellIcons.TypeName(en));
        item.SubItems.Add(en.Modified is DateTime d ? d.ToString("g") : "");
        if ((en.Attributes & FileAttributes.Hidden) != 0) item.ForeColor = ThemeManager.Current.Gray;
        e.Item = item;
    }

    // Recherche par frappe : taper « doc » saute au premier élément qui commence par « doc ».
    void OnSearchItem(object? sender, SearchForVirtualItemEventArgs e)
    {
        int n = view.Count;
        var text = e.Text ?? "";
        if (n == 0 || text.Length == 0) return;
        int start = Math.Clamp(e.StartIndex, 0, n - 1);
        for (int k = 0; k < n; k++)
        {
            int i = (start + k) % n;
            if (view[i].Name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)) { e.Index = i; return; }
        }
    }

    IEnumerable<FileEntry> SelectedEntries(bool includeParent = false)
    {
        foreach (int i in list.SelectedIndices)
            if (i < view.Count && (includeParent || view[i].Kind != EntryKind.Parent))
                yield return view[i];
    }

    string[] SelectedPaths() => SelectedEntries().Where(e => e.IsRealItem).Select(e => e.FullPath).ToArray();

    void QueueStatus()
    {
        if (statusPending || !IsHandleCreated) return;
        statusPending = true;
        BeginInvoke(new Action(() => { statusPending = false; UpdateStatus(); }));
    }

    void UpdateStatus()
    {
        int count = view.Count(e => e.Kind != EntryKind.Parent);
        string text = loadError != null ? "⚠ " + loadError : $"{count} élément{(count > 1 ? "s" : "")}";
        if (filterText.Length > 0) text += $" (filtré sur {all.Count(e => e.Kind != EntryKind.Parent)})";
        var sel = SelectedEntries().ToList();
        if (sel.Count > 0)
        {
            text += $"   |   {sel.Count} sélectionné{(sel.Count > 1 ? "s" : "")}";
            long size = sel.Where(e => e.Kind == EntryKind.File).Sum(e => e.Size);
            if (size > 0) text += $" ({FileEntry.FormatSize(size)})";
        }
        if (freeInfo != null) text += "   |   " + freeInfo;
        status.Text = text;
    }

    // =====================================================================
    // Clavier / souris
    // =====================================================================

    void ListKeyDown(object? sender, KeyEventArgs e)
    {
        bool handled = true;
        switch (e.KeyData)
        {
            case Keys.Enter: OpenSelected(); break;
            case Keys.Back: GoUp(); break;
            case Keys.F2: BeginRename(); break;
            case Keys.Delete: DeleteSelected(true); break;
            case Keys.Shift | Keys.Delete: DeleteSelected(false); break;
            case Keys.Control | Keys.C: ToClipboard(false); break;
            case Keys.Control | Keys.X: ToClipboard(true); break;
            case Keys.Control | Keys.V: Paste(); break;
            case Keys.Control | Keys.A: SelectAll(); break;
            case Keys.Control | Keys.Shift | Keys.C: CopyPath(); break;
            case Keys.Alt | Keys.Enter: ShowProperties(); break;
            default: handled = false; break;
        }
        if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
    }

    void AddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            if (Navigate(address.Text)) FocusList();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            address.Text = DisplayPath;
            FocusList();
        }
    }

    void FilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            filter.Text = "";
            FocusList();
        }
        else if (e.KeyCode is Keys.Down or Keys.Enter)
        {
            e.SuppressKeyPress = true;
            FocusList();
        }
    }

    void ListDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var hit = list.HitTest(e.Location).Item;
        if (hit != null && hit.Index < view.Count) OpenEntry(view[hit.Index]);
    }

    void ListMouseDown(object? sender, MouseEventArgs e)
    {
        switch (e.Button)
        {
            case MouseButtons.XButton1: GoBack(); break;
            case MouseButtons.XButton2: GoForward(); break;
            case MouseButtons.Middle:
                var hit = list.HitTest(e.Location).Item;
                if (hit != null && hit.Index < view.Count && view[hit.Index].IsContainer)
                    Pane.AddTab(view[hit.Index].FullPath, select: false); // comme un fureteur : onglet en arrière-plan
                break;
        }
    }

    void SelectAll()
    {
        list.BeginUpdate();
        for (int i = 0; i < view.Count; i++)
            if (view[i].Kind != EntryKind.Parent) list.SelectedIndices.Add(i);
        list.EndUpdate();
    }

    // =====================================================================
    // Actions
    // =====================================================================

    void OpenSelected()
    {
        var sel = SelectedEntries(includeParent: true).ToList();
        if (sel.Count == 0 && list.FocusedItem is { } f && f.Index < view.Count) sel.Add(view[f.Index]);
        if (sel.Count == 1) { OpenEntry(sel[0]); return; }
        foreach (var e in sel.Where(x => x.IsContainer)) Pane.AddTab(e.FullPath, select: false);
        foreach (var e in sel.Where(x => x.Kind == EntryKind.File).Take(20)) ShellOpen(e.FullPath);
    }

    void OpenEntry(FileEntry e)
    {
        switch (e.Kind)
        {
            case EntryKind.Parent: GoUp(); break;
            case EntryKind.Drive:
            case EntryKind.Folder: Navigate(e.FullPath); break;
            default:
                if (e.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    && ResolveShortcut(e.FullPath) is { } target && Directory.Exists(target))
                    Navigate(target);
                else
                    ShellOpen(e.FullPath);
                break;
        }
    }

    void ShellOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1155) // aucune application associée
        {
            NativeMethods.ShellExec(Handle, path, "openas");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { } // annulé par l'utilisateur
        catch (Exception ex) { Dialogs.Error(FindForm(), ex.Message); }
    }

    static string? ResolveShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic sc = shell.CreateShortcut(lnk);
                string target = sc.TargetPath;
                Marshal.FinalReleaseComObject(sc);
                return target;
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }

    public void BeginRename()
    {
        if (CurrentPath.Length == 0) return;
        int idx = list.FocusedItem?.Index ?? -1;
        if (idx < 0 || !list.SelectedIndices.Contains(idx))
            idx = list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1;
        if (idx < 0 || idx >= view.Count || !view[idx].IsRealItem) return;
        BeginEditAt(idx);
    }

    void BeginEditAt(int idx)
    {
        list.Focus();
        list.EnsureVisible(idx);
        list.Items[idx].BeginEdit();
        // Comme l'Explorateur : on sélectionne le nom sans l'extension.
        var en = view[idx];
        int len = en.Kind == EntryKind.File ? Path.GetFileNameWithoutExtension(en.Name).Length : en.Name.Length;
        if (len == 0) len = en.Name.Length;
        var edit = NativeMethods.SendMessage(list.Handle, NativeMethods.LVM_GETEDITCONTROL, IntPtr.Zero, IntPtr.Zero);
        if (edit != IntPtr.Zero) NativeMethods.SendMessage(edit, NativeMethods.EM_SETSEL, IntPtr.Zero, (IntPtr)len);
    }

    void OnBeforeLabelEdit(object? sender, LabelEditEventArgs e)
    {
        if (CurrentPath.Length == 0 || e.Item >= view.Count || !view[e.Item].IsRealItem)
        {
            e.CancelEdit = true;
            return;
        }
        editing = true;
    }

    void OnAfterLabelEdit(object? sender, LabelEditEventArgs e)
    {
        editing = false;
        e.CancelEdit = true; // la liste sera rechargée
        if (e.Label == null || e.Item >= view.Count) return;
        var en = view[e.Item];
        var name = e.Label.Trim();
        if (name.Length == 0 || name == en.Name) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Dialogs.Error(FindForm(), "Un nom de fichier ne peut pas contenir les caractères suivants :\n\\ / : * ? \" < > |");
            return;
        }
        var dest = Path.Combine(CurrentPath, name);
        try
        {
            if (en.Kind == EntryKind.Folder)
            {
                if (string.Equals(en.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    var tmp = en.FullPath + ".~" + Guid.NewGuid().ToString("N")[..8];
                    Directory.Move(en.FullPath, tmp);
                    Directory.Move(tmp, dest);
                }
                else Directory.Move(en.FullPath, dest);
            }
            else File.Move(en.FullPath, dest);
        }
        catch (Exception ex)
        {
            Dialogs.Error(FindForm(), ex.Message);
            return;
        }
        _ = LoadAsync(new[] { dest });
    }

    public async void NewFolder() => await CreateAndRename("Nouveau dossier", "", folder: true);
    async void NewTextFile() => await CreateAndRename("Nouveau document texte", ".txt", folder: false);

    async Task CreateAndRename(string baseName, string ext, bool folder)
    {
        if (CurrentPath.Length == 0) return;
        string name = baseName + ext;
        for (int n = 2; Path.Exists(Path.Combine(CurrentPath, name)); n++) name = $"{baseName} ({n}){ext}";
        var full = Path.Combine(CurrentPath, name);
        try
        {
            if (folder) Directory.CreateDirectory(full);
            else File.Create(full).Dispose();
        }
        catch (Exception ex) { Dialogs.Error(FindForm(), ex.Message); return; }

        await LoadAsync(new[] { full });
        int idx = view.FindIndex(x => FileEntry.PathEquals(x.FullPath, full));
        if (idx >= 0) BeginEditAt(idx);
    }

    public void DeleteSelected(bool recycle)
    {
        var paths = SelectedPaths();
        if (paths.Length > 0) FileOps.Delete(FindForm()!, paths, recycle);
    }

    void ToClipboard(bool cut)
    {
        var paths = SelectedPaths();
        if (paths.Length == 0) return;
        var files = new StringCollection();
        files.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(files);
        // 2 = déplacer (couper), 5 = copier|lien : même convention que l'Explorateur.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        try { Clipboard.SetDataObject(data, true); } catch (ExternalException) { }
    }

    public void Paste()
    {
        if (CurrentPath.Length == 0) return;
        IDataObject? data;
        try { data = Clipboard.GetDataObject(); } catch { return; }
        if (data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        bool move = false;
        try
        {
            if (data.GetData("Preferred DropEffect") is MemoryStream ms)
            {
                var b = new byte[4];
                if (ms.Read(b, 0, 4) == 4)
                {
                    int effect = BitConverter.ToInt32(b, 0);
                    move = (effect & 2) != 0 && (effect & 1) == 0;
                }
            }
        }
        catch { }
        FileOps.Transfer(FindForm()!, files, CurrentPath, move);
        if (move) try { Clipboard.Clear(); } catch { }
    }

    static bool ClipboardHasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); } catch { return false; }
    }

    void CopyPath()
    {
        var paths = SelectedEntries().Select(e => e.FullPath).ToArray();
        var text = paths.Length > 0 ? string.Join(Environment.NewLine, paths) : CurrentPath;
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); } catch (ExternalException) { }
    }

    /// <summary>F5 / F6 : copie ou déplace la sélection vers le dossier de l'autre panneau.</summary>
    public void TransferToOther(bool move)
    {
        var other = host.OtherPane(Pane).Current;
        var paths = SelectedPaths();
        if (paths.Length == 0) return;
        if (other == null || other.CurrentPath.Length == 0)
        {
            Dialogs.Error(FindForm(), "L'autre panneau doit afficher un dossier.");
            return;
        }
        if (FileEntry.PathEquals(other.CurrentPath, CurrentPath))
        {
            Dialogs.Error(FindForm(), "Les deux panneaux affichent le même dossier.");
            return;
        }
        string what = paths.Length == 1 ? $"« {Path.GetFileName(paths[0])} »" : $"{paths.Length} éléments";
        var answer = MessageBox.Show(FindForm(), $"{(move ? "Déplacer" : "Copier")} {what} vers :\n{other.CurrentPath}",
            Dialogs.AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.OK) FileOps.Transfer(FindForm()!, paths, other.CurrentPath, move);
    }

    void ShowProperties()
    {
        var target = SelectedEntries().FirstOrDefault()?.FullPath ?? CurrentPath;
        if (target.Length > 0) NativeMethods.ShellExec(Handle, target, "properties");
    }

    public void OpenTerminal()
    {
        var dir = CurrentPath.Length > 0 ? CurrentPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var quoted = "\"" + (dir.EndsWith('\\') ? dir + "\\" : dir) + "\"";
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", "-d " + quoted) { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = dir, UseShellExecute = true }); }
            catch (Exception ex) { Dialogs.Error(FindForm(), ex.Message); }
        }
    }

    // =====================================================================
    // Glisser-déposer
    // =====================================================================

    void OnItemDrag(object? sender, ItemDragEventArgs e)
    {
        var paths = SelectedPaths();
        if (paths.Length == 0) return;
        list.DoDragDrop(new DataObject(DataFormats.FileDrop, paths),
            DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    string? DropTarget(DragEventArgs e)
    {
        var hit = list.HitTest(list.PointToClient(new Point(e.X, e.Y))).Item;
        if (hit != null && hit.Index < view.Count)
        {
            var en = view[hit.Index];
            if (en.IsContainer) return en.FullPath;
            if (en.Kind == EntryKind.Parent) return en.FullPath.Length > 0 ? en.FullPath : null;
        }
        return CurrentPath.Length > 0 ? CurrentPath : null;
    }

    DragDropEffects ComputeEffect(DragEventArgs e, out string[] files, out string? target)
    {
        target = null;
        files = e.Data?.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        if (files.Length == 0) return DragDropEffects.None;
        target = DropTarget(e);
        if (target == null) return DragDropEffects.None;
        foreach (var f in files)
        {
            var src = f.TrimEnd('\\');
            if (FileEntry.PathEquals(src, target) || target.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase))
                return DragDropEffects.None; // on ne dépose pas un dossier dans lui-même
        }
        var dest = target;
        if (files.All(f => FileEntry.PathEquals(Path.GetDirectoryName(f), dest))) return DragDropEffects.None;

        bool sameVolume = string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
        // Ctrl = copier, Maj = déplacer, sinon comme l'Explorateur : déplacer sur le même volume.
        var want = (e.KeyState & 8) != 0 ? DragDropEffects.Copy
                 : (e.KeyState & 4) != 0 ? DragDropEffects.Move
                 : sameVolume ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((e.AllowedEffect & want) == 0)
            want = e.AllowedEffect.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
                 : e.AllowedEffect.HasFlag(DragDropEffects.Move) ? DragDropEffects.Move
                 : DragDropEffects.None;
        return want;
    }

    void OnDragOver(object? sender, DragEventArgs e) => e.Effect = ComputeEffect(e, out _, out _);

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        var effect = ComputeEffect(e, out var files, out var target);
        if (effect == DragDropEffects.None || target == null) return;
        FileOps.Transfer(FindForm()!, files, target, effect == DragDropEffects.Move);
    }

    // =====================================================================
    // Menu contextuel
    // =====================================================================

    void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        menu.Items.Clear();
        var pt = list.PointToClient(Cursor.Position);
        bool onItem;
        if (list.ClientRectangle.Contains(pt))
        {
            onItem = list.HitTest(pt).Item != null;
            if (!onItem) list.SelectedIndices.Clear();
        }
        else onItem = list.SelectedIndices.Count > 0; // touche Menu du clavier

        var sel = SelectedEntries().ToList();
        if (onItem && sel.Count > 0) BuildItemMenu(sel);
        else BuildBackgroundMenu();
        e.Cancel = false;
    }

    ToolStripMenuItem AddMenu(string text, Action action, string? keys = null, bool enabled = true, bool bold = false)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys, Enabled = enabled };
        if (bold) item.Font = new Font(item.Font, FontStyle.Bold);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    void AddSeparator() => menu.Items.Add(new ToolStripSeparator());

    void BuildItemMenu(List<FileEntry> sel)
    {
        var first = sel[0];
        bool single = sel.Count == 1;
        bool real = CurrentPath.Length > 0;
        bool ops = sel.Any(x => x.IsRealItem);

        AddMenu("Ouvrir", OpenSelected, "Entrée", bold: true);
        if (single && first.IsContainer)
        {
            AddMenu("Ouvrir dans un nouvel onglet", () => Pane.AddTab(first.FullPath).FocusList(), "Clic milieu");
            AddMenu("Ouvrir dans l'autre panneau", () => host.OtherPane(Pane).Current?.Navigate(first.FullPath));
        }
        if (single && first.Kind == EntryKind.File)
            AddMenu("Ouvrir avec…", () => NativeMethods.ShellExec(Handle, first.FullPath, "openas"));
        AddSeparator();
        if (ops)
        {
            AddMenu("Couper", () => ToClipboard(true), "Ctrl+X");
            AddMenu("Copier", () => ToClipboard(false), "Ctrl+C");
        }
        AddMenu("Coller", Paste, "Ctrl+V", real && ClipboardHasFiles());
        if (ops)
        {
            AddMenu("Copier vers l'autre panneau", () => TransferToOther(false), "F5");
            AddMenu("Déplacer vers l'autre panneau", () => TransferToOther(true), "F6");
        }
        AddSeparator();
        AddMenu("Copier le chemin", CopyPath, "Ctrl+Maj+C");
        if (ops)
        {
            AddMenu("Renommer", BeginRename, "F2", single);
            AddMenu("Supprimer", () => DeleteSelected(true), "Suppr");
        }
        AddSeparator();
        if (single && first.IsContainer) AddMenu("Ajouter aux favoris", () => host.AddFavorite(first.FullPath));
        AddMenu("Propriétés", ShowProperties, "Alt+Entrée");
    }

    void BuildBackgroundMenu()
    {
        bool real = CurrentPath.Length > 0;
        AddMenu("Nouveau dossier", NewFolder, "F7", real);
        AddMenu("Nouveau document texte", NewTextFile, null, real);
        AddSeparator();
        AddMenu("Coller", Paste, "Ctrl+V", real && ClipboardHasFiles());
        AddSeparator();
        AddMenu("Actualiser", ReloadView, "Ctrl+R");
        AddMenu("Afficher les fichiers cachés", host.ToggleHidden, "Ctrl+H").Checked = AppState.ShowHidden;
        AddMenu("Ouvrir un terminal ici", OpenTerminal, null, real);
        AddMenu("Ajouter aux favoris", () => host.AddFavorite(CurrentPath), "Ctrl+D");
        AddMenu("Propriétés", ShowProperties, null, real);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            loadCts?.Cancel();
            watcher?.Dispose();
            refreshTimer.Dispose();
            menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
