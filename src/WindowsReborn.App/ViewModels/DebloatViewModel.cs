using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Localization;
using WindowsReborn.Tweaks;

namespace WindowsReborn.App.ViewModels;

/// <summary>Programa preinstalado en la lista del desinstalador.</summary>
public partial class DebloatRow(InstalledAppx app, DebloatViewModel owner) : ObservableObject
{
    public InstalledAppx App { get; } = app;
    public string Title => App.Candidate.Title(Loc.Instance.Language);
    public RiskLevel Risk => (RiskLevel)App.Candidate.Risk;
    public string RiskLabel => Loc.T($"risk.{App.Candidate.Risk}");
    public string GroupLabel => Loc.T("appx.group." + App.Candidate.Group);
    public bool Recommended => App.Candidate.Recommended;
    public string? Warning => App.Candidate.Warning(Loc.Instance.Language);
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isRemoved;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isBusy;

    partial void OnIsSelectedChanged(bool value) => owner.OnSelectionChanged();
}

public sealed record DebloatGroup(string Title, IReadOnlyList<DebloatRow> Rows);

/// <summary>«Desinstalar programas»: quita de golpe la basura que trae Windows.</summary>
public partial class DebloatViewModel : ObservableObject
{
    private bool _syncing;

    public ObservableCollection<DebloatRow> Rows { get; } = [];
    public ObservableCollection<DebloatGroup> Groups { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _preventReinstall = true;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _isEmpty;

    public DebloatViewModel()
    {
        AppServices.ConfigurationReplaced += (_, _) => ApplySavedSelection();
    }

    public static List<string> DefaultSelection() => AppxManager.Candidates.Where(c => c.Recommended).Select(c => c.Name).ToList();

    /// <summary>Lo marcado más lo ya quitado: lo que se guarda en «Mi configuración».</summary>
    public static List<string> SavedList() =>
        [.. AppServices.Settings.RemovedApps.Union(AppServices.Settings.DebloatSelection ?? DefaultSelection(), StringComparer.OrdinalIgnoreCase)];

    public async Task EnsureLoadedAsync()
    {
        if (!IsLoaded && !IsLoading) await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var list = await AppServices.Appx.ListInstalledAsync();
            _syncing = true;
            Rows.Clear();
            foreach (var a in list) Rows.Add(new DebloatRow(a, this));
            _syncing = false;
            Groups.Clear();
            foreach (var key in new[] { "recommended", "optional", "advanced", "oem" })
            {
                var rows = Rows.Where(r => GroupOf(r) == key).ToList();
                if (rows.Count > 0) Groups.Add(new DebloatGroup(Loc.T("debloat.group." + key), rows));
            }
            IsEmpty = Rows.Count == 0;
            IsLoaded = true;
            ApplySavedSelection();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string GroupOf(DebloatRow r) =>
        r.App.Candidate.Group == "oem" ? "oem" : r.Recommended ? "recommended" : r.Risk == RiskLevel.Advanced ? "advanced" : "optional";

    private void ApplySavedSelection()
    {
        var selection = SavedList().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _syncing = true;
        foreach (var r in Rows) r.IsSelected = !r.IsRemoved && selection.Contains(r.App.Candidate.Name);
        _syncing = false;
        SelectedCount = Rows.Count(r => r.IsSelected && !r.IsRemoved);
    }

    public void OnSelectionChanged()
    {
        SelectedCount = Rows.Count(r => r.IsSelected && !r.IsRemoved);
        if (_syncing || !IsLoaded) return;
        // Se guarda como el resto de opciones (también lo de programas que ya no están instalados).
        var shown = Rows.Select(r => r.App.Candidate.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previous = AppServices.Settings.DebloatSelection ?? DefaultSelection();
        AppServices.Settings.DebloatSelection = previous.Where(n => !shown.Contains(n))
            .Concat(Rows.Where(r => r.IsSelected).Select(r => r.App.Candidate.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AppServices.SaveSettingsSoon();
    }

    [RelayCommand]
    private void MarkRecommended()
    {
        foreach (var r in Rows.Where(r => !r.IsRemoved)) r.IsSelected = r.Recommended;
    }

    [RelayCommand]
    private void UnmarkAll()
    {
        foreach (var r in Rows) r.IsSelected = false;
    }

    /// <summary>Quita de golpe todo lo marcado (y opcionalmente evita que vuelva a instalarse).</summary>
    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var rows = Rows.Where(r => r.IsSelected && !r.IsRemoved).ToList();
        if (rows.Count == 0 || IsBusy) return;

        var message = Loc.T("appx.confirm.many", rows.Count) + "\n\n" + string.Join("\n", rows.Select(r => "• " + r.Title));
        var warned = rows.Where(r => r.Warning is not null).ToList();
        if (warned.Count > 0)
            message += "\n\n" + Loc.T("appx.confirm.warnings") + "\n" + string.Join("\n", warned.Select(r => $"• {r.Title}: {r.Warning}"));
        if (PreventReinstall) message += "\n\n" + Loc.T("appx.confirm.provisioned");
        if (!await Dialogs.ConfirmAsync(Loc.T("appx.remove-selected"), message, Loc.T("appx.remove"), rows.Any(r => r.Risk == RiskLevel.Advanced)))
            return;
        await RemoveRowsAsync(rows);
    }

    private async Task RemoveRowsAsync(List<DebloatRow> rows)
    {
        IsBusy = true;
        var removed = 0;
        try
        {
            var i = 0;
            foreach (var row in rows)
            {
                StatusText = Loc.T("appx.removing", row.Title, ++i, rows.Count);
                row.IsBusy = true;
                try
                {
                    row.IsRemoved = await AppServices.Appx.RemoveAsync(row.App);
                    row.IsFailed = !row.IsRemoved;
                }
                catch (Exception ex)
                {
                    Core.Logging.Log.Warn($"Remove {row.App.Candidate.Name}: {ex.Message}");
                    row.IsFailed = true;
                }
                finally
                {
                    row.IsBusy = false;
                }
                if (row.IsRemoved)
                {
                    removed++;
                    if (!AppServices.Settings.RemovedApps.Contains(row.App.Candidate.Name, StringComparer.OrdinalIgnoreCase))
                        AppServices.Settings.RemovedApps.Add(row.App.Candidate.Name);
                }
            }
            await AppServices.SaveSettingsAsync();

            var provisionedOk = true;
            if (PreventReinstall)
            {
                StatusText = Loc.T("appx.provisioning");
                try { provisionedOk = await AppxManager.RemoveProvisionedAsync(rows.Where(r => !r.App.Candidate.IsWinGet).Select(r => r.App.Candidate.Name)); }
                catch (ElevationCancelledException) { provisionedOk = false; }
            }
            var failed = rows.Count - removed;
            StatusText = Loc.T(failed == 0 ? "appx.done" : "appx.done-errors", removed, failed) +
                         (PreventReinstall && !provisionedOk ? " " + Loc.T("appx.provisioned-failed") : "");
        }
        finally
        {
            IsBusy = false;
            SelectedCount = Rows.Count(r => r.IsSelected && !r.IsRemoved);
        }
    }

    /// <summary>Quita los programas de una configuración importada (los que sigan instalados).</summary>
    public async Task<int> RemoveListedAsync(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0) return 0;
        await EnsureLoadedAsync();
        var rows = Rows.Where(r => !r.IsRemoved && names.Contains(r.App.Candidate.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (rows.Count == 0) return 0;
        foreach (var r in rows) r.IsSelected = true;
        var message = Loc.T("appx.confirm.many", rows.Count) + "\n\n" + string.Join("\n", rows.Select(r => "• " + r.Title));
        if (!await Dialogs.ConfirmAsync(Loc.T("appx.remove-selected"), message, Loc.T("appx.remove")))
            return 0;
        await RemoveRowsAsync(rows);
        return rows.Count(r => r.IsRemoved);
    }

    [RelayCommand]
    private void Reinstall(DebloatRow row) => Dialogs.OpenUri(AppxManager.StoreSearchUri(row.App.Candidate));

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        var config = new MyConfig { Parts = [MyConfig.PartDebloat], RemoveApps = SavedList() };
        StatusText = await ConfigFiles.SaveAsync(config, "section.file.debloat") ?? StatusText;
    }

    [RelayCommand]
    private async Task LoadConfigAsync()
    {
        var config = await ConfigFiles.LoadAsync();
        if (config is null) return;
        if (config.RemoveApps.Count == 0)
        {
            StatusText = Loc.T("section.empty");
            return;
        }
        AppServices.Settings.DebloatSelection = [.. config.RemoveApps];
        AppServices.SaveSettingsSoon();
        ApplySavedSelection();
        StatusText = Loc.T("debloat.loaded", config.RemoveApps.Count);
    }
}
