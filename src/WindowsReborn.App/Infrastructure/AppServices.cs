using System.Reflection;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Localization;
using WindowsReborn.Core.Paths;
using WindowsReborn.Programs;
using WindowsReborn.Storage;

namespace WindowsReborn.App.Infrastructure;

/// <summary>
/// Servicios compartidos de la aplicación (una única instancia). Los modelos de vista
/// viven aquí para que su estado no se pierda al cambiar de sección.
/// </summary>
public static class AppServices
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.1.0";

    public static AppPaths Paths { get; private set; } = null!;
    public static ConfigStore Config { get; private set; } = null!;
    public static AppSettings Settings { get; set; } = new();
    public static ProgramLibraryStore ProgramStore { get; private set; } = null!;
    public static HttpClient Http { get; private set; } = null!;
    public static WinGetClient WinGet { get; private set; } = null!;
    public static SourceResolver Sources { get; private set; } = null!;
    public static SourceIdentifier Identifier { get; private set; } = null!;
    public static Downloader Downloader { get; private set; } = null!;
    public static InstalledDetector Detector { get; private set; } = null!;

    // Ajustes de Windows
    public static Tweaks.TweakEngine Tweaks { get; private set; } = null!;
    public static Tweaks.TweakPreferences TweakPreferences { get; private set; } = new();
    public static Tweaks.StartupManager Startup { get; private set; } = null!;
    public static Tweaks.AppxManager Appx { get; private set; } = null!;
    public static string StateDirectory => Path.Combine(Paths.DataRoot, "state");
    public static string TweakPreferencesPath => Path.Combine(Paths.ConfigDirectory, WindowsReborn.Tweaks.TweakPreferences.FileName);

    public static Task SaveTweakPreferencesAsync() => TweakPreferences.SaveAsync(TweakPreferencesPath);

    public static async Task ReloadTweakPreferencesAsync() =>
        TweakPreferences = await WindowsReborn.Tweaks.TweakPreferences.LoadAsync(TweakPreferencesPath);

    /// <summary>Backup abierto en la sección Restauración (lo usa también «Mis programas»).</summary>
    public static IBackupReader? OpenedBackup { get; set; }
    public static BackupManifest? OpenedManifest { get; set; }

    public static event EventHandler? OpenedBackupChanged;
    public static event EventHandler? ConfigurationReplaced;

    public static void RaiseOpenedBackupChanged() => OpenedBackupChanged?.Invoke(null, EventArgs.Empty);
    public static void RaiseConfigurationReplaced() => ConfigurationReplaced?.Invoke(null, EventArgs.Empty);

    public static string? SevenZipPath => SevenZipRunner.Locate(Settings.SevenZipPath);

    public static async Task InitializeAsync()
    {
        Paths = AppPaths.Detect();
        Paths.EnsureCreated();
        Core.Logging.Log.Initialize(Paths.LogsDirectory);
        Config = new ConfigStore(Paths.ConfigDirectory);
        Settings = await Config.LoadSettingsAsync();
        Loc.Instance.Load(Settings.Language);

        ProgramStore = new ProgramLibraryStore(Paths.ConfigDirectory);
        Http = Programs.Http.CreateDefault();
        WinGet = new WinGetClient(new ProcessRunner());
        Sources = new SourceResolver(
        [
            new MozillaFirefoxAdapter(Http, () => Loc.Instance.Language),
            new GitHubReleasesAdapter(Http),
            new WinGetAdapter(WinGet),
            new DirectUrlAdapter(),
        ]);
        Identifier = new SourceIdentifier(Http, WinGet);
        Downloader = new Downloader(Http, WinGet);
        Detector = new InstalledDetector(WinGet);

        var writer = new Tweaks.SystemRegistryWriter(StateDirectory);
        var journal = await WindowsReborn.Tweaks.TweakJournal.LoadAsync(Path.Combine(StateDirectory, WindowsReborn.Tweaks.TweakJournal.FileName));
        Tweaks = new Tweaks.TweakEngine(WindowsReborn.Tweaks.WindowsRegistry.Instance, writer, journal, Core.Compatibility.WindowsVersion.Current.Build);
        await ReloadTweakPreferencesAsync();
        Startup = new Tweaks.StartupManager(WindowsReborn.Tweaks.WindowsRegistry.Instance, writer);
        Appx = new Tweaks.AppxManager(new ProcessRunner());
        Core.Logging.Log.Info($"Windows Reborn {Version} started on {Core.Compatibility.WindowsVersion.Current.FriendlyName}");
    }

    public static Task SaveSettingsAsync() => Config.SaveSettingsAsync(Settings);

    /// <summary>Carpeta donde se guardan los instaladores descargados.</summary>
    public static string InstallersFolder =>
        string.IsNullOrWhiteSpace(Settings.InstallersFolder) ? Paths.DownloadsDirectory : Settings.InstallersFolder!;
}
