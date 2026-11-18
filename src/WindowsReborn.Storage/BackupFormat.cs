namespace WindowsReborn.Storage;

/// <summary>Formato de salida del backup.</summary>
public enum BackupFormat
{
    Zip,
    SevenZip,
    Folder,
}

public enum CompressionPreset
{
    Fast,
    Balanced,
    Maximum,
    Store,
}

public static class BackupFormatExtensions
{
    public static string Extension(this BackupFormat format) => format switch
    {
        BackupFormat.Zip => ".wrbk.zip",
        BackupFormat.SevenZip => ".wrbk.7z",
        _ => "",
    };

    public static string Id(this BackupFormat format) => format switch
    {
        BackupFormat.Zip => "zip",
        BackupFormat.SevenZip => "7z",
        _ => "folder",
    };

    /// <summary>Deduce el formato a partir de una ruta existente.</summary>
    public static BackupFormat? Detect(string path)
    {
        if (Directory.Exists(path))
            return File.Exists(Path.Combine(path, BackupManifest.FileName)) ? BackupFormat.Folder : null;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return BackupFormat.Zip;
        if (path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            return BackupFormat.SevenZip;
        if (Path.GetFileName(path).Equals(BackupManifest.FileName, StringComparison.OrdinalIgnoreCase))
            return BackupFormat.Folder;
        return null;
    }
}
