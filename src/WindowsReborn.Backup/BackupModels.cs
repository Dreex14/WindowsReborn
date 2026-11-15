using WindowsReborn.Storage;

namespace WindowsReborn.Backup;

public enum SourceKind
{
    KnownFolder,
    CustomFolder,
    AppConfig,
}

/// <summary>Una fuente de datos que se va a copiar.</summary>
public sealed class BackupSource
{
    /// <summary>Id de categoría (también carpeta bajo <c>data/</c>); único dentro del backup.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SourceKind Kind { get; init; }
    /// <summary>Raíz simbólica para el manifiesto.</summary>
    public required string Root { get; init; }
    /// <summary>Subcarpeta dentro de la raíz.</summary>
    public string SubPath { get; init; } = "";
    /// <summary>Ruta absoluta en este equipo.</summary>
    public required string AbsolutePath { get; init; }
    /// <summary>Lista blanca (solo adaptadores); vacía = toda la carpeta.</summary>
    public IReadOnlyList<string> Include { get; init; } = [];
    public IReadOnlyList<string> ExtraExclusions { get; init; } = [];
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
    public string? AdapterId { get; init; }
}

/// <summary>Archivo adicional (configuración, instaladores…) que se guarda fuera de <c>data/</c>.</summary>
public sealed record ExtraFile(string ArchivePath, string Section, string? SourcePath, byte[]? Content);

public sealed class BackupPlan
{
    public required IReadOnlyList<BackupSource> Sources { get; init; }
    public IReadOnlyList<string> Exclusions { get; init; } = BackupDefaults.Exclusions;
    public required string DestinationFolder { get; init; }
    public required string Name { get; init; }
    public BackupFormat Format { get; init; } = BackupFormat.Zip;
    public CompressionPreset Compression { get; init; } = CompressionPreset.Balanced;
    public string? Password { get; init; }
    public string? SevenZipPath { get; init; }
    /// <summary>Incluir archivos sensibles detectados (solo permitido con cifrado).</summary>
    public bool IncludeSensitive { get; init; }
    public bool AnonymizeManifest { get; init; }
    public IReadOnlyList<ExtraFile> Extras { get; init; } = [];
    public string AppVersion { get; init; } = "0.0.0";

    public string FinalPath => Path.Combine(DestinationFolder, Name + Format.Extension());
}

public static class BackupDefaults
{
    public static IReadOnlyList<string> Exclusions { get; } =
    [
        "node_modules/", "__pycache__/", ".venv/", "*.tmp", "~$*", "Thumbs.db", "*.partial", "*.wrtmp",
    ];

    /// <summary>Exclusiones adicionales sugeridas para proyectos de programación (se regeneran al compilar).</summary>
    public static IReadOnlyList<string> ProjectExclusions { get; } =
    [
        "bin/", "obj/", "target/", "dist/", "build/", ".vs/", ".gradle/", ".next/",
    ];
}

public enum BackupPhase
{
    Inventory,
    Writing,
    Finalizing,
    Verifying,
    Committing,
    Done,
}

public sealed record BackupProgress(BackupPhase Phase, long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, string? Current);

public enum BackupStatus
{
    Success,
    SuccessWithSkips,
    Failed,
    Cancelled,
}

public sealed class BackupResult
{
    public BackupStatus Status { get; set; }
    public string? FinalPath { get; set; }
    public int FilesWritten { get; set; }
    public long BytesWritten { get; set; }
    public long ArchiveSize { get; set; }
    public CreationReport Report { get; set; } = new();
    public VerificationResult? Verification { get; set; }
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }
}
