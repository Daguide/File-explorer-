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
