using System.IO.Compression;
using System.Text.Json;
using WindowsReborn.Core.Logging;
using WindowsReborn.Core.Paths;

namespace WindowsReborn.Core.Configuration;

/// <summary>
/// AlmacÃ©n de la configuraciÃ³n portable de Windows Reborn. Solo maneja una lista
/// blanca de archivos JSON conocidos, para que nunca se cuele en un backup nada que
/// no sea configuraciÃ³n de la propia aplicaciÃ³n.
/// </summary>
public sealed class ConfigStore
{
    /// <summary>VersiÃ³n del formato del paquete de configuraciÃ³n.</summary>
    public const int FormatVersion = 1;
    public const string ManifestName = "reborn-config.json";
    public const string ArchiveFolder = "reborn";
    public const string ProfilesFolder = "profiles";

    /// <summary>Archivos de configuraciÃ³n reconocidos (en la raÃ­z del directorio de configuraciÃ³n).</summary>
    public static IReadOnlyList<string> KnownFiles { get; } =
        ["settings.json", "programs.json", "tweaks.json", "backup-selection.json"];

    public ConfigStore(string configDirectory)
    {
        Directory = configDirectory;
    }

    public string Directory { get; }

    public string PathOf(string fileName) => Path.Combine(Directory, fileName);

    public bool HasAnyConfiguration =>
        KnownFiles.Any(f => File.Exists(PathOf(f))) ||
        (System.IO.Directory.Exists(Path.Combine(Directory, ProfilesFolder)) &&
         System.IO.Directory.EnumerateFiles(Path.Combine(Directory, ProfilesFolder), "*.json").Any());

    public async Task<AppSettings> LoadSettingsAsync()
    {
        var path = PathOf(AppSettings.FileName);
        if (!File.Exists(path))
            return new AppSettings();
        try
        {
            return await Json.ReadFileAsync<AppSettings>(path) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("settings.json could not be read; defaults used", ex);
            return new AppSettings();
        }
    }

    public Task SaveSettingsAsync(AppSettings settings) =>
        Json.WriteFileAtomicAsync(PathOf(AppSettings.FileName), settings);

    /// <summary>
    /// Enumera los archivos de configuraciÃ³n existentes como pares
    /// (ruta relativa dentro del paquete, ruta absoluta).
    /// </summary>
    public IEnumerable<(string Relative, string FullPath)> EnumerateFiles()
    {
        foreach (var name in KnownFiles)
        {
            var path = PathOf(name);
            if (File.Exists(path))
                yield return (name, path);
        }

        var profiles = Path.Combine(Directory, ProfilesFolder);
        if (System.IO.Directory.Exists(profiles))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(profiles, "*.json"))
                yield return ($"{ProfilesFolder}/{Path.GetFileName(file)}", file);
        }
    }

    /// <summary>Crea el contenido de <c>reborn-config.json</c>.</summary>
    public ConfigManifest CreateManifest(string appVersion) => new()
    {
        FormatVersion = FormatVersion,
        AppVersion = appVersion,
        CreatedUtc = DateTime.UtcNow,
        Files = EnumerateFiles().Select(f => f.Relative).ToList(),
    };

    /// <summary>Exporta la configuraciÃ³n a un archivo <c>.wrconfig</c> (ZIP).</summary>
    public async Task ExportAsync(string destinationFile, string appVersion)
    {
        var temp = destinationFile + ".partial";
        await using (var fs = File.Create(temp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (relative, full) in EnumerateFiles())
                zip.CreateEntryFromFile(full, relative, CompressionLevel.Optimal);
            var entry = zip.CreateEntry(ManifestName);
            await using var es = entry.Open();
            await JsonSerializer.SerializeAsync(es, CreateManifest(appVersion), Json.Options);
        }
        File.Move(temp, destinationFile, overwrite: true);
        Log.Info("Configuration exported");
    }

    /// <summary>
    /// Lee un paquete de configuraciÃ³n desde un proveedor de archivos (un ZIP
    /// <c>.wrconfig</c> o la carpeta <c>reborn/</c> de un backup) y lo valida sin
    /// escribir nada todavÃ­a.
    /// </summary>
    public static ConfigPackage ReadPackage(IReadOnlyDictionary<string, byte[]> files)
    {
        ConfigManifest? manifest = null;
        if (files.TryGetValue(ManifestName, out var manifestBytes))
            manifest = JsonSerializer.Deserialize<ConfigManifest>(manifestBytes, Json.Options);

        if (manifest is not null && manifest.FormatVersion > FormatVersion)
            throw new InvalidDataException($"Configuration format {manifest.FormatVersion} is newer than supported ({FormatVersion}).");

        var accepted = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var rejected = new List<string>();
        foreach (var (name, bytes) in files)
        {
            if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsAllowedRelative(name))
            {
                rejected.Add(name);
                continue;
            }
            try
            {
                using var _ = JsonDocument.Parse(bytes);
                accepted[name.Replace('\\', '/')] = bytes;
            }
            catch (JsonException)
            {
                rejected.Add(name);
            }
        }

        return new ConfigPackage(manifest, accepted, rejected);
    }

    public static ConfigPackage ReadPackageFromZip(string wrconfigPath)
    {
        using var zip = ZipFile.OpenRead(wrconfigPath);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.Length > 16 * 1024 * 1024)
                continue;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            files[entry.FullName] = ms.ToArray();
        }
        return ReadPackage(files);
    }

    /// <summary>
    /// Aplica un paquete validado. Antes de sobrescribir, guarda una copia de la
    /// configuraciÃ³n actual en <c>config.previous</c> para poder volver atrÃ¡s.
    /// </summary>
    public async Task ApplyPackageAsync(ConfigPackage package)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var previous = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Directory))!, "config.previous");
        if (HasAnyConfiguration)
        {
            if (System.IO.Directory.Exists(previous))
                System.IO.Directory.Delete(previous, recursive: true);
            System.IO.Directory.CreateDirectory(previous);
            foreach (var (relative, full) in EnumerateFiles())
            {
                var target = SafePath.Combine(previous, relative);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(full, target, overwrite: true);
            }
        }

        foreach (var (relative, bytes) in package.Files)
        {
            var target = SafePath.Combine(Directory, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, target, overwrite: true);
        }
        Log.Info($"Configuration imported ({package.Files.Count} files)");
    }

    internal static bool IsAllowedRelative(string name)
    {
        var n = name.Replace('\\', '/');
        if (KnownFiles.Contains(n, StringComparer.OrdinalIgnoreCase))
            return true;
        if (n.StartsWith(ProfilesFolder + "/", StringComparison.OrdinalIgnoreCase))
        {
            var file = n[(ProfilesFolder.Length + 1)..];
            return file.Length > 5 && !file.Contains('/') && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                   && SafeName(file);
        }
        return false;
    }

    private static bool SafeName(string file)
    {
        try { SafePath.NormalizeRelative(file); return true; }
        catch (UnsafePathException) { return false; }
    }
}

public sealed class ConfigManifest
{
    public int FormatVersion { get; set; }
    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public List<string> Files { get; set; } = [];
}

/// <summary>Paquete de configuraciÃ³n validado y listo para aplicarse.</summary>
public sealed record ConfigPackage(ConfigManifest? Manifest, IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<string> Rejected)
{
    public bool IsEmpty => Files.Count == 0;
}
