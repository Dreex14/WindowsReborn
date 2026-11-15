using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Core.Localization;
using WindowsReborn.Storage;

namespace WindowsReborn.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    public const string ReleasesUrl = "https://github.com/Dreex14/WindowsReborn/releases";

    private bool _loading;

    public SettingsViewModel()
    {
        Load();
        AppServices.ConfigurationReplaced += (_, _) => Load();
    }

    public IReadOnlyList<Option<string>> Languages { get; } =
        [new("auto", Loc.T("settings.lang-auto")), new("es", "Español"), new("en", "English")];

    public IReadOnlyList<Option<string>> Themes { get; } =
        [new("dark", Loc.T("settings.theme-dark")), new("light", Loc.T("settings.theme-light")), new("system", Loc.T("settings.theme-system"))];

    [ObservableProperty] private Option<string> _language = null!;
    [ObservableProperty] private Option<string> _theme = null!;
    [ObservableProperty] private string _sevenZipPath = "";
    [ObservableProperty] private string _sevenZipStatus = "";
    [ObservableProperty] private string _installersFolder = "";
    [ObservableProperty] private bool _anonymize;
    [ObservableProperty] private bool _allowUpdateCheck;
    [ObservableProperty] private string? _message;

    public string DataFolder => Core.Paths.KnownRoots.Friendly(AppServices.Paths.DataRoot);

    private void Load()
    {
        _loading = true;
        var s = AppServices.Settings;
        Language = Languages.FirstOrDefault(l => l.Value == s.Language) ?? Languages[0];
        Theme = Themes.FirstOrDefault(t => t.Value == s.Theme) ?? Themes[0];
        SevenZipPath = s.SevenZipPath ?? "";
        InstallersFolder = Core.Paths.KnownRoots.Friendly(AppServices.InstallersFolder);
        Anonymize = s.AnonymizeManifest;
        AllowUpdateCheck = s.AllowUpdateCheck;
        UpdateSevenZipStatus();
        _loading = false;
    }

    private void UpdateSevenZipStatus() =>
        SevenZipStatus = AppServices.SevenZipPath is { } p ? Loc.T("settings.7z-found", p) : Loc.T("settings.7z-missing");

    private async Task SaveAsync()
    {
        if (_loading) return;
        await AppServices.SaveSettingsAsync();
    }

    partial void OnLanguageChanged(Option<string> value)
    {
        if (_loading) return;
        AppServices.Settings.Language = value.Value;
        Loc.Instance.Load(value.Value);
        Message = Loc.T("settings.lang-restart");
        _ = SaveAsync();
    }

    partial void OnThemeChanged(Option<string> value)
    {
        if (_loading) return;
        AppServices.Settings.Theme = value.Value;
        App.ApplyTheme(value.Value);
        _ = SaveAsync();
    }

    partial void OnAnonymizeChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.AnonymizeManifest = value;
        _ = SaveAsync();
    }

    partial void OnAllowUpdateCheckChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.AllowUpdateCheck = value;
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        _ = SaveAsync();
    }

    [RelayCommand]
    private async Task BrowseSevenZipAsync()
    {
        var file = Dialogs.OpenFile(Loc.T("settings.7z-pick"), "7z.exe|7z.exe");
        if (file is null) return;
        AppServices.Settings.SevenZipPath = file;
        SevenZipPath = file;
        UpdateSevenZipStatus();
        await SaveAsync();
        Message = Loc.T("settings.restart-for-formats");
    }

    [RelayCommand]
    private async Task BrowseInstallersAsync()
    {
        var folder = Dialogs.PickFolder(Loc.T("settings.installers-pick"), AppServices.InstallersFolder);
        if (folder is null) return;
        AppServices.Settings.InstallersFolder = folder;
        InstallersFolder = Core.Paths.KnownRoots.Friendly(folder);
        await SaveAsync();
        await ViewModelLocator.Programs.LoadAsync();
    }

    private bool CanCheckUpdates() => AllowUpdateCheck;

    /// <summary>Comprobación manual: abre la página de publicaciones en el navegador; la app no se conecta sola.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private void CheckUpdates() => Dialogs.OpenUri(ReleasesUrl);

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        var file = Dialogs.SaveFile(Loc.T("config.export-title"), Loc.T("config.filter") + "|*.wrconfig", $"WindowsReborn-{DateTime.Now:yyyy-MM-dd}.wrconfig");
        if (file is null) return;
        await AppServices.Config.ExportAsync(file, AppServices.Version);
        Message = Loc.T("config.export-done", Path.GetFileName(file));
    }

    [RelayCommand]
    private Task ImportConfigAsync() => ViewModelLocator.Home.ImportConfigCommand.ExecuteAsync(null);

    [RelayCommand]
    private void OpenDataFolder() => Dialogs.OpenFolder(AppServices.Paths.DataRoot);

    [RelayCommand]
    private void OpenLogs() => Dialogs.OpenFolder(AppServices.Paths.LogsDirectory);
}
