using WindowsReborn.Core.Paths;
using WindowsReborn.Storage;

namespace WindowsReborn.Backup;

public enum CheckState
{
    Ok,
    Warning,
    Error,
    Pending,
}

/// <summary>Resultado de una comprobación previa al formateo. <see cref="Key"/> sirve para el texto traducido.</summary>
public sealed record ReadinessCheck(string Key, CheckState State, string DetailKey, IReadOnlyList<object> Args);

/// <summary>Lo que se sabe del backup elegido y de la configuración actual.</summary>
public sealed class ReadinessInput
{
    public string? BackupPath { get; init; }
    public BackupManifest? Manifest { get; init; }
    /// <summary><c>true</c> verificado, <c>false</c> con errores, <c>null</c> sin verificar.</summary>
    public bool? Verified { get; init; }
    public BackupProfile? Selection { get; init; }
    public int LibraryPrograms { get; init; }
    public DateTime? ConfigChangedUtc { get; init; }
    public string SystemDrive { get; init; } = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    public bool AppCopiedNextToBackup { get; init; }
}

/// <summary>
/// Comprobaciones del asistente «Preparar para formatear». Solo informa: nunca
/// formatea ni cambia nada.
/// </summary>
public static class FormatReadiness
{
    public static IReadOnlyList<ReadinessCheck> Evaluate(ReadinessInput input)
    {
        var list = new List<ReadinessCheck>();
        var m = input.Manifest;

        // 1. Existe un backup legible.
        if (m is null || input.BackupPath is null)
        {
            list.Add(new("exists", CheckState.Error, "exists.none", []));
            return list;
        }
        list.Add(new("exists", CheckState.Ok, "exists.ok", [m.CreatedUtc.ToLocalTime(), m.DataFiles.Count()]));

        // 2. Integridad.
        list.Add(input.Verified switch
        {
            true => new("integrity", CheckState.Ok, "integrity.ok", []),
            false => new("integrity", CheckState.Error, "integrity.bad", []),
            _ => new("integrity", CheckState.Pending, "integrity.pending", []),
        });

        // 3. Carpetas personalizadas seleccionadas que están en el backup.
        var wanted = input.Selection?.CustomFolders.Where(f => f.Selected && f.Exists).ToList() ?? [];
        var missing = wanted.Where(f => !m.Categories.Any(c => c.Kind == "customFolder" && Same(c, f))).Select(f => f.DisplayPath).ToList();
        if (wanted.Count == 0)
            list.Add(new("custom", CheckState.Ok, "custom.none", []));
        else if (missing.Count == 0)
            list.Add(new("custom", CheckState.Ok, "custom.ok", [wanted.Count]));
        else
            list.Add(new("custom", CheckState.Warning, "custom.missing", [string.Join(", ", missing)]));

        // 4. Configuración de Windows Reborn.
        var configFiles = m.Files.Where(f => f.Section == Sections.Config).Select(f => f.Path).ToList();
        if (configFiles.Count == 0)
            list.Add(new("config", CheckState.Error, "config.none", []));
        else if (input.ConfigChangedUtc is { } changed && changed > m.CreatedUtc.AddSeconds(5))
            list.Add(new("config", CheckState.Warning, "config.changed", []));
        else
            list.Add(new("config", CheckState.Ok, "config.ok", [configFiles.Count]));

        // 5. Biblioteca de programas.
        var hasLibrary = configFiles.Any(p => p.EndsWith("/programs.json", StringComparison.OrdinalIgnoreCase));
        if (input.LibraryPrograms == 0)
            list.Add(new("programs", CheckState.Ok, "programs.empty", []));
        else
            list.Add(hasLibrary
                ? new("programs", CheckState.Ok, "programs.ok", [input.LibraryPrograms])
                : new("programs", CheckState.Warning, "programs.missing", [input.LibraryPrograms]));

        // 6. Instaladores, solo si se eligió guardarlos.
        if (input.Selection?.IncludeProgramInstallers == true)
            list.Add(m.HasInstallers
                ? new("installers", CheckState.Ok, "installers.ok", [m.Files.Count(f => f.Section == Sections.Installers && !f.Path.EndsWith("index.json", StringComparison.OrdinalIgnoreCase))])
                : new("installers", CheckState.Warning, "installers.missing", []));

        // 7. Ubicación: no en la unidad de Windows.
        var root = Path.GetPathRoot(Path.GetFullPath(input.BackupPath)) ?? "";
        if (root.Equals(input.SystemDrive, StringComparison.OrdinalIgnoreCase))
            list.Add(new("location", CheckState.Error, "location.system", [root.TrimEnd('\\')]));
        else
        {
            DriveType type;
            try { type = new DriveInfo(root).DriveType; } catch { type = DriveType.Unknown; }
            list.Add(new("location", type is DriveType.Removable or DriveType.Network ? CheckState.Ok : CheckState.Warning,
                type is DriveType.Removable or DriveType.Network ? "location.external" : "location.other-disk", [root.TrimEnd('\\')]));
        }

        // 8. Recuperable desde otra instalación.
        if (m.Encrypted)
            list.Add(new("recoverable", CheckState.Warning, "recoverable.encrypted", []));
        else if (m.Format == "7z")
            list.Add(new("recoverable", CheckState.Warning, "recoverable.7z", []));
        else
            list.Add(new("recoverable", input.AppCopiedNextToBackup ? CheckState.Ok : CheckState.Warning,
                input.AppCopiedNextToBackup ? "recoverable.ok" : "recoverable.copy-app", []));

        return list;
    }

    private static bool Same(CategoryInfo c, CustomFolderEntry f)
    {
        if (!string.IsNullOrEmpty(c.OriginalPath) && f.Resolve() is { } p &&
            string.Equals(Path.TrimEndingDirectorySeparator(c.OriginalPath), Path.TrimEndingDirectorySeparator(p), StringComparison.OrdinalIgnoreCase))
            return true;
        if (!KnownRoots.IsCustom(f.Root) && c.Root == f.Root && string.Equals(c.SubPath, f.SubPath, StringComparison.OrdinalIgnoreCase))
            return true;
        return KnownRoots.IsCustom(c.Root) && string.Equals(c.Name, f.Name, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReady(IReadOnlyList<ReadinessCheck> checks) => checks.All(c => c.State is CheckState.Ok or CheckState.Warning) && checks.Any();
}
