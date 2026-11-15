using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Backup;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Localization;
using WindowsReborn.Restore;
using WindowsReborn.Tweaks;
using Wpf.Ui.Controls;

namespace WindowsReborn.App.ViewModels;

public enum StepState
{
    Pending,
    Done,
    Skipped,
}

public partial class WizardStep(int index, string key) : ObservableObject
{
    public int Index { get; } = index;
    public string Key { get; } = key;
    public string Number => (Index + 1).ToString();
    public string Title => Loc.T("wizard." + Key);
    [ObservableProperty] private StepState _state;
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>Preferencia de ajuste guardada en el backup que se puede volver a aplicar.</summary>
public partial class PreferenceItem : ObservableObject
{
    public required TweakDefinition Definition { get; init; }
    public required string Option { get; init; }
    public string Title => Definition.Text(Loc.Instance.Language).Title;
    public string OptionLabel => Definition.Kind == TweakKind.Choice
        ? Definition.Text(Loc.Instance.Language).Choices.GetValueOrDefault(Option, Option)
        : Loc.T(Option == TweakDefinition.On ? "common.on" : "common.off");
    public RiskLevel Risk => Definition.Risk;
    public string RiskLabel => Loc.T($"risk.{(int)Definition.Risk}");
    public required TweakStatus Status { get; init; }
    public bool IsSupported => Status.IsSupported;
    public bool AlreadyApplied => Status.CurrentOption == Option;
    public string StateText => !IsSupported ? Loc.T("tweak.unsupported-build") : AlreadyApplied ? Loc.T("wizard.pref-already") : Loc.T("wizard.pref-pending");
    [ObservableProperty] private bool _isSelected;
}

public partial class RestoreViewModel
{
    public static readonly string[] StepKeys = ["open", "verify", "select", "files", "config", "programs", "settings", "report"];

    public ObservableCollection<WizardStep> Steps { get; } = [.. StepKeys.Select((k, i) => new WizardStep(i, k))];
    public ObservableCollection<PreferenceItem> Preferences { get; } = [];
    public ObservableCollection<string> FinalReport { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentKey), nameof(IsFirstStep), nameof(IsLastStep), nameof(StepTitle))]
    private int _currentStep;

    [ObservableProperty] private string _programsSummary = "";
    [ObservableProperty] private string _preferencesStatus = "";
    [ObservableProperty] private bool _createRestorePoint;

    public string CurrentKey => StepKeys[CurrentStep];
    public bool IsFirstStep => CurrentStep == 0;
    public bool IsLastStep => CurrentStep == StepKeys.Length - 1;
    public string StepTitle => Loc.T("wizard.step-of", CurrentStep + 1, StepKeys.Length, Steps[CurrentStep].Title);

    partial void OnCurrentStepChanged(int value)
    {
        foreach (var s in Steps) s.IsCurrent = s.Index == value;
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        switch (CurrentKey)
        {
            case "programs":
                _ = LoadProgramsSummaryAsync();
                break;
            case "settings":
                _ = LoadPreferencesAsync();
                break;
            case "report":
                BuildFinalReport();
                break;
        }
    }

    private void Mark(string key, StepState state)
    {
        var step = Steps.First(s => s.Key == key);
        if (step.State != StepState.Done || state == StepState.Done) step.State = state;
    }

    private bool CanNext() => !IsLastStep && !IsBusy && (CurrentStep > 0 || IsOpen);
    private bool CanBack() => !IsFirstStep && !IsBusy;
    private bool CanSkip() => CanNext() && CurrentStep > 0;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        if (Steps[CurrentStep].State == StepState.Pending && CurrentKey is "programs") Mark("programs", StepState.Done);
        if (CurrentKey == "select") Analyze();
        CurrentStep++;
    }

    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void Skip()
    {
        Mark(CurrentKey, StepState.Skipped);
        CurrentStep++;
    }

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back() => CurrentStep--;

    [RelayCommand]
    private void GoToStep(WizardStep? step)
    {
        if (step is null || IsBusy) return;
        if (step.Index > 0 && !IsOpen) return;
        CurrentStep = step.Index;
    }

    private void ResetWizard()
    {
        foreach (var s in Steps) s.State = StepState.Pending;
        Preferences.Clear();
        FinalReport.Clear();
        PreferencesStatus = "";
        CurrentStep = 0;
        Steps[0].IsCurrent = true;
    }

    /// <summary>Aplica los destinos de restauración guardados para carpetas personalizadas.</summary>
    private async Task ApplySavedTargetsAsync()
    {
        var selection = await new ProfileStore(AppServices.Paths.ConfigDirectory).LoadSelectionAsync();
        if (selection is null) return;
        foreach (var item in Categories.Where(c => c.Target.Category.Kind == "customFolder"))
        {
            var cat = item.Target.Category;
            var entry = selection.CustomFolders.FirstOrDefault(f =>
                (f.Root == cat.Root && string.Equals(f.SubPath, cat.SubPath, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(f.Name, cat.Name, StringComparison.OrdinalIgnoreCase));
            if (entry?.RestoreTarget is { Length: > 0 } target)
                item.TargetFolder = target;
            else if (string.IsNullOrWhiteSpace(item.TargetFolder) && entry?.Resolve() is { } resolved)
                item.TargetFolder = resolved;
        }
    }

    private async Task LoadProgramsSummaryAsync()
    {
        try
        {
            var library = await AppServices.ProgramStore.LoadAsync();
            ProgramsSummary = library.Programs.Count == 0
                ? Loc.T("wizard.programs-empty")
                : Loc.T("wizard.programs-count", library.Programs.Count);
        }
        catch
        {
            ProgramsSummary = "";
        }
    }

    /// <summary>Preferencias de ajustes: del backup (tweaks.json) o de la configuración ya importada.</summary>
    private async Task LoadPreferencesAsync()
    {
        Preferences.Clear();
        TweakPreferences? prefs = null;
        if (_reader is not null && _manifest is not null)
        {
            var package = RestoreEngine.ReadConfiguration(_reader, _manifest);
            if (package?.Files.TryGetValue(TweakPreferences.FileName, out var bytes) == true)
            {
                try { prefs = JsonSerializer.Deserialize<TweakPreferences>(bytes, Json.Options); }
                catch (JsonException) { }
            }
        }
        if (prefs is null || prefs.Selections.Count == 0)
        {
            await AppServices.ReloadTweakPreferencesAsync();
            prefs = AppServices.TweakPreferences;
        }

        foreach (var (id, option) in prefs.Selections)
        {
            if (TweakCatalog.Find(id) is not { } def) continue;
            var status = AppServices.Tweaks.Evaluate(def);
            var item = new PreferenceItem { Definition = def, Option = option, Status = status };
            item.IsSelected = item.IsSupported && !item.AlreadyApplied && def.Risk == RiskLevel.Safe;
            item.PropertyChanged += (_, _) => ApplyPreferencesCommand.NotifyCanExecuteChanged();
            Preferences.Add(item);
        }
        PreferencesStatus = Preferences.Count == 0 ? Loc.T("wizard.prefs-none") : Loc.T("wizard.prefs-count", Preferences.Count);
        ApplyPreferencesCommand.NotifyCanExecuteChanged();
    }

    private bool CanApplyPreferences() => Preferences.Any(p => p.IsSelected && p.IsSupported && !p.AlreadyApplied) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanApplyPreferences))]
    private async Task ApplyPreferencesAsync()
    {
        var chosen = Preferences.Where(p => p.IsSelected && p.IsSupported && !p.AlreadyApplied).ToList();
        var list = string.Join(Environment.NewLine, chosen.Select(p => $"• {p.Title}: {p.OptionLabel} ({p.RiskLabel})"));
        if (!await Dialogs.ConfirmAsync(Loc.T("wizard.prefs-confirm-title"), Loc.T("wizard.prefs-confirm", chosen.Count, list), Loc.T("tweak.apply"),
                danger: chosen.Any(c => c.Risk == RiskLevel.Advanced)))
            return;

        IsBusy = true;
        try
        {
            var results = await AppServices.Tweaks.ApplyAsync(chosen.Select(c => (c.Definition, c.Option)).ToList(), CreateRestorePoint);
            foreach (var r in results.Where(r => r.Outcome == TweakOutcome.Applied))
                AppServices.TweakPreferences.Selections[r.TweakId] = chosen.First(c => c.Definition.Id == r.TweakId).Option;
            await AppServices.SaveTweakPreferencesAsync();
            var applied = results.Count(r => r.Outcome is TweakOutcome.Applied or TweakOutcome.Unchanged);
            var failed = results.Count(r => r.Outcome is TweakOutcome.Failed or TweakOutcome.Unsupported);
            PreferencesStatus = Loc.T("wizard.prefs-result", applied, failed);
            Mark("settings", StepState.Done);
            AppServices.RaiseConfigurationReplaced();
        }
        catch (ElevationCancelledException)
        {
            PreferencesStatus = Loc.T("tweak.elevation-cancelled");
        }
        catch (Exception ex)
        {
            PreferencesStatus = Loc.T("tweak.error") + ": " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        await LoadPreferencesAsync();
    }

    public bool HasLastBackup => AppServices.Settings.LastBackupPath is { } p && (File.Exists(p) || Directory.Exists(p));

    [RelayCommand]
    private async Task OpenLastBackupAsync()
    {
        if (AppServices.Settings.LastBackupPath is { } p) await OpenAsync(p);
    }

    [RelayCommand]
    private Task ImportConfigOnlyAsync() => ViewModelLocator.Home.ImportConfigCommand.ExecuteAsync(null);

    [RelayCommand]
    private void OpenPersonalization() => MainWindow.Current?.Navigate("personalization");

    private void BuildFinalReport()
    {
        FinalReport.Clear();
        foreach (var s in Steps.Take(Steps.Count - 1))
            FinalReport.Add($"{s.Title}: {Loc.T("wizard.state." + s.State.ToString().ToLowerInvariant())}");
        if (ResultVisible) FinalReport.Add(ResultMessage);
        if (ConfigStatus.Length > 0) FinalReport.Add(ConfigStatus);
        if (PreferencesStatus.Length > 0) FinalReport.Add(PreferencesStatus);
        Mark("report", StepState.Done);
    }

    [RelayCommand]
    private async Task SaveFinalReportAsync()
    {
        var path = Path.Combine(AppServices.Paths.ReportsDirectory, $"restore-summary-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await Json.WriteFileAtomicAsync(path, new
        {
            Backup = Path.GetFileName(BackupPath.TrimEnd('\\')),
            CreatedUtc = DateTime.UtcNow,
            Steps = Steps.Select(s => new { s.Key, State = s.State.ToString() }),
            Lines = FinalReport,
        });
        Dialogs.OpenFolder(path);
    }
}
