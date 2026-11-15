namespace WindowsReborn.Programs;

/// <summary>De dónde sale el instalador de un programa del catálogo.</summary>
public enum CatalogSource
{
    /// <summary>Paquete de WinGet (siempre la última versión; WinGet verifica el hash).</summary>
    WinGet,
    /// <summary>El paquete de WinGet con la versión más alta cuyo Id empieza por el prefijo (p. ej. Python 3.x).</summary>
    WinGetNewest,
    /// <summary>Se lee la web oficial y se toma el enlace de descarga más reciente que encaja con el patrón.</summary>
    OfficialPageLink,
    /// <summary>Enlace oficial «latest» que siempre apunta a la última versión.</summary>
    DirectLatest,
    /// <summary>El fabricante no permite descargas automáticas: se abre su web oficial.</summary>
    OfficialPage,
}

public enum CatalogCategory
{
    Browsers,
    Gaming,
    Communication,
    Media,
    Development,
    Utilities,
    Security,
}

/// <summary>Programa del catálogo integrado («lo normal que uno se instala al formatear»).</summary>
public sealed record CatalogProgram
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public CatalogCategory Category { get; init; }
    /// <summary>Color de la insignia (hex sin #).</summary>
    public string Color { get; init; } = "5B5FC7";
    /// <summary>Texto de la insignia (1-2 letras).</summary>
    public string Monogram { get; init; } = "";
    public CatalogSource Source { get; init; } = CatalogSource.WinGet;
    /// <summary>Id de WinGet o prefijo (para <see cref="CatalogSource.WinGetNewest"/>).</summary>
    public string? WinGetId { get; init; }
    /// <summary>Web oficial (y página que se analiza para <see cref="CatalogSource.OfficialPageLink"/>).</summary>
    public string? Homepage { get; init; }
    /// <summary>URL de descarga fija («latest») o expresión regular que encuentra el enlace en la web oficial.</summary>
    public string? DownloadUrl { get; init; }
    public string? LinkPattern { get; init; }
    /// <summary>Texto que debe contener el firmante Authenticode del instalador descargado.</summary>
    public string? Publisher { get; init; }
    /// <summary>Nombre que se busca en «Programas instalados» si no está en WinGet.</summary>
    public string? DetectName { get; init; }
    /// <summary>Argumentos extra para el instalador (WinGet <c>--custom</c>).</summary>
    public string? CustomArgs { get; init; }
    /// <summary>Viene marcado la primera vez.</summary>
    public bool Preselected { get; init; }

    public bool IsAutomatic => Source is not CatalogSource.OfficialPage;
}

public static class SoftwareCatalog
{
    public static IReadOnlyList<CatalogProgram> All { get; } =
    [
        // Navegadores
        new() { Id = "brave", Name = "Brave", Category = CatalogCategory.Browsers, Color = "FB542B", Monogram = "B", WinGetId = "Brave.Brave", Homepage = "https://brave.com", Preselected = true },
        new() { Id = "chrome", Name = "Google Chrome", Category = CatalogCategory.Browsers, Color = "1A73E8", Monogram = "C", WinGetId = "Google.Chrome", Homepage = "https://www.google.com/chrome/", Preselected = true },
        new() { Id = "helium", Name = "Helium", Category = CatalogCategory.Browsers, Color = "6E56CF", Monogram = "He", WinGetId = "ImputNet.Helium", Homepage = "https://helium.computer", Preselected = true },
        new() { Id = "firefox", Name = "Firefox", Category = CatalogCategory.Browsers, Color = "FF7139", Monogram = "Fx", WinGetId = "Mozilla.Firefox", Homepage = "https://www.mozilla.org/firefox/" },

        // Juegos
        new() { Id = "steam", Name = "Steam", Category = CatalogCategory.Gaming, Color = "1B2838", Monogram = "S", WinGetId = "Valve.Steam", Homepage = "https://store.steampowered.com", Preselected = true },
        new() { Id = "epic", Name = "Epic Games Launcher", Category = CatalogCategory.Gaming, Color = "2A2A2A", Monogram = "E", WinGetId = "EpicGames.EpicGamesLauncher", Homepage = "https://store.epicgames.com", Preselected = true },
        new() { Id = "hoyoplay", Name = "HoYoPlay", Category = CatalogCategory.Gaming, Color = "3D8BFF", Monogram = "H", Source = CatalogSource.OfficialPage, Homepage = "https://hoyoplay.hoyoverse.com/", DetectName = "HoYoPlay", Preselected = true },
        new() { Id = "nvidia-app", Name = "NVIDIA App", Category = CatalogCategory.Gaming, Color = "76B900", Monogram = "N", Source = CatalogSource.OfficialPageLink,
            Homepage = "https://www.nvidia.com/en-us/software/nvidia-app/", LinkPattern = @"https://us\.download\.nvidia\.com/nvapp/client/[\d.]+/x64/NVIDIA_app_v[\d.]+_x64\.exe",
            Publisher = "NVIDIA", DetectName = "NVIDIA app", Preselected = true },
        new() { Id = "gog", Name = "GOG Galaxy", Category = CatalogCategory.Gaming, Color = "86328A", Monogram = "G", WinGetId = "GOG.Galaxy", Homepage = "https://www.gog.com/galaxy" },
        new() { Id = "battlenet", Name = "Battle.net", Category = CatalogCategory.Gaming, Color = "148EFF", Monogram = "Bn", WinGetId = "Blizzard.BattleNet", Homepage = "https://www.blizzard.com/apps/battle.net/desktop" },
        new() { Id = "ea", Name = "EA app", Category = CatalogCategory.Gaming, Color = "FF4747", Monogram = "EA", WinGetId = "ElectronicArts.EADesktop", Homepage = "https://www.ea.com/ea-app" },
        new() { Id = "ubisoft", Name = "Ubisoft Connect", Category = CatalogCategory.Gaming, Color = "0070FF", Monogram = "U", WinGetId = "Ubisoft.Connect", Homepage = "https://ubisoftconnect.com" },

        // Comunicación
        new() { Id = "discord", Name = "Discord", Category = CatalogCategory.Communication, Color = "5865F2", Monogram = "D", WinGetId = "Discord.Discord", Homepage = "https://discord.com", Preselected = true },
        new() { Id = "telegram", Name = "Telegram", Category = CatalogCategory.Communication, Color = "26A5E4", Monogram = "T", WinGetId = "Telegram.TelegramDesktop", Homepage = "https://desktop.telegram.org" },

        // Multimedia
        new() { Id = "spotify", Name = "Spotify", Category = CatalogCategory.Media, Color = "1DB954", Monogram = "Sp", WinGetId = "Spotify.Spotify", Homepage = "https://www.spotify.com", Preselected = true },
        new() { Id = "obs", Name = "OBS Studio", Category = CatalogCategory.Media, Color = "302E31", Monogram = "O", WinGetId = "OBSProject.OBSStudio", Homepage = "https://obsproject.com", Preselected = true },
        new() { Id = "audacity", Name = "Audacity", Category = CatalogCategory.Media, Color = "0000CC", Monogram = "Au", WinGetId = "Audacity.Audacity", Homepage = "https://www.audacityteam.org", Preselected = true },
        new() { Id = "davinci", Name = "DaVinci Resolve", Category = CatalogCategory.Media, Color = "E04E39", Monogram = "Dv", Source = CatalogSource.OfficialPage,
            Homepage = "https://www.blackmagicdesign.com/products/davinciresolve", DetectName = "DaVinci Resolve", Preselected = true },
        new() { Id = "vlc", Name = "VLC", Category = CatalogCategory.Media, Color = "FF8800", Monogram = "V", WinGetId = "VideoLAN.VLC", Homepage = "https://www.videolan.org" },

        // Desarrollo
        new() { Id = "vscode", Name = "Visual Studio Code", Category = CatalogCategory.Development, Color = "007ACC", Monogram = "VS", WinGetId = "Microsoft.VisualStudioCode", Homepage = "https://code.visualstudio.com", Preselected = true },
        new() { Id = "git", Name = "Git", Category = CatalogCategory.Development, Color = "F05032", Monogram = "Gi", WinGetId = "Git.Git", Homepage = "https://git-scm.com", Preselected = true },
        new() { Id = "python", Name = "Python 3", Category = CatalogCategory.Development, Color = "3776AB", Monogram = "Py", Source = CatalogSource.WinGetNewest, WinGetId = "Python.Python.3",
            Homepage = "https://www.python.org", CustomArgs = "PrependPath=1 Include_launcher=1", Preselected = true },
        new() { Id = "eclipse", Name = "Eclipse IDE", Category = CatalogCategory.Development, Color = "2C2255", Monogram = "Ec", Source = CatalogSource.DirectLatest,
            Homepage = "https://www.eclipse.org/downloads/", DownloadUrl = "https://download.eclipse.org/oomph/products/latest/eclipse-inst-jre-win64.exe",
            Publisher = "Eclipse", Preselected = true },
        new() { Id = "java", Name = "Java 21 (Temurin JDK)", Category = CatalogCategory.Development, Color = "E76F00", Monogram = "J", WinGetId = "EclipseAdoptium.Temurin.21.JDK", Homepage = "https://adoptium.net" },
        new() { Id = "nodejs", Name = "Node.js LTS", Category = CatalogCategory.Development, Color = "5FA04E", Monogram = "Nd", WinGetId = "OpenJS.NodeJS.LTS", Homepage = "https://nodejs.org" },
        new() { Id = "notepadpp", Name = "Notepad++", Category = CatalogCategory.Development, Color = "90E59A", Monogram = "N+", WinGetId = "Notepad++.Notepad++", Homepage = "https://notepad-plus-plus.org" },

        // Utilidades
        new() { Id = "winrar", Name = "WinRAR", Category = CatalogCategory.Utilities, Color = "7B3FA0", Monogram = "R", WinGetId = "RARLab.WinRAR", Homepage = "https://www.win-rar.com", Preselected = true },
        new() { Id = "eartrumpet", Name = "EarTrumpet", Category = CatalogCategory.Utilities, Color = "0078D4", Monogram = "Ea", WinGetId = "File-New-Project.EarTrumpet", Homepage = "https://eartrumpet.app", Preselected = true },
        new() { Id = "7zip", Name = "7-Zip", Category = CatalogCategory.Utilities, Color = "333333", Monogram = "7z", WinGetId = "7zip.7zip", Homepage = "https://www.7-zip.org" },
        new() { Id = "powertoys", Name = "PowerToys", Category = CatalogCategory.Utilities, Color = "4B4B9B", Monogram = "PT", WinGetId = "Microsoft.PowerToys", Homepage = "https://learn.microsoft.com/windows/powertoys/" },
        new() { Id = "sharex", Name = "ShareX", Category = CatalogCategory.Utilities, Color = "2E9BE6", Monogram = "Sx", WinGetId = "ShareX.ShareX", Homepage = "https://getsharex.com" },
        new() { Id = "everything", Name = "Everything", Category = CatalogCategory.Utilities, Color = "F28C28", Monogram = "Ev", WinGetId = "voidtools.Everything", Homepage = "https://www.voidtools.com" },
        new() { Id = "hwinfo", Name = "HWiNFO", Category = CatalogCategory.Utilities, Color = "1E5AA8", Monogram = "Hw", WinGetId = "REALiX.HWiNFO", Homepage = "https://www.hwinfo.com" },

        // Seguridad y red
        new() { Id = "protonvpn", Name = "Proton VPN", Category = CatalogCategory.Security, Color = "6D4AFF", Monogram = "P", WinGetId = "Proton.ProtonVPN", Homepage = "https://protonvpn.com", Preselected = true },
        new() { Id = "warp", Name = "Cloudflare WARP", Category = CatalogCategory.Security, Color = "F38020", Monogram = "W", WinGetId = "Cloudflare.Warp", Homepage = "https://one.one.one.one", Preselected = true },
        new() { Id = "veracrypt", Name = "VeraCrypt", Category = CatalogCategory.Security, Color = "1F4E79", Monogram = "Vc", WinGetId = "IDRIX.VeraCrypt", Homepage = "https://www.veracrypt.fr", Preselected = true },
        new() { Id = "bitwarden", Name = "Bitwarden", Category = CatalogCategory.Security, Color = "175DDC", Monogram = "Bw", WinGetId = "Bitwarden.Bitwarden", Homepage = "https://bitwarden.com" },
    ];

    public static CatalogProgram? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
