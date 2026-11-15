using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Core.Localization;
using WindowsReborn.Core.Paths;
using WindowsReborn.Restore;
using WindowsReborn.Storage;
using Wpf.Ui.Controls;

namespace WindowsReborn.App.ViewModels;

public partial class RestoreCategoryItem : ObservableObject
{
    public required CategoryTarget Target { get; init; }
    public string Name => Target.Category.Name;
    public string Summary => $"{Loc.T("unit.files", Target.Category.FileCount)} · {Format.Bytes(Target.Category.Bytes)}";
    public string Origin => Target.Category.OriginalPath is { } p ? KnownRoots.Friendly(p) : Target.Category.Root;
    public bool IsAppConfig => Target.Category.Kind == "appConfig";
    public string? AppNote => IsAppConfig && Target.Category.ProcessNames.Count > 0
        ? Loc.T("restore.app-closed-note", string.Join(", ", Target.Category.ProcessNames)) : null;

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string? _targetFolder;

    partial void OnIsSelectedChanged(bool value) => Target.Selected = value;
    partial void OnTargetFolderChanged(string? value) => Target.TargetFolder = value;
}

public enum IntegrityState
{
    NotChecked,
    Checking,
    Valid,
    Invalid,
}

public partial class RestoreViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private IBackupReader? _reader;
    private BackupManifest? _manifest;
    private RestorePlan? _plan;

    public ObservableCollection<RestoreCategoryItem> Categories { get; } = [];
    public ObservableCollection<string> Messages { get; } = [];
    public ObservableCollection<string> ReportLines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackupDisplayPath))]
    private string _backupPath = "";

    public string BackupDisplayPath => BackupPath.Length == 0 ? "" : KnownRoots.Friendly(BackupPath);
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _needsPassword;
    public string Password { get; set; } = "";

    // Información del backup
    [ObservableProperty] private string _createdText = "";
    [ObservableProperty] private string _machineText = "";
    [ObservableProperty] private string _windowsText = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _formatText = "";
    [ObservableProperty] private bool _hasConfiguration;
    [ObservableProperty] private bool _hasInstallers;
    [ObservableProperty] private bool _hasWinGetList;
    [ObservableProperty] private IntegrityState _integrity;
    [ObservableProperty] private string _integrityText = "";

    [ObservableProperty] private ConflictPolicy _policy = ConflictPolicy.Skip;
    [ObservableProperty] private bool _isPlanned;
    [ObservableProperty] private string _planText = "";

    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _currentItem = "";
    [ObservableProperty] private string _progressDetail = "";
    [ObservableProperty] private bool _showProgress;

    [ObservableProperty] private bool _resultVisible;
    [ObservableProperty] private InfoBarSeverity _resultSeverity;
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultMessage = "";

    [ObservableProperty] private string _configStatus = "";

    partial void OnPolicyChanged(ConflictPolicy value) => IsPlanned = false;

    partial void OnIsBusyChanged(bool value)
    {
        OpenFileCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        AnalyzeCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        VerifyCommand.NotifyCanExecuteChanged();
        RecoverConfigurationCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        ApplyPreferencesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPlannedChanged(bool value) => RestoreCommand.NotifyCanExecuteChanged();

    private bool NotBusy() => !IsBusy;
    private bool CanUseBackup() => IsOpen && !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task OpenFileAsync()
    {
        var file = Dialogs.OpenFile(Loc.T("restore.pick-file"), Loc.T("restore.file-filter") + "|*.wrbk.zip;*.wrbk.7z;*.zip;*.7z|" + Loc.T("restore.manifest-filter") + "|manifest.json");
        if (file is not null) await OpenAsync(file);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task OpenFolderAsync()
    {
        var folder = Dialogs.PickFolder(Loc.T("restore.pick-folder"));
        if (folder is not null) await OpenAsync(folder);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task RetryWithPasswordAsync() => OpenAsync(BackupPath);

    public async Task OpenAsync(string path)
    {
        Close();
        BackupPath = path;
        IsBusy = true;
        PhaseText = Loc.T("restore.opening");
        ShowProgress = true;
        ProgressValue = 0;
        try
        {
            var sevenZip = AppServices.SevenZipPath;
            var isSevenZip = BackupFormatExtensions.Detect(path) == BackupFormat.SevenZip;
            if (isSevenZip && sevenZip is null)
            {
                Messages.Add(Loc.T("restore.needs-7zip"));
                return;
            }

            var password = Password.Length > 0 ? Password : null;
            (_reader, _manifest) = await Task.Run(() =>
            {
                var reader = BackupReader.Open(path, sevenZip, password);
                try { return (reader, BackupReader.ReadManifest(reader)); }
                catch { reader.Dispose(); throw; }
            });

            NeedsPassword = false;
            IsOpen = true;
            CreatedText = _manifest.CreatedUtc.ToLocalTime().ToString("f");
            MachineText = _manifest.MachineName ?? Loc.T("restore.anonymized");
            WindowsText = $"{_manifest.Windows.ProductName} {_manifest.Windows.DisplayVersion} ({_manifest.Windows.Build})".Trim();
            SizeText = $"{Loc.T("unit.files", _manifest.DataFiles.Count())} · {Format.Bytes(_manifest.DataFiles.Sum(f => f.Size))}";
            FormatText = _manifest.Format.ToUpperInvariant() + (_manifest.Encrypted ? " · " + Loc.T("restore.encrypted") : "");
            HasConfiguration = _manifest.HasConfiguration;
            HasInstallers = _manifest.HasInstallers;
            HasWinGetList = _manifest.Files.Any(f => f.Section == Sections.Apps);
            Integrity = IntegrityState.NotChecked;
            IntegrityText = Loc.T("integrity.not-checked");
            ConfigStatus = HasConfiguration ? Loc.T("restore.config-available") : Loc.T("restore.config-missing");

            if (_manifest.Windows.Build > 0 && _manifest.Windows.Build != Core.Compatibility.WindowsVersion.Current.Build)
                Messages.Add(Loc.T("restore.different-windows", WindowsText, Core.Compatibility.WindowsVersion.Current.FriendlyName));
            if (_manifest.UserName is not null && !string.Equals(_manifest.UserName, Environment.UserName, StringComparison.OrdinalIgnoreCase))
                Messages.Add(Loc.T("restore.different-user"));

            foreach (var target in RestoreEngine.SuggestTargets(_manifest))
            {
                var item = new RestoreCategoryItem { Target = target, TargetFolder = target.TargetFolder };
                item.PropertyChanged += (_, _) => IsPlanned = false;
                Categories.Add(item);
            }

            var report = BackupReader.ReadReport(_reader);
            if (report is { Skipped.Count: > 0 })
                Messages.Add(Loc.T("restore.created-with-skips", report.Skipped.Count));

            AppServices.OpenedBackup = _reader;
            AppServices.OpenedManifest = _manifest;
            AppServices.RaiseOpenedBackupChanged();
            await ApplySavedTargetsAsync();
            Mark("open", StepState.Done);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException or InvalidOperationException or Core.Paths.UnsafePathException or System.Text.Json.JsonException)
        {
            NeedsPassword = BackupFormatExtensions.Detect(path) == BackupFormat.SevenZip;
            Messages.Add(Loc.T("restore.open-failed", ex.Message));
        }
        finally
        {
            IsBusy = false;
            ShowProgress = false;
            PhaseText = "";
        }
    }

    private void Close()
    {
        _cts?.Cancel();
        if (ReferenceEquals(AppServices.OpenedBackup, _reader))
        {
            AppServices.OpenedBackup = null;
            AppServices.OpenedManifest = null;
            AppServices.RaiseOpenedBackupChanged();
        }
        _reader?.Dispose();
        _reader = null;
        _manifest = null;
        _plan = null;
        IsOpen = false;
        IsPlanned = false;
        ResultVisible = false;
        Categories.Clear();
        Messages.Clear();
        ReportLines.Clear();
        ResetWizard();
    }

    [RelayCommand]
    private void BrowseTarget(RestoreCategoryItem? item)
    {
        if (item is null) return;
        var folder = Dialogs.PickFolder(Loc.T("restore.pick-target", item.Name), item.TargetFolder);
        if (folder is not null) item.TargetFolder = folder;
    }

    [RelayCommand(CanExecute = nameof(CanUseBackup))]
    private async Task VerifyAsync()
    {
        if (_reader is null || _manifest is null) return;
        IsBusy = true;
        ShowProgress = true;
        Integrity = IntegrityState.Checking;
        IntegrityText = Loc.T("integrity.checking");
        PhaseText = Loc.T("phase.verifying");
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<VerificationProgress>(p =>
            {
                ProgressValue = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal : 0;
                CurrentItem = p.Current ?? "";
                ProgressDetail = $"{Format.Bytes(p.BytesDone)} / {Format.Bytes(p.BytesTotal)}";
            });
            var reader = _reader;
            var manifest = _manifest;
            var token = _cts.Token;
            var result = await Task.Run(() => BackupVerifier.VerifyAsync(reader, manifest, null, progress, token));
            Integrity = result.IsValid ? IntegrityState.Valid : IntegrityState.Invalid;
            if (result.IsValid) Mark("verify", StepState.Done);
            IntegrityText = result.IsValid
                ? Loc.T("integrity.valid", result.CheckedFiles)
                : Loc.T("integrity.invalid", result.HashMismatch.Count, result.Missing.Count, result.Unreadable.Count);
        }
        catch (OperationCanceledException)
        {
            Integrity = IntegrityState.NotChecked;
            IntegrityText = Loc.T("integrity.not-checked");
        }
        finally
        {
            IsBusy = false;
            ShowProgress = false;
            CurrentItem = "";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseBackup))]
    private void Analyze()
    {
        if (_manifest is null) return;
        var missing = Categories.Where(c => c.IsSelected && string.IsNullOrWhiteSpace(c.TargetFolder)).Select(c => c.Name).ToList();
        if (missing.Count > 0)
        {
            PlanText = Loc.T("restore.missing-targets", string.Join(", ", missing));
            IsPlanned = false;
            return;
        }

        _plan = RestoreEngine.BuildPlan(_manifest, Categories.Select(c => c.Target));
        if (_plan.Items.Count == 0)
        {
            PlanText = Loc.T("restore.nothing-selected");
            IsPlanned = false;
            return;
        }
        PlanText = Loc.T("restore.plan-summary", _plan.Items.Count, Format.Bytes(_plan.TotalBytes), _plan.Conflicts) +
                   (_plan.InvalidPaths.Count > 0 ? " " + Loc.T("restore.plan-invalid", _plan.InvalidPaths.Count) : "");
        IsPlanned = true;
        Mark("select", StepState.Done);
    }

    private bool CanRestore() => IsPlanned && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        if (_reader is null || _plan is null) return;

        if (Policy == ConflictPolicy.Overwrite && _plan.Conflicts > 0 &&
            !await Dialogs.ConfirmAsync(Loc.T("restore.overwrite-title"), Loc.T("restore.overwrite-message", _plan.Conflicts), Loc.T("restore.overwrite-confirm"), danger: true))
            return;

        var blockers = RestoreEngine.RunningBlockers(_plan.Items.Select(i => i.Category).DistinctBy(c => c.Id));
        if (blockers.Count > 0 &&
            !await Dialogs.ConfirmAsync(Loc.T("restore.apps-running-title"), Loc.T("restore.apps-running-message", string.Join(", ", blockers)), Loc.T("restore.continue")))
            return;

        IsBusy = true;
        ShowProgress = true;
        ResultVisible = false;
        ReportLines.Clear();
        PhaseText = Loc.T("phase.restoring");
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<RestoreProgress>(p =>
            {
                ProgressValue = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal : 0;
                CurrentItem = p.Current ?? "";
                ProgressDetail = $"{Format.Bytes(p.BytesDone)} / {Format.Bytes(p.BytesTotal)} · {Loc.T("unit.files-of", p.FilesDone, p.FilesTotal)}";
            });
            var reader = _reader;
            var plan = _plan;
            var policy = Policy;
            var token = _cts.Token;
            var name = Path.GetFileName(BackupPath.TrimEnd('\\'));
            var report = await Task.Run(() => RestoreEngine.ExecuteAsync(reader, plan, policy, name, progress, token));
            await RestoreEngine.SaveReportAsync(report, AppServices.Paths.ReportsDirectory);

            var restored = report.Count(RestoreOutcome.Restored) + report.Count(RestoreOutcome.Renamed) + report.Count(RestoreOutcome.Overwritten);
            ResultVisible = true;
            ResultSeverity = report.Cancelled ? InfoBarSeverity.Informational
                : report.HasFailures ? InfoBarSeverity.Error
                : report.Count(RestoreOutcome.SkippedAppRunning) + report.Count(RestoreOutcome.SkippedExisting) > 0 ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
            ResultTitle = report.Cancelled ? Loc.T("restore.result.cancelled")
                : report.HasFailures ? Loc.T("restore.result.failures")
                : Loc.T("restore.result.done");
            ResultMessage = Loc.T("restore.result.summary", restored, report.Count(RestoreOutcome.Renamed), report.Count(RestoreOutcome.Overwritten),
                report.Count(RestoreOutcome.SkippedExisting), report.Count(RestoreOutcome.SkippedAppRunning), report.Count(RestoreOutcome.Failed));
            foreach (var item in report.Items.Where(i => i.Outcome is not RestoreOutcome.Restored).Take(500))
                ReportLines.Add($"{Loc.T("outcome." + item.Outcome.ToString().ToLowerInvariant())} — {item.Path}{(item.Detail is null ? "" : $" ({item.Detail})")}");
            IsPlanned = false;
            if (!report.Cancelled) Mark("files", StepState.Done);
        }
        finally
        {
            IsBusy = false;
            ShowProgress = false;
            CurrentItem = "";
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanUseBackup))]
    private async Task RecoverConfigurationAsync()
    {
        if (_reader is null || _manifest is null) return;
        var package = RestoreEngine.ReadConfiguration(_reader, _manifest);
        if (package is null || package.IsEmpty)
        {
            ConfigStatus = Loc.T("restore.config-missing");
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
        Mark("config", StepState.Done);
        await ApplySavedTargetsAsync();
        ConfigStatus = Loc.T("restore.config-done", package.Files.Count) +
                       (package.Rejected.Count > 0 ? " " + Loc.T("restore.config-rejected", package.Rejected.Count) : "");
    }

    [RelayCommand]
    private void GoToPrograms() => MainWindow.Current?.Navigate("programs");

    [RelayCommand]
    private void OpenRecoverySettings() => Dialogs.OpenUri("ms-settings:recovery");

    [RelayCommand]
    private void OpenWindowsBackup() => Dialogs.OpenUri("ms-settings:backup");

    [RelayCommand]
    private void OpenSystemRestore() => Dialogs.RunSystemTool("rstrui.exe");

    [RelayCommand]
    private void OpenReports() => Dialogs.OpenFolder(AppServices.Paths.ReportsDirectory);
}
