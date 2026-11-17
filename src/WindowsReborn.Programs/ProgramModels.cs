namespace WindowsReborn.Programs;

/// <summary>Tipo de fuente elegida para un programa. Nunca se cambia sin autorización del usuario.</summary>
public enum SourceKind
{
    /// <summary>Sin fuente automática: solo se abre la web oficial.</summary>
    None,
    /// <summary>API oficial del desarrollador mediante un adaptador específico.</summary>
    Vendor,
    /// <summary>GitHub Releases del repositorio indicado.</summary>
    GitHub,
    /// <summary>Paquete de WinGet.</summary>
    WinGet,
    /// <summary>URL directa elegida por el usuario (no permite conocer la última versión).</summary>
    DirectUrl,
}

/// <summary>Nivel de confianza de la fuente.</summary>
public enum SourceTrust
{
    /// <summary>No se pudo contrastar.</summary>
    Unverified,
    /// <summary>Elegida o escrita por el usuario, sin contraste independiente.</summary>
    UserSelected,
    /// <summary>Contrastada: adaptador oficial, o coincide con el manifiesto de WinGet / la web oficial.</summary>
    Verified,
}

public enum InstallMode
{
    /// <summary>Solo descargar; el usuario instala cuando quiera.</summary>
    DownloadOnly,
    /// <summary>Abrir el instalador para que el usuario lo complete.</summary>
    Manual,
    /// <summary>Instalación automatizada (WinGet) tras confirmación.</summary>
    Automated,
}

public sealed class ProgramSource
{
    public SourceKind Kind { get; set; }
    public SourceTrust Trust { get; set; }
    /// <summary>Motivo del nivel de confianza (clave de localización + datos).</summary>
    public string? TrustReason { get; set; }
    /// <summary><c>owner/repo</c> para GitHub.</summary>
    public string? GitHubRepo { get; set; }
    /// <summary>Patrón del recurso de la release (p. ej. <c>*-x64.msi</c>).</summary>
    public string? AssetPattern { get; set; }
    /// <summary>Id del adaptador oficial (p. ej. <c>mozilla-firefox</c>).</summary>
    public string? VendorAdapter { get; set; }
    public string? DirectUrl { get; set; }
}

/// <summary>Un programa de la biblioteca «Mis programas».</summary>
public sealed class ProgramEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Página web oficial elegida por el usuario.</summary>
    public string? Homepage { get; set; }
    public string? WinGetId { get; set; }
    public ProgramSource Source { get; set; } = new();
    /// <summary><c>x64</c>, <c>arm64</c>, <c>x86</c> o <c>neutral</c>.</summary>
    public string Architecture { get; set; } = "x64";
    /// <summary><c>any</c>, <c>exe</c>, <c>msi</c>, <c>msix</c> o <c>zip</c>.</summary>
    public string InstallerType { get; set; } = "any";
    public InstallMode InstallMode { get; set; } = InstallMode.Manual;
    /// <summary>Texto que debe contener el firmante Authenticode (p. ej. «Mozilla Corporation»).</summary>
    public string? ExpectedPublisher { get; set; }
    /// <summary>Nombre que se busca en «Programas instalados» si difiere del nombre.</summary>
    public string? DetectName { get; set; }
    /// <summary>Adaptadores de configuración asociados (ids de <c>AppConfigCatalog</c>).</summary>
    public List<string> ConfigAdapters { get; set; } = [];
    public string? Notes { get; set; }

    // Estado (no son preferencias; se refrescan).
    public string? LatestVersion { get; set; }
    public DateTime? LatestCheckedUtc { get; set; }
    public string? LastDownloadPath { get; set; }
    public string? LastDownloadVersion { get; set; }
}

/// <summary>Contenido de <c>programs.json</c>.</summary>
public sealed class ProgramLibrary
{
    public const string FileName = "programs.json";
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;
    public List<ProgramEntry> Programs { get; set; } = [];
}

/// <summary>Versión disponible encontrada por un adaptador.</summary>
public sealed class ReleaseInfo
{
    public string? Version { get; set; }
    public string? DownloadUrl { get; set; }
    public string? FileName { get; set; }
    /// <summary>SHA-256 publicado por la fuente (si existe).</summary>
    public string? ExpectedSha256 { get; set; }
    public string? ExpectedPublisher { get; set; }
    /// <summary>Descripción legible del origen (p. ej. «GitHub Releases owner/repo»).</summary>
    public string Origin { get; set; } = "";
    /// <summary>La descarga la hace WinGet (que verifica el hash de su manifiesto).</summary>
    public bool ViaWinGet { get; set; }
}

public enum InstallState
{
    Unknown,
    NotInstalled,
    Installed,
    ProbablyInstalled,
}
