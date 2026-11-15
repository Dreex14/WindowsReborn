using WindowsReborn.Core.Catalog;
using WindowsReborn.Core.Paths;

namespace WindowsReborn.Backup;

/// <summary>Crea fuentes de backup a partir de categorías conocidas, carpetas y adaptadores.</summary>
public static class SourceFactory
{
    /// <summary>Categorías recomendadas (id, raíz).</summary>
    public static IReadOnlyList<(string Id, string Root)> Recommended { get; } =
    [
        ("desktop", KnownRoots.Desktop),
        ("documents", KnownRoots.Documents),
        ("downloads", KnownRoots.Downloads),
        ("pictures", KnownRoots.Pictures),
        ("videos", KnownRoots.Videos),
        ("music", KnownRoots.Music),
        ("savedgames", KnownRoots.SavedGames),
    ];

    public static BackupSource? ForKnownFolder(string id, string root, string displayName)
    {
        var path = KnownRoots.Resolve(root);
        if (path is null || !Directory.Exists(path))
            return null;
        return new BackupSource { Id = id, Name = displayName, Kind = SourceKind.KnownFolder, Root = root, AbsolutePath = path };
    }

    /// <summary>
    /// Convierte una ruta absoluta en raíz simbólica + subruta usando la carpeta
    /// conocida más específica que la contiene. Fuera de ellas, raíz personalizada.
    /// </summary>
    public static (string Root, string SubPath) Symbolize(string full)
    {
        var name = Path.GetFileName(full);
        string root = KnownRoots.MakeCustom(string.IsNullOrEmpty(name) ? full.TrimEnd(':', '\\') : name);
        string subPath = "";
        string? best = null;
        foreach (var token in KnownRoots.Standard)
        {
            var resolved = KnownRoots.Resolve(token);
            if (resolved is null || !SafePath.IsSameOrInside(full, resolved))
                continue;
            if (best is null || resolved.Length > best.Length)
            {
                best = resolved;
                root = token;
                subPath = full.Equals(resolved, StringComparison.OrdinalIgnoreCase) ? "" : Path.GetRelativePath(resolved, full);
            }
        }
        return (root, subPath);
    }

    /// <summary>Fuente para una carpeta elegida por el usuario (ruta absoluta).</summary>
    public static BackupSource ForCustomFolder(string folder, IEnumerable<string> usedIds, bool isProjects) =>
        ForCustomFolder(CustomFolderEntry.FromPath(folder, isProjects), usedIds)
        ?? throw new DirectoryNotFoundException(folder);

    /// <summary>Fuente para una carpeta personalizada guardada; <c>null</c> si no existe en este equipo.</summary>
    public static BackupSource? ForCustomFolder(CustomFolderEntry entry, IEnumerable<string> usedIds)
    {
        var full = entry.Resolve();
        if (full is null || !Directory.Exists(full))
            return null;
        var name = entry.Name.Length > 0 ? entry.Name : Path.GetFileName(full);
        var root = entry.Root.Length > 0 ? entry.Root : KnownRoots.MakeCustom(name);
        return new BackupSource
        {
            Id = UniqueId("custom-" + Slug(name), usedIds),
            Name = name,
            Kind = SourceKind.CustomFolder,
            Root = root,
            SubPath = KnownRoots.IsCustom(root) ? "" : entry.SubPath,
            AbsolutePath = full,
            ExtraExclusions = [.. (entry.IsProjects ? BackupDefaults.ProjectExclusions : []), .. entry.Exclusions],
        };
    }

    public static BackupSource? ForAdapter(AppConfigAdapter adapter)
    {
        var folder = AppConfigCatalog.ResolveFolder(adapter);
        if (folder is null)
            return null;
        return new BackupSource
        {
            Id = "app-" + adapter.Id,
            Name = adapter.Name,
            Kind = SourceKind.AppConfig,
            Root = adapter.Root,
            SubPath = adapter.SubPath,
            AbsolutePath = folder,
            Include = adapter.Include,
            ProcessNames = adapter.ProcessNames,
            AdapterId = adapter.Id,
        };
    }

    public static string Slug(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length == 0 ? "folder" : slug.Length > 40 ? slug[..40] : slug;
    }

    public static string UniqueId(string baseId, IEnumerable<string> used)
    {
        var set = used.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(baseId)) return baseId;
        for (var i = 2; ; i++)
            if (!set.Contains($"{baseId}-{i}")) return $"{baseId}-{i}";
    }
}
