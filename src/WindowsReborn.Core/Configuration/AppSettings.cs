namespace WindowsReborn.Core.Configuration;

/// <summary>Preferencias de la propia aplicación (<c>settings.json</c>).</summary>
public sealed class AppSettings
{
    public const string FileName = "settings.json";

    public int SchemaVersion { get; set; } = 1;

    /// <summary><c>auto</c> (según el teclado), <c>es</c> o <c>en</c>.</summary>
    public string Language { get; set; } = "auto";

    /// <summary><c>dark</c>, <c>light</c> o <c>system</c>.</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Última carpeta de destino de backups (para proponerla de nuevo).</summary>
    public string? LastBackupDestination { get; set; }

    /// <summary>Formato preferido: <c>zip</c>, <c>7z</c> o <c>folder</c>.</summary>
    public string PreferredFormat { get; set; } = "zip";

    /// <summary>Ruta de 7z.exe indicada por el usuario (si no está en la ubicación habitual).</summary>
    public string? SevenZipPath { get; set; }

    /// <summary>Carpeta de descargas de instaladores elegida por el usuario.</summary>
    public string? InstallersFolder { get; set; }

    /// <summary>Comprobación manual de actualizaciones. Desactivada por defecto.</summary>
    public bool AllowUpdateCheck { get; set; }

    /// <summary>Último backup terminado y verificado (para el asistente de formateo).</summary>
    public string? LastBackupPath { get; set; }
    public bool LastBackupVerified { get; set; }

    /// <summary>Anonimizar nombre de equipo y usuario en los manifiestos.</summary>
    public bool AnonymizeManifest { get; set; }
}
