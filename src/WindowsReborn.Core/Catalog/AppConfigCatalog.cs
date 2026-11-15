using WindowsReborn.Core.Paths;

namespace WindowsReborn.Core.Catalog;

/// <summary>
/// Adaptador de configuración de una aplicación conocida. Solo incluye una lista
/// blanca de archivos seguros y portables; nunca carpetas internas completas, ni
/// cookies, sesiones, contraseñas o claves.
/// </summary>
public sealed record AppConfigAdapter(
    string Id,
    string Name,
    string Root,
    string SubPath,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> ProcessNames,
    string? WarningKey = null);

public static class AppConfigCatalog
{
    public static IReadOnlyList<AppConfigAdapter> All { get; } =
    [
        new("vscode", "Visual Studio Code", KnownRoots.RoamingAppData, @"Code\User",
            ["settings.json", "keybindings.json", "snippets/"], ["Code"]),
        new("windows-terminal", "Windows Terminal", KnownRoots.LocalAppData,
            @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState",
            ["settings.json"], ["WindowsTerminal"]),
        new("git", "Git", KnownRoots.UserProfile, "",
            [".gitconfig"], [], "adapter.git.warning"),
        new("powershell", "PowerShell 7", KnownRoots.Documents, "PowerShell",
            ["Microsoft.PowerShell_profile.ps1", "profile.ps1", "Microsoft.VSCode_profile.ps1"], ["pwsh"]),
        new("windows-powershell", "Windows PowerShell", KnownRoots.Documents, "WindowsPowerShell",
            ["Microsoft.PowerShell_profile.ps1", "profile.ps1"], ["powershell"]),
        new("notepadpp", "Notepad++", KnownRoots.RoamingAppData, "Notepad++",
            ["config.xml", "shortcuts.xml", "stylers.xml", "contextMenu.xml", "userDefineLangs/"], ["notepad++"]),
        new("edge-bookmarks", "Microsoft Edge — favoritos / favorites", KnownRoots.LocalAppData,
            @"Microsoft\Edge\User Data\Default", ["Bookmarks"], ["msedge"], "adapter.browser.warning"),
        new("chrome-bookmarks", "Google Chrome — marcadores / bookmarks", KnownRoots.LocalAppData,
            @"Google\Chrome\User Data\Default", ["Bookmarks"], ["chrome"], "adapter.browser.warning"),
        new("brave-bookmarks", "Brave — marcadores / bookmarks", KnownRoots.LocalAppData,
            @"BraveSoftware\Brave-Browser\User Data\Default", ["Bookmarks"], ["brave"], "adapter.browser.warning"),
    ];

    public static AppConfigAdapter? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>Ruta absoluta del adaptador en este equipo, o <c>null</c> si no existe.</summary>
    public static string? ResolveFolder(AppConfigAdapter adapter)
    {
        var root = KnownRoots.Resolve(adapter.Root);
        if (root is null) return null;
        var path = adapter.SubPath.Length == 0 ? root : Path.Combine(root, adapter.SubPath);
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>Indica si alguno de los elementos incluidos existe en este equipo.</summary>
    public static bool IsPresent(AppConfigAdapter adapter)
    {
        var folder = ResolveFolder(adapter);
        if (folder is null) return false;
        return adapter.Include.Any(i => i.EndsWith('/')
            ? Directory.Exists(Path.Combine(folder, i.TrimEnd('/')))
            : File.Exists(Path.Combine(folder, i)));
    }
}

/// <summary>Patrones de archivos sensibles que nunca se incluyen automáticamente.</summary>
public static class SensitivePatterns
{
    public static IReadOnlyList<string> FileNames { get; } =
    [
        "Login Data", "Login Data For Account", "Cookies", "Local State", "Web Data", "Network Persistent State",
        "logins.json", "key4.db", "key3.db", "cookies.sqlite", "cert9.db",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "*.pem", "*.key", "*.pfx", "*.p12", "*.ppk",
        ".git-credentials", ".netrc", "*.kdbx", "credentials", "*.ovpn",
    ];

    public static IReadOnlyList<string> DirectoryNames { get; } = [".ssh", ".gnupg", ".aws", ".azure", ".kube"];
}
