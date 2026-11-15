using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Core.Localization;
using WindowsReborn.Tweaks;
using Wpf.Ui.Controls;

namespace WindowsReborn.App.ViewModels;

public sealed record CategoryFilter(string Key, string Label, string Symbol)
{
    public override string ToString() => Label;
}

/// <summary>Un ajuste en pantalla con su estado real.</summary>
public partial class TweakItem : ObservableObject
{
    private readonly TweaksViewModel _owner;
    private bool _syncing;

    public TweakItem(TweakDefinition definition, TweaksViewModel owner)
    {
        Definition = definition;
        _owner = owner;
        var text = definition.Text(Loc.Instance.Language);
        Title = text.Title;
        Description = text.Description;
        Warning = text.Warning;
        Choices = definition.Kind == TweakKind.Choice
            ? definition.Options.Select(o => new Option<string>(o, text.Choices.GetValueOrDefault(o, o))).ToList()
            : [];
    }

    public TweakDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Title { get; }
    public string Description { get; }
    public string? Warning { get; }
    public IReadOnlyList<Option<string>> Choices { get; }
    public RiskLevel Risk => Definition.Risk;
    public string RiskLabel => Loc.T($"risk.{(int)Definition.Risk}");
    public string CategoryLabel => Loc.T($"tweakcat.{Definition.Category.ToString().ToLowerInvariant()}");
    public bool IsToggle => Definition.Kind == TweakKind.Toggle;
    public bool IsChoice => Definition.Kind == TweakKind.Choice;
    public bool IsInfo => Definition.Kind == TweakKind.Info;
    public bool RequiresAdmin => Definition.RequiresAdmin;
    public string? RestartLabel => Definition.Restart == RestartNeed.None ? null : Loc.T($"restart.{Definition.Restart.ToString().ToLowerInvariant()}");
    public string SearchText => $"{Title} {Description} {Id} {CategoryLabel}";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSupported), nameof(SupportText), nameof(CanRevert), nameof(IsCustom))]
    private TweakStatus _status = new(Support.Supported, null, false);

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private Option<string>? _selectedChoice;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEnabled))] private bool _isBusy;

    public bool IsSupported => Status.IsSupported;
    public bool IsEnabled => IsSupported && !IsBusy;
    public bool IsCustom => Status.IsCustom;
    public bool CanRevert => Status.HasOriginal;
    public string? SupportText => Status.Support switch
    {
        Support.WrongBuild => Loc.T("tweak.unsupported-build"),
        Support.Missing => Loc.T("tweak.missing-component"),
        _ => Status.IsCustom ? Loc.T("tweak.custom-value") : null,
    };

    public void Refresh(TweakStatus status)
    {
        _syncing = true;
        Status = status;
        IsOn = status.CurrentOption == TweakDefinition.On;
        SelectedChoice = Choices.FirstOrDefault(c => c.Value == status.CurrentOption);
        _syncing = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_syncing) _ = _owner.ChangeAsync(this, value ? TweakDefinition.On : TweakDefinition.Off);
    }

    partial void OnSelectedChoiceChanged(Option<string>? value)
    {
        if (!_syncing && value is not null) _ = _owner.ChangeAsync(this, value.Value);
    }

    [RelayCommand]
    private Task RevertAsync() => _owner.RevertAsync(this);

    [RelayCommand]
    private void Open()
    {
        if (Definition.OpenUri is { } uri) Dialogs.OpenUri(uri);
    }
}

public partial class StartupItem(StartupEntry entry, TweaksViewModel owner) : ObservableObject
{
    private bool _syncing;
    public StartupEntry Entry { get; private set; } = entry;
    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string LocationLabel => Loc.T("startup.loc." + Entry.Location.ToString().ToLowerInvariant());
    public bool RequiresAdmin => Entry.RequiresAdmin;
    public bool CanToggle => !Entry.IsProtected;
    [ObservableProperty] private bool _isEnabled = entry.Enabled;
    [ObservableProperty] private bool _isBusy;

    public void Sync(StartupEntry e)
    {
        Entry = e;
        _syncing = true;
        IsEnabled = e.Enabled;
        _syncing = false;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_syncing) _ = owner.SetStartupAsync(this, value);
    }
}

public partial class AppxItem(InstalledAppx app) : ObservableObject
{
    public InstalledAppx App { get; } = app;
    public string Title => App.Candidate.Title(Loc.Instance.Language);
    public string PackageName => App.Candidate.Name;
    public string RiskLabel => Loc.T($"risk.{App.Candidate.Risk}");
    public RiskLevel Risk => (RiskLevel)App.Candidate.Risk;
    public string? Warning => App.Candidate.Warning(Loc.Instance.Language);
    [ObservableProperty] private bool _isRemoved;
    [ObservableProperty] private bool _isBusy;
}

/// <summary>
/// Centro de ajustes. Una instancia para PersonalizaciÃ³n y otra para Privacidad;
/// comparten el motor, el diario y las preferencias.
/// </summary>
public partial class TweaksViewModel : ObservableObject
{
    private static readonly TweakCategory[] PersonalizationCategories =
        [TweakCategory.Explorer, TweakCategory.Taskbar, TweakCategory.Start, TweakCategory.Appearance, TweakCategory.Apps, TweakCategory.System];
    private static readonly TweakCategory[] PrivacyCategories =
        [TweakCategory.Privacy, TweakCategory.Permissions, TweakCategory.Search];

    public const string AllKey = "all";
    public const string StartupKey = "startup";
    public const string AppxKey = "appx";

    private readonly ICollectionView _view;
    private bool _appxLoaded;

    public TweaksViewModel(string page)
    {
        Page = page;
        IsPrivacy = page == "privacy";
        var cats = IsPrivacy ? PrivacyCategories : PersonalizationCategories;
        foreach (var t in TweakCatalog.All.Where(t => cats.Contains(t.Category)))
            Items.Add(new TweakItem(t, this));

        Categories.Add(new CategoryFilter(AllKey, Loc.T("tweakcat.all"), "Grid24"));
        foreach (var c in cats)
            Categories.Add(new CategoryFilter(c.ToString().ToLowerInvariant(), Loc.T($"tweakcat.{c.ToString().ToLowerInvariant()}"), SymbolFor(c)));
        if (!IsPrivacy)
        {
            Categories.Add(new CategoryFilter(StartupKey, Loc.T("tweakcat.startup"), "Rocket24"));
            Categories.Add(new CategoryFilter(AppxKey, Loc.T("tweakcat.appx"), "AppsList24"));
        }
        RiskFilters =
        [
            new(0, Loc.T("risk.all")),
            new(1, Loc.T("risk.1")),
            new(2, Loc.T("risk.2")),
            new(3, Loc.T("risk.3")),
        ];
        _selectedRisk = RiskFilters[0];
        _selectedCategory = Categories[0];

        _view = CollectionViewSource.GetDefaultView(Items);
        _view.Filter = o => o is TweakItem t && Matches(t);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TweakItem.CategoryLabel)));
        RefreshAll();
        AppServices.ConfigurationReplaced += (_, _) => Application.Current.Dispatcher.Invoke(RefreshAll);
    }

    public string Page { get; }
    public bool IsPrivacy { get; }
    public string Title => Loc.T(IsPrivacy ? "privacy.title" : "personalization.title");
    public string Subtitle => Loc.T(IsPrivacy ? "privacy.subtitle" : "personalization.subtitle");
    public string WindowsText => Core.Compatibility.WindowsVersion.Current.FriendlyName;

    public ObservableCollection<TweakItem> Items { get; } = [];
    public ICollectionView View => _view;
    public ObservableCollection<CategoryFilter> Categories { get; } = [];
    public IReadOnlyList<Option<int>> RiskFilters { get; }
    public ObservableCollection<StartupItem> StartupItems { get; } = [];
    public ObservableCollection<AppxItem> AppxItems { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsTweakList), nameof(IsStartupList), nameof(IsAppxList))]
    private CategoryFilter _selectedCategory;
    [ObservableProperty] private Option<int> _selectedRisk;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private int _changedCount;

    [ObservableProperty] private bool _statusVisible;
    [ObservableProperty] private InfoBarSeverity _statusSeverity;
    [ObservableProperty] private string _statusTitle = "";
    [ObservableProperty] private string _statusMessage = "";

    [ObservableProperty] private RestartNeed _pendingRestart;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _appxLoading;

    public bool IsTweakList => SelectedCategory.Key is not (StartupKey or AppxKey);
    public bool IsStartupList => SelectedCategory.Key == StartupKey;
    public bool IsAppxList => SelectedCategory.Key == AppxKey;
    public bool HasPendingRestart => PendingRestart != RestartNeed.None;
    public bool NeedsExplorerRestart => PendingRestart == RestartNeed.Explorer;
    public string PendingRestartText => PendingRestart == RestartNeed.None ? "" : Loc.T("restart.pending." + PendingRestart.ToString().ToLowerInvariant());

    partial void OnPendingRestartChanged(RestartNeed value)
    {
        OnPropertyChanged(nameof(HasPendingRestart));
        OnPropertyChanged(nameof(NeedsExplorerRestart));
        OnPropertyChanged(nameof(PendingRestartText));
    }

    partial void OnSelectedCategoryChanged(CategoryFilter value)
    {
        _view.Refresh();
        UpdateCounts();
        if (value.Key == StartupKey) LoadStartup();
        if (value.Key == AppxKey && !_appxLoaded) _ = LoadAppxAsync();
    }

    partial void OnSelectedRiskChanged(Option<int> value)
    {
        _view.Refresh();
        UpdateCounts();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (value.Length > 0 && !IsTweakList)
            SelectedCategory = Categories[0];
        _view.Refresh();
        UpdateCounts();
    }

    private bool Matches(TweakItem t)
    {
        if (SelectedCategory.Key != AllKey && t.Definition.Category.ToString().ToLowerInvariant() != SelectedCategory.Key)
            return false;
        if (SelectedRisk.Value > 0 && (int)t.Risk != SelectedRisk.Value)
            return false;
        if (SearchText.Length > 0)
        {
            var words = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.All(w => t.SearchText.Contains(w, StringComparison.CurrentCultureIgnoreCase));
        }
        return true;
    }

    private void UpdateCounts()
    {
        VisibleCount = Items.Count(Matches);
        ChangedCount = Items.Count(i => i.CanRevert);
        RevertAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RefreshAll()
    {
        foreach (var item in Items)
            item.Refresh(AppServices.Tweaks.Evaluate(item.Definition));
        UpdateCounts();
        if (IsStartupList) LoadStartup();
    }

    private void Refresh(TweakItem item)
    {
        item.Refresh(AppServices.Tweaks.Evaluate(item.Definition));
        UpdateCounts();
    }

    // ----- Aplicar y revertir -----

    internal async Task ChangeAsync(TweakItem item, string option)
    {
        // Apagar un conmutador = volver a los valores originales registrados.
        if (item.IsToggle && option == TweakDefinition.Off)
        {
            await RevertAsync(item);
            return;
        }

        var restorePoint = false;
        if (item.Risk >= RiskLevel.Moderate)
        {
            var decision = await ConfirmChangeAsync([(item, option)]);
            if (decision is null)
            {
                Refresh(item);
                return;
            }
            restorePoint = decision.Value;
        }
        await RunAsync([(item, option)], restorePoint);
    }

    internal async Task RevertAsync(TweakItem item)
    {
        item.IsBusy = true;
        try
        {
            var result = await AppServices.Tweaks.RevertAsync(item.Definition, false);
            await AfterChangeAsync([(item, result)]);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            item.IsBusy = false;
            Refresh(item);
        }
    }

    private async Task RunAsync(IReadOnlyList<(TweakItem Item, string Option)> changes, bool restorePoint)
    {
        foreach (var (i, _) in changes) i.IsBusy = true;
        IsBusy = true;
        try
        {
            var results = await AppServices.Tweaks.ApplyAsync(changes.Select(c => (c.Item.Definition, c.Option)).ToList(), restorePoint);
            await AfterChangeAsync(changes.Select(c => (c.Item, results.First(r => r.TweakId == c.Item.Id))).ToList());
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
            foreach (var (i, _) in changes)
            {
                i.IsBusy = false;
                Refresh(i);
            }
        }
    }

    private async Task AfterChangeAsync(IReadOnlyList<(TweakItem Item, TweakResult Result)> results)
    {
        var prefs = AppServices.TweakPreferences.Selections;
        foreach (var (item, r) in results)
        {
            if (r.Outcome == TweakOutcome.Applied)
                prefs[item.Id] = AppServices.Tweaks.Evaluate(item.Definition).CurrentOption ?? "";
            else if (r.Outcome == TweakOutcome.Reverted)
                prefs.Remove(item.Id);
            if (r.Outcome is TweakOutcome.Applied or TweakOutcome.Reverted && item.Definition.Restart > PendingRestart)
                PendingRestart = item.Definition.Restart;
        }
        await AppServices.SaveTweakPreferencesAsync();

        var failed = results.Where(r => r.Result.Outcome is TweakOutcome.Failed or TweakOutcome.Unsupported).ToList();
        var done = results.Count(r => r.Result.Outcome is TweakOutcome.Applied or TweakOutcome.Reverted);
        if (failed.Count > 0)
            Show(InfoBarSeverity.Warning, Loc.T("tweak.result.failed-title"),
                Loc.T("tweak.result.failed", string.Join(", ", failed.Select(f => f.Item.Title))));
        else if (done > 0)
            Show(InfoBarSeverity.Success, Loc.T("tweak.result.done-title"),
                results.Count == 1 ? results[0].Item.Title : Loc.T("tweak.result.done", done));
    }

    private void ShowError(Exception ex)
    {
        switch (ex)
        {
            case ElevationCancelledException:
                Show(InfoBarSeverity.Informational, Loc.T("tweak.elevation-cancelled"), Loc.T("tweak.nothing-changed"));
                break;
            case RestorePointException rp:
                Show(InfoBarSeverity.Error, Loc.T("tweak.restore-point-failed"), Loc.T("tweak.restore-point-failed-detail") + " " + rp.Message);
                break;
            default:
                Show(InfoBarSeverity.Error, Loc.T("tweak.error"), ex.Message);
                break;
        }
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        StatusVisible = true;
    }

    /// <summary>
    /// ConfirmaciÃ³n de cambios moderados o avanzados: quÃ© hace, consecuencias, quÃ©
    /// valores se escriben y cÃ³mo se revierte. Devuelve si crear punto de
    /// restauraciÃ³n, o <c>null</c> si se cancela.
    /// </summary>
    private static async Task<bool?> ConfirmChangeAsync(IReadOnlyList<(TweakItem Item, string Option)> changes)
    {
        var maxRisk = changes.Max(c => c.Item.Risk);
        var panel = new System.Windows.Controls.StackPanel { MaxWidth = 560 };
        foreach (var (item, option) in changes)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = $"{item.Title}  Â·  {item.RiskLabel}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) });
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = item.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
            if (item.Warning is not null)
                panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "âš  " + item.Warning, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"] });
            if (changes.Count == 1)
            {
                var values = string.Join(Environment.NewLine, item.Definition.Settings.Select(s =>
                {
                    var v = s.Values.GetValueOrDefault(option);
                    return $"{(s.Hive == RegHive.LocalMachine ? "HKLM" : "HKCU")}\\{s.Key}\\{(s.Name.Length == 0 ? Loc.T("tweak.default-value") : s.Name)} = {v ?? Loc.T("tweak.value-deleted")}";
                }));
                panel.Children.Add(new System.Windows.Controls.TextBlock { Text = Loc.T("tweak.confirm-changes"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
                panel.Children.Add(new System.Windows.Controls.TextBox { Text = values, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto });
            }
        }
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = Loc.T("tweak.confirm-revert"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.85 });
        if (changes.Any(c => c.Item.RequiresAdmin))
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = Loc.T("tweak.confirm-admin"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Opacity = 0.85 });
        var check = new System.Windows.Controls.CheckBox { Content = Loc.T("tweak.create-restore-point"), IsChecked = maxRisk == RiskLevel.Advanced, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(check);

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = Loc.T(maxRisk == RiskLevel.Advanced ? "tweak.confirm-title-advanced" : "tweak.confirm-title"),
            Content = new System.Windows.Controls.ScrollViewer { Content = panel, MaxHeight = 460, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto },
            PrimaryButtonText = Loc.T(maxRisk == RiskLevel.Advanced ? "tweak.apply-anyway" : "tweak.apply"),
            CloseButtonText = Loc.T("common.cancel"),
            PrimaryButtonAppearance = maxRisk == RiskLevel.Advanced ? ControlAppearance.Caution : ControlAppearance.Primary,
            Owner = Application.Current.MainWindow,
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary ? check.IsChecked == true : null;
    }

    /// <summary>Privacidad: aplica de una vez los ajustes seguros (nivel 1) visibles que aÃºn no estÃ¡n activos.</summary>
    [RelayCommand]
    private async Task ApplyRecommendedAsync()
    {
        var targets = Items.Where(i => Matches(i) && i.IsToggle && i.IsSupported && i.Risk == RiskLevel.Safe && !i.IsOn).ToList();
        if (targets.Count == 0)
        {
            Show(InfoBarSeverity.Informational, Loc.T("tweak.recommended-none"), "");
            return;
        }
        var list = string.Join(Environment.NewLine, targets.Select(t => "â€¢ " + t.Title));
        if (!await Dialogs.ConfirmAsync(Loc.T("tweak.recommended-title"), Loc.T("tweak.recommended-message", targets.Count, list), Loc.T("tweak.apply")))
            return;
        await RunAsync(targets.Select(t => (t, TweakDefinition.On)).ToList(), false);
    }

    private bool CanRevertAll() => ChangedCount > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private async Task RevertAllAsync()
    {
        var targets = Items.Where(i => i.CanRevert).ToList();
        var list = string.Join(Environment.NewLine, targets.Select(t => "â€¢ " + t.Title));
        if (!await Dialogs.ConfirmAsync(Loc.T("tweak.revert-all-title"), Loc.T("tweak.revert-all-message", targets.Count, list), Loc.T("tweak.revert-all")))
            return;
        IsBusy = true;
        var results = new List<(TweakItem, TweakResult)>();
        try
        {
            foreach (var t in targets)
                results.Add((t, await AppServices.Tweaks.RevertAsync(t.Definition, false)));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
        await AfterChangeAsync(results);
        RefreshAll();
    }

    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        if (!await Dialogs.ConfirmAsync(Loc.T("restart.explorer-title"), Loc.T("restart.explorer-message"), Loc.T("restart.explorer-confirm")))
            return;
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer").Where(p => p.SessionId == session))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        }
        await Task.Delay(1500);
        if (!Process.GetProcessesByName("explorer").Any(p => p.SessionId == session))
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        PendingRestart = RestartNeed.None;
    }

    [RelayCommand]
    private void DismissStatus() => StatusVisible = false;

    [RelayCommand]
    private void OpenSystemProtection() => Dialogs.RunSystemTool("SystemPropertiesProtection.exe");

    // ----- Aplicaciones de inicio -----

    [RelayCommand]
    private void LoadStartup()
    {
        var entries = AppServices.Startup.List();
        StartupItems.Clear();
        foreach (var e in entries)
            StartupItems.Add(new StartupItem(e, this));
    }

    internal async Task SetStartupAsync(StartupItem item, bool enabled)
    {
        item.IsBusy = true;
        try
        {
            await AppServices.Startup.SetEnabledAsync(item.Entry, enabled);
            Show(InfoBarSeverity.Success, Loc.T(enabled ? "startup.enabled" : "startup.disabled"), item.Name);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            item.IsBusy = false;
            var fresh = AppServices.Startup.List().FirstOrDefault(e => e.Name == item.Name && e.Location == item.Entry.Location);
            if (fresh is not null) item.Sync(fresh);
        }
    }

    [RelayCommand]
    private void OpenStartupSettings() => Dialogs.OpenUri("ms-settings:startupapps");

    // ----- Aplicaciones preinstaladas -----

    [RelayCommand]
    private async Task LoadAppxAsync()
    {
        AppxLoading = true;
        try
        {
            var list = await AppServices.Appx.ListInstalledAsync();
            AppxItems.Clear();
            foreach (var a in list) AppxItems.Add(new AppxItem(a));
            _appxLoaded = true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            AppxLoading = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAppxAsync(AppxItem? item)
    {
        if (item is null || item.IsRemoved) return;
        var message = Loc.T("appx.remove-message", item.Title) + (item.Warning is null ? "" : Environment.NewLine + Environment.NewLine + "âš  " + item.Warning);
        if (!await Dialogs.ConfirmAsync(Loc.T("appx.remove-title"), message, Loc.T("appx.remove"), danger: item.Risk == RiskLevel.Advanced))
            return;
        item.IsBusy = true;
        try
        {
            if (await AppServices.Appx.RemoveAsync(item.App))
            {
                item.IsRemoved = true;
                Show(InfoBarSeverity.Success, Loc.T("appx.removed"), item.Title);
            }
            else
            {
                Show(InfoBarSeverity.Error, Loc.T("appx.remove-failed"), item.Title);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private void ReinstallAppx(AppxItem? item)
    {
        if (item is not null) Dialogs.OpenUri(AppxManager.StoreSearchUri(item.App.Candidate));
    }

    private static string SymbolFor(TweakCategory c) => c switch
    {
        TweakCategory.Explorer => "FolderOpen24",
        TweakCategory.Taskbar => "PanelBottom20",
        TweakCategory.Start => "Board24",
        TweakCategory.Appearance => "PaintBrush24",
        TweakCategory.Apps => "Apps24",
        TweakCategory.System => "Wrench24",
        TweakCategory.Privacy => "ShieldCheckmark24",
        TweakCategory.Permissions => "LockClosed24",
        TweakCategory.Search => "Search24",
        _ => "Settings24",
    };
}
