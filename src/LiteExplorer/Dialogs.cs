namespace LiteExplorer;

internal interface IExplorerHost
{
    FilePane ActivePane { get; }
    FilePane OtherPane(FilePane pane);
    void SetActivePane(FilePane pane);
    void AddFavorite(string path);
    void ToggleHidden();
    void ViewPathChanged(FolderView view);
}

internal static class Dialogs
{
    public const string AppName = "LiteExplorer";

    public static string? InputBox(IWin32Window owner, string title, string prompt, string value)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Font,
            Font = SystemFonts.MessageBoxFont,
            ClientSize = new Size(380, 110),
        };
        var label = new Label { Text = prompt, Left = 12, Top = 12, AutoSize = true };
        var box = new TextBox { Left = 12, Top = 36, Width = 356, Text = value };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 212, Top = 72, Width = 75 };
        var cancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Left = 293, Top = 72, Width = 75 };
        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }

    /// <summary>Fixe la position d'un séparateur sans lever d'exception si la place manque.</summary>
    public static void SetSplitter(SplitContainer split, int distance)
    {
        int max = split.Width - split.Panel2MinSize - split.SplitterWidth;
        if (max <= split.Panel1MinSize) return;
        try { split.SplitterDistance = Math.Clamp(distance, split.Panel1MinSize, max); } catch { }
    }

    public static void Error(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
