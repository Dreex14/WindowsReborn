using WindowsReborn.Core.Paths;
using WindowsReborn.Storage;

namespace WindowsReborn.Backup;

public sealed class DestinationCheck
{
    /// <summary>Códigos de error que impiden empezar (claves de localización).</summary>
    public List<string> Errors { get; } = [];
    /// <summary>Avisos que el usuario debe ver pero que no bloquean.</summary>
    public List<string> Warnings { get; } = [];
    public long FreeBytes { get; set; } = -1;
    public long RequiredBytes { get; set; }
}

/// <summary>Comprueba que el destino es seguro y tiene espacio.</summary>
public static class DestinationValidator
{
    private const long SafetyMargin = 64L * 1024 * 1024;

    public static DestinationCheck Validate(BackupPlan plan, long sourceBytes)
    {
        var check = new DestinationCheck();
        string destination;
        try
        {
            destination = Path.GetFullPath(plan.DestinationFolder);
        }
        catch
        {
            check.Errors.Add("dest.invalid");
            return check;
        }

        if (string.IsNullOrWhiteSpace(plan.Name) || plan.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            check.Errors.Add("dest.invalid-name");

        foreach (var source in plan.Sources)
        {
            if (SafePath.IsSameOrInside(destination, source.AbsolutePath))
                check.Errors.Add("dest.inside-source");
        }

        if (File.Exists(plan.FinalPath) || Directory.Exists(plan.FinalPath))
            check.Errors.Add("dest.exists");

        if (plan.Format == BackupFormat.SevenZip && plan.SevenZipPath is null)
            check.Errors.Add("dest.no-7zip");

        // La unidad del sistema se borra al formatear.
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        var destRoot = Path.GetPathRoot(destination);
        if (systemDrive is not null && string.Equals(systemDrive, destRoot, StringComparison.OrdinalIgnoreCase))
            check.Warnings.Add("dest.system-drive");

        try
        {
            if (destRoot is not null && !destRoot.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var drive = new DriveInfo(destRoot);
                check.FreeBytes = drive.AvailableFreeSpace;
                if (drive.DriveType == DriveType.Removable && drive.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase)
                    && plan.Format != BackupFormat.Folder && sourceBytes > uint.MaxValue)
                    check.Errors.Add("dest.fat32-limit");
            }
        }
        catch
        {
            check.Warnings.Add("dest.space-unknown");
        }

        // Peor caso: sin compresión. 7z necesita además la carpeta de preparación.
        check.RequiredBytes = sourceBytes + SafetyMargin;
        if (plan.Format == BackupFormat.SevenZip)
            check.RequiredBytes += sourceBytes;

        if (check.FreeBytes >= 0 && check.FreeBytes < check.RequiredBytes)
        {
            if (plan.Format == BackupFormat.Zip && check.FreeBytes >= sourceBytes / 4)
                check.Warnings.Add("dest.space-tight");
            else
                check.Errors.Add("dest.no-space");
        }

        return check;
    }
}
