# LiteExplorer

Remplaçant léger de l'Explorateur Windows, écrit en C# (WinForms, .NET 8).

- **Double panneau** côte à côte (Tab pour passer de l'un à l'autre)
- **Onglets façon fureteur Web** dans chaque panneau : `+`, `×`, clic milieu, Ctrl+T / Ctrl+W / Ctrl+Maj+T
- **Barre de favoris** : clic pour ouvrir, clic milieu pour un nouvel onglet, glisser un dossier dessus pour l'ajouter
- **Thème clair / sombre** (ou selon Windows) : menu *Affichage → Thème*
- **Aucune dépendance externe** : seulement .NET et les API de Windows. Pas de DLL tierce, pas de télémétrie,
  pas d'accès réseau. Tout le code est là, lisible (contrairement à Clover…).

Les copies, déplacements et suppressions passent par `SHFileOperation` : on obtient les vrais dialogues de
Windows (progression, conflits, Corbeille, Ctrl+Z dans l'Explorateur).

## Compiler

Prérequis : [SDK .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet run --project src/LiteExplorer
# ou un seul .exe (~230 Ko, utilise le runtime .NET 8 Desktop installé) :
dotnet publish src/LiteExplorer -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

On peut aussi ouvrir `LiteExplorer.sln` dans Visual Studio 2022.

Sortie de compilation (réglée dans `Directory.Build.props`, à la racine de la solution) :

| Commande | Résultat |
|---|---|
| Build Debug / Release (VS ou `dotnet build`) | `bin\Debug\` ou `bin\Release\` → `LiteExplorer.exe` + ses fichiers |
| `dotnet publish` (commande ci-dessus, sans `-o`) | `bin\Release\publish\LiteExplorer.exe` (fichier unique) | La CI GitHub (`.github/workflows/build.yml`)
produit `LiteExplorer.exe` à chaque push (onglet *Actions* → artefact `LiteExplorer-win-x64`).

## Raccourcis

| Touche | Action |
|---|---|
| Entrée / double-clic | Ouvrir |
| Retour arrière, Alt+↑, double-clic dans le vide | Dossier parent |
| Alt+← / Alt+→, boutons de souris Précédent/Suivant | Historique |
| Tab | Changer de panneau |
| Ctrl+T / Ctrl+W / Ctrl+Maj+T | Nouvel onglet / fermer / rouvrir le dernier fermé |
| Ctrl+Tab / Ctrl+Maj+Tab | Onglet suivant / précédent |
| Clic milieu sur un dossier | L'ouvrir dans un onglet en arrière-plan |
| Ctrl+L, Alt+D, F4 | Barre d'adresse (accepte `%APPDATA%`, `..`, chemins relatifs) |
| Ctrl+F | Filtrer (texte ou jokers : `*.pdf`) |
| F2 | Renommer |
| F5 / F6 | Copier / déplacer vers l'autre panneau |
| F7, Ctrl+Maj+N | Nouveau dossier |
| Suppr, F8 / Maj+Suppr | Corbeille / suppression définitive |
| Ctrl+C / Ctrl+X / Ctrl+V | Presse-papiers (compatible avec l'Explorateur) |
| Ctrl+Maj+C | Copier le chemin |
| Ctrl+D | Ajouter le dossier courant aux favoris |
| Ctrl+1 … Ctrl+9 | Ouvrir le n-ième favori (Ctrl+Maj = nouvel onglet) |
| Ctrl+H | Afficher/masquer les fichiers cachés |
| Ctrl+U | Échanger les deux panneaux |
| Ctrl+R | Actualiser (le dossier se met aussi à jour tout seul) |
| Alt+Entrée | Propriétés Windows |

Glisser-déposer entre les panneaux et avec l'Explorateur/le Bureau : déplacement sur le même disque,
copie entre disques, Ctrl force la copie, Maj force le déplacement.

## Thèmes

Menu **Affichage → Thème** : *Selon Windows* (suit le réglage « mode des applications » de Windows,
y compris quand il change pendant que l'app est ouverte), *Clair* ou *Sombre*. Le choix est mémorisé.
En sombre : barre de titre, onglets, en-têtes de colonnes, barres de défilement et menus sont sombres
(Windows 10 1809 ou plus récent).

## Réglages

Favoris, onglets ouverts, taille de la fenêtre, etc. sont enregistrés dans
`%APPDATA%\LiteExplorer\settings.json`.
**Mode portable** : créez un fichier vide `LiteExplorer.json` à côté de l'exe et les réglages y seront écrits.

## Ajouter « Ouvrir dans LiteExplorer » au clic droit des dossiers (optionnel)

Sans toucher au comportement par défaut de Windows (adaptez le chemin) :

```reg
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\Classes\Directory\shell\LiteExplorer]
@="Ouvrir dans LiteExplorer"

[HKEY_CURRENT_USER\Software\Classes\Directory\shell\LiteExplorer\command]
@="\"C:\\Outils\\LiteExplorer.exe\" \"%1\""
```

## Organisation du code

| Fichier | Rôle |
|---|---|
| `MainForm.cs` | Fenêtre : favoris, deux panneaux, barre de touches F, raccourcis globaux, sauvegarde |
| `FilePane.cs` | Un panneau = une rangée d'onglets dessinés à la main (`+`, `×`) |
| `FolderView.cs` | Contenu d'un onglet : adresse, filtre, liste virtuelle, historique, glisser-déposer, menus |
| `FavoritesBar.cs` | Barre de favoris |
| `FileOps.cs` | Copier / déplacer / supprimer via les dialogues natifs |
| `ShellIcons.cs` | Icônes et types de fichiers système, mis en cache |
| `Theme.cs` | Palettes clair/sombre, application du thème, rendu sombre des menus |
| `AppSettings.cs` | Réglages JSON |
| `NativeMethods.cs` | Appels Win32 |

## Pistes pour la suite

- Menu contextuel complet de Windows (7-Zip, Git, etc.) via `IContextMenu`
- Fil d'Ariane cliquable dans la barre d'adresse
- Réorganiser les onglets par glisser-déposer
- Aperçu des images / fichiers texte
