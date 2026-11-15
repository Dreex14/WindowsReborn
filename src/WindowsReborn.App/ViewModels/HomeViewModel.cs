using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Core.Compatibility;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Localization;

namespace WindowsReborn.App.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    public HomeViewModel()
    {
        _isFirstRun = !AppServices.Config.HasAnyConfiguration;
        AppServices.ConfigurationReplaced += (_, _) =>
        {
            IsFirstRun = false;
            _ = RefreshAsync();
        };
        _ = RefreshAsync();
    }

    [ObservableProperty] private string _lastBackupText = "";
    [ObservableProperty] private string _lastBackupDetail = "";
    [ObservableProperty] private string _programsText = "";
    [ObservableProperty] private string _tweaksText = "";

    /// <summary>Estado real: último backup, biblioteca y ajustes aplicados.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var s = AppServices.Settings;
        if (s.LastBackupPath is { } path && (File.Exists(path) || Directory.Exists(path)))
        {
            var when = File.Exists(path) ? File.GetLastWriteTime(path) : Directory.GetLastWriteTime(path);
            LastBackupText = Loc.T("home.last-backup", when.ToString("g"));
            LastBackupDetail = (s.LastBackupVerified ? Loc.T("home.verified") + " · " : "") + Core.Paths.KnownRoots.Friendly(path);
        }
        else
        {
            LastBackupText = Loc.T("home.no-backup");
            LastBackupDetail = Loc.T("home.no-backup-detail");
        }
        try
        {
            var library = await AppServices.ProgramStore.LoadAsync();
            ProgramsText = Loc.T("home.programs-count", library.Programs.Count);
        }
        catch
        {
            ProgramsText = "";
        }
        TweaksText = Loc.T("home.tweaks-count", AppServices.Tweaks.Journal.Entries.Count);
    }

    [RelayCommand]
    private void OpenRufus() => Dialogs.OpenUri(PrepareViewModel.RufusUrl);

    [RelayCommand]
    private void OpenWindows11() => Dialogs.OpenUri(PrepareViewModel.Windows11Url);

    public string WindowsText => WindowsVersion.Current.FriendlyName;
    public string VersionText => Loc.T("home.version", AppServices.Version);
    public bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));

    [ObservableProperty] private bool _isFirstRun;
    [ObservableProperty] private string? _message;

    [RelayCommand]
    private void Navigate(string key) => MainWindow.Current?.Navigate(key);

    [RelayCommand]
    private async Task ImportBackupAsync()
    {
        MainWindow.Current?.Navigate("restore");
        await ViewModelLocator.Restore.OpenFileCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        var file = Dialogs.OpenFile(Loc.T("config.import-title"), Loc.T("config.filter") + "|*.wrconfig");
        if (file is null) return;
        try
        {
            var package = ConfigStore.ReadPackageFromZip(file);
            if (package.IsEmpty)
            {
                Message = Loc.T("config.import-empty");
                return;
            }
            var files = string.Join(Environment.NewLine, package.Files.Keys.Select(k => "• " + k));
            if (!await Dialogs.ConfirmAsync(Loc.T("restore.config-title"), Loc.T("restore.config-message", files), Loc.T("restore.config-confirm")))
                return;
            await AppServices.Config.ApplyPackageAsync(package);
            AppServices.Settings = await AppServices.Config.LoadSettingsAsync();
            Loc.Instance.Load(AppServices.Settings.Language);
            App.ApplyTheme(AppServices.Settings.Theme);
            AppServices.RaiseConfigurationReplaced();
            Message = Loc.T("restore.config-done", package.Files.Count);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Message = Loc.T("config.import-failed", ex.Message);
        }
    }
}
