using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Backup;
using WindowsReborn.Core.Catalog;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Localization;
using WindowsReborn.Core.Paths;
using WindowsReborn.Programs;
using WindowsReborn.Storage;

namespace WindowsReborn.App.ViewModels;

public partial class CategoryItem : ObservableObject
{
    public required string Id { get; init; }
    public required string Root { get; init; }
    public required string Name { get; init; }
    public required string Symbol { get; init; }
    public string? Path { get; init; }
    public string DisplayPath => Path is null ? Loc.T("backup.folder-missing") : KnownRoots.Friendly(Path);
    public bool Available => Path is not null;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _sizeText = "";
}

public partial class CustomFolderItem : ObservableObject
{
    public CustomFolderItem(CustomFolderEntry entry)
    {
        Entry = entry;
        _isSelected = entry.Selected;
        _isProjects = entry.IsProjects;
        _exclusionsText = string.Join("; ", entry.Exclusions);
        ResolvedPath = entry.Resolve();
        Exists = ResolvedPath is not null && Directory.Exists(ResolvedPath);
    }

    public CustomFolderEntry Entry { get; }
    public string? ResolvedPath { get; }
    public bool Exists { get; }
    public string Name => Entry.Name;
    public string DisplayPath => Entry.DisplayPath;
    public string StatusText => Exists ? SizeText : Loc.T("backup.folder-missing");

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isProjects;
    [ObservableProperty] private string _exclusionsText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private string _sizeText = "";

    /// <summary>Entrada actualizada con lo que hay en pantalla.</summary>
    public CustomFolderEntry ToEntry() => new()
    {
        Path = Entry.Path,
        Name = Entry.Name,
        Root = Entry.Root,
        SubPath = Entry.SubPath,
        Selected = IsSelected,
        IsProjects = IsProjects,
        Exclusions = ExclusionsText.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        RestoreTarget = Entry.RestoreTarget,
    };
}

public partial class AdapterItem : ObservableObject
{
    public required AppConfigAdapter Adapter { get; init; }
    public required bool Present { get; init; }
    public string Name => Adapter.Name;
    public string? Warning => Adapter.WarningKey is null ? null : Loc.T(Adapter.WarningKey);
    public string Files => string.Join(", ", Adapter.Include);
    [ObservableProperty] private bool _isSelected;
}

public sealed record Option<T>(T Value, string Label, bool Enabled = true)
{
    public override string ToString() => Label;
}

public partial class BackupViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer;
    private bool _loadingSelection;
    private InventoryResult? _inventory;
    private List<BackupSource> _analyzedSources = [];

    public BackupViewModel()
    {
        foreach (var (id, root) in SourceFactory.Recommended)
        {
            var item = new CategoryItem
            {
                Id = id, Root = root, Name = Loc.T($"category.{id}"), Symbol = SymbolFor(id),
                Path = KnownRoots.Resolve(root) is { } p && Directory.Exists(p) ? p : null,
            };
            item.IsSelected = item.Available && id is "desktop" or "documents" or "pictures";
            item.PropertyChanged += (_, _) => Invalidate();
            Categories.Add(item);
        }

        foreach (var adapter in AppConfigCatalog.All)
        {
            var item = new AdapterItem { Adapter = adapter, Present = AppConfigCatalog.IsPresent(adapter) };
            item.IsSelected = item.Present && adapter.WarningKey is null;
            item.PropertyChanged += (_, _) => Invalidate();
            Adapters.Add(item);
        }

        var settings = AppServices.Settings;
        _destination = settings.LastBackupDestination ?? "";
        _backupName = DefaultName();
        _exclusionsText = string.Join(Environment.NewLine, BackupDefaults.Exclusions);
        _anonymize = settings.AnonymizeManifest;

        var sevenZip = AppServices.SevenZipPath;
        Formats =
        [
            new(BackupFormat.Zip, Loc.T("format.zip")),
            new(BackupFormat.SevenZip, sevenZip is null ? Loc.T("format.7z-missing") : Loc.T("format.7z"), sevenZip is not null),
            new(BackupFormat.Folder, Loc.T("format.folder")),
        ];
        _selectedFormat = Formats.FirstOrDefault(f => f.Value.Id() == settings.PreferredFormat && f.Enabled) ?? Formats[0];
        Compressions =
        [
            new(CompressionPreset.Balanced, Loc.T("compression.balanced")),
            new(CompressionPreset.Fast, Loc.T("compression.fast")),
            new(CompressionPreset.Maximum, Loc.T("compression.maximum")),
            new(CompressionPreset.Store, Loc.T("compression.store")),
        ];
        _selectedCompression = Compressions[0];

        CustomFolders.CollectionChanged += (_, _) => Invalidate();
        AppServices.ConfigurationReplaced += async (_, _) =>
        {
            await LoadProfilesAsync();
            await LoadSelectionAsync();
        };
        _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveSelectionAsync();
        };
        _ = LoadProfilesAsync();
        _ = LoadSelectionAsync();
        _ = RefreshInstallersAsync();
    }

    public ObservableCollection<CategoryItem> Categories { get; } = [];
    public ObservableCollection<CustomFolderItem> CustomFolders { get; } = [];
    public ObservableCollection<AdapterItem> Adapters { get; } = [];
    public ObservableCollection<BackupProfile> Profiles { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> SkippedItems { get; } = [];
    public IReadOnlyList<Option<BackupFormat>> Formats { get; }
    public IReadOnlyList<Option<CompressionPreset>> Compressions { get; }

    [ObservableProperty] private string _destination;
    [ObservableProperty] private string _backupName;
    [ObservableProperty] private Option<BackupFormat> _selectedFormat;
    [ObservableProperty] private Option<CompressionPreset> _selectedCompression;
    [ObservableProperty] private string _exclusionsText;
    [ObservableProperty] private bool _anonymize;
    [ObservableProperty] private bool _includeWinGetList = true;
    [ObservableProperty] private bool _includeInstallers;
    [ObservableProperty] private int _availableInstallers;
    [ObservableProperty] private bool _includeSensitive;
    [ObservableProperty] private BackupProfile? _selectedProfile;
    [ObservableProperty] private string _customPathText = "";

    public string Password { get; set; } = "";
    public string PasswordConfirm { get; set; } = "";

    // Estado del análisis
    [ObservableProperty] private bool _isAnalyzed;
    [ObservableProperty] private string _summaryFiles = "—";
    [ObservableProperty] private string _summarySize = "—";
    [ObservableProperty] private string _summaryFree = "—";
    [ObservableProperty] private string _summaryRequired = "—";

    // Estado de la ejecución
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string _currentItem = "";
    [ObservableProperty] private string _progressDetail = "";

    // Resultado
    [ObservableProperty] private bool _resultVisible;
    [ObservableProperty] private Wpf.Ui.Controls.InfoBarSeverity _resultSeverity;
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultMessage = "";
    [ObservableProperty] private string? _resultPath;

    public bool IsSevenZip => SelectedFormat.Value == BackupFormat.SevenZip;
    public bool IsCompressed => SelectedFormat.Value != BackupFormat.Folder;
    public bool CanEncrypt => IsSevenZip;

    partial void OnSelectedFormatChanged(Option<BackupFormat> value)
    {
        if (!value.Enabled)
        {
            SelectedFormat = Formats[0];
            return;
        }
        OnPropertyChanged(nameof(IsSevenZip));
        OnPropertyChanged(nameof(IsCompressed));
        OnPropertyChanged(nameof(CanEncrypt));
        Invalidate();
    }

    partial void OnDestinationChanged(string value) => Invalidate();
    partial void OnBackupNameChanged(string value) => Invalidate();
    partial void OnExclusionsTextChanged(string value) => Invalidate();
    partial void OnIncludeSensitiveChanged(bool value) => Invalidate();
    partial void OnSelectedCompressionChanged(Option<CompressionPreset> value) => Invalidate();
    partial void OnIncludeInstallersChanged(bool value) => Invalidate();
    partial void OnIncludeWinGetListChanged(bool value) => Invalidate();
    partial void OnAnonymizeChanged(bool value) => Invalidate();

    partial void OnSelectedProfileChanged(BackupProfile? value)
    {
        DeleteProfileCommand.NotifyCanExecuteChanged();
        if (value is not null)
            ApplyProfile(value);
    }

    private void Invalidate()
    {
        if (!_loadingSelection)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        if (IsRunning) return;
        IsAnalyzed = false;
        CreateBackupCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var folder = Dialogs.PickFolder(Loc.T("backup.pick-destination"), Destination);
        if (folder is not null) Destination = folder;
    }

    [RelayCommand]
    private void AddFolder() => AddCustom(false);

    [RelayCommand]
    private void AddProjectsFolder() => AddCustom(true);

    private void AddCustom(bool projects)
    {
        var folder = Dialogs.PickFolder(Loc.T(projects ? "backup.pick-projects" : "backup.pick-folder"));
        if (folder is null) return;
        AddEntry(CustomFolderEntry.FromPath(folder, projects));
    }

    /// <summary>Añade una ruta escrita a mano; admite variables como <c>%APPDATA%\.minecraft</c>.</summary>
    [RelayCommand]
    private void AddTypedPath()
    {
        Errors.Clear();
        if (string.IsNullOrWhiteSpace(CustomPathText)) return;
        CustomFolderEntry entry;
        try
        {
            entry = CustomFolderEntry.FromPath(CustomPathText);
        }
        catch (Exception)
        {
            Errors.Add(Loc.T("backup.path-invalid"));
            return;
        }
        if (!entry.Exists)
        {
            Errors.Add(Loc.T("backup.path-not-found", entry.DisplayPath));
            return;
        }
        AddEntry(entry);
        CustomPathText = "";
    }

    private void AddEntry(CustomFolderEntry entry)
    {
        var path = entry.Resolve();
        if (CustomFolders.Any(c => string.Equals(c.ResolvedPath, path, StringComparison.OrdinalIgnoreCase))) return;
        AddItem(entry);
    }

    private void AddItem(CustomFolderEntry entry)
    {
        var item = new CustomFolderItem(entry);
        item.PropertyChanged += (_, e) => { if (e.PropertyName != nameof(CustomFolderItem.SizeText) && e.PropertyName != nameof(CustomFolderItem.StatusText)) Invalidate(); };
        CustomFolders.Add(item);
    }

    [RelayCommand]
    private void RemoveFolder(CustomFolderItem? item)
    {
        if (item is not null) CustomFolders.Remove(item);
    }

    private List<BackupSource> BuildSources()
    {
        var sources = new List<BackupSource>();
        foreach (var c in Categories.Where(c => c.IsSelected && c.Available))
        {
            var s = SourceFactory.ForKnownFolder(c.Id, c.Root, c.Name);
            if (s is not null) sources.Add(s);
        }
        foreach (var f in CustomFolders.Where(f => f.IsSelected && f.Exists))
        {
            var s = SourceFactory.ForCustomFolder(f.ToEntry(), sources.Select(x => x.Id));
            if (s is not null) sources.Add(s);
        }
        foreach (var a in Adapters.Where(a => a.IsSelected && a.Present))
        {
            var s = SourceFactory.ForAdapter(a.Adapter);
            if (s is not null) sources.Add(s);
        }
        return sources;
    }

    private IReadOnlyList<string> Exclusions =>
        ExclusionsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private BackupPlan BuildPlan(IReadOnlyList<BackupSource> sources, IReadOnlyList<ExtraFile>? extras = null) => new()
    {
        Sources = sources,
        Exclusions = Exclusions,
        DestinationFolder = Destination,
        Name = BackupName.Trim(),
        Format = SelectedFormat.Value,
        Compression = SelectedCompression.Value,
        Password = IsSevenZip && Password.Length > 0 ? Password : null,
        SevenZipPath = AppServices.SevenZipPath,
        IncludeSensitive = IncludeSensitive && IsSevenZip && Password.Length > 0,
        AnonymizeManifest = Anonymize,
        Extras = extras ?? [],
        AppVersion = AppServices.Version,
    };

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        Errors.Clear();
        Warnings.Clear();
        ResultVisible = false;
        if (string.IsNullOrWhiteSpace(Destination))
        {
            Errors.Add(Loc.T("dest.missing"));
            return;
        }

        var sources = BuildSources();
        if (sources.Count == 0)
        {
            Errors.Add(Loc.T("backup.nothing-selected"));
            return;
        }
        if (IncludeSensitive && !(IsSevenZip && Password.Length > 0))
            Warnings.Add(Loc.T("backup.sensitive-needs-encryption"));
        foreach (var missing in CustomFolders.Where(f => f.IsSelected && !f.Exists))
            Warnings.Add(Loc.T("backup.custom-missing", missing.DisplayPath));

        IsBusy = true;
        ProgressIndeterminate = true;
        PhaseText = Loc.T("phase.inventory");
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                CurrentItem = p.Current ?? "";
                ProgressDetail = Loc.T("progress.found", p.FilesDone, Format.Bytes(p.BytesDone));
            });
            var plan = BuildPlan(sources);
            _inventory = await Inventory.ScanAsync(sources, plan.Exclusions, plan.IncludeSensitive, progress, _cts.Token);
            _analyzedSources = sources;

            var check = DestinationValidator.Validate(plan, _inventory.TotalBytes);
            foreach (var e in check.Errors) Errors.Add(Loc.T(e));
            foreach (var w in check.Warnings) Warnings.Add(Loc.T(w));
            foreach (var w in _inventory.Warnings) Warnings.Add(DescribeWarning(w));
            if (IsSevenZip && Password.Length > 0 && Password != PasswordConfirm)
                Errors.Add(Loc.T("backup.password-mismatch"));
            if (IncludeInstallers && AvailableInstallers == 0)
                Warnings.Add(Loc.T("backup.no-installers"));

            var skippedSensitive = _inventory.Skipped.Count(s => s.Reason == SkipReason.Sensitive);
            if (skippedSensitive > 0) Warnings.Add(Loc.T("backup.sensitive-skipped", skippedSensitive));
            var cloud = _inventory.Skipped.Count(s => s.Reason == SkipReason.CloudOnly);
            if (cloud > 0) Warnings.Add(Loc.T("backup.cloud-skipped", cloud));

            for (var i = 0; i < sources.Count; i++)
            {
                var text = $"{Loc.T("unit.files", _inventory.CountForSource(i))} · {Format.Bytes(_inventory.BytesForSource(i))}";
                var cat = Categories.FirstOrDefault(c => c.Id == sources[i].Id);
                if (cat is not null) cat.SizeText = text;
                var custom = CustomFolders.FirstOrDefault(c => string.Equals(c.ResolvedPath, sources[i].AbsolutePath, StringComparison.OrdinalIgnoreCase));
                if (custom is not null) custom.SizeText = text;
            }

            SummaryFiles = _inventory.Files.Count.ToString("N0");
            SummarySize = Format.Bytes(_inventory.TotalBytes);
            SummaryFree = check.FreeBytes >= 0 ? Format.Bytes(check.FreeBytes) : Loc.T("common.unknown");
            SummaryRequired = Format.Bytes(check.RequiredBytes);
            IsAnalyzed = Errors.Count == 0;
        }
        catch (OperationCanceledException)
        {
            PhaseText = Loc.T("phase.cancelled");
        }
        finally
        {
            IsBusy = false;
            ProgressIndeterminate = false;
            CurrentItem = "";
            CreateBackupCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanAnalyze() => !IsBusy && !IsRunning;

    partial void OnIsBusyChanged(bool value)
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        CreateBackupCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        CreateBackupCommand.NotifyCanExecuteChanged();
    }

    private bool CanCreate() => IsAnalyzed && !IsBusy && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateBackupAsync()
    {
        if (_inventory is null) return;
        IsRunning = true;
        ResultVisible = false;
        SkippedItems.Clear();
        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        string? wingetTemp = null;

        try
        {
            // La selección actual viaja en el backup aunque no se haya guardado como perfil.
            _saveTimer.Stop();
            await SaveSelectionAsync();

            // Configuración de Windows Reborn: siempre incluida.
            var extras = new List<ExtraFile>();
            foreach (var (relative, full) in AppServices.Config.EnumerateFiles())
                extras.Add(new ExtraFile($"{Sections.Config}/{relative}", Sections.Config, full, null));
            extras.Add(new ExtraFile($"{Sections.Config}/{ConfigStore.ManifestName}", Sections.Config, null,
                JsonSerializer.SerializeToUtf8Bytes(AppServices.Config.CreateManifest(AppServices.Version), Json.Options)));

            if (IncludeWinGetList)
            {
                PhaseText = Loc.T("phase.winget-export");
                ProgressIndeterminate = true;
                try
                {
                    wingetTemp = Path.Combine(Path.GetTempPath(), $"wr-winget-{Guid.NewGuid():N}.json");
                    await AppServices.WinGet.ExportAsync(wingetTemp, _cts.Token);
                    extras.Add(new ExtraFile($"{Sections.Apps}/winget-export.json", Sections.Apps, wingetTemp, null));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Warnings.Add(Loc.T("backup.winget-export-failed"));
                }
            }

            if (IncludeInstallers)
            {
                var installers = await InstallerStore.LoadFolderAsync(AppServices.InstallersFolder, _cts.Token);
                var verified = new List<DownloadedInstaller>();
                foreach (var inst in installers)
                {
                    if (await Hashing.HashFileAsync(inst.Path, _cts.Token) == inst.Sha256) verified.Add(inst);
                    else Warnings.Add(Loc.T("backup.installer-changed", inst.ProgramName));
                }
                var (files, index) = InstallerStore.PrepareForBackup(verified);
                extras.AddRange(files.Select(f => new ExtraFile(f.ArchivePath, Sections.Installers, f.SourcePath, null)));
                extras.Add(new ExtraFile($"{Sections.Installers}/{InstallerIndex.FileName}", Sections.Installers, null, index));
            }

            ProgressIndeterminate = false;
            var plan = BuildPlan(_analyzedSources, extras);
            var progress = new Progress<BackupProgress>(p => OnProgress(p, sw.Elapsed));
            var result = await new BackupEngine().RunAsync(plan, _inventory, progress, _cts.Token);
            ShowResult(result);

            if (result.Status is BackupStatus.Success or BackupStatus.SuccessWithSkips)
            {
                AppServices.Settings.LastBackupDestination = Destination;
                AppServices.Settings.LastBackupPath = result.FinalPath;
                AppServices.Settings.LastBackupVerified = result.Verification?.IsValid == true;
                AppServices.Settings.PreferredFormat = SelectedFormat.Value.Id();
                await AppServices.SaveSettingsAsync();
                await SaveReportAsync(result);
                BackupName = DefaultName();
            }
        }
        catch (OperationCanceledException)
        {
            ShowResult(new BackupResult { Status = BackupStatus.Cancelled });
        }
        catch (Exception ex)
        {
            ShowResult(new BackupResult { Status = BackupStatus.Failed, Error = ex.Message });
        }
        finally
        {
            if (wingetTemp is not null) try { File.Delete(wingetTemp); } catch { }
            IsRunning = false;
            IsAnalyzed = false;
            ProgressIndeterminate = false;
            CurrentItem = "";
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void OnProgress(BackupProgress p, TimeSpan elapsed)
    {
        PhaseText = Loc.T($"phase.{p.Phase.ToString().ToLowerInvariant()}");
        ProgressIndeterminate = p.Phase == BackupPhase.Finalizing && IsSevenZip;
        ProgressValue = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal : 0;
        CurrentItem = p.Current ?? "";
        var speed = elapsed.TotalSeconds > 1 ? p.BytesDone / elapsed.TotalSeconds : 0;
        ProgressDetail = p.BytesTotal > 0
            ? $"{Format.Bytes(p.BytesDone)} / {Format.Bytes(p.BytesTotal)} · {Loc.T("unit.files-of", p.FilesDone, p.FilesTotal)}" + (speed > 0 && p.Phase is BackupPhase.Writing or BackupPhase.Verifying ? $" · {Format.Bytes((long)speed)}/s" : "")
            : "";
    }

    private void ShowResult(BackupResult result)
    {
        ResultVisible = true;
        ResultPath = result.FinalPath;
        switch (result.Status)
        {
            case BackupStatus.Success:
                ResultSeverity = Wpf.Ui.Controls.InfoBarSeverity.Success;
                ResultTitle = Loc.T("backup.result.success");
                ResultMessage = Loc.T("backup.result.success-detail", result.FilesWritten, Format.Bytes(result.BytesWritten), Format.Bytes(result.ArchiveSize), Format.Duration(result.Duration));
                break;
            case BackupStatus.SuccessWithSkips:
                ResultSeverity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
                ResultTitle = Loc.T("backup.result.skips");
                ResultMessage = Loc.T("backup.result.skips-detail", result.FilesWritten, result.Report.Skipped.Count);
                break;
            case BackupStatus.Cancelled:
                ResultSeverity = Wpf.Ui.Controls.InfoBarSeverity.Informational;
                ResultTitle = Loc.T("backup.result.cancelled");
                ResultMessage = Loc.T("backup.result.cancelled-detail");
                break;
            default:
                ResultSeverity = Wpf.Ui.Controls.InfoBarSeverity.Error;
                ResultTitle = Loc.T("backup.result.failed");
                ResultMessage = DescribeError(result);
                break;
        }
        PhaseText = ResultTitle;
        foreach (var s in result.Report.Skipped.Take(500))
            SkippedItems.Add($"{Loc.T("skip." + s.Reason.ToString().ToLowerInvariant())} — {s.Path}");
    }

    private static string DescribeError(BackupResult result)
    {
        if (result.Error is null) return Loc.T("error.unknown");
        if (result.Error == "verification-failed" && result.Verification is { } v)
            return Loc.T("backup.result.verification-failed", v.HashMismatch.Count + v.Missing.Count + v.Unreadable.Count);
        var parts = result.Error.Split(';', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, parts.Select(p => Loc.Instance.Get(p) is var t && t != p ? t : p));
    }

    private static string DescribeWarning(string warning)
    {
        if (warning.StartsWith("overlap:", StringComparison.Ordinal))
        {
            var names = warning[8..].Split('|');
            return Loc.T("backup.overlap", names[0], names.ElementAtOrDefault(1) ?? "");
        }
        return Loc.Instance.Get(warning);
    }

    private static async Task SaveReportAsync(BackupResult result)
    {
        try
        {
            await Json.WriteFileAtomicAsync(Path.Combine(AppServices.Paths.ReportsDirectory, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.json"), new
            {
                Status = result.Status.ToString(),
                result.FilesWritten,
                result.BytesWritten,
                result.ArchiveSize,
                result.Duration,
                Backup = result.FinalPath is null ? null : Path.GetFileName(result.FinalPath),
                result.Report,
            });
        }
        catch { }
    }

    [RelayCommand]
    private void OpenResult()
    {
        if (ResultPath is not null) Dialogs.OpenFolder(ResultPath);
    }

    // ----- Perfiles -----

    private async Task LoadProfilesAsync()
    {
        var list = await new ProfileStore(AppServices.Paths.ConfigDirectory).LoadAllAsync();
        Application.Current.Dispatcher.Invoke(() =>
        {
            Profiles.Clear();
            foreach (var p in list) Profiles.Add(p);
        });
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        var box = new Wpf.Ui.Controls.TextBox { PlaceholderText = Loc.T("profile.name-placeholder"), Text = SelectedProfile?.Name ?? "", MinWidth = 320 };
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = Loc.T("profile.save-title"),
            Content = box,
            PrimaryButtonText = Loc.T("common.save"),
            CloseButtonText = Loc.T("common.cancel"),
            Owner = Application.Current.MainWindow,
        };
        if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary || string.IsNullOrWhiteSpace(box.Text))
            return;

        var profile = Snapshot(box.Text.Trim());
        await new ProfileStore(AppServices.Paths.ConfigDirectory).SaveAsync(profile);
        await LoadProfilesAsync();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == profile.Name);
    }

    private bool HasProfile() => SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(HasProfile))]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is null) return;
        if (!await Dialogs.ConfirmAsync(Loc.T("profile.delete-title"), Loc.T("profile.delete-message", SelectedProfile.Name), Loc.T("common.delete"), danger: true))
            return;
        new ProfileStore(AppServices.Paths.ConfigDirectory).Delete(SelectedProfile.Name);
        SelectedProfile = null;
        await LoadProfilesAsync();
    }

    /// <summary>Estado actual de la pantalla como perfil.</summary>
    private BackupProfile Snapshot(string name) => new()
    {
        Name = name,
        Categories = Categories.Where(c => c.IsSelected).Select(c => c.Id).ToList(),
        CustomFolders = CustomFolders.Select(c => c.ToEntry()).ToList(),
        Adapters = Adapters.Where(a => a.IsSelected).Select(a => a.Adapter.Id).ToList(),
        Exclusions = Exclusions.ToList(),
        Format = SelectedFormat.Value.Id(),
        Compression = SelectedCompression.Value.ToString().ToLowerInvariant(),
        DestinationFolder = string.IsNullOrWhiteSpace(Destination) ? null : Destination,
        IncludeProgramInstallers = IncludeInstallers,
        IncludeWinGetList = IncludeWinGetList,
    };

    private async Task SaveSelectionAsync()
    {
        try
        {
            await new ProfileStore(AppServices.Paths.ConfigDirectory).SaveSelectionAsync(Snapshot("current"));
        }
        catch (Exception ex)
        {
            Core.Logging.Log.Error("Backup selection could not be saved", ex);
        }
    }

    private async Task LoadSelectionAsync()
    {
        var selection = await new ProfileStore(AppServices.Paths.ConfigDirectory).LoadSelectionAsync();
        if (selection is null) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            _loadingSelection = true;
            try { ApplyProfile(selection); }
            finally { _loadingSelection = false; }
        });
    }

    private void ApplyProfile(BackupProfile p)
    {
        foreach (var c in Categories) c.IsSelected = c.Available && p.Categories.Contains(c.Id);
        foreach (var a in Adapters) a.IsSelected = a.Present && p.Adapters.Contains(a.Adapter.Id);
        CustomFolders.Clear();
        // Las carpetas que no existen en este equipo se conservan (marcadas) en vez de perderse.
        foreach (var f in p.CustomFolders)
            AddItem(f);
        ExclusionsText = string.Join(Environment.NewLine, p.Exclusions);
        SelectedFormat = Formats.FirstOrDefault(f => f.Value.Id() == p.Format && f.Enabled) ?? Formats[0];
        SelectedCompression = Compressions.FirstOrDefault(c => c.Value.ToString().Equals(p.Compression, StringComparison.OrdinalIgnoreCase)) ?? Compressions[0];
        if (!string.IsNullOrWhiteSpace(p.DestinationFolder)) Destination = p.DestinationFolder!;
        IncludeInstallers = p.IncludeProgramInstallers;
        IncludeWinGetList = p.IncludeWinGetList;
    }

    public async Task RefreshInstallersAsync()
    {
        try
        {
            var list = await InstallerStore.LoadFolderAsync(AppServices.InstallersFolder);
            AvailableInstallers = list.Count;
        }
        catch
        {
            AvailableInstallers = 0;
        }
    }

    private static string DefaultName() => $"WindowsReborn-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd-HHmm}";

    private static string SymbolFor(string id) => id switch
    {
        "desktop" => "Desktop24",
        "documents" => "Document24",
        "downloads" => "ArrowDownload24",
        "pictures" => "Image24",
        "videos" => "Video24",
        "music" => "MusicNote224",
        "savedgames" => "Games24",
        _ => "Folder24",
    };
}
