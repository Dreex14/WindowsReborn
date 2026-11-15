namespace WindowsReborn.Storage;

/// <summary>
/// Manifiesto de un backup (<c>manifest.json</c>). Es la fuente de verdad para
/// verificar y restaurar. Los lectores aceptan cualquier versión de formato igual o
/// anterior a <see cref="CurrentFormatVersion"/> e ignoran campos desconocidos.
/// </summary>
public sealed class BackupManifest
{
    public const int CurrentFormatVersion = 1;
    public const string FileName = "manifest.json";
    public const string ReportFileName = "report.json";

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string? MachineName { get; set; }
    public string? UserName { get; set; }
    public WindowsInfo Windows { get; set; } = new();
    public string Format { get; set; } = "zip";
    public bool Encrypted { get; set; }
    public List<CategoryInfo> Categories { get; set; } = [];
    public List<FileRecord> Files { get; set; } = [];
    public List<DirectoryRecord> EmptyDirectories { get; set; } = [];

    public long TotalBytes => Files.Sum(f => f.Size);

    public IEnumerable<FileRecord> DataFiles => Files.Where(f => f.Section == Sections.Data);

    public bool HasConfiguration => Files.Any(f => f.Section == Sections.Config);

    public bool HasInstallers => Files.Any(f => f.Section == Sections.Installers);
}

public static class Sections
{
    public const string Data = "data";
    public const string Config = "reborn";
    public const string Installers = "installers";
    public const string Apps = "apps";
}

public sealed class WindowsInfo
{
    public string ProductName { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public int Build { get; set; }
}

/// <summary>Categoría incluida (Documentos, carpeta personalizada…).</summary>
public sealed class CategoryInfo
{
    /// <summary>Identificador estable de la categoría dentro del backup (también es la carpeta bajo <c>data/</c>).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Raíz simbólica (<c>{Documents}</c>, <c>{Custom:Proyectos}</c>…).</summary>
    public string Root { get; set; } = "";
    /// <summary>Subcarpeta relativa a la raíz (vacía si es la raíz completa).</summary>
    public string SubPath { get; set; } = "";
    /// <summary>Ruta de origen informativa (puede omitirse si se anonimiza).</summary>
    public string? OriginalPath { get; set; }
    /// <summary><c>knownFolder</c>, <c>customFolder</c> o <c>appConfig</c>.</summary>
    public string Kind { get; set; } = "knownFolder";
    /// <summary>Id del adaptador de configuración de aplicación, si lo hay.</summary>
    public string? Adapter { get; set; }
    /// <summary>Procesos que deben estar cerrados antes de restaurar esta categoría.</summary>
    public List<string> ProcessNames { get; set; } = [];
    public int FileCount { get; set; }
    public long Bytes { get; set; }
}

/// <summary>Un archivo dentro del backup.</summary>
public sealed class FileRecord
{
    /// <summary>Ruta dentro del archivo, con '/' (p. ej. <c>data/documents/Informe.docx</c>).</summary>
    public string Path { get; set; } = "";
    public string Section { get; set; } = Sections.Data;
    /// <summary>Id de categoría (solo para la sección de datos).</summary>
    public string? Category { get; set; }
    /// <summary>Ruta relativa a la carpeta de la categoría, con '/'.</summary>
    public string? Relative { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public FileAttributes Attributes { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class DirectoryRecord
{
    public string Category { get; set; } = "";
    public string Relative { get; set; } = "";
}

/// <summary>Informe de creación (<c>report.json</c>).</summary>
public sealed class CreationReport
{
    public List<SkippedItem> Skipped { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class SkippedItem
{
    public string Path { get; set; } = "";
    public SkipReason Reason { get; set; }
    public string? Detail { get; set; }
}

public enum SkipReason
{
    Locked,
    AccessDenied,
    CloudOnly,
    ReparsePoint,
    PathTooLong,
    Excluded,
    Sensitive,
    NotFound,
    Error,
}
