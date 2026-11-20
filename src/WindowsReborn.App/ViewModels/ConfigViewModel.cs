using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Localization;

namespace WindowsReborn.App.ViewModels;

/// <summary>«Mi configuración»: exportar/importar todas las elecciones y preferencias de la app.</summary>
public partial class ConfigViewModel : ObservableObject
{
    public ConfigViewModel()
    {
        _languageIndex = AppServices.Settings.Language switch { "es" => 1, "en" => 2, _ => 0 };
        _themeIndex = AppServices.Settings.Theme switch { "light" => 1, "system" => 2, _ => 0 };
        RefreshSummary();
    }

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasImported;
    [ObservableProperty] private string? _importedText;
    [ObservableProperty] private int _importedTweaks;
    [ObservableProperty] private int _importedPrograms;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private int _languageIndex;
    [ObservableProperty] private int _themeIndex;
    /// <summary>Incluir el token de GitHub (cifrado con contraseña) al guardar la configuración.</summary>
    [ObservableProperty] private bool _includeGitHubToken;
    [ObservableProperty] private bool _canIncludeGitHubToken;

    private MyConfig? _imported;

    public void RefreshSummary()
    {
        var c = BuildConfig();
        CanIncludeGitHubToken = SecretStore.Unprotect(AppServices.Settings.GitHubTokenProtected) is not null;
        if (!CanIncludeGitHubToken) IncludeGitHubToken = false;
        Summary = Loc.T("config.summary", c.Tweaks.Count, c.Programs.Count + c.GitHubRepos.Count, c.BackupItems.Count + c.CustomFolders.Count);
    }

    /// <summary>Configuración actual (lo que se exporta).</summary>
    public MyConfig BuildConfig()
    {
        var s = AppServices.Settings;
        var programs = s.SelectedPrograms ?? Programs.SoftwareCatalog.All.Where(p => p.Preselected).Select(p => p.Id).ToList();
        var items = s.BackupItems ?? Backup.BackupCatalog.All.Where(i => i.Preselected).Select(i => i.Id).ToList();
        // Lo aplicado más lo marcado (aunque aún no se haya aplicado) en Privacidad y Optimizar.
        var tweaks = new Dictionary<string, string>(AppServices.TweakPreferences.Selections, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in ViewModelLocator.Privacy.Snapshot().Concat(ViewModelLocator.Optimize.Snapshot())) tweaks[k] = v;
        var config = MyConfig.FromSettings(s, tweaks, programs, items, AppServices.Version);
        config.AppPermissions = Tweaks.AppPermissions.Capture();
        config.RemoveApps = DebloatViewModel.SavedList();
        return config;
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = Dialogs.SaveFile(Loc.T("config.export"), Loc.T("config.filter") + "|*.zip", Loc.T("config.zip-name") + ".zip");
        if (path is null) return;
        try
        {
            var config = BuildConfig();
            if (IncludeGitHubToken && SecretStore.Unprotect(AppServices.Settings.GitHubTokenProtected) is { } token)
            {
                var password = await Dialogs.PasswordAsync(Loc.T("config.token.title"), Loc.T("config.token.set"), Loc.T("config.export.button"), confirm: true);
                if (password is null) return;
                config.GitHubToken = PasswordVault.Protect(token, password, AppServices.Settings.GitHubTokenUser);
            }
            await config.SaveZipAsync(path);
            StatusText = Loc.T("config.exported", Path.GetFileName(path));
            Dialogs.OpenFolder(path);
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("config.error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = Dialogs.OpenFile(Loc.T("config.import"), Loc.T("config.filter") + "|*.zip;*.json");
        if (path is not null) await ImportFromAsync(path);
    }

    /// <summary>Carga una configuración: sustituye las selecciones y ofrece aplicarla.</summary>
    public async Task ImportFromAsync(string path)
    {
        try
        {
            var config = await MyConfig.LoadAsync(path);
            config.ApplyTo(AppServices.Settings);
            await ProgramsViewModel.ImportGitHubTokenAsync(config);
            if (config.Has(MyConfig.PartPrivacy) && config.Has(MyConfig.PartOptimize))
                AppServices.TweakPreferences.Selections = new Dictionary<string, string>(config.Tweaks, StringComparer.OrdinalIgnoreCase);
            else
                foreach (var (k, v) in config.Tweaks) AppServices.TweakPreferences.Selections[k] = v;
            await AppServices.SaveSettingsAsync();
            await AppServices.SaveTweakPreferencesAsync();
            AppServices.RaiseConfigurationReplaced();
            if (config.Has(MyConfig.PartPrivacy))
                ViewModelLocator.Privacy.SetPendingPermissions(config.AppPermissions);
            _imported = config;
            ImportedTweaks = config.Tweaks.Count;
            ImportedPrograms = config.Programs.Count + config.GitHubRepos.Count;
            ImportedText = Loc.T("config.imported", ImportedTweaks, ImportedPrograms);
            HasImported = true;
            RefreshSummary();
            MainWindow.Current?.Navigate("config");
        }
        catch (Exception ex)
        {
            Core.Logging.Log.Warn("Config import failed: " + ex.Message);
            await Dialogs.InfoAsync(Loc.T("config.import"), Loc.T("config.invalid"));
        }
    }

    /// <summary>Aplica las optimizaciones importadas y después instala los programas.</summary>
    [RelayCommand]
    private async Task ApplyAllAsync()
    {
        if (_imported is null || IsBusy) return;
        IsBusy = true;
        try
        {
            if (_imported.Tweaks.Count > 0 || ViewModelLocator.Privacy.PendingPermissionApps > 0)
            {
                var message = Loc.T("config.apply.confirm", _imported.Tweaks.Count);
                if (await Dialogs.ConfirmAsync(Loc.T("config.apply"), message, Loc.T("tweaks.apply")))
                {
                    var privacy = _imported.Tweaks.Where(kv => ViewModelLocator.Privacy.Rows.Any(r => r.Def.Id.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                    var optimize = _imported.Tweaks.Where(kv => !privacy.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                    ViewModelLocator.Privacy.Refresh();
                    ViewModelLocator.Optimize.Refresh();
                    await ApplyQuietAsync(privacy, optimize);
                }
            }
            if (_imported.RemoveApps.Count > 0)
            {
                MainWindow.Current?.Navigate("debloat");
                await ViewModelLocator.Debloat.RemoveListedAsync(_imported.RemoveApps);
            }
            if (_imported.Programs.Count + _imported.GitHubRepos.Count > 0)
            {
                MainWindow.Current?.Navigate("programs");
                await ViewModelLocator.Programs.EnsureLoadedAsync();
                await ViewModelLocator.Programs.InstallSelectedAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task ApplyQuietAsync(Dictionary<string, string> privacy, Dictionary<string, string> optimize)
    {
        // Una sola petición de permisos por página; sin segunda confirmación.
        if (privacy.Count > 0) await ViewModelLocator.Privacy.ApplySelectionsAsync(privacy);
        if (optimize.Count > 0) await ViewModelLocator.Optimize.ApplySelectionsAsync(optimize);
    }

    partial void OnLanguageIndexChanged(int value)
    {
        AppServices.Settings.Language = value switch { 1 => "es", 2 => "en", _ => "auto" };
        Loc.Instance.Load(AppServices.Settings.Language);
        AppServices.SaveSettingsSoon();
        StatusText = Loc.T("config.restart-language");
    }

    partial void OnThemeIndexChanged(int value)
    {
        AppServices.Settings.Theme = value switch { 1 => "light", 2 => "system", _ => "dark" };
        App.ApplyTheme(AppServices.Settings.Theme);
        AppServices.SaveSettingsSoon();
    }

    [RelayCommand]
    private static void OpenLink(string url) => Dialogs.OpenUri(url);

    [RelayCommand]
    private void OpenData() => Dialogs.OpenFolder(AppServices.Paths.DataRoot);
}
