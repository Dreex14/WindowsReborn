using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Core.Localization;
using WindowsReborn.Tweaks;

namespace WindowsReborn.App.ViewModels;

public enum TweaksPage
{
    Privacy,
    Optimize,
}

public sealed record ChoiceOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record FilterTab(string Key, string Label);

/// <summary>Una optimización con su estado deseado (lo que marca el usuario) y el real.</summary>
public partial class TweakRow : ObservableObject
{
    private readonly TweaksViewModel _owner;
    private bool _syncing;

    public TweakRow(TweakDefinition def, TweaksViewModel owner)
    {
        _owner = owner;
        Def = def;
        var text = def.Text(Loc.Instance.Language);
        Title = text.Title;
        Description = text.Description;
        Warning = text.Warning;
        Choices = def.Kind == TweakKind.Choice
            ? def.Options.Select(o => new ChoiceOption(o, text.Choices.GetValueOrDefault(o, o))).ToList()
            : [];
    }

    public TweakDefinition Def { get; }
    public string Title { get; }
    public string Description { get; }
    public string? Warning { get; }
    public RiskLevel Risk => Def.Risk;
    public string RiskLabel => Loc.T($"risk.{(int)Def.Risk}");
    public string CategoryLabel => Loc.T("tweakcat." + Def.Category.ToString().ToLowerInvariant());
    public string? RestartLabel => Def.Restart == RestartNeed.None ? null : Loc.T("restart." + Def.Restart.ToString().ToLowerInvariant());
    public bool NeedsAdmin => Def.RequiresAdmin;
    public bool IsChoice => Def.Kind == TweakKind.Choice;
    public bool IsToggle => Def.Kind == TweakKind.Toggle;
    public bool IsInfo => Def.Kind == TweakKind.Info;
    public IReadOnlyList<ChoiceOption> Choices { get; }
    public bool InPreset { get; init; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChanged))] private bool _isOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChanged))] private ChoiceOption? _selectedChoice;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChanged))] private string? _currentOption;
    [ObservableProperty] private bool _canRevert;
    [ObservableProperty] private bool _isBusy;

    public string? Desired => IsChoice ? SelectedChoice?.Id : IsOn ? TweakDefinition.On : TweakDefinition.Off;

    /// <summary>El usuario ha cambiado algo respecto a lo que hay ahora en Windows.</summary>
    public bool IsChanged => !IsInfo && Desired is not null &&
                             (CurrentOption is null ? (IsChoice || IsOn) : Desired != CurrentOption);

    /// <summary>Lee el estado real y deja lo deseado igual que lo actual.</summary>
    public void Sync(TweakStatus status)
    {
        _syncing = true;
        CurrentOption = status.CurrentOption;
        IsOn = status.CurrentOption == TweakDefinition.On;
        SelectedChoice = Choices.FirstOrDefault(c => c.Id == status.CurrentOption);
        CanRevert = status.HasOriginal;
        _syncing = false;
        OnPropertyChanged(nameof(IsChanged));
    }

    partial void OnIsOnChanged(bool value) { if (!_syncing) _owner.OnRowChanged(); }
    partial void OnSelectedChoiceChanged(ChoiceOption? value) { if (!_syncing) _owner.OnRowChanged(); }

    [RelayCommand]
    private async Task RevertAsync() => await _owner.RevertAsync(this);

    [RelayCommand]
    private void Open()
    {
        if (Def.OpenUri is not null) Dialogs.OpenUri(Def.OpenUri);
    }
}

public partial class StartupRow(StartupEntry entry, TweaksViewModel owner) : ObservableObject
{
    public StartupEntry Entry { get; private set; } = entry;
    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string LocationLabel => Loc.T("startup.loc." + Entry.Location.ToString().ToLowerInvariant());
    public bool CanToggle => !Entry.IsProtected;
    [ObservableProperty] private bool _isEnabled = entry.Enabled;
    private bool _syncing;

    public void Sync(StartupEntry e)
    {
        _syncing = true;
        Entry = e;
        IsEnabled = e.Enabled;
        _syncing = false;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_syncing) _ = owner.SetStartupAsync(this, value);
    }
}

public partial class AppxRow(InstalledAppx app) : ObservableObject
{
    public InstalledAppx App { get; } = app;
    public string Title => App.Candidate.Title(Loc.Instance.Language);
    public RiskLevel Risk => (RiskLevel)App.Candidate.Risk;
    public string RiskLabel => Loc.T($"risk.{App.Candidate.Risk}");
    public string? Warning => App.Candidate.Warning(Loc.Instance.Language);
    [ObservableProperty] private bool _isRemoved;
    [ObservableProperty] private bool _isBusy;
}

public partial class TweaksViewModel : ObservableObject
{
    public const string AllKey = "all";
    public const string StartupKey = "startup";
    public const string AppxKey = "appx";

    private readonly ICollectionView _view;

    public TweaksViewModel(TweaksPage page)
    {
        Page = page;
        var categories = page == TweaksPage.Privacy
            ? new[] { TweakCategory.Privacy, TweakCategory.Permissions, TweakCategory.Search }
            : [TweakCategory.Explorer, TweakCategory.Taskbar, TweakCategory.Start, TweakCategory.Appearance,
               TweakCategory.Performance, TweakCategory.Apps, TweakCategory.System];
        var preset = (page == TweaksPage.Privacy ? TweakPresets.Privacy : TweakPresets.Optimize).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var def in TweakCatalog.All.Where(t => categories.Contains(t.Category))
                     .OrderBy(t => Array.IndexOf(categories, t.Category)).ThenBy(t => t.Risk))
            Rows.Add(new TweakRow(def, this) { InPreset = preset.Contains(def.Id) });

        Tabs.Add(new FilterTab(AllKey, Loc.T("tweakcat.all")));
        foreach (var c in categories.Where(c => Rows.Any(r => r.Def.Category == c)))
            Tabs.Add(new FilterTab(c.ToString(), Loc.T("tweakcat." + c.ToString().ToLowerInvariant())));
        if (page == TweaksPage.Optimize)
        {
            Tabs.Add(new FilterTab(StartupKey, Loc.T("tweakcat.startup")));
            Tabs.Add(new FilterTab(AppxKey, Loc.T("tweakcat.appx")));
        }
        _selectedTab = Tabs[0];

        _view = CollectionViewSource.GetDefaultView(Rows);
        _view.Filter = Filter;
        PresetIds = preset;
        CreateRestorePoint = AppServices.Settings.CreateRestorePoint;
        AppServices.ConfigurationReplaced += (_, _) => Refresh();
    }

    public TweaksPage Page { get; }
    public bool IsPrivacy => Page == TweaksPage.Privacy;
    public string Title => Loc.T(IsPrivacy ? "privacy.title" : "optimize.title");
    public string Subtitle => Loc.T(IsPrivacy ? "privacy.subtitle" : "optimize.subtitle");
    public string PresetTitle => Loc.T(IsPrivacy ? "privacy.preset.title" : "optimize.preset.title");
    public string PresetText => Loc.T(IsPrivacy ? "privacy.preset.text" : "optimize.preset.text", PresetSupportedCount);
    public IReadOnlySet<string> PresetIds { get; }

    public ObservableCollection<TweakRow> Rows { get; } = [];
    public ICollectionView View => _view;
    public ObservableCollection<FilterTab> Tabs { get; } = [];
    public ObservableCollection<StartupRow> StartupRows { get; } = [];
    public ObservableCollection<AppxRow> AppxRows { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsTweakList), nameof(IsStartupList), nameof(IsAppxList))]
    private FilterTab _selectedTab;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _showSafe = true;
    [ObservableProperty] private bool _showModerate = true;
    [ObservableProperty] private bool _showAdvanced = true;
    [ObservableProperty] private int _changedCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _appxLoading;
    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _explorerRestartNeeded;

    public bool IsTweakList => SelectedTab.Key is not (StartupKey or AppxKey);
    public bool IsStartupList => SelectedTab.Key == StartupKey;
    public bool IsAppxList => SelectedTab.Key == AppxKey;
    public int PresetSupportedCount => Rows.Count(r => r.InPreset);

    partial void OnSelectedTabChanged(FilterTab value)
    {
        _view.Refresh();
        if (value.Key == StartupKey) LoadStartup();
        if (value.Key == AppxKey && AppxRows.Count == 0) _ = LoadAppxAsync();
    }

    partial void OnSearchChanged(string value) => _view.Refresh();
    partial void OnShowSafeChanged(bool value) => _view.Refresh();
    partial void OnShowModerateChanged(bool value) => _view.Refresh();
    partial void OnShowAdvancedChanged(bool value) => _view.Refresh();

    partial void OnCreateRestorePointChanged(bool value)
    {
        AppServices.Settings.CreateRestorePoint = value;
        AppServices.SaveSettingsSoon();
    }

    private bool Filter(object o)
    {
        if (o is not TweakRow r) return false;
        if (SelectedTab.Key != AllKey && SelectedTab.Key != r.Def.Category.ToString()) return false;
        if (r.Risk == RiskLevel.Safe && !ShowSafe || r.Risk == RiskLevel.Moderate && !ShowModerate || r.Risk == RiskLevel.Advanced && !ShowAdvanced)
            return false;
        return Search.Length == 0 || r.Title.Contains(Search, StringComparison.CurrentCultureIgnoreCase) ||
               r.Description.Contains(Search, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Lee el estado real de Windows. Los ajustes no compatibles se ocultan.</summary>
    public void Refresh()
    {
        foreach (var row in Rows.ToList())
        {
            var status = AppServices.Tweaks.Evaluate(row.Def);
            if (status.Support is Support.WrongBuild or Support.Missing)
            {
                Rows.Remove(row);
                continue;
            }
            row.Sync(status);
        }
        // Lo guardado en «Mi configuración» se marca como deseado (pendiente de aplicar).
        foreach (var (id, option) in AppServices.TweakPreferences.Selections)
        {
            var row = Rows.FirstOrDefault(r => r.Def.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (row is null) continue;
            if (row.IsToggle) row.IsOn = option == TweakDefinition.On;
            else if (row.IsChoice) row.SelectedChoice = row.Choices.FirstOrDefault(c => c.Id == option) ?? row.SelectedChoice;
        }
        OnRowChanged();
        OnPropertyChanged(nameof(PresetText));
    }

    public void OnRowChanged() => ChangedCount = Rows.Count(r => r.IsChanged);

    [RelayCommand]
    private void Discard()
    {
        foreach (var row in Rows) row.Sync(AppServices.Tweaks.Evaluate(row.Def));
        OnRowChanged();
    }

    /// <summary>Marca la configuración recomendada (sin aplicarla todavía).</summary>
    [RelayCommand]
    private void MarkPreset()
    {
        foreach (var row in Rows.Where(r => r.InPreset && r.IsToggle)) row.IsOn = true;
        SelectedTab = Tabs[0];
        OnRowChanged();
    }

    /// <summary>Marca y aplica la configuración recomendada de una vez.</summary>
    [RelayCommand]
    private async Task ApplyPresetAsync()
    {
        MarkPreset();
        if (ChangedCount == 0)
        {
            StatusText = Loc.T("tweaks.preset.already");
            return;
        }
        await ApplyAsync();
    }

    [RelayCommand]
    public async Task ApplyAsync()
    {
        var rows = Rows.Where(r => r.IsChanged).ToList();
        if (rows.Count == 0 || IsBusy) return;

        var message = Loc.T("tweaks.confirm.body", rows.Count);
        var advanced = rows.Where(r => r.Risk == RiskLevel.Advanced).ToList();
        if (advanced.Count > 0)
            message += "\n\n" + Loc.T("tweaks.confirm.advanced") + "\n" + string.Join("\n", advanced.Select(r => $"• {r.Title}: {r.Warning}"));
        if (rows.Any(r => r.NeedsAdmin) || CreateRestorePoint)
            message += "\n\n" + Loc.T("tweaks.confirm.admin");
        if (CreateRestorePoint)
            message += "\n" + Loc.T("tweaks.confirm.restore-point");
        if (!await Dialogs.ConfirmAsync(Loc.T("tweaks.confirm.title"), message, Loc.T("tweaks.apply"), advanced.Count > 0))
            return;

        await ApplyRowsAsync(rows, CreateRestorePoint);
    }

    private async Task ApplyRowsAsync(List<TweakRow> rows, bool restorePoint)
    {
        IsBusy = true;
        StatusText = Loc.T("tweaks.applying");
        try
        {
            var changes = rows.Select(r => (r.Def, r.Desired!)).ToList();
            IReadOnlyList<TweakResult> results;
            try
            {
                results = await AppServices.Tweaks.ApplyAsync(changes, restorePoint);
            }
            catch (RestorePointException)
            {
                if (!await Dialogs.ConfirmAsync(Loc.T("tweaks.confirm.title"), Loc.T("tweaks.restore-point-failed"), Loc.T("tweaks.apply-anyway")))
                {
                    StatusText = null;
                    return;
                }
                results = await AppServices.Tweaks.ApplyAsync(changes, false);
            }

            foreach (var r in results.Where(r => r.Outcome is TweakOutcome.Applied or TweakOutcome.Unchanged))
            {
                var row = rows.First(x => x.Def.Id == r.TweakId);
                AppServices.TweakPreferences.Selections[r.TweakId] = row.Desired!;
            }
            await AppServices.SaveTweakPreferencesAsync();

            var applied = results.Count(r => r.Outcome == TweakOutcome.Applied);
            var failed = results.Count(r => r.Outcome is TweakOutcome.Failed or TweakOutcome.Unsupported);
            var restart = rows.Where(r => results.Any(x => x.TweakId == r.Def.Id && x.Outcome == TweakOutcome.Applied))
                .Select(r => r.Def.Restart).DefaultIfEmpty(RestartNeed.None).Max();
            ExplorerRestartNeeded = restart == RestartNeed.Explorer;
            StatusText = Loc.T(failed == 0 ? "tweaks.applied" : "tweaks.applied-errors", applied, failed) +
                         (restart is RestartNeed.SignOut or RestartNeed.Reboot ? " " + Loc.T("restart.pending." + restart.ToString().ToLowerInvariant()) : "");
        }
        catch (ElevationCancelledException)
        {
            StatusText = Loc.T("tweaks.uac-cancelled");
        }
        catch (Exception ex)
        {
            Core.Logging.Log.Error("Apply tweaks failed", ex);
            StatusText = Loc.T("tweaks.error", ex.Message);
        }
        finally
        {
            IsBusy = false;
            Refresh();
            OtherPage?.Refresh();
        }
    }

    /// <summary>Aplica una lista de ajustes importada (desde «Mi configuración»).</summary>
    public async Task<int> ApplySelectionsAsync(IReadOnlyDictionary<string, string> selections)
    {
        var rows = new List<TweakRow>();
        foreach (var (id, option) in selections)
        {
            var row = Rows.FirstOrDefault(r => r.Def.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (row is null || !row.Def.Options.Contains(option)) continue;
            if (row.IsToggle) row.IsOn = option == TweakDefinition.On;
            else row.SelectedChoice = row.Choices.FirstOrDefault(c => c.Id == option);
            if (row.IsChanged) rows.Add(row);
        }
        if (rows.Count > 0) await ApplyRowsAsync(rows, CreateRestorePoint);
        return rows.Count;
    }

    private TweaksViewModel? OtherPage => IsPrivacy ? ViewModelLocator.Optimize : ViewModelLocator.Privacy;

    public async Task RevertAsync(TweakRow row)
    {
        row.IsBusy = true;
        try
        {
            var r = await AppServices.Tweaks.RevertAsync(row.Def, false);
            if (r.Outcome is TweakOutcome.Reverted or TweakOutcome.Applied)
            {
                AppServices.TweakPreferences.Selections.Remove(row.Def.Id);
                await AppServices.SaveTweakPreferencesAsync();
            }
            StatusText = Loc.T(r.Outcome == TweakOutcome.Reverted ? "tweaks.reverted" : "tweaks.revert-failed", row.Title);
            ExplorerRestartNeeded |= row.Def.Restart == RestartNeed.Explorer;
        }
        catch (ElevationCancelledException)
        {
            StatusText = Loc.T("tweaks.uac-cancelled");
        }
        finally
        {
            row.IsBusy = false;
            row.Sync(AppServices.Tweaks.Evaluate(row.Def));
            OnRowChanged();
        }
    }

    [RelayCommand]
    private async Task RevertAllAsync()
    {
        var rows = Rows.Where(r => r.CanRevert).ToList();
        if (rows.Count == 0)
        {
            StatusText = Loc.T("tweaks.nothing-to-revert");
            return;
        }
        if (!await Dialogs.ConfirmAsync(Loc.T("tweaks.revert-all"), Loc.T("tweaks.revert-all.body", rows.Count), Loc.T("tweaks.revert-all")))
            return;
        foreach (var row in rows) await RevertAsync(row);
    }

    [RelayCommand]
    private void RestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); } catch { }
                p.Dispose();
            }
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { }
        ExplorerRestartNeeded = false;
    }

    // ---- Inicio de Windows ----

    private void LoadStartup()
    {
        var entries = AppServices.Startup.List(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
        StartupRows.Clear();
        foreach (var e in entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            StartupRows.Add(new StartupRow(e, this));
    }

    public async Task SetStartupAsync(StartupRow row, bool enabled)
    {
        try
        {
            await AppServices.Startup.SetEnabledAsync(row.Entry, enabled);
            row.Sync(row.Entry with { Enabled = enabled });
        }
        catch (ElevationCancelledException)
        {
            row.Sync(row.Entry);
            StatusText = Loc.T("tweaks.uac-cancelled");
        }
        catch (Exception ex)
        {
            row.Sync(row.Entry);
            StatusText = Loc.T("tweaks.error", ex.Message);
        }
    }

    // ---- Apps preinstaladas ----

    [RelayCommand]
    private async Task LoadAppxAsync()
    {
        AppxLoading = true;
        try
        {
            var list = await AppServices.Appx.ListInstalledAsync();
            AppxRows.Clear();
            foreach (var a in list) AppxRows.Add(new AppxRow(a));
        }
        finally
        {
            AppxLoading = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAppxAsync(AppxRow row)
    {
        var body = Loc.T("appx.confirm", row.Title) + (row.Warning is null ? "" : "\n\n" + row.Warning);
        if (!await Dialogs.ConfirmAsync(Loc.T("appx.remove"), body, Loc.T("appx.remove"), row.Risk == RiskLevel.Advanced))
            return;
        row.IsBusy = true;
        try
        {
            row.IsRemoved = await AppServices.Appx.RemoveAsync(row.App);
            StatusText = Loc.T(row.IsRemoved ? "appx.removed" : "appx.failed", row.Title);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private void ReinstallAppx(AppxRow row) => Dialogs.OpenUri(AppxManager.StoreSearchUri(row.App.Candidate));
}
