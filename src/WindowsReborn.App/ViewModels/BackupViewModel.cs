using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Backup;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Localization;
using WindowsReborn.Core.Paths;
using WindowsReborn.Storage;

namespace WindowsReborn.App.ViewModels;

/// <summary>Elemento marcable en «Copia de seguridad» (del catálogo o carpeta añadida).</summary>
public partial class BackupItem : ObservableObject
{
    private readonly BackupViewModel _owner;

    public BackupItem(BackupItemDef def, BackupViewModel owner)
    {
        _owner = owner;
        Def = def;
        Name = Loc.T(def.NameKey);
        Icon = def.Icon;
        var folder = BackupCatalog.Resolve(def);
        Exists = folder is not null;
        Location = folder is null ? Loc.T("backup.not-found") : KnownRoots.Friendly(folder);
        if ((def.Chromium || def.Gecko) && folder is not null)
        {
            var profiles = def.Chromium ? BackupCatalog.ChromiumProfiles(folder) : BackupCatalog.GeckoProfiles(folder);
            Location = Loc.T("backup.profiles", profiles.Count, string.Join(", ", profiles));
        }
    }

    public BackupItem(string customPath, BackupViewModel owner)
    {
        _owner = owner;
        CustomPath = customPath;
        var full = Environment.ExpandEnvironmentVariables(customPath);
        Name = Path.GetFileName(full.TrimEnd('\\')) is { Length: > 0 } n ? n : full;
        Icon = "FolderAdd24";
        Exists = Directory.Exists(full);
        Location = Exists ? KnownRoots.Friendly(full) : Loc.T("backup.not-found");
    }

    public BackupItemDef? Def { get; }
    public string? CustomPath { get; }
    public bool IsCustom => CustomPath is not null;
    public string Name { get; }
    public string Icon { get; }
    public string Location { get; private set; }
    public bool Exists { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _sizeText = "";

    partial void OnIsSelectedChanged(bool value) => _owner.OnSelectionChanged();

    [RelayCommand]
    private void Remove() => _owner.RemoveCustom(this);

    /// <summary>Calcula el tamaño aproximado en segundo plano.</summary>
    public async Task MeasureAsync()
    {
        if (!Exists) return;
        SizeText = "…";
        long bytes = 0;
        await Task.Run(() =>
        {
            foreach (var source in Sources())
            {
                try
                {
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    if (source.Include.Count == 0)
                    {
                        bytes += new DirectoryInfo(source.AbsolutePath).EnumerateFiles("*", opts).Sum(f => f.Length);
                        continue;
                    }
                    foreach (var inc in source.Include)
                    {
                        var path = Path.Combine(source.AbsolutePath, inc.TrimEnd('/'));
                        if (File.Exists(path)) bytes += new FileInfo(path).Length;
                        else if (Directory.Exists(path)) bytes += new DirectoryInfo(path).EnumerateFiles("*", opts).Sum(f => f.Length);
                    }
                }
                catch { }
            }
        });
        Bytes = bytes;
        SizeText = Format.Bytes(bytes);
        _owner.OnSelectionChanged();
    }

    public long Bytes { get; private set; }

    public BackupGroup Group => Def?.Group ?? BackupGroup.Files;

    public IReadOnlyList<BackupSource> Sources()
    {
        if (Def is not null)
            return BackupCatalog.SourcesFor(Def, Name);
        var full = Environment.ExpandEnvironmentVariables(CustomPath!);
        if (!Directory.Exists(full)) return [];
        return [SourceFactory.ForCustomFolder(full, [], isProjects: false)];
    }
}

public partial class BackupViewModel : ObservableObject
{
    private bool _loading;
    private CancellationTokenSource? _cts;

    public ObservableCollection<BackupItem> AppItems { get; } = [];
    public ObservableCollection<BackupItem> FileItems { get; } = [];

    [ObservableProperty] private string _destination = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string? _phaseText;
    [ObservableProperty] private string? _currentFile;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _resultOk;
    [ObservableProperty] private string? _resultTitle;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _resultFolder;
    [ObservableProperty] private bool _includeConfig = true;

    public BackupViewModel()
    {
        Destination = AppServices.Settings.LastBackupDestination is { } d && Directory.Exists(d) ? d : AppServices.DesktopFolder;
        Reload();
        AppServices.ConfigurationReplaced += (_, _) => Reload();
    }

    private void Reload()
    {
        _loading = true;
        AppItems.Clear();
        FileItems.Clear();
        var chosen = AppServices.Settings.BackupItems?.ToHashSet();
        foreach (var def in BackupCatalog.All)
        {
            var item = new BackupItem(def, this) { };
            item.IsSelected = item.Exists && (chosen?.Contains(def.Id) ?? def.Preselected);
            (def.Group == BackupGroup.Apps ? AppItems : FileItems).Add(item);
        }
        foreach (var path in AppServices.Settings.CustomFolders)
            FileItems.Add(new BackupItem(path, this) { IsSelected = true });
        _loading = false;
        OnSelectionChanged();
        foreach (var item in AppItems.Concat(FileItems))
            _ = item.MeasureAsync();
    }

    private IEnumerable<BackupItem> All => AppItems.Concat(FileItems);

    public void OnSelectionChanged()
    {
        var selected = All.Where(i => i.IsSelected).ToList();
        Summary = Loc.T("backup.summary", selected.Count, Format.Bytes(selected.Sum(i => i.Bytes)));
        if (_loading) return;
        AppServices.Settings.BackupItems = All.Where(i => i.IsSelected && i.Def is not null).Select(i => i.Def!.Id).ToList();
        AppServices.SaveSettingsSoon();
    }

    public void RemoveCustom(BackupItem item)
    {
        FileItems.Remove(item);
        AppServices.Settings.CustomFolders.RemoveAll(p => p == item.CustomPath);
        OnSelectionChanged();
        AppServices.SaveSettingsSoon();
    }

    [RelayCommand]
    private void AddFolder()
    {
        var folder = Dialogs.PickFolder(Loc.T("backup.add-folder"));
        if (folder is null) return;
        var portable = MyConfig.ToPortable(folder);
        if (AppServices.Settings.CustomFolders.Contains(portable, StringComparer.OrdinalIgnoreCase)) return;
        AppServices.Settings.CustomFolders.Add(portable);
        var item = new BackupItem(portable, this) { IsSelected = true };
        FileItems.Add(item);
        _ = item.MeasureAsync();
        AppServices.SaveSettingsSoon();
    }

    [RelayCommand]
    private void ChangeDestination()
    {
        var folder = Dialogs.PickFolder(Loc.T("backup.destination"), Destination);
        if (folder is null) return;
        Destination = folder;
        AppServices.Settings.LastBackupDestination = folder;
        AppServices.SaveSettingsSoon();
    }

    [RelayCommand]
    private void OpenResult()
    {
        if (ResultFolder is not null) Dialogs.OpenFolder(ResultFolder);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        var selected = All.Where(i => i.IsSelected && i.Exists).ToList();
        if (selected.Count == 0)
        {
            await Dialogs.InfoAsync(Loc.T("backup.title"), Loc.T("backup.nothing"));
            return;
        }

        // Navegadores abiertos: sus archivos están en uso y la copia saldría incompleta.
        var processes = selected.SelectMany(i => i.Def?.Processes ?? []).ToList();
        var running = Restore.RestoreEngine.RunningBlockers([new CategoryInfo { ProcessNames = processes }]);
        if (running.Count > 0 &&
            !await Dialogs.ConfirmAsync(Loc.T("backup.title"), Loc.T("backup.running", string.Join(", ", running)), Loc.T("backup.continue-anyway")))
            return;

        HasResult = false;
        IsRunning = true;
        Percent = 0;
        _cts = new CancellationTokenSource();
        var folder = Path.Combine(Destination, Loc.T("backup.folder", DateTime.Now.ToString("yyyy-MM-dd HHmm")));
        var errors = new List<string>();
        var warnings = 0;
        long totalSize = 0;
        try
        {
            Directory.CreateDirectory(folder);
            var groups = new[]
            {
                (Group: BackupGroup.Apps, Name: Loc.T("backup.zip.apps")),
                (Group: BackupGroup.Files, Name: Loc.T("backup.zip.files")),
            }.Where(g => selected.Any(i => i.Group == g.Group)).ToList();

            for (var gi = 0; gi < groups.Count; gi++)
            {
                var (group, name) = groups[gi];
                var items = selected.Where(i => i.Group == group).ToList();
                var sources = new List<BackupSource>();
                foreach (var source in items.SelectMany(i => i.Sources()))
                {
                    // Ids únicos dentro del zip.
                    var id = SourceFactory.UniqueId(source.Id, sources.Select(s => s.Id));
                    sources.Add(id == source.Id ? source : new BackupSource
                    {
                        Id = id, Name = source.Name, Kind = source.Kind, Root = source.Root, SubPath = source.SubPath,
                        AbsolutePath = source.AbsolutePath, Include = source.Include, ExtraExclusions = source.ExtraExclusions,
                        ProcessNames = source.ProcessNames, AdapterId = source.AdapterId,
                    });
                }
                var extras = items.Where(i => i.Def?.Chromium == true)
                    .Select(i => BackupCatalog.ChromiumProfileList(i.Def!)).Where(e => e is not null).Select(e => e!).ToList();
                if (sources.Count == 0) continue;

                var plan = new BackupPlan
                {
                    Sources = sources,
                    DestinationFolder = folder,
                    Name = name,
                    Format = BackupFormat.Zip,
                    Compression = group == BackupGroup.Files ? CompressionPreset.Fast : CompressionPreset.Balanced,
                    Extras = extras,
                    AppVersion = AppServices.Version,
                };

                var step = gi;
                var progress = new Progress<BackupProgress>(p =>
                {
                    PhaseText = Loc.T("backup.phase", step + 1, groups.Count, name, Loc.T("backup.phase." + p.Phase.ToString().ToLowerInvariant()));
                    var fraction = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0;
                    if (p.Phase == BackupPhase.Writing) fraction *= 0.8;
                    else if (p.Phase == BackupPhase.Verifying) fraction = 0.8 + fraction * 0.2;
                    else if (p.Phase >= BackupPhase.Committing) fraction = 1;
                    else fraction = 0;
                    Percent = (step + fraction) * 100 / groups.Count;
                    CurrentFile = p.Current;
                });

                var result = await new BackupEngine().RunAsync(plan, null, progress, _cts.Token);
                if (result.Status == BackupStatus.Cancelled) throw new OperationCanceledException();
                if (result.Status == BackupStatus.Failed)
                {
                    errors.Add($"{name}: " + string.Join(" · ", (result.Error ?? "").Split(';').Select(LocalizeError)));
                    continue;
                }
                if (result.Status == BackupStatus.SuccessWithSkips) warnings += result.Report.Skipped.Count(s => s.Reason != SkipReason.Excluded);
                totalSize += result.ArchiveSize;
            }

            if (IncludeConfig)
                await ViewModelLocator.Config.BuildConfig().SaveZipAsync(Path.Combine(folder, Loc.T("config.zip-name") + ".zip"));

            ResultOk = errors.Count == 0;
            ResultTitle = ResultOk ? Loc.T("backup.done.title") : Loc.T("backup.failed.title");
            ResultText = ResultOk
                ? Loc.T("backup.done.text", Format.Bytes(totalSize)) + (warnings > 0 ? "\n" + Loc.T("backup.done.skipped", warnings) : "")
                : string.Join("\n", errors);
            ResultFolder = folder;
            AppServices.Settings.LastBackupPath = folder;
            AppServices.Settings.LastBackupVerified = ResultOk;
            await AppServices.SaveSettingsAsync();
        }
        catch (OperationCanceledException)
        {
            ResultOk = false;
            ResultTitle = Loc.T("backup.cancelled");
            ResultText = null;
            ResultFolder = null;
            TryDeleteEmpty(folder);
        }
        catch (Exception ex)
        {
            Core.Logging.Log.Error("Backup failed", ex);
            ResultOk = false;
            ResultTitle = Loc.T("backup.failed.title");
            ResultText = ex.Message;
            ResultFolder = null;
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

    private static string LocalizeError(string key) => key.Contains('.') && !key.Contains(' ') ? Loc.T(key) : key;

    private static void TryDeleteEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch { }
    }
}
