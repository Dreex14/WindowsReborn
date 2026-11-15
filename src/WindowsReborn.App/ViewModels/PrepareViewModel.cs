using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Backup;
using WindowsReborn.Core.Localization;
using WindowsReborn.Core.Paths;
using WindowsReborn.Storage;

namespace WindowsReborn.App.ViewModels;

public sealed class CheckItem(ReadinessCheck check, IRelayCommand? action, string? actionLabel)
{
    public ReadinessCheck Check { get; } = check;
    public string Title => Loc.T("prepare.check." + Check.Key);
    public string Detail => Loc.T("prepare.detail." + Check.DetailKey, [.. Check.Args]);
    public CheckState State => Check.State;
    public string Symbol => State switch
    {
        CheckState.Ok => "CheckmarkCircle24",
        CheckState.Warning => "Warning24",
        CheckState.Error => "DismissCircle24",
        _ => "Circle24",
    };
    public IRelayCommand? Action { get; } = action;
    public string? ActionLabel { get; } = actionLabel;
}

/// <summary>
/// Asistente «Preparar para formatear»: revisa el backup y la configuración y
/// enlaza las herramientas oficiales para crear el medio de instalación. No
/// formatea nada.
/// </summary>
public partial class PrepareViewModel : ObservableObject
{
    public const string RufusUrl = "https://rufus.ie/";
    public const string Windows11Url = "https://www.microsoft.com/software-download/windows11";

    private BackupManifest? _manifest;
    private bool? _verified;
    private CancellationTokenSource? _cts;

    public PrepareViewModel()
    {
        var last = AppServices.Settings.LastBackupPath;
        if (last is not null && (File.Exists(last) || Directory.Exists(last)))
        {
            _backupPath = last;
            _verified = AppServices.Settings.LastBackupVerified ? true : null;
        }
        _ = CheckAsync();
    }

    public ObservableCollection<CheckItem> Checks { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(BackupDisplay))] private string? _backupPath;

    partial void OnBackupPathChanged(string? value) => OpenBackupFolderCommand.NotifyCanExecuteChanged();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _message;

    public string BackupDisplay => BackupPath is null ? Loc.T("prepare.no-backup") : KnownRoots.Friendly(BackupPath);

    [RelayCommand]
    private async Task ChooseBackupAsync()
    {
        var file = Dialogs.OpenFile(Loc.T("restore.pick-file"), Loc.T("restore.file-filter") + "|*.wrbk.zip;*.wrbk.7z;*.zip;*.7z|" + Loc.T("restore.manifest-filter") + "|manifest.json");
        if (file is null) return;
        BackupPath = Path.GetFileName(file).Equals(BackupManifest.FileName, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(file) : file;
        _verified = null;
        await CheckAsync();
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        Message = null;
        _manifest = null;
        if (BackupPath is not null)
        {
            try
            {
                var path = BackupPath;
                _manifest = await Task.Run(() =>
                {
                    using var reader = BackupReader.Open(path, AppServices.SevenZipPath);
                    return BackupReader.ReadManifest(reader);
                });
            }
            catch (Exception ex)
            {
                Message = Loc.T("prepare.open-failed", ex.Message);
            }
        }

        var selection = await new ProfileStore(AppServices.Paths.ConfigDirectory).LoadSelectionAsync();
        var library = await AppServices.ProgramStore.LoadAsync();
        var configChanged = AppServices.Config.EnumerateFiles()
            .Where(f => !f.Relative.Equals(Core.Configuration.AppSettings.FileName, StringComparison.OrdinalIgnoreCase))
            .Select(f => (DateTime?)File.GetLastWriteTimeUtc(f.FullPath)).DefaultIfEmpty(null).Max();

        var checks = FormatReadiness.Evaluate(new ReadinessInput
        {
            BackupPath = BackupPath,
            Manifest = _manifest,
            Verified = _verified,
            Selection = selection,
            LibraryPrograms = library.Programs.Count,
            ConfigChangedUtc = configChanged,
            AppCopiedNextToBackup = BackupPath is not null && File.Exists(Path.Combine(BackupFolder(BackupPath), "WindowsReborn.exe")),
        });

        Checks.Clear();
        foreach (var c in checks)
        {
            var (cmd, label) = c.Key switch
            {
                "exists" when c.State == CheckState.Error => ((IRelayCommand?)GoToBackupCommand, Loc.T("prepare.action.backup")),
                "integrity" when c.State != CheckState.Ok => (VerifyCommand, Loc.T("prepare.action.verify")),
                "custom" or "config" or "programs" or "installers" when c.State != CheckState.Ok => (GoToBackupCommand, Loc.T("prepare.action.new-backup")),
                "recoverable" when c.DetailKey == "recoverable.copy-app" => (CopyAppCommand, Loc.T("prepare.action.copy-app")),
                _ => (null, null),
            };
            Checks.Add(new CheckItem(c, cmd, label));
        }
        IsReady = FormatReadiness.IsReady(checks);
        var warnings = checks.Count(c => c.State == CheckState.Warning);
        Summary = IsReady
            ? (warnings == 0 ? Loc.T("prepare.ready") : Loc.T("prepare.ready-warnings", warnings))
            : Loc.T("prepare.not-ready");
    }

    private bool CanVerify() => _manifest is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifyAsync()
    {
        if (BackupPath is null || _manifest is null) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var path = BackupPath;
            var manifest = _manifest;
            var token = _cts.Token;
            var progress = new Progress<VerificationProgress>(p =>
            {
                Progress = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal : 0;
                ProgressText = $"{Format.Bytes(p.BytesDone)} / {Format.Bytes(p.BytesTotal)}";
            });
            var result = await Task.Run(async () =>
            {
                using var reader = BackupReader.Open(path, AppServices.SevenZipPath);
                return await BackupVerifier.VerifyAsync(reader, manifest, null, progress, token);
            });
            _verified = result.IsValid;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Message = Loc.T("prepare.open-failed", ex.Message);
            _verified = false;
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
        await CheckAsync();
    }

    partial void OnIsBusyChanged(bool value) => VerifyCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Copia el ejecutable portátil junto al backup para poder restaurar sin descargar nada.</summary>
    [RelayCommand]
    private async Task CopyAppAsync()
    {
        if (BackupPath is null || Environment.ProcessPath is not { } exe) return;
        var target = Path.Combine(BackupFolder(BackupPath), "WindowsReborn.exe");
        try
        {
            await Task.Run(() => File.Copy(exe, target, overwrite: false));
            Message = Loc.T("prepare.app-copied", KnownRoots.Friendly(target));
        }
        catch (IOException) when (File.Exists(target))
        {
            Message = Loc.T("prepare.app-exists");
        }
        catch (Exception ex)
        {
            Message = Loc.T("prepare.open-failed", ex.Message);
        }
        await CheckAsync();
    }

    private static string BackupFolder(string backupPath) =>
        Directory.Exists(backupPath) ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(backupPath))! : Path.GetDirectoryName(backupPath)!;

    [RelayCommand]
    private void GoToBackup() => MainWindow.Current?.Navigate("backup");

    [RelayCommand]
    private void OpenRufus() => Dialogs.OpenUri(RufusUrl);

    [RelayCommand]
    private void OpenWindows11() => Dialogs.OpenUri(Windows11Url);

    private bool HasBackup() => BackupPath is not null;

    [RelayCommand(CanExecute = nameof(HasBackup))]
    private void OpenBackupFolder()
    {
        if (BackupPath is not null) Dialogs.OpenFolder(BackupPath);
    }
}
