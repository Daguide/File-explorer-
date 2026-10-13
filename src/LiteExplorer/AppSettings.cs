using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteExplorer;

internal static class AppState
{
    public static bool ShowHidden;
}

internal sealed class Favorite
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

internal sealed class FavoriteBarData
{
    public string Name { get; set; } = "Favoris";
    public bool Visible { get; set; } = true;
    public List<Favorite> Items { get; set; } = new();
}

/// <summary>
/// Réglages persistés en JSON dans %AppData%\LiteExplorer\settings.json,
/// ou à côté de l'exécutable si un fichier LiteExplorer.json y existe (mode portable).
/// </summary>
internal sealed class AppSettings
{
    public List<FavoriteBarData> FavoriteBars { get; set; } = new();
    /// <summary>Ancien format (une seule barre) : lu pour la migration, plus écrit.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Favorite>? Favorites { get; set; }
    public List<string> LeftTabs { get; set; } = new();
    public int LeftIndex { get; set; }
    public List<string> RightTabs { get; set; } = new();
    public int RightIndex { get; set; }
    public bool ShowHidden { get; set; }
    /// <summary>System, Light ou Dark.</summary>
    public string Theme { get; set; } = "System";
    public double SplitRatio { get; set; } = 0.5;
    public int WinX { get; set; }
    public int WinY { get; set; }
    public int WinW { get; set; }
    public int WinH { get; set; }
    public bool Maximized { get; set; }

    static string FilePath
    {
        get
        {
            var portable = System.IO.Path.Combine(AppContext.BaseDirectory, "LiteExplorer.json");
            if (File.Exists(portable)) return portable;
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LiteExplorer", "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) is { } s)
                return s.Migrated();
        }
        catch { /* fichier corrompu : on repart des valeurs par défaut */ }
        return new AppSettings { Favorites = DefaultFavorites() }.Migrated();
    }

    AppSettings Migrated()
    {
        if (FavoriteBars.Count == 0)
            FavoriteBars.Add(new FavoriteBarData { Name = "Favoris", Items = Favorites ?? DefaultFavorites() });
        Favorites = null;
        return this;
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    static List<Favorite> DefaultFavorites()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<Favorite>();
        void Add(string name, string path)
        {
            if (path.Length == 0 || Directory.Exists(path)) list.Add(new Favorite { Name = name, Path = path });
        }
        Add("Ce PC", "");
        Add("Bureau", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Téléchargements", System.IO.Path.Combine(user, "Downloads"));
        Add("Images", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("Musique", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add("Vidéos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        return list;
    }
}
