using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsReborn.Core.Paths;
using WindowsReborn.Storage;

namespace WindowsReborn.Backup;

/// <summary>A qué zip va cada elemento.</summary>
public enum BackupGroup
{
    /// <summary>Configuración de aplicaciones (zip pequeño).</summary>
    Apps,
    /// <summary>Carpetas personales (documentos, imágenes…), en un zip aparte.</summary>
    Files,
}

/// <summary>Elemento que se puede marcar en «Copia de seguridad».</summary>
public sealed record BackupItemDef
{
    public required string Id { get; init; }
    /// <summary>Clave de localización del nombre (o el nombre literal si no hay traducción).</summary>
    public required string NameKey { get; init; }
    public BackupGroup Group { get; init; }
    public required string Root { get; init; }
    public string SubPath { get; init; } = "";
    /// <summary>Lista blanca; vacía = toda la carpeta.</summary>
    public IReadOnlyList<string> Include { get; init; } = [];
    public IReadOnlyList<string> Exclude { get; init; } = [];
    public IReadOnlyList<string> Processes { get; init; } = [];
    /// <summary>Navegador basado en Chromium: se copian marcadores y ajustes de todos los perfiles.</summary>
    public bool Chromium { get; init; }
    /// <summary>Navegador basado en Firefox: <c>profiles.ini</c> y los perfiles de la carpeta Profiles.</summary>
    public bool Gecko { get; init; }
    public string Icon { get; init; } = "Folder24";
    public bool Preselected { get; init; }
}

/// <summary>Elementos de copia conocidos y construcción de los planes (un zip por grupo).</summary>
public static class BackupCatalog
{
    /// <summary>Lo que se guarda de cada perfil de un navegador Chromium (nunca cookies ni contraseñas).</summary>
    public static IReadOnlyList<string> ChromiumProfileFiles { get; } =
        ["Bookmarks", "Preferences", "Custom Dictionary.txt", "Extensions/", "Local Extension Settings/", "Extension State/"];

    /// <summary>Lo que se guarda de cada perfil de Firefox, Zen o LibreWolf (nunca contraseñas ni cookies).</summary>
    public static IReadOnlyList<string> GeckoProfileFiles { get; } =
    [
        "places.sqlite", "favicons.sqlite", "prefs.js", "user.js", "search.json.mozlz4", "containers.json", "handlers.json", "xulstore.json",
        "extensions.json", "extension-settings.json", "extension-preferences.json", "extensions/", "browser-extension-data/", "bookmarkbackups/",
    ];

    public const string ChromiumExtraPrefix = "browsers/";

    public static IReadOnlyList<BackupItemDef> All { get; } =
    [
        // Aplicaciones
        new() { Id = "brave", NameKey = "item.brave", Root = KnownRoots.LocalAppData, SubPath = @"BraveSoftware\Brave-Browser\User Data",
            Chromium = true, Processes = ["brave"], Icon = "Globe24", Preselected = true },
        new() { Id = "chrome", NameKey = "item.chrome", Root = KnownRoots.LocalAppData, SubPath = @"Google\Chrome\User Data",
            Chromium = true, Processes = ["chrome"], Icon = "Globe24" },
        new() { Id = "edge", NameKey = "item.edge", Root = KnownRoots.LocalAppData, SubPath = @"Microsoft\Edge\User Data",
            Chromium = true, Processes = ["msedge"], Icon = "Globe24" },
        new() { Id = "firefox", NameKey = "item.firefox", Root = KnownRoots.RoamingAppData, SubPath = @"Mozilla\Firefox",
            Gecko = true, Processes = ["firefox"], Icon = "Globe24" },
        new() { Id = "opera", NameKey = "item.opera", Root = KnownRoots.RoamingAppData, SubPath = @"Opera Software\Opera Stable",
            Include = ChromiumProfileFiles, Processes = ["opera"], Icon = "Globe24" },
        new() { Id = "operagx", NameKey = "item.operagx", Root = KnownRoots.RoamingAppData, SubPath = @"Opera Software\Opera GX Stable",
            Include = ChromiumProfileFiles, Processes = ["opera"], Icon = "Globe24" },
        new() { Id = "vivaldi", NameKey = "item.vivaldi", Root = KnownRoots.LocalAppData, SubPath = @"Vivaldi\User Data",
            Chromium = true, Processes = ["vivaldi"], Icon = "Globe24" },
        new() { Id = "helium", NameKey = "item.helium", Root = KnownRoots.LocalAppData, SubPath = @"imput\Helium\User Data",
            Chromium = true, Processes = ["chrome"], Icon = "Globe24" },
        new() { Id = "zen", NameKey = "item.zen", Root = KnownRoots.RoamingAppData, SubPath = "zen",
            Gecko = true, Processes = ["zen"], Icon = "Globe24" },
        new() { Id = "librewolf", NameKey = "item.librewolf", Root = KnownRoots.RoamingAppData, SubPath = "librewolf",
            Gecko = true, Processes = ["librewolf"], Icon = "Globe24" },
        new() { Id = "minecraft", NameKey = "item.minecraft", Root = KnownRoots.RoamingAppData, SubPath = ".minecraft",
            Exclude = ["logs/", "crash-reports/", "webcache2/"], Processes = ["javaw", "Minecraft", "MinecraftLauncher"], Icon = "Games24", Preselected = true },
        new() { Id = "obs", NameKey = "item.obs", Root = KnownRoots.RoamingAppData, SubPath = "obs-studio",
            Include = ["basic/", "global.ini", "user.ini"], Processes = ["obs64"], Icon = "Video24" },
        new() { Id = "vscode", NameKey = "item.vscode", Root = KnownRoots.RoamingAppData, SubPath = @"Code\User",
            Include = ["settings.json", "keybindings.json", "snippets/"], Processes = ["Code"], Icon = "Code24" },
        new() { Id = "terminal", NameKey = "item.terminal", Root = KnownRoots.LocalAppData,
            SubPath = @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState", Include = ["settings.json"], Processes = ["WindowsTerminal"], Icon = "WindowConsole20" },
        new() { Id = "git", NameKey = "item.git", Root = KnownRoots.UserProfile, Include = [".gitconfig"], Icon = "BranchFork24" },

        // Carpetas personales
        new() { Id = "pictures", NameKey = "item.pictures", Group = BackupGroup.Files, Root = KnownRoots.Pictures, Icon = "Image24", Preselected = true },
        new() { Id = "documents", NameKey = "item.documents", Group = BackupGroup.Files, Root = KnownRoots.Documents, Icon = "Document24" },
        new() { Id = "downloads", NameKey = "item.downloads", Group = BackupGroup.Files, Root = KnownRoots.Downloads, Icon = "ArrowDownload24" },
        new() { Id = "desktop", NameKey = "item.desktop", Group = BackupGroup.Files, Root = KnownRoots.Desktop, Icon = "Desktop24" },
        new() { Id = "videos", NameKey = "item.videos", Group = BackupGroup.Files, Root = KnownRoots.Videos, Icon = "VideoClip24" },
        new() { Id = "music", NameKey = "item.music", Group = BackupGroup.Files, Root = KnownRoots.Music, Icon = "MusicNote224" },
        new() { Id = "savedgames", NameKey = "item.savedgames", Group = BackupGroup.Files, Root = KnownRoots.SavedGames, Icon = "Trophy24" },
    ];

    public static BackupItemDef? Find(string id) => All.FirstOrDefault(i => i.Id == id);

    /// <summary>Carpeta del elemento en este equipo, o null si no existe.</summary>
    public static string? Resolve(BackupItemDef item)
    {
        var root = KnownRoots.Resolve(item.Root);
        if (root is null) return null;
        var path = item.SubPath.Length == 0 ? root : Path.Combine(root, item.SubPath);
        if (item.Include.Count > 0 && item.SubPath.Length == 0)
            return item.Include.Any(i => File.Exists(Path.Combine(path, i)) || Directory.Exists(Path.Combine(path, i.TrimEnd('/')))) ? path : null;
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>Perfiles de un navegador Chromium (Default, Profile 1…).</summary>
    public static IReadOnlyList<string> ChromiumProfiles(string userData)
    {
        if (!Directory.Exists(userData)) return [];
        return Directory.EnumerateDirectories(userData)
            .Select(Path.GetFileName)
            .Where(n => n is not null && (n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal)) &&
                        (File.Exists(Path.Combine(userData, n, "Preferences")) || File.Exists(Path.Combine(userData, n, "Bookmarks"))))
            .Select(n => n!)
            .OrderBy(n => n == "Default" ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Perfiles de un navegador basado en Firefox (carpetas de Profiles con prefs.js o places.sqlite).</summary>
    public static IReadOnlyList<string> GeckoProfiles(string root)
    {
        var profiles = Path.Combine(root, "Profiles");
        if (!Directory.Exists(profiles)) return [];
        return Directory.EnumerateDirectories(profiles)
            .Where(d => File.Exists(Path.Combine(d, "prefs.js")) || File.Exists(Path.Combine(d, "places.sqlite")))
            .Select(d => Path.GetFileName(d)!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Fuentes de copia de un elemento (una por perfil en los navegadores).</summary>
    public static IReadOnlyList<BackupSource> SourcesFor(BackupItemDef item, string displayName)
    {
        var folder = Resolve(item);
        if (folder is null) return [];
        if (item.Gecko)
        {
            var list = new List<BackupSource>
            {
                new()
                {
                    Id = $"{item.Id}-perfiles", Name = $"{displayName} · profiles.ini", Kind = SourceKind.AppConfig,
                    Root = item.Root, SubPath = item.SubPath, AbsolutePath = folder,
                    Include = ["profiles.ini", "installs.ini"], ProcessNames = item.Processes, AdapterId = item.Id,
                },
            };
            list.AddRange(GeckoProfiles(folder).Select(profile => new BackupSource
            {
                Id = $"{item.Id}-{SourceFactory.Slug(profile)}",
                Name = $"{displayName} · {profile}",
                Kind = SourceKind.AppConfig,
                Root = item.Root,
                SubPath = Path.Combine(item.SubPath, "Profiles", profile),
                AbsolutePath = Path.Combine(folder, "Profiles", profile),
                Include = GeckoProfileFiles,
                ProcessNames = item.Processes,
                AdapterId = item.Id,
            }));
            return list;
        }

        if (!item.Chromium)
        {
            return
            [
                new BackupSource
                {
                    Id = item.Id, Name = displayName,
                    Kind = item.Group == BackupGroup.Files ? SourceKind.KnownFolder : SourceKind.AppConfig,
                    Root = item.Root, SubPath = item.SubPath, AbsolutePath = folder,
                    Include = item.Include, ExtraExclusions = item.Exclude, ProcessNames = item.Processes, AdapterId = item.Id,
                },
            ];
        }

        return ChromiumProfiles(folder).Select(profile => new BackupSource
        {
            Id = $"{item.Id}-{SourceFactory.Slug(profile)}",
            Name = $"{displayName} · {profile}",
            Kind = SourceKind.AppConfig,
            Root = item.Root,
            SubPath = Path.Combine(item.SubPath, profile),
            AbsolutePath = Path.Combine(folder, profile),
            Include = ChromiumProfileFiles,
            ProcessNames = item.Processes,
            AdapterId = item.Id,
        }).ToList();
    }

    /// <summary>
    /// Lista de perfiles del navegador (solo nombres y avatares de <c>Local State</c>,
    /// sin la clave de cifrado), para que vuelvan a aparecer tras restaurar.
    /// </summary>
    public static ExtraFile? ChromiumProfileList(BackupItemDef item)
    {
        var folder = Resolve(item);
        if (folder is null || !item.Chromium) return null;
        var localState = Path.Combine(folder, "Local State");
        if (!File.Exists(localState)) return null;
        try
        {
            using var fs = new FileStream(localState, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var node = JsonNode.Parse(fs);
            var cache = node?["profile"]?["info_cache"];
            if (cache is null) return null;
            var data = JsonSerializer.SerializeToUtf8Bytes(cache);
            return new ExtraFile($"{ChromiumExtraPrefix}{item.Id}.json", Sections.Apps, null, data);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Añade los perfiles guardados a <c>Local State</c> sin tocar el resto (la clave de
    /// cifrado nueva se conserva). Si el navegador aún no se ha abierto, lo crea.
    /// </summary>
    public static void MergeChromiumProfileList(BackupItemDef item, byte[] savedInfoCache)
    {
        var root = KnownRoots.Resolve(item.Root);
        if (root is null) return;
        MergeChromiumProfileList(Path.Combine(root, item.SubPath), savedInfoCache);
    }

    /// <summary>Igual que la anterior, sobre una carpeta «User Data» concreta.</summary>
    public static void MergeChromiumProfileList(string folder, byte[] savedInfoCache)
    {
        Directory.CreateDirectory(folder);
        var localState = Path.Combine(folder, "Local State");

        var saved = JsonNode.Parse(savedInfoCache)?.AsObject();
        if (saved is null) return;

        JsonObject state;
        try { state = File.Exists(localState) ? JsonNode.Parse(File.ReadAllText(localState))?.AsObject() ?? new JsonObject() : new JsonObject(); }
        catch { return; } // Si no se puede leer, mejor no tocarlo.

        var profile = state["profile"] as JsonObject ?? new JsonObject();
        state["profile"] = profile;
        var cache = profile["info_cache"] as JsonObject ?? new JsonObject();
        profile["info_cache"] = cache;
        foreach (var (name, value) in saved)
        {
            if (!cache.ContainsKey(name))
                cache[name] = value?.DeepClone();
        }
        File.WriteAllText(localState, state.ToJsonString());
    }
}
