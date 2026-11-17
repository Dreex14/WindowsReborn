using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Backup;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Localization;
using WindowsReborn.Core.Paths;
using WindowsReborn.Restore;
using WindowsReborn.Storage;

namespace WindowsReborn.App.ViewModels;

/// <summary>Un backup (.zip) abierto.</summary>
public sealed class OpenedBackup(string path, IBackupReader reader, BackupManifest manifest)
{
    public string Path { get; } = path;
    public IBackupReader Reader { get; } = reader;
    public BackupManifest Manifest { get; } = manifest;
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>Categoría de un backup que se puede restaurar a su ruta original.</summary>
public partial class RestoreEntry(OpenedBackup backup, CategoryTarget target) : ObservableObject
{
    public OpenedBackup Backup { get; } = backup;
    public CategoryTarget Target { get; } = target;
    public string Name => Target.Category.Name;
    public string ZipName => Backup.FileName;
    public bool HasTarget => !string.IsNullOrWhiteSpace(Target.TargetFolder);
    public string TargetDisplay => HasTarget ? KnownRoots.Friendly(Target.TargetFolder!) : Loc.T("restore.no-target");
    public string SizeText => Loc.T("restore.files", Target.Category.FileCount, Format.Bytes(Target.Category.Bytes));
    public string Icon => Target.Category.Kind == "appConfig" ? "Apps24" : "Folder24";

    [ObservableProperty] private bool _isSelected = true;

    [RelayCommand]
    private void ChangeTarget()
    {
        var folder = Dialogs.PickFolder(Loc.T("restore.change-target"), Target.TargetFolder);
        if (folder is null) return;
        Target.TargetFolder = folder;
        OnPropertyChanged(nameof(TargetDisplay));
        OnPropertyChanged(nameof(HasTarget));
    }
}

public partial class RestoreViewModel : ObservableObject
{
    private readonly List<OpenedBackup> _opened = [];
    private CancellationTokenSource? _cts;

    public ObservableCollection<RestoreEntry> Entries { get; } = [];

    [ObservableProperty] private bool _hasEntries;
    [ObservableProperty] private string? _foundConfigPath;
    [ObservableProperty] private int _policyIndex;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string? _currentFile;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _resultOk;
    [ObservableProperty] private string? _resultTitle;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _error;

    public bool HasFoundConfig => FoundConfigPath is not null;
    partial void OnFoundConfigPathChanged(string? value) => OnPropertyChanged(nameof(HasFoundConfig));

    [RelayCommand]
    private async Task PickZipsAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("restore.pick"),
            Filter = Loc.T("restore.filter") + "|*.zip",
            Multiselect = true,
            InitialDirectory = AppServices.DesktopFolder,
        };
        if (dlg.ShowDialog(System.Windows.Application.Current.MainWindow) == true)
            await ImportPathsAsync(dlg.FileNames);
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var folder = Dialogs.PickFolder(Loc.T("restore.pick-folder"), AppServices.DesktopFolder);
        if (folder is not null) await ImportPathsAsync([folder]);
    }

    /// <summary>Abre zips o una carpeta de backup (busca los .zip dentro).</summary>
    public async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        Error = null;
        HasResult = false;
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) files.AddRange(Directory.EnumerateFiles(p, "*.zip"));
            else if (File.Exists(p)) files.Add(p);
        }

        var errors = new List<string>();
        foreach (var file in files)
        {
            if (_opened.Any(o => o.Path.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
            if (MyConfig.IsConfigZip(file))
            {
                FoundConfigPath = file;
                continue;
            }
            try
            {
                var (reader, manifest) = await Task.Run(() =>
                {
                    var r = BackupReader.Open(file);
                    try { return (r, BackupReader.ReadManifest(r)); }
                    catch { r.Dispose(); throw; }
                });
                var backup = new OpenedBackup(file, reader, manifest);
                _opened.Add(backup);
                foreach (var target in RestoreEngine.SuggestTargets(manifest))
                    Entries.Add(new RestoreEntry(backup, target) { IsSelected = target.TargetFolder is not null });
            }
            catch (Exception ex)
            {
                Core.Logging.Log.Warn($"Not a backup: {ex.Message}");
                errors.Add(Path.GetFileName(file));
            }
        }
        HasEntries = Entries.Count > 0;
        if (errors.Count > 0) Error = Loc.T("restore.not-backup", string.Join(", ", errors));
        if (files.Count == 0) Error = Loc.T("restore.no-zips");
    }

    [RelayCommand]
    private void Clear()
    {
        foreach (var o in _opened) o.Reader.Dispose();
        _opened.Clear();
        Entries.Clear();
        HasEntries = false;
        FoundConfigPath = null;
        HasResult = false;
        Error = null;
    }

    [RelayCommand]
    private async Task LoadFoundConfigAsync()
    {
        if (FoundConfigPath is null) return;
        await ViewModelLocator.Config.ImportFromAsync(FoundConfigPath);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task StartAsync()
    {
        var selected = Entries.Where(e => e.IsSelected && e.HasTarget).ToList();
        if (selected.Count == 0)
        {
            await Dialogs.InfoAsync(Loc.T("restore.title"), Loc.T("restore.nothing"));
            return;
        }

        // Las apps deben estar cerradas para poder escribir su configuración.
        while (true)
        {
            var running = RestoreEngine.RunningBlockers(selected.Select(e => e.Target.Category));
            if (running.Count == 0) break;
            if (!await Dialogs.ConfirmAsync(Loc.T("restore.title"), Loc.T("restore.close-apps", string.Join(", ", running)), Loc.T("common.retry")))
                return;
        }

        var policy = PolicyIndex switch { 1 => ConflictPolicy.Rename, 2 => ConflictPolicy.Skip, _ => ConflictPolicy.Overwrite };
        var plans = selected.GroupBy(e => e.Backup).Select(g =>
        {
            foreach (var e in Entries.Where(x => x.Backup == g.Key)) e.Target.Selected = e.IsSelected && e.HasTarget;
            return (Backup: g.Key, Plan: RestoreEngine.BuildPlan(g.Key.Manifest, Entries.Where(x => x.Backup == g.Key).Select(x => x.Target)));
        }).ToList();

        var conflicts = plans.Sum(p => p.Plan.Conflicts);
        var message = Loc.T("restore.confirm.body", plans.Sum(p => p.Plan.Items.Count), Format.Bytes(plans.Sum(p => p.Plan.TotalBytes)));
        if (conflicts > 0)
            message += "\n\n" + Loc.T(policy switch
            {
                ConflictPolicy.Overwrite => "restore.confirm.overwrite",
                ConflictPolicy.Rename => "restore.confirm.rename",
                _ => "restore.confirm.skip",
            }, conflicts);
        if (!await Dialogs.ConfirmAsync(Loc.T("restore.title"), message, Loc.T("restore.start"), policy == ConflictPolicy.Overwrite && conflicts > 0))
            return;

        IsRunning = true;
        HasResult = false;
        Percent = 0;
        _cts = new CancellationTokenSource();
        int restored = 0, skipped = 0, failed = 0;
        try
        {
            long total = Math.Max(1, plans.Sum(p => p.Plan.TotalBytes));
            long before = 0;
            foreach (var (backup, plan) in plans)
            {
                var offset = before;
                var progress = new Progress<RestoreProgress>(p =>
                {
                    Percent = (offset + p.BytesDone) * 100.0 / total;
                    CurrentFile = p.Current;
                });
                var report = await RestoreEngine.ExecuteAsync(backup.Reader, plan, policy, backup.FileName, progress, _cts.Token);
                before += plan.TotalBytes;
                restored += report.Count(RestoreOutcome.Restored) + report.Count(RestoreOutcome.Overwritten) + report.Count(RestoreOutcome.Renamed);
                skipped += report.Count(RestoreOutcome.SkippedExisting) + report.Count(RestoreOutcome.SkippedAppRunning);
                failed += report.Count(RestoreOutcome.Failed);
                try { await RestoreEngine.SaveReportAsync(report, AppServices.Paths.ReportsDirectory); } catch { }

                // Perfiles de los navegadores: se vuelven a dar de alta en «Local State».
                foreach (var def in BackupCatalog.All.Where(d => d.Chromium))
                {
                    var entryName = $"{BackupCatalog.ChromiumExtraPrefix}{def.Id}.json";
                    if (!backup.Reader.Contains(entryName) ||
                        !selected.Any(s => s.Backup == backup && s.Target.Category.Adapter == def.Id)) continue;
                    using var stream = backup.Reader.OpenEntry(entryName);
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    try { BackupCatalog.MergeChromiumProfileList(def, ms.ToArray()); }
                    catch (Exception ex) { Core.Logging.Log.Warn("Profile list merge failed: " + ex.Message); }
                }
            }
            ResultOk = failed == 0;
            ResultTitle = ResultOk ? Loc.T("restore.done.title") : Loc.T("restore.done.errors");
            ResultText = Loc.T("restore.done.text", restored, skipped, failed);
        }
        catch (OperationCanceledException)
        {
            ResultOk = false;
            ResultTitle = Loc.T("restore.cancelled");
            ResultText = Loc.T("restore.done.text", restored, skipped, failed);
        }
        catch (Exception ex)
        {
            Core.Logging.Log.Error("Restore failed", ex);
            ResultOk = false;
            ResultTitle = Loc.T("restore.done.errors");
            ResultText = ex.Message;
        }
        finally
        {
            IsRunning = false;
            HasResult = true;
            CurrentFile = null;
            _cts.Dispose();
            _cts = null;
        }
    }
}
