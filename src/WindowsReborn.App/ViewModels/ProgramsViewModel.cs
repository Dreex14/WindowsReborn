using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Core.Localization;
using WindowsReborn.Programs;

namespace WindowsReborn.App.ViewModels;

public enum ItemState
{
    Idle,
    Installed,
    UpdateAvailable,
    Queued,
    Working,
    Done,
    Failed,
    Manual,
}

/// <summary>Programa del catálogo en la cuadrícula.</summary>
public partial class ProgramItem(CatalogProgram def, ProgramsViewModel owner) : ObservableObject
{
    public CatalogProgram Def { get; } = def;
    public string Name => Def.Name;
    public string Color => Def.Color;
    public string Monogram => Def.Monogram;
    public bool IsManual => !Def.IsAutomatic;
    /// <summary>Icono incluido en el ejecutable (null = se usa la insignia con iniciales).</summary>
    public System.Windows.Media.ImageSource? Icon { get; } = ProgramIcons.For(def.Id);
    public bool HasIcon => Icon is not null;
    public string SourceLabel => Loc.T("programs.source." + Def.Source.ToString().ToLowerInvariant());

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText), nameof(ShowState))] private ItemState _state;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText))] private string? _detail;
    public InstalledInfo? Info { get; set; }

    public bool ShowState => State != ItemState.Idle;

    public string StateText => State switch
    {
        ItemState.Installed => Loc.T("state.installed"),
        ItemState.UpdateAvailable => Loc.T("state.update", Info?.AvailableUpdate ?? ""),
        ItemState.Queued => Loc.T("state.queued"),
        ItemState.Working => string.IsNullOrEmpty(Detail) ? Loc.T("state.working") : Loc.T("state.downloading", Detail),
        ItemState.Done => Detail ?? Loc.T("state.done"),
        ItemState.Failed => Loc.T("state.failed"),
        ItemState.Manual => Loc.T("state.manual"),
        _ => "",
    };

    partial void OnIsSelectedChanged(bool value) => owner.OnSelectionChanged();
}

public sealed class ProgramGroup(string title, IReadOnlyList<ProgramItem> items)
{
    public string Title { get; } = title;
    public IReadOnlyList<ProgramItem> Items { get; } = items;
}

/// <summary>App publicada en GitHub (tus .exe).</summary>
public partial class GitHubItem(GitHubApp app, string? installedVersion, ProgramsViewModel owner) : ObservableObject
{
    public GitHubApp App { get; } = app;
    public string Name => App.Name;
    public string Repo => App.Repo;
    public string? Description => App.Description;
    public string Version => "v" + App.Version;
    public string Monogram => new string(App.Name.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
    public string? InstalledVersion { get; set; } = installedVersion;

    public bool IsPrivate => App.IsPrivate;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText), nameof(ShowState))] private ItemState _state;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText))] private string? _detail;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIcon))] private System.Windows.Media.ImageSource? _icon;
    public bool HasIcon => Icon is not null;

    public bool ShowState => State != ItemState.Idle;

    public string StateText => State switch
    {
        ItemState.Installed => Loc.T("state.installed"),
        ItemState.UpdateAvailable => Loc.T("state.update", App.Version),
        ItemState.Queued => Loc.T("state.queued"),
        ItemState.Working => string.IsNullOrEmpty(Detail) ? Loc.T("state.working") : Loc.T("state.downloading", Detail),
        ItemState.Done => Detail ?? Loc.T("state.done"),
        ItemState.Failed => Loc.T("state.failed"),
        _ => "",
    };

    partial void OnIsSelectedChanged(bool value) => owner.OnSelectionChanged();
}

public partial class ProgramsViewModel : ObservableObject
{
    private bool _loading;
    private CancellationTokenSource? _cts;

    public ObservableCollection<ProgramGroup> Groups { get; } = [];
    public IEnumerable<ProgramItem> AllItems => Groups.SelectMany(g => g.Items);
    public ObservableCollection<GitHubItem> GitHubApps { get; } = [];

    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDetecting;
    [ObservableProperty] private bool _gitHubLoading;
    [ObservableProperty] private string? _gitHubError;
    [ObservableProperty] private string _gitHubUser = "";
    [ObservableProperty] private string _newRepo = "";
    [ObservableProperty] private bool _desktopShortcut;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsGitHubConnected))] private string? _gitHubAccount;
    [ObservableProperty] private bool _gitHubConnecting;
    public bool IsGitHubConnected => GitHubAccount is not null;

    public const string TokenPageUrl = "https://github.com/settings/personal-access-tokens/new?name=Windows+Reborn&description=Descargar+mis+releases+privadas&contents=read";

    /// <summary>Conecta la cuenta de GitHub con un token de solo lectura (se guarda cifrado).</summary>
    public async Task ConnectGitHubAsync(string token)
    {
        token = token.Trim();
        if (token.Length == 0) return;
        GitHubConnecting = true;
        GitHubError = null;
        try
        {
            var login = await AppServices.GitHub.ValidateTokenAsync(token, CancellationToken.None);
            if (login is null)
            {
                GitHubError = Loc.T("programs.github.token-invalid");
                return;
            }
            AppServices.Settings.GitHubTokenProtected = Core.Configuration.SecretStore.Protect(token);
            AppServices.Settings.GitHubTokenUser = login;
            if (string.IsNullOrWhiteSpace(AppServices.Settings.GitHubUser)) AppServices.Settings.GitHubUser = login;
            await AppServices.SaveSettingsAsync();
            GitHubAccount = login;
            await LoadGitHubAsync();
        }
        catch (Exception)
        {
            GitHubError = Loc.T("programs.github.offline");
        }
        finally
        {
            GitHubConnecting = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectGitHubAsync()
    {
        AppServices.Settings.GitHubTokenProtected = null;
        AppServices.Settings.GitHubTokenUser = null;
        await AppServices.SaveSettingsAsync();
        GitHubAccount = null;
        await LoadGitHubAsync();
    }

    /// <summary>
    /// Si la configuración trae el token cifrado con contraseña, la pide y lo guarda con DPAPI
    /// en este PC. Devuelve true si se importó.
    /// </summary>
    public static async Task<bool> ImportGitHubTokenAsync(Core.Configuration.MyConfig config)
    {
        if (config.GitHubToken is null) return false;
        string? error = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var password = await Dialogs.PasswordAsync(Loc.T("config.token.title"), Loc.T("config.token.ask", config.GitHubToken.User ?? "GitHub"),
                Loc.T("config.token.unlock"), confirm: false, error);
            if (password is null) return false;
            var token = Core.Configuration.PasswordVault.Unprotect(config.GitHubToken, password);
            if (token is null)
            {
                error = Loc.T("config.token.wrong");
                continue;
            }
            AppServices.Settings.GitHubTokenProtected = Core.Configuration.SecretStore.Protect(token);
            AppServices.Settings.GitHubTokenUser = config.GitHubToken.User;
            if (string.IsNullOrWhiteSpace(AppServices.Settings.GitHubUser) && config.GitHubToken.User is { } user)
                AppServices.Settings.GitHubUser = user;
            await AppServices.SaveSettingsAsync();
            return true;
        }
        return false;
    }

    [RelayCommand]
    private static void OpenTokenPage() => Dialogs.OpenUri(TokenPageUrl);
    [ObservableProperty] private int _progress;

    public ProgramsViewModel()
    {
        foreach (var cat in Enum.GetValues<CatalogCategory>())
        {
            var items = SoftwareCatalog.All.Where(p => p.Category == cat).Select(p => new ProgramItem(p, this)).ToList();
            if (items.Count > 0)
                Groups.Add(new ProgramGroup(Loc.T("programs.cat." + cat.ToString().ToLowerInvariant()), items));
        }
        LoadSelection();
        AppServices.ConfigurationReplaced += (_, _) => { LoadSelection(); _ = LoadGitHubAsync(); };
    }

    private void LoadSelection()
    {
        _loading = true;
        var s = AppServices.Settings;
        var selected = s.SelectedPrograms?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Programas nuevos que vienen marcados: se añaden una vez a una selección ya guardada.
        if (selected is not null)
        {
            foreach (var id in SoftwareCatalog.AddedPreselected.Where(id => !s.SeenNewPrograms.Contains(id, StringComparer.OrdinalIgnoreCase)))
            {
                selected.Add(id);
                s.SeenNewPrograms.Add(id);
                s.SelectedPrograms!.Add(id);
            }
        }
        foreach (var item in AllItems)
            item.IsSelected = selected?.Contains(item.Def.Id) ?? item.Def.Preselected;
        GitHubUser = s.GitHubUser;
        // El token cifrado solo vale en este usuario y equipo: si no se puede leer, se olvida.
        GitHubAccount = Core.Configuration.SecretStore.Unprotect(s.GitHubTokenProtected) is not null ? s.GitHubTokenUser ?? "?" : null;
        DesktopShortcut = s.GitHubDesktopShortcut;
        foreach (var g in GitHubApps)
            g.IsSelected = s.SelectedRepos.Contains(g.Repo, StringComparer.OrdinalIgnoreCase);
        _loading = false;
        OnSelectionChanged();
    }

    public void OnSelectionChanged()
    {
        SelectedCount = AllItems.Count(i => i.IsSelected) + GitHubApps.Count(g => g.IsSelected);
        if (_loading) return;
        AppServices.Settings.SelectedPrograms = AllItems.Where(i => i.IsSelected).Select(i => i.Def.Id).ToList();
        AppServices.Settings.SelectedRepos = GitHubApps.Where(g => g.IsSelected).Select(g => g.Repo).ToList();
        AppServices.SaveSettingsSoon();
    }

    partial void OnDesktopShortcutChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.GitHubDesktopShortcut = value;
        AppServices.SaveSettingsSoon();
    }

    /// <summary>Se llama al abrir la sección por primera vez.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_detectedOnce) return;
        _detectedOnce = true;
        await Task.WhenAll(DetectAsync(), LoadGitHubAsync());
    }

    private bool _detectedOnce;

    [RelayCommand]
    private async Task DetectAsync()
    {
        if (IsDetecting) return;
        IsDetecting = true;
        try
        {
            var info = await AppServices.Installer.DetectAsync(AllItems.Select(i => i.Def), CancellationToken.None);
            foreach (var item in AllItems)
            {
                if (item.State is ItemState.Working or ItemState.Queued) continue;
                item.Info = info.GetValueOrDefault(item.Def.Id);
                item.State = item.Info switch
                {
                    { AvailableUpdate: not null } => ItemState.UpdateAvailable,
                    { Installed: true } => ItemState.Installed,
                    _ => ItemState.Idle,
                };
            }
        }
        finally
        {
            IsDetecting = false;
        }
    }

    [RelayCommand]
    private async Task LoadGitHubAsync()
    {
        GitHubLoading = true;
        GitHubError = null;
        try
        {
            var s = AppServices.Settings;
            var apps = await AppServices.GitHub.ListUserAppsAsync(s.GitHubUser, s.ExtraRepos, CancellationToken.None);
            var state = AppServices.GitHub.LoadState();
            _loading = true;
            GitHubApps.Clear();
            foreach (var app in apps)
            {
                var installed = state.GetValueOrDefault(app.Repo)?.Version;
                var item = new GitHubItem(app, installed, this)
                {
                    IsSelected = s.SelectedRepos.Contains(app.Repo, StringComparer.OrdinalIgnoreCase),
                    Icon = ProgramIcons.FromExe(state.GetValueOrDefault(app.Repo)?.ExePath),
                };
                item.State = installed is null ? ItemState.Idle : installed == app.Version ? ItemState.Installed : ItemState.UpdateAvailable;
                GitHubApps.Add(item);
            }
            _loading = false;
            OnSelectionChanged();
        }
        catch (SourceException ex)
        {
            GitHubError = Loc.T(ex.Key);
        }
        catch (Exception)
        {
            GitHubError = Loc.T("programs.github.offline");
        }
        finally
        {
            _loading = false;
            GitHubLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveGitHubUserAsync()
    {
        var user = GitHubUser.Trim();
        if (user.Length > 0 && !GitHubAppsClient.IsValidUser(user))
        {
            GitHubError = Loc.T("programs.github.bad-user");
            return;
        }
        AppServices.Settings.GitHubUser = user;
        await AppServices.SaveSettingsAsync();
        await LoadGitHubAsync();
    }

    [RelayCommand]
    private async Task AddRepoAsync()
    {
        var repo = NewRepo.Trim();
        if (repo.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            repo = string.Join('/', repo["https://github.com/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).Take(2));
        if (!GitHubAppsClient.IsValidRepo(repo))
        {
            GitHubError = Loc.T("programs.github.bad-repo");
            return;
        }
        if (!AppServices.Settings.ExtraRepos.Contains(repo, StringComparer.OrdinalIgnoreCase))
            AppServices.Settings.ExtraRepos.Add(repo);
        if (!AppServices.Settings.SelectedRepos.Contains(repo, StringComparer.OrdinalIgnoreCase))
            AppServices.Settings.SelectedRepos.Add(repo);
        await AppServices.SaveSettingsAsync();
        NewRepo = "";
        await LoadGitHubAsync();
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        var s = AppServices.Settings;
        var config = new Core.Configuration.MyConfig
        {
            Parts = [Core.Configuration.MyConfig.PartPrograms],
            Programs = AllItems.Where(i => i.IsSelected).Select(i => i.Def.Id).ToList(),
            GitHubUser = s.GitHubUser,
            GitHubRepos = GitHubApps.Where(g => g.IsSelected).Select(g => g.Repo).ToList(),
            ExtraRepos = [.. s.ExtraRepos],
        };
        StatusText = await ConfigFiles.SaveAsync(config, "section.file.programs") ?? StatusText;
    }

    [RelayCommand]
    private async Task LoadConfigAsync()
    {
        var config = await ConfigFiles.LoadAsync();
        if (config is null) return;
        var tokenImported = await ImportGitHubTokenAsync(config);
        if (tokenImported) GitHubAccount = AppServices.Settings.GitHubTokenUser ?? "?";
        if (!config.Has(Core.Configuration.MyConfig.PartPrograms) || config.Programs.Count + config.GitHubRepos.Count == 0)
        {
            if (tokenImported) await LoadGitHubAsync();
            StatusText = Loc.T("section.empty");
            return;
        }
        var s = AppServices.Settings;
        s.SelectedPrograms = [.. config.Programs];
        if (!string.IsNullOrWhiteSpace(config.GitHubUser)) s.GitHubUser = config.GitHubUser;
        s.SelectedRepos = [.. config.GitHubRepos];
        s.ExtraRepos = s.ExtraRepos.Union(config.ExtraRepos, StringComparer.OrdinalIgnoreCase).ToList();
        await AppServices.SaveSettingsAsync();
        LoadSelection();
        await LoadGitHubAsync();
        StatusText = Loc.T("section.loaded.programs", config.Programs.Count + config.GitHubRepos.Count);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in AllItems) i.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in AllItems) i.IsSelected = false;
        foreach (var g in GitHubApps) g.IsSelected = false;
    }

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var i in AllItems) i.IsSelected = i.Def.Preselected;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Instala (o actualiza) todo lo marcado, uno detrás de otro.</summary>
    [RelayCommand]
    public async Task InstallSelectedAsync()
    {
        if (IsBusy) return;
        var programs = AllItems.Where(i => i.IsSelected && i.State != ItemState.Installed).ToList();
        var apps = GitHubApps.Where(g => g.IsSelected && g.State != ItemState.Installed).ToList();
        var skipped = AllItems.Count(i => i.IsSelected && i.State == ItemState.Installed) +
                      GitHubApps.Count(g => g.IsSelected && g.State == ItemState.Installed);

        if (programs.Count + apps.Count == 0)
        {
            await Dialogs.InfoAsync(Loc.T("programs.title"), skipped > 0 ? Loc.T("programs.nothing-all-installed") : Loc.T("programs.nothing-selected"));
            return;
        }

        var manual = programs.Count(p => p.IsManual);
        var message = Loc.T("programs.confirm.body", programs.Count + apps.Count);
        if (skipped > 0) message += "\n\n" + Loc.T("programs.confirm.skipped", skipped);
        if (manual > 0) message += "\n\n" + Loc.T("programs.confirm.manual", manual);
        if (!await Dialogs.ConfirmAsync(Loc.T("programs.confirm.title"), message, Loc.T("programs.install")))
            return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var total = programs.Count + apps.Count;
        var done = 0;
        var failed = 0;
        foreach (var p in programs) p.State = ItemState.Queued;
        foreach (var a in apps) a.State = ItemState.Queued;

        try
        {
            foreach (var p in programs)
            {
                if (ct.IsCancellationRequested) { p.State = ItemState.Idle; continue; }
                StatusText = Loc.T("programs.status.installing", p.Name, done + 1, total);
                p.Detail = null;
                p.State = ItemState.Working;
                var status = new Progress<string>(d => p.Detail = string.IsNullOrEmpty(d) ? null : d);
                var r = await AppServices.Installer.InstallAsync(p.Def, p.Info?.AvailableUpdate is not null, status, ct);
                Finish(p, r, ref failed);
                Progress = ++done * 100 / total;
            }

            foreach (var a in apps)
            {
                if (ct.IsCancellationRequested) { a.State = ItemState.Idle; continue; }
                StatusText = Loc.T("programs.status.installing", a.Name, done + 1, total);
                a.Detail = null;
                a.State = ItemState.Working;
                var status = new Progress<string>(d => a.Detail = string.IsNullOrEmpty(d) ? null : d);
                var r = await AppServices.GitHub.InstallAsync(a.App, DesktopShortcut, status, ct);
                a.Detail = OutcomeText(r);
                if (r.Outcome == InstallOutcome.Failed) failed++;
                a.State = r.Outcome switch
                {
                    InstallOutcome.Failed => ItemState.Failed,
                    InstallOutcome.Cancelled => ItemState.Idle,
                    _ => ItemState.Done,
                };
                if (r.Outcome is InstallOutcome.Installed or InstallOutcome.Updated)
                {
                    a.InstalledVersion = a.App.Version;
                    a.Icon ??= ProgramIcons.FromExe(AppServices.GitHub.LoadState().GetValueOrDefault(a.Repo)?.ExePath);
                }
                Progress = ++done * 100 / total;
            }
        }
        finally
        {
            IsBusy = false;
            StatusText = ct.IsCancellationRequested
                ? Loc.T("programs.status.cancelled")
                : failed == 0 ? Loc.T("programs.status.done") : Loc.T("programs.status.done-errors", failed);
            _cts.Dispose();
            _cts = null;
        }
    }

    private static void Finish(ProgramItem p, InstallResult r, ref int failed)
    {
        p.Detail = OutcomeText(r);
        if (r.Outcome == InstallOutcome.Failed) failed++;
        p.State = r.Outcome switch
        {
            InstallOutcome.Failed => ItemState.Failed,
            InstallOutcome.Cancelled => ItemState.Idle,
            InstallOutcome.PageOpened => ItemState.Manual,
            InstallOutcome.AlreadyInstalled => ItemState.Installed,
            _ => ItemState.Done,
        };
        if (r.Outcome is InstallOutcome.Failed)
            Core.Logging.Log.Warn($"Install {p.Def.Id}: {r.Detail}");
    }

    private static string OutcomeText(InstallResult r) => r.Outcome switch
    {
        InstallOutcome.Installed => r.Detail == "reboot" ? Loc.T("outcome.installed-reboot") : Loc.T("outcome.installed"),
        InstallOutcome.Updated => Loc.T("outcome.updated"),
        InstallOutcome.AlreadyInstalled => Loc.T("state.installed"),
        InstallOutcome.InstallerLaunched => Loc.T("outcome.launched"),
        InstallOutcome.PageOpened => Loc.T("state.manual"),
        InstallOutcome.Failed => r.Detail is { } d && d.Contains('.') && !d.Contains(' ') ? Loc.T(d) : Loc.T("state.failed"),
        _ => "",
    };
}
