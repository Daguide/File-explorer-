using System.Runtime.InteropServices;
using static LiteExplorer.NativeMethods;

namespace LiteExplorer;

/// <summary>
/// Cache d'icônes système partagé par toutes les vues. Les icônes sont
/// mises en cache par extension (sans accès disque) sauf pour les types
/// qui ont une icône propre à chaque fichier (.exe, .lnk, .ico…).
/// </summary>
internal static class ShellIcons
{
    public static readonly ImageList List = CreateList();

    static readonly Dictionary<string, int> iconCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> typeCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> perFileExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".ico", ".lnk", ".url", ".cur", ".ani", ".scr", ".msc", ".appref-ms",
    };
    const string ThisPcParsingName = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    static ImageList CreateList()
    {
        var size = SystemInformation.SmallIconSize;
        var list = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = size };
        _ = list.Handle;
        return list;
    }

    public static int FolderIndex => Lookup("<folder>", "folder", FILE_ATTRIBUTE_DIRECTORY, true);

    public static int GetIndex(FileEntry e)
    {
        switch (e.Kind)
        {
            case EntryKind.Parent:
            case EntryKind.Folder:
                return FolderIndex;
            case EntryKind.Drive:
                return Lookup("drive|" + e.FullPath, e.FullPath, 0, false);
            default:
                if (perFileExt.Contains(e.Extension))
                    return Lookup("file|" + e.FullPath, e.FullPath, 0, false);
                return Lookup("ext|" + e.Extension, "file" + e.Extension, FILE_ATTRIBUTE_NORMAL, true);
        }
    }

    /// <summary>Icône réelle d'un chemin (dossiers spéciaux, lecteurs…). "" = Ce PC.</summary>
    public static int ForPath(string path) =>
        path.Length == 0
            ? Lookup("<thispc>", ThisPcParsingName, 0, false)
            : Lookup("path|" + path, path, 0, false);

    static int Lookup(string key, string path, uint attrs, bool useAttrs)
    {
        if (iconCache.TryGetValue(key, out int idx)) return idx;
        idx = -1;
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | (useAttrs ? SHGFI_USEFILEATTRIBUTES : 0);
        IntPtr ok = SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (ok != IntPtr.Zero && info.hIcon != IntPtr.Zero)
        {
            try
            {
                using var icon = Icon.FromHandle(info.hIcon);
                List.Images.Add(icon.ToBitmap());
                idx = List.Images.Count - 1;
            }
            catch { idx = -1; }
            finally { DestroyIcon(info.hIcon); }
        }
        iconCache[key] = idx;
        return idx;
    }

    public static string TypeName(FileEntry e)
    {
        switch (e.Kind)
        {
            case EntryKind.Parent: return "";
            case EntryKind.Folder: return "Dossier de fichiers";
            case EntryKind.Drive: return e.Info;
        }
        if (typeCache.TryGetValue(e.Extension, out var name)) return name;
        var info = new SHFILEINFO();
        SHGetFileInfo("file" + e.Extension, FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
        name = string.IsNullOrEmpty(info.szTypeName)
            ? (e.Extension.Length > 0 ? "Fichier " + e.Extension.TrimStart('.').ToUpperInvariant() : "Fichier")
            : info.szTypeName;
        typeCache[e.Extension] = name;
        return name;
    }
}
