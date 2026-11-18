using System.Reflection;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Localization;
using WindowsReborn.Core.Paths;
using WindowsReborn.Programs;
using WindowsReborn.Tweaks;

namespace WindowsReborn.App.Infrastructure;

/// <summary>Servicios compartidos de la aplicación (una única instancia).</summary>
public static class AppServices
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";

    public static AppPaths Paths { get; private set; } = null!;
    public static ConfigStore Config { get; private set; } = null!;
    public static AppSettings Settings { get; set; } = new();
    public static HttpClient Http { get; private set; } = null!;
    public static SoftwareInstaller Installer { get; private set; } = null!;
    public static GitHubAppsClient GitHub { get; private set; } = null!;

    public static TweakEngine Tweaks { get; private set; } = null!;
    public static TweakPreferences TweakPreferences { get; private set; } = new();
    public static StartupManager Startup { get; private set; } = null!;
    public static AppxManager Appx { get; private set; } = null!;

    public static string StateDirectory => Path.Combine(Paths.DataRoot, "state");
    public static string TweakPreferencesPath => Path.Combine(Paths.ConfigDirectory, TweakPreferences.FileName);

    /// <summary>Se dispara al importar «Mi configuración» para que cada sección recargue sus selecciones.</summary>
    public static event EventHandler? ConfigurationReplaced;
    public static void RaiseConfigurationReplaced() => ConfigurationReplaced?.Invoke(null, EventArgs.Empty);

    public static async Task InitializeAsync()
    {
        Paths = AppPaths.Detect();
        Paths.EnsureCreated();
        Core.Logging.Log.Initialize(Paths.LogsDirectory);
        Config = new ConfigStore(Paths.ConfigDirectory);
        Settings = await Config.LoadSettingsAsync();
        Loc.Instance.Load(Settings.Language);

        Http = Programs.Http.CreateDefault();
        var runner = new ProcessRunner();
        var winget = new WinGetClient(runner);
        Installer = new SoftwareInstaller(runner, Http, new Downloader(Http, winget));
        GitHub = new GitHubAppsClient(Http, StateDirectory);

        var writer = new SystemRegistryWriter(StateDirectory);
        var journal = await TweakJournal.LoadAsync(Path.Combine(StateDirectory, TweakJournal.FileName));
        Tweaks = new TweakEngine(WindowsRegistry.Instance, writer, journal, Core.Compatibility.WindowsVersion.Current.Build);
        TweakPreferences = await TweakPreferences.LoadAsync(TweakPreferencesPath);
        Startup = new StartupManager(WindowsRegistry.Instance, writer);
        Appx = new AppxManager(runner);
        Core.Logging.Log.Info($"Windows Reborn {Version} started on {Core.Compatibility.WindowsVersion.Current.FriendlyName}");
    }

    public static Task SaveSettingsAsync() => Config.SaveSettingsAsync(Settings);
    public static Task SaveTweakPreferencesAsync() => TweakPreferences.SaveAsync(TweakPreferencesPath);

    public static string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Guarda los ajustes medio segundo después del último cambio (marcar casillas).</summary>
    public static void SaveSettingsSoon()
    {
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTick;
        _saveTimer.Tick += OnSaveTick;
        _saveTimer.Start();
    }

    private static System.Windows.Threading.DispatcherTimer? _saveTimer;

    private static async void OnSaveTick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        try { await SaveSettingsAsync(); } catch (Exception ex) { Core.Logging.Log.Warn("Settings save failed: " + ex.Message); }
    }
}
