using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.App.Views;
using WindowsReborn.Core.Localization;
using WindowsReborn.Core.Logging;
using WindowsReborn.Programs;
using Wpf.Ui.Controls;

namespace WindowsReborn.App.ViewModels;

public partial class ProgramItem : ObservableObject
{
    public ProgramItem(ProgramEntry entry) => Entry = entry;

    public ProgramEntry Entry { get; private set; }

    public string Name => Entry.Name;
    public string Initial => Entry.Name.Length > 0 ? Entry.Name[..1].ToUpperInvariant() : "?";

    public string SourceText => Entry.Source.Kind switch
    {
        SourceKind.WinGet => $"WinGet · {Entry.WinGetId}",
        SourceKind.GitHub => $"GitHub · {Entry.Source.GitHubRepo}",
        SourceKind.Vendor => Loc.T("programs.source-vendor", Entry.Source.VendorAdapter ?? ""),
        SourceKind.DirectUrl => Uri.TryCreate(Entry.Source.DirectUrl, UriKind.Absolute, out var u) ? u.Host : Loc.T("programs.source-url"),
        _ => Entry.Homepage is { } h && Uri.TryCreate(h, UriKind.Absolute, out var hu) ? Loc.T("programs.source-page", hu.Host) : Loc.T("programs.source-none"),
    };

    public string TrustText => Loc.T("trust." + Entry.Source.Trust.ToString().ToLowerInvariant());
    public string TrustSymbol => Entry.Source.Trust switch
    {
        SourceTrust.Verified => "ShieldCheckmark24",
        SourceTrust.UserSelected => "Person24",
        _ => "Warning24",
    };

    public bool HasHomepage => !string.IsNullOrWhiteSpace(Entry.Homepage);

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private InstallState _installState = InstallState.Unknown;
    [ObservableProperty] private string? _installedVersion;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private DownloadedInstaller? _downloaded;
    [ObservableProperty] private InstallerIndexEntry? _backupInstaller;

    public string StateText => InstallState switch
    {
        InstallState.Installed => Loc.T("state.installed") + (InstalledVersion is null ? "" : $" · {InstalledVersion}"),
        InstallState.ProbablyInstalled => Loc.T("state.probably") + (InstalledVersion is null ? "" : $" · {InstalledVersion}"),
        InstallState.NotInstalled => Loc.T("state.pending"),
        _ => Loc.T("state.unknown"),
    };

    public string VersionText => Entry.LatestVersion is { } v ? Loc.T("programs.latest", v) : Loc.T("programs.latest-unknown");

    public bool CanDownload => !IsBusy && Entry.Source.Kind != SourceKind.None;

    /// <summary>Solo se puede instalar si hay un método fiable: WinGet o un instalador verificado.</summary>
    public bool CanInstall => !IsBusy && Entry.InstallMode != InstallMode.DownloadOnly &&
                              ((Entry.InstallMode == InstallMode.Automated && !string.IsNullOrEmpty(Entry.WinGetId)) || Downloaded is not null || BackupInstaller is not null);

    public void Replace(ProgramEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(string.Empty);
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanDownload)); OnPropertyChanged(nameof(CanInstall)); }
    partial void OnInstallStateChanged(InstallState value) => OnPropertyChanged(nameof(StateText));
    partial void OnInstalledVersionChanged(string? value) => OnPropertyChanged(nameof(StateText));
    partial void OnDownloadedChanged(DownloadedInstaller? value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnBackupInstallerChanged(InstallerIndexEntry? value) => OnPropertyChanged(nameof(CanInstall));
}

public partial class ProgramsViewModel : ObservableObject
{
    private ProgramLibrary _library = new();
    private CancellationTokenSource? _cts;

    public ProgramsViewModel()
    {
        AppServices.ConfigurationReplaced += async (_, _) => await LoadAsync();
        AppServices.OpenedBackupChanged += (_, _) => AttachBackupInstallers();
        _ = LoadAsync();
    }

    public ObservableCollection<ProgramItem> Items { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _wingetAvailable = true;
    [ObservableProperty] private bool _hasBackupInstallers;

    public bool IsEmpty => Items.Count == 0;

    public async Task LoadAsync()
    {
        _library = await AppServices.ProgramStore.LoadAsync();
        var downloaded = await InstallerStore.LoadFolderAsync(AppServices.InstallersFolder);
        Items.Clear();
        foreach (var p in _library.Programs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Items.Add(new ProgramItem(p)
            {
                Downloaded = downloaded.Where(d => d.ProgramId == p.Id).OrderByDescending(d => d.DownloadedUtc).FirstOrDefault(),
            });
        }
        OnPropertyChanged(nameof(IsEmpty));
        AttachBackupInstallers();
        WingetAvailable = await AppServices.WinGet.IsAvailableAsync(CancellationToken.None);
        await RefreshInstalledAsync();
    }

    private void AttachBackupInstallers()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            InstallerIndex? index = null;
            try
            {
                if (AppServices.OpenedBackup is { } reader && AppServices.OpenedManifest?.HasInstallers == true)
                    index = InstallerStore.ReadIndexFromBackup(reader);
            }
            catch (Exception ex)
            {
                Log.Error("Installer index in backup could not be read", ex);
            }
            foreach (var item in Items)
                item.BackupInstaller = index?.Installers.FirstOrDefault(i => i.ProgramId == item.Entry.Id);
            HasBackupInstallers = index is { Installers.Count: > 0 };
        });
    }

    private Task SaveAsync() => AppServices.ProgramStore.SaveAsync(_library);

    [RelayCommand]
    private async Task RefreshInstalledAsync()
    {
        AppServices.Detector.Invalidate();
        foreach (var item in Items.ToList())
        {
            try
            {
                var (state, version) = await AppServices.Detector.DetectAsync(item.Entry, WingetAvailable, CancellationToken.None);
                item.InstallState = state;
                item.InstalledVersion = version;
            }
            catch (Exception ex)
            {
                Log.Error("Installed detection failed", ex);
            }
        }
    }

    // ----- Edición -----

    [RelayCommand]
    private async Task AddAsync()
    {
        var entry = ProgramEditorWindow.Edit(null);
        if (entry is null) return;
        _library.Programs.Add(entry);
        await SaveAsync();
        var item = new ProgramItem(entry);
        Items.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
        var (state, version) = await AppServices.Detector.DetectAsync(entry, WingetAvailable, CancellationToken.None);
        item.InstallState = state;
        item.InstalledVersion = version;
    }

    [RelayCommand]
    private async Task EditAsync(ProgramItem? item)
    {
        if (item is null) return;
        var edited = ProgramEditorWindow.Edit(item.Entry);
        if (edited is null) return;
        var index = _library.Programs.FindIndex(p => p.Id == edited.Id);
        if (index >= 0) _library.Programs[index] = edited;
        item.Replace(edited);
        await SaveAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(ProgramItem? item)
    {
        if (item is null) return;
        if (!await Dialogs.ConfirmAsync(Loc.T("programs.delete-title"), Loc.T("programs.delete-message", item.Name), Loc.T("common.delete"), danger: true))
            return;
        _library.Programs.RemoveAll(p => p.Id == item.Entry.Id);
        Items.Remove(item);
        OnPropertyChanged(nameof(IsEmpty));
        await SaveAsync();
    }

    [RelayCommand]
    private void OpenHomepage(ProgramItem? item)
    {
        if (item?.Entry.Homepage is { } url) Dialogs.OpenUri(url);
    }

    // ----- Importar / exportar -----

    [RelayCommand]
    private async Task ImportListAsync()
    {
        var file = Dialogs.OpenFile(Loc.T("programs.import-title"), Loc.T("programs.list-filter") + "|*.json");
        if (file is null) return;
        try
        {
            var incoming = await ProgramLibraryStore.ReadListAsync(file);
            var outcome = ProgramLibraryStore.Merge(_library, incoming);
            await SaveAsync();
            await LoadAsync();
            var msg = Loc.T("programs.import-done", outcome.Added);
            if (outcome.ConflictingSources.Count > 0)
                msg += Environment.NewLine + Loc.T("programs.import-conflicts", string.Join(", ", outcome.ConflictingSources));
            await Dialogs.InfoAsync(Loc.T("programs.import-title"), msg);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or NotSupportedException)
        {
            await Dialogs.InfoAsync(Loc.T("programs.import-title"), Loc.T("programs.import-failed", ex.Message));
        }
    }

    [RelayCommand]
    private async Task ExportListAsync()
    {
        var file = Dialogs.SaveFile(Loc.T("programs.export-title"), Loc.T("programs.list-filter") + "|*.json", "mis-programas.wrprograms.json");
        if (file is null) return;
        await ProgramLibraryStore.ExportAsync(_library, file);
        StatusText = Loc.T("programs.export-done", Path.GetFileName(file));
    }

    // ----- Versiones y descargas -----

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ok = 0;
        var failed = 0;
        try
        {
            foreach (var item in Items.Where(i => i.Entry.Source.Kind != SourceKind.None).ToList())
            {
                if (_cts.IsCancellationRequested) break;
                item.IsBusy = true;
                item.StatusText = Loc.T("programs.checking");
                try
                {
                    var release = await AppServices.Sources.GetLatestAsync(item.Entry, _cts.Token);
                    item.Entry.LatestVersion = release.Version;
                    item.Entry.LatestCheckedUtc = DateTime.UtcNow;
                    item.StatusText = release.Version is null ? Loc.T("programs.version-unknown-open") : null;
                    ok++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    item.StatusText = DescribeError(ex);
                    failed++;
                }
                finally
                {
                    item.IsBusy = false;
                    item.Refresh();
                }
            }
            await SaveAsync();
            StatusText = Loc.T("programs.check-done", ok, failed);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.T("phase.cancelled");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var selected = Items.Where(i => i.IsSelected && i.CanDownload).ToList();
        if (selected.Count == 0)
        {
            StatusText = Loc.T("programs.select-first");
            return;
        }
        await DownloadManyAsync(selected);
    }

    [RelayCommand]
    private Task DownloadAsync(ProgramItem? item) => item is null ? Task.CompletedTask : DownloadManyAsync([item]);

    private async Task<List<DownloadedInstaller>> DownloadManyAsync(IReadOnlyList<ProgramItem> items, string? folder = null)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var results = new List<DownloadedInstaller>();
        var failed = 0;
        folder ??= AppServices.InstallersFolder;
        try
        {
            foreach (var item in items)
            {
                if (_cts.IsCancellationRequested) break;
                var r = await DownloadOneAsync(item, folder, _cts.Token);
                if (r is not null) results.Add(r); else failed++;
            }
            if (results.Count > 0)
                await InstallerStore.CopyToFolderAsync(results, folder, CancellationToken.None);
            await SaveAsync();
            StatusText = Loc.T("programs.download-done", results.Count, failed);
            await ViewModelLocator.Backup.RefreshInstallersAsync();
        }
        finally
        {
            IsBusy = false;
        }
        return results;
    }

    private async Task<DownloadedInstaller?> DownloadOneAsync(ProgramItem item, string folder, CancellationToken ct)
    {
        item.IsBusy = true;
        item.Progress = 0;
        item.ProgressIndeterminate = true;
        item.StatusText = Loc.T("programs.resolving");
        try
        {
            var release = await AppServices.Sources.GetLatestAsync(item.Entry, ct);
            if (release.Version is null && item.Entry.Source.Kind != SourceKind.DirectUrl)
            {
                item.StatusText = Loc.T("programs.version-unknown-open");
                return null;
            }
            item.StatusText = release.ViaWinGet ? Loc.T("programs.downloading-winget") : Loc.T("programs.downloading");
            var progress = new Progress<DownloadProgress>(p =>
            {
                item.ProgressIndeterminate = p.BytesTotal is null;
                if (p.BytesTotal is > 0) item.Progress = 100.0 * p.BytesDone / p.BytesTotal.Value;
            });
            var result = await AppServices.Downloader.DownloadAsync(item.Entry, release, folder, progress, ct);
            item.Downloaded = result;
            item.Entry.LatestVersion = result.Version ?? release.Version;
            item.Entry.LastDownloadVersion = result.Version;
            item.Entry.LastDownloadPath = result.Path;
            item.StatusText = DescribeDownload(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            item.StatusText = Loc.T("phase.cancelled");
            return null;
        }
        catch (Exception ex)
        {
            item.StatusText = DescribeError(ex);
            Log.Error($"Download failed for a program ({item.Entry.Source.Kind})", ex);
            return null;
        }
        finally
        {
            item.IsBusy = false;
            item.ProgressIndeterminate = false;
            item.Refresh();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    // ----- Instalación (siempre con confirmación) -----

    [RelayCommand]
    private async Task InstallAsync(ProgramItem? item)
    {
        if (item is null || !item.CanInstall) return;
        try
        {
            if (item.Entry.InstallMode == InstallMode.Automated && !string.IsNullOrEmpty(item.Entry.WinGetId) && WingetAvailable)
            {
                if (!await Dialogs.ConfirmAsync(Loc.T("install.title", item.Name), Loc.T("install.winget-message", item.Entry.WinGetId!), Loc.T("install.confirm")))
                    return;
                item.IsBusy = true;
                item.ProgressIndeterminate = true;
                item.StatusText = Loc.T("install.running");
                var code = await AppServices.WinGet.InstallAsync(item.Entry, silent: true, CancellationToken.None);
                item.StatusText = code == 0 ? Loc.T("install.done") : Loc.T("install.failed-code", $"0x{code:X8}");
            }
            else
            {
                string path;
                string details;
                if (item.Downloaded is { } d && File.Exists(d.Path))
                {
                    // Se vuelve a comprobar el hash: el archivo pudo cambiar desde la descarga.
                    if (await Storage.Hashing.HashFileAsync(d.Path) != d.Sha256)
                    {
                        item.StatusText = Loc.T("download.hash-mismatch");
                        return;
                    }
                    path = d.Path;
                    details = DescribeInstaller(d.Version, d.HashVerified, d.Signature, d.Signer, d.Origin);
                }
                else if (item.BackupInstaller is { } b && AppServices.OpenedBackup is { } reader)
                {
                    item.IsBusy = true;
                    item.StatusText = Loc.T("install.extracting");
                    var temp = Path.Combine(Path.GetTempPath(), "WindowsReborn", "installers");
                    var (extracted, sig) = await InstallerStore.ExtractFromBackupAsync(reader, b, temp, CancellationToken.None);
                    path = extracted;
                    details = DescribeInstaller(b.Version, b.HashVerified, sig.Status, sig.Signer, b.Origin) + Environment.NewLine + Loc.T("install.archived-warning");
                    item.IsBusy = false;
                }
                else
                {
                    return;
                }

                if (!await Dialogs.ConfirmAsync(Loc.T("install.title", item.Name), Loc.T("install.file-message", Path.GetFileName(path), details), Loc.T("install.confirm")))
                {
                    item.StatusText = null;
                    return;
                }
                item.IsBusy = true;
                item.ProgressIndeterminate = true;
                item.StatusText = Loc.T("install.running");
                var code = await InstallerStore.RunInstallerAsync(path, CancellationToken.None);
                item.StatusText = InstallerStore.IsSuccessExitCode(code) ? Loc.T("install.done") : Loc.T("install.failed-code", code.ToString());
            }

            AppServices.Detector.Invalidate();
            var (state, version) = await AppServices.Detector.DetectAsync(item.Entry, WingetAvailable, CancellationToken.None);
            item.InstallState = state;
            item.InstalledVersion = version;
            if (state != InstallState.NotInstalled && item.Entry.ConfigAdapters.Count > 0)
                item.StatusText += " " + Loc.T("install.restore-config-hint");
        }
        catch (Exception ex)
        {
            item.StatusText = DescribeError(ex);
        }
        finally
        {
            item.IsBusy = false;
            item.ProgressIndeterminate = false;
        }
    }

    // ----- Preparar para formatear -----

    [RelayCommand]
    private async Task PrepareForFormatAsync()
    {
        var choice = new Wpf.Ui.Controls.MessageBox
        {
            Title = Loc.T("prepare.title"),
            Content = new System.Windows.Controls.TextBlock { Text = Loc.T("prepare.message"), TextWrapping = TextWrapping.Wrap, MaxWidth = 540 },
            PrimaryButtonText = Loc.T("prepare.list-only"),
            SecondaryButtonText = Loc.T("prepare.with-installers"),
            CloseButtonText = Loc.T("common.cancel"),
            Owner = Application.Current.MainWindow,
        };
        var result = await choice.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            await ExportListAsync();
            return;
        }
        if (result != Wpf.Ui.Controls.MessageBoxResult.Secondary) return;

        var targets = Items.Where(i => i.IsSelected && i.CanDownload).ToList();
        if (targets.Count == 0) targets = Items.Where(i => i.CanDownload).ToList();
        if (targets.Count == 0)
        {
            StatusText = Loc.T("programs.nothing-to-download");
            return;
        }

        var where = new Wpf.Ui.Controls.MessageBox
        {
            Title = Loc.T("prepare.where-title"),
            Content = new System.Windows.Controls.TextBlock { Text = Loc.T("prepare.where-message", targets.Count), TextWrapping = TextWrapping.Wrap, MaxWidth = 540 },
            PrimaryButtonText = Loc.T("prepare.into-backup"),
            SecondaryButtonText = Loc.T("prepare.into-folder"),
            CloseButtonText = Loc.T("common.cancel"),
            Owner = Application.Current.MainWindow,
        };
        var whereResult = await where.ShowDialogAsync();
        string? folder = null;
        if (whereResult == Wpf.Ui.Controls.MessageBoxResult.Secondary)
        {
            folder = Dialogs.PickFolder(Loc.T("prepare.pick-folder"));
            if (folder is null) return;
        }
        else if (whereResult != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        var downloaded = await DownloadManyAsync(targets, folder);
        if (folder is null)
        {
            ViewModelLocator.Backup.IncludeInstallers = downloaded.Count > 0;
            StatusText = Loc.T("prepare.ready-backup", downloaded.Count);
        }
        else
        {
            await ProgramLibraryStore.ExportAsync(_library, Path.Combine(folder, "mis-programas.wrprograms.json"));
            StatusText = Loc.T("prepare.ready-folder", downloaded.Count);
        }
    }

    // ----- Textos -----

    private static string DescribeDownload(DownloadedInstaller d)
    {
        var parts = new List<string> { Loc.T("programs.downloaded", d.Version ?? "?") };
        parts.Add(d.HashVerified ? Loc.T("verify.hash-ok") : Loc.T("verify.hash-none"));
        parts.Add(d.Signature switch
        {
            SignatureStatus.Valid => Loc.T("verify.signed", d.Signer ?? "?"),
            SignatureStatus.Unsigned => Loc.T("verify.unsigned"),
            _ => Loc.T("verify.not-applicable"),
        });
        return string.Join(" · ", parts);
    }

    private static string DescribeInstaller(string? version, bool hashVerified, SignatureStatus sig, string? signer, string origin) =>
        string.Join(Environment.NewLine,
            Loc.T("install.detail-version", version ?? "?"),
            Loc.T("install.detail-origin", origin),
            hashVerified ? Loc.T("verify.hash-ok") : Loc.T("verify.hash-none"),
            sig switch
            {
                SignatureStatus.Valid => Loc.T("verify.signed", signer ?? "?"),
                SignatureStatus.Unsigned => Loc.T("verify.unsigned-warning"),
                _ => Loc.T("verify.not-applicable"),
            });

    internal static string DescribeError(Exception ex) => ex switch
    {
        SourceException se => Loc.T(se.Key) + (string.IsNullOrEmpty(se.Detail) || se.Detail == se.Key ? "" : $" ({se.Detail})"),
        WinGetException we => Loc.T("error.winget", $"0x{we.ExitCode:X8}"),
        HttpRequestException he => Loc.T("error.network", he.StatusCode?.ToString() ?? he.Message),
        TaskCanceledException => Loc.T("error.timeout"),
        _ => ex.Message,
    };
}
