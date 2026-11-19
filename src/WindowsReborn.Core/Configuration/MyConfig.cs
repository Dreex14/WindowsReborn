using System.IO.Compression;
using System.Text.Json;

namespace WindowsReborn.Core.Configuration;

/// <summary>
/// «Mi configuración»: todo lo que el usuario ha elegido (optimizaciones, programas,
/// apps de GitHub y qué copiar) en un .zip que se importa tras cada formateo.
/// No contiene archivos personales ni rutas con el nombre de usuario.
/// </summary>
public sealed class MyConfig
{
    public const int CurrentSchema = 1;
    public const string EntryName = "windows-reborn-config.json";

    public const string PartPrivacy = "privacy";
    public const string PartOptimize = "optimize";
    public const string PartPrograms = "programs";
    public const string PartBackup = "backup";
    public static IReadOnlyList<string> AllParts { get; } = [PartPrivacy, PartOptimize, PartPrograms, PartBackup];

    public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>Secciones que contiene el archivo (vacío = todas, archivos antiguos).</summary>
    public List<string> Parts { get; set; } = [];

    public bool Has(string part) => Parts.Count == 0 || Parts.Contains(part, StringComparer.OrdinalIgnoreCase);
    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Id del ajuste → opción (lo que se aplica de nuevo).</summary>
    public Dictionary<string, string> Tweaks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Permisos de cada app (permiso → app → Allow/Deny), parte de Privacidad.</summary>
    public Dictionary<string, Dictionary<string, string>> AppPermissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Programs { get; set; } = [];
    public string? GitHubUser { get; set; }
    public List<string> GitHubRepos { get; set; } = [];
    public List<string> ExtraRepos { get; set; } = [];
    public List<string> BackupItems { get; set; } = [];
    public List<string> CustomFolders { get; set; } = [];

    public static MyConfig FromSettings(AppSettings s, IReadOnlyDictionary<string, string> tweaks, IEnumerable<string> programs,
        IEnumerable<string> backupItems, string appVersion) => new()
    {
        AppVersion = appVersion,
        Parts = [.. AllParts],
        Tweaks = new Dictionary<string, string>(tweaks, StringComparer.OrdinalIgnoreCase),
        Programs = programs.ToList(),
        GitHubUser = s.GitHubUser,
        GitHubRepos = [.. s.SelectedRepos],
        ExtraRepos = [.. s.ExtraRepos],
        BackupItems = backupItems.ToList(),
        CustomFolders = s.CustomFolders.Select(ToPortable).ToList(),
    };

    /// <summary>Copia las selecciones a los ajustes de la app.</summary>
    public void ApplyTo(AppSettings s)
    {
        if (Has(PartPrograms))
        {
            s.SelectedPrograms = [.. Programs];
            if (!string.IsNullOrWhiteSpace(GitHubUser)) s.GitHubUser = GitHubUser;
            s.SelectedRepos = [.. GitHubRepos];
            s.ExtraRepos = [.. ExtraRepos];
        }
        if (Has(PartBackup))
        {
            s.BackupItems = [.. BackupItems];
            s.CustomFolders = [.. CustomFolders];
        }
    }

    /// <summary>Sustituye la carpeta del usuario por variables (<c>%USERPROFILE%</c>, <c>%APPDATA%</c>…).</summary>
    public static string ToPortable(string path)
    {
        (string Var, Environment.SpecialFolder Folder)[] map =
        [
            ("%APPDATA%", Environment.SpecialFolder.ApplicationData),
            ("%LOCALAPPDATA%", Environment.SpecialFolder.LocalApplicationData),
            ("%USERPROFILE%", Environment.SpecialFolder.UserProfile),
        ];
        foreach (var (v, f) in map)
        {
            var folder = Environment.GetFolderPath(f);
            if (folder.Length > 0 && (path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                                      path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)))
                return v + path[folder.Length..];
        }
        return path;
    }

    public async Task SaveZipAsync(string zipPath, CancellationToken ct = default)
    {
        var temp = zipPath + ".tmp";
        await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(EntryName, CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, this, Json.Options, ct);
        }
        File.Move(temp, zipPath, overwrite: true);
    }

    /// <summary>Lee un .zip de configuración (o el .json suelto).</summary>
    public static async Task<MyConfig> LoadAsync(string path, CancellationToken ct = default)
    {
        MyConfig? config;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            config = await Json.ReadFileAsync<MyConfig>(path, ct);
        }
        else
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(EntryName) ?? throw new InvalidDataException("not-a-config");
            await using var stream = entry.Open();
            config = await JsonSerializer.DeserializeAsync<MyConfig>(stream, Json.Options, ct);
        }
        if (config is null || config.SchemaVersion > CurrentSchema)
            throw new InvalidDataException("not-a-config");
        config.Tweaks = new Dictionary<string, string>(config.Tweaks, StringComparer.OrdinalIgnoreCase);
        return config;
    }

    /// <summary>Indica si el .zip contiene una configuración de Windows Reborn.</summary>
    public static bool IsConfigZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry(EntryName) is not null;
        }
        catch
        {
            return false;
        }
    }
}
