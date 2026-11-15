using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Paths;

namespace WindowsReborn.Backup;

/// <summary>Perfil de backup reutilizable (<c>config/profiles/*.json</c>).</summary>
public sealed class BackupProfile
{
    /// <summary>1 = carpetas guardadas como ruta absoluta; 2 = raíz simbólica + subruta.</summary>
    public const int CurrentSchema = 2;

    public int SchemaVersion { get; set; } = CurrentSchema;
    public string Name { get; set; } = "";
    public List<string> Categories { get; set; } = [];
    public List<CustomFolderEntry> CustomFolders { get; set; } = [];
    public List<string> Adapters { get; set; } = [];
    public List<string> Exclusions { get; set; } = [.. BackupDefaults.Exclusions];
    public string Format { get; set; } = "zip";
    public string Compression { get; set; } = "balanced";
    public string? DestinationFolder { get; set; }
    public bool IncludeProgramInstallers { get; set; }
    public bool IncludeWinGetList { get; set; } = true;

    /// <summary>Actualiza en memoria un perfil de una versión anterior. Devuelve <c>true</c> si cambió.</summary>
    public bool Migrate()
    {
        var changed = false;
        foreach (var f in CustomFolders)
            changed |= f.Migrate();
        if (SchemaVersion < CurrentSchema)
        {
            SchemaVersion = CurrentSchema;
            changed = true;
        }
        return changed;
    }
}

/// <summary>
/// Carpeta personalizada de un perfil. Se guarda como raíz simbólica + subruta
/// (<c>{RoamingAppData}</c> + <c>.minecraft</c>) para que se adapte a otro usuario o
/// equipo; la ruta absoluta solo decide en carpetas fuera del perfil de usuario.
/// </summary>
public sealed class CustomFolderEntry
{
    /// <summary>Ruta absoluta original (también es el formato v1).</summary>
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Root { get; set; } = "";
    public string SubPath { get; set; } = "";
    public bool Selected { get; set; } = true;
    public bool IsProjects { get; set; }
    /// <summary>Exclusiones propias de esta carpeta (se suman a las generales).</summary>
    public List<string> Exclusions { get; set; } = [];
    /// <summary>Destino de restauración elegido (vacío = el mismo sitio adaptado).</summary>
    public string? RestoreTarget { get; set; }

    /// <summary>Crea una entrada desde una ruta; acepta variables como <c>%APPDATA%\.minecraft</c>.</summary>
    public static CustomFolderEntry FromPath(string folder, bool isProjects = false)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"'))));
        var (root, sub) = SourceFactory.Symbolize(full);
        var name = System.IO.Path.GetFileName(full);
        return new CustomFolderEntry
        {
            Name = string.IsNullOrEmpty(name) ? full.TrimEnd(':', '\\') : name,
            Root = root,
            SubPath = sub,
            Path = full,
            IsProjects = isProjects,
        };
    }

    /// <summary>Ruta en este equipo (puede no existir).</summary>
    public string? Resolve()
    {
        if (Root.Length > 0 && !KnownRoots.IsCustom(Root))
        {
            var root = KnownRoots.Resolve(Root);
            if (root is null) return null;
            return SubPath.Length == 0 ? root : System.IO.Path.Combine(root, SubPath);
        }
        return Path.Length > 0 ? Path : null;
    }

    public bool Exists => Resolve() is { } p && Directory.Exists(p);

    /// <summary>Texto legible: <c>%APPDATA%\.minecraft</c>, <c>%USERPROFILE%\…</c> o la ruta.</summary>
    public string DisplayPath
    {
        get
        {
            var token = Root switch
            {
                KnownRoots.RoamingAppData => "%APPDATA%",
                KnownRoots.LocalAppData => "%LOCALAPPDATA%",
                _ => null,
            };
            if (token is not null)
                return SubPath.Length == 0 ? token : token + "\\" + SubPath;
            var resolved = Resolve();
            return resolved is null ? Root + (SubPath.Length > 0 ? "\\" + SubPath : "") : KnownRoots.Friendly(resolved);
        }
    }

    internal bool Migrate()
    {
        if (Root.Length > 0 || Path.Length == 0) return false;
        var m = FromPath(Path, IsProjects);
        Root = m.Root;
        SubPath = m.SubPath;
        if (Name.Length == 0) Name = m.Name;
        return true;
    }
}

public sealed class ProfileStore(string configDirectory)
{
    private string Folder => Path.Combine(configDirectory, ConfigStore.ProfilesFolder);

    public async Task<List<BackupProfile>> LoadAllAsync()
    {
        var list = new List<BackupProfile>();
        if (!Directory.Exists(Folder))
            return list;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                var p = await Json.ReadFileAsync<BackupProfile>(file);
                if (p is not null && !string.IsNullOrWhiteSpace(p.Name))
                {
                    if (p.Migrate())
                        await Json.WriteFileAtomicAsync(file, p);
                    list.Add(p);
                }
            }
            catch
            {
                // Un perfil dañado no impide cargar los demás.
            }
        }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task SaveAsync(BackupProfile profile) =>
        Json.WriteFileAtomicAsync(SafePath.Combine(Folder, FileNameFor(profile.Name)), profile);

    public void Delete(string name)
    {
        var path = SafePath.Combine(Folder, FileNameFor(name));
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Selección de trabajo actual: se guarda sola y viaja en cada backup.</summary>
    public const string SelectionFile = "backup-selection.json";

    public async Task<BackupProfile?> LoadSelectionAsync()
    {
        var path = System.IO.Path.Combine(configDirectory, SelectionFile);
        if (!File.Exists(path)) return null;
        try
        {
            var p = await Json.ReadFileAsync<BackupProfile>(path);
            p?.Migrate();
            return p;
        }
        catch
        {
            return null;
        }
    }

    public Task SaveSelectionAsync(BackupProfile selection) =>
        Json.WriteFileAtomicAsync(System.IO.Path.Combine(configDirectory, SelectionFile), selection);

    internal static string FileNameFor(string name) => SourceFactory.Slug(name) + ".json";
}
