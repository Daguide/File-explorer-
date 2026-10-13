using static LiteExplorer.NativeMethods;

namespace LiteExplorer;

/// <summary>
/// Copie / déplacement / suppression via SHFileOperation : on profite des
/// dialogues natifs de Windows (progression, conflits, Corbeille, annuler).
/// Chaque opération tourne sur son propre thread pour ne pas figer l'interface.
/// </summary>
internal static class FileOps
{
    public static void Transfer(IWin32Window owner, string[] sources, string destDir, bool move)
    {
        if (sources.Length == 0 || destDir.Length == 0) return;
        bool sameDir = sources.All(s => FileEntry.PathEquals(Path.GetDirectoryName(s), destDir));
        if (move && sameDir) return;

        ushort flags = FOF_ALLOWUNDO;
        if (!move && sameDir) flags |= FOF_RENAMEONCOLLISION; // « fichier - Copie »
        Run(owner.Handle, move ? FO_MOVE : FO_COPY, sources, destDir, flags);
    }

    /// <summary>
    /// Effet d'un dépôt de fichiers sur un dossier, comme l'Explorateur :
    /// Ctrl = copier, Maj = déplacer, sinon déplacer sur le même volume et copier entre volumes.
    /// </summary>
    public static DragDropEffects DropEffect(string[] files, string target, int keyState, DragDropEffects allowed)
    {
        if (files.Length == 0 || target.Length == 0) return DragDropEffects.None;
        foreach (var f in files)
        {
            var src = f.TrimEnd('\\');
            if (FileEntry.PathEquals(src, target) || target.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase))
                return DragDropEffects.None; // on ne dépose pas un dossier dans lui-même
        }
        if (files.All(f => FileEntry.PathEquals(Path.GetDirectoryName(f), target))) return DragDropEffects.None;

        bool sameVolume = string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
        var want = (keyState & 8) != 0 ? DragDropEffects.Copy
                 : (keyState & 4) != 0 ? DragDropEffects.Move
                 : sameVolume ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((allowed & want) == 0)
            want = allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
                 : allowed.HasFlag(DragDropEffects.Move) ? DragDropEffects.Move
                 : DragDropEffects.None;
        return want;
    }

    public static void Delete(IWin32Window owner, string[] sources, bool recycle)
    {
        if (sources.Length == 0) return;
        ushort flags = FOF_WANTNUKEWARNING;
        if (recycle) flags |= FOF_ALLOWUNDO;
        Run(owner.Handle, FO_DELETE, sources, null, flags);
    }

    static void Run(IntPtr hwnd, uint func, string[] sources, string? dest, ushort flags)
    {
        var thread = new Thread(() =>
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = hwnd,
                wFunc = func,
                // Listes terminées par un double caractère nul.
                pFrom = string.Join("\0", sources) + "\0",
                pTo = dest == null ? null : dest + "\0",
                fFlags = flags,
            };
            SHFileOperation(ref op);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
