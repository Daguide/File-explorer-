using System.Globalization;

namespace LiteExplorer;

internal enum EntryKind { Parent, Drive, Folder, File }

internal sealed class FileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required EntryKind Kind { get; init; }
    public long Size { get; init; } = -1;
    public DateTime? Modified { get; init; }
    public FileAttributes Attributes { get; init; }
    public string Extension { get; init; } = "";
    /// <summary>Texte libre (espace libre pour un lecteur).</summary>
    public string Info { get; init; } = "";

    public bool IsContainer => Kind is EntryKind.Folder or EntryKind.Drive;
    public bool IsRealItem => Kind is EntryKind.Folder or EntryKind.File;

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "";
        if (bytes < 1024) return bytes + " o";
        string[] units = { "Ko", "Mo", "Go", "To", "Po" };
        double v = bytes;
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        string fmt = v < 10 ? "0.##" : v < 100 ? "0.#" : "0";
        return v.ToString(fmt, CultureInfo.CurrentCulture) + " " + units[i];
    }

    public static bool PathEquals(string? a, string? b) =>
        string.Equals(a?.TrimEnd('\\', '/'), b?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
