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

    // ---- Selecciones (null = valores por defecto del catálogo) ----

    /// <summary>Programas del catálogo marcados.</summary>
    public List<string>? SelectedPrograms { get; set; }

    /// <summary>Programas preinstalados marcados para quitar (null = los recomendados).</summary>
    public List<string>? DebloatSelection { get; set; }

    /// <summary>Programas preinstalados que el usuario ha quitado (se vuelven a quitar tras formatear).</summary>
    public List<string> RemovedApps { get; set; } = [];

    /// <summary>Programas nuevos ya ofrecidos (para marcar una sola vez los que vienen marcados).</summary>
    public List<string> SeenNewPrograms { get; set; } = [];

    /// <summary>Usuario de GitHub cuyas releases se listan en «Mis programas de GitHub».</summary>
    public string GitHubUser { get; set; } = "Dreex14";

    /// <summary>Repositorios extra (<c>owner/repo</c>) añadidos a mano.</summary>
    public List<string> ExtraRepos { get; set; } = [];

    /// <summary>Repositorios de GitHub marcados para instalar.</summary>
    public List<string> SelectedRepos { get; set; } = [];

    /// <summary>Crear también un acceso directo en el escritorio para las apps de GitHub.</summary>
    public bool GitHubDesktopShortcut { get; set; } = true;

    /// <summary>Elementos de copia marcados.</summary>
    public List<string>? BackupItems { get; set; }

    /// <summary>Carpetas añadidas a mano a la copia (rutas, admiten variables como %APPDATA%).</summary>
    public List<string> CustomFolders { get; set; } = [];

    /// <summary>Tamaño y posición de la ventana la última vez que se cerró.</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Crear un punto de restauración antes de aplicar optimizaciones.</summary>
    public bool CreateRestorePoint { get; set; } = true;
}
