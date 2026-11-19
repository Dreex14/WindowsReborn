using WindowsReborn.Core.Paths;
using WindowsReborn.Tweaks;

namespace WindowsReborn.Tests;

/// <summary>
/// Pruebas del motor de ajustes. Todas usan un registro en memoria: ninguna prueba
/// modifica la configuración real de Windows.
/// </summary>
public class TweaksTests
{
    private const int Win11_23H2 = 22631;
    private static TweakDefinition Get(string id) => TweakCatalog.Find(id) ?? throw new InvalidOperationException(id);

    private static (TweakEngine Engine, MemoryRegistry Reg) NewEngine(int build = Win11_23H2)
    {
        var reg = new MemoryRegistry();
        return (new TweakEngine(reg, reg, new TweakJournal(), build), reg);
    }

    [Fact]
    public void Catalog_is_complete_and_bilingual()
    {
        var all = TweakCatalog.All;
        Assert.True(all.Count >= 60, $"only {all.Count} tweaks");
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var t in all)
        {
            foreach (var lang in new[] { "es", "en" })
            {
                Assert.True(t.Texts.ContainsKey(lang), $"{t.Id} lacks {lang}");
                Assert.False(string.IsNullOrWhiteSpace(t.Texts[lang].Title), $"{t.Id} {lang} title");
                Assert.False(string.IsNullOrWhiteSpace(t.Texts[lang].Description), $"{t.Id} {lang} desc");
                if (t.Kind == TweakKind.Choice)
                    Assert.All(t.Options, o => Assert.True(t.Texts[lang].Choices.ContainsKey(o), $"{t.Id} {lang} choice {o}"));
                if (t.Risk == RiskLevel.Advanced && t.Kind != TweakKind.Info)
                    Assert.False(string.IsNullOrWhiteSpace(t.Texts[lang].Warning), $"{t.Id} advanced without warning");
            }
            if (t.Kind == TweakKind.Info)
            {
                Assert.Empty(t.Settings);
                Assert.NotNull(t.OpenUri);
                continue;
            }
            Assert.NotEmpty(t.Settings);
            // Cada opción controla al menos un valor (un valor puede no importar en alguna opción).
            Assert.All(t.Options, o => Assert.True(t.Settings.Any(s => s.Values.ContainsKey(o)), $"{t.Id} option {o} controls nothing"));
            if (t.Kind == TweakKind.Toggle)
                foreach (var s in t.Settings)
                    Assert.All(t.Options, o => Assert.True(s.Values.ContainsKey(o), $"{t.Id} {s.Name} lacks {o}"));
        }
    }

    [Fact]
    public void Catalog_never_touches_essential_protections()
    {
        string[] forbidden = ["Windows Defender", "WindowsUpdate", "SmartScreen", "EnableLUA", "ConsentPromptBehavior", "SharedAccess", "MpsSvc", "WinDefend", "wuauserv", "BootExecute", "Winlogon"];
        foreach (var t in TweakCatalog.All)
            foreach (var s in t.Settings)
                Assert.DoesNotContain(forbidden, f => (s.Key + "\\" + s.Name).Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_admin_operation_is_accepted_by_the_elevated_helper()
    {
        // El proceso con permisos de administrador rechaza lo que no esté en el catálogo:
        // cualquier operación de cualquier opción tiene que pasar ese filtro o el ajuste fallaría.
        var rejected = new List<string>();
        foreach (var t in TweakCatalog.All.Where(t => t.Kind != TweakKind.Info))
            foreach (var option in t.Options)
                foreach (var op in TweakEngine.OpsFor(t, option).Where(SystemRegistryWriter.NeedsElevation))
                    if (!TweakCatalog.IsAllowed(op))
                        rejected.Add($"{t.Id}/{option}: {op.Type} {op.Hive}\\{op.Key}\\{op.Name}");
        Assert.True(rejected.Count == 0, "Rejected: " + string.Join("; ", rejected));
    }

    [Fact]
    public async Task Permission_has_three_states_matching_both_windows_switches()
    {
        var (engine, reg) = NewEngine();
        var t = Get("permissions.contacts");
        // Sin valores escritos, Windows lo trata como permitido.
        Assert.Equal("allow", engine.Evaluate(t).CurrentOption);

        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(t, "apps", false)).Outcome);
        Assert.Equal("Deny", reg.Read(RegHive.CurrentUser, t.Settings[1].Key, "Value")!.Data);
        Assert.Equal("Allow", reg.Read(RegHive.LocalMachine, t.Settings[0].Key, "Value")!.Data);

        // «Acceso a contactos» apagado en Configuración: bloqueado en todo el equipo, sea cual sea el interruptor de usuario.
        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(t, "all", false)).Outcome);
        Assert.Equal("all", engine.Evaluate(t).CurrentOption);
        Assert.Equal("Deny", reg.Read(RegHive.LocalMachine, t.Settings[0].Key, "Value")!.Data);
    }

    [Fact]
    public async Task Toggle_apply_and_revert_restores_missing_value()
    {
        var (engine, reg) = NewEngine();
        var t = Get("explorer.open-this-pc");
        Assert.Equal(TweakDefinition.Off, engine.Evaluate(t).CurrentOption);

        var r = await engine.ApplyAsync(t, TweakDefinition.On, false);
        Assert.Equal(TweakOutcome.Applied, r.Outcome);
        Assert.Equal("1", reg.Read(RegHive.CurrentUser, t.Settings[0].Key, "LaunchTo")!.Data);
        Assert.True(engine.Evaluate(t).HasOriginal);

        var back = await engine.RevertAsync(t, false);
        Assert.Equal(TweakOutcome.Reverted, back.Outcome);
        Assert.Null(reg.Read(RegHive.CurrentUser, t.Settings[0].Key, "LaunchTo"));
        Assert.False(engine.Evaluate(t).HasOriginal);
    }

    [Fact]
    public async Task Revert_returns_recorded_original_not_assumed_default()
    {
        var (engine, reg) = NewEngine();
        var t = Get("explorer.show-hidden");
        reg.Set(RegHive.CurrentUser, t.Settings[0].Key, "Hidden", RegKind.DWord, "7"); // valor raro puesto por otra herramienta
        Assert.True(engine.Evaluate(t).IsCustom);

        await engine.ApplyAsync(t, TweakDefinition.On, false);
        Assert.Equal(TweakDefinition.On, engine.Evaluate(t).CurrentOption);
        await engine.RevertAsync(t, false);
        Assert.Equal("7", reg.Read(RegHive.CurrentUser, t.Settings[0].Key, "Hidden")!.Data);
    }

    [Fact]
    public async Task Choice_keeps_first_original_across_changes()
    {
        var (engine, reg) = NewEngine();
        var t = Get("taskbar.search");
        reg.Set(RegHive.CurrentUser, t.Settings[0].Key, t.Settings[0].Name, RegKind.DWord, "2");
        Assert.Equal("box", engine.Evaluate(t).CurrentOption);

        await engine.ApplyAsync(t, "icon", false);
        await engine.ApplyAsync(t, "hidden", false);
        Assert.Equal("hidden", engine.Evaluate(t).CurrentOption);

        await engine.RevertAsync(t, false);
        Assert.Equal("box", engine.Evaluate(t).CurrentOption);
    }

    [Fact]
    public async Task Classic_menu_revert_deletes_the_created_key()
    {
        var (engine, reg) = NewEngine();
        reg.Set(RegHive.CurrentUser, @"Software\Classes\CLSID", "", RegKind.String, "");
        var t = Get("explorer.classic-context-menu");

        await engine.ApplyAsync(t, TweakDefinition.On, false);
        Assert.True(reg.KeyExists(RegHive.CurrentUser, t.Settings[0].Key));
        Assert.Equal(TweakDefinition.On, engine.Evaluate(t).CurrentOption);

        await engine.RevertAsync(t, false);
        Assert.False(reg.KeyExists(RegHive.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"));
        Assert.True(reg.KeyExists(RegHive.CurrentUser, @"Software\Classes\CLSID"));
    }

    [Fact]
    public async Task Classic_menu_off_without_journal_removes_whole_key()
    {
        var (engine, reg) = NewEngine();
        var t = Get("explorer.classic-context-menu");
        reg.Set(RegHive.CurrentUser, t.Settings[0].Key, "", RegKind.String, ""); // aplicado por otra herramienta
        var r = await engine.RevertAsync(t, false);
        Assert.Equal(TweakOutcome.Reverted, r.Outcome);
        Assert.False(reg.KeyExists(RegHive.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"));
    }

    [Fact]
    public async Task Unsupported_build_is_detected_and_nothing_is_written()
    {
        var (engine, reg) = NewEngine(build: 19045); // Windows 10 22H2
        var t = Get("taskbar.align-left");
        Assert.Equal(Support.WrongBuild, engine.Evaluate(t).Support);
        var r = await engine.ApplyAsync(t, TweakDefinition.On, false);
        Assert.Equal(TweakOutcome.Unsupported, r.Outcome);
        Assert.Empty(reg.Log);
    }

    [Fact]
    public void Missing_service_is_reported_as_missing()
    {
        var (engine, _) = NewEngine();
        Assert.Equal(Support.Missing, engine.Evaluate(Get("system.service-fax")).Support);
    }

    [Fact]
    public async Task Rejected_change_is_rolled_back_and_reported()
    {
        var (engine, reg) = NewEngine();
        var t = Get("privacy.settings-suggestions");
        var key = t.Settings[0].Key;
        reg.Set(RegHive.CurrentUser, key, "SubscribedContent-338393Enabled", RegKind.DWord, "1");
        reg.Set(RegHive.CurrentUser, key, "SubscribedContent-353694Enabled", RegKind.DWord, "1");
        reg.Set(RegHive.CurrentUser, key, "SubscribedContent-353696Enabled", RegKind.DWord, "1");
        reg.Protected.Add("SubscribedContent-353696Enabled"); // Windows rechaza uno de los tres

        var r = await engine.ApplyAsync(t, TweakDefinition.On, false);
        Assert.Equal(TweakOutcome.Failed, r.Outcome);
        Assert.Equal("1", reg.Read(RegHive.CurrentUser, key, "SubscribedContent-338393Enabled")!.Data);
        Assert.False(engine.Evaluate(t).HasOriginal);
    }

    [Fact]
    public async Task Batch_with_restore_point_requests_it_once()
    {
        var (engine, reg) = NewEngine();
        var results = await engine.ApplyAsync([(Get("explorer.show-extensions"), "on"), (Get("explorer.show-hidden"), "on")], createRestorePoint: true);
        Assert.All(results, r => Assert.Equal(TweakOutcome.Applied, r.Outcome));
        Assert.Equal(1, reg.RestorePoints);
    }

    [Fact]
    public async Task Journal_survives_reload()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Root, TweakJournal.FileName);
        var reg = new MemoryRegistry();
        var engine = new TweakEngine(reg, reg, await TweakJournal.LoadAsync(path), Win11_23H2);
        await engine.ApplyAsync(Get("appearance.dark-apps"), TweakDefinition.On, false);

        var reloaded = new TweakEngine(reg, reg, await TweakJournal.LoadAsync(path), Win11_23H2);
        Assert.True(reloaded.Evaluate(Get("appearance.dark-apps")).HasOriginal);
        await reloaded.RevertAsync(Get("appearance.dark-apps"), false);
        Assert.Null(reg.Read(RegHive.CurrentUser, Get("appearance.dark-apps").Settings[0].Key, "AppsUseLightTheme"));
    }

    [Fact]
    public void Elevated_helper_only_accepts_catalog_operations()
    {
        Assert.True(TweakCatalog.IsAllowed(new RegOp(RegOpType.SetValue, RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", RegKind.DWord, "1")));
        Assert.True(TweakCatalog.IsAllowed(new RegOp(RegOpType.DeleteValue, RegHive.LocalMachine, @"Software\Policies\Microsoft\Dsh", "AllowNewsAndInterests")));
        Assert.True(TweakCatalog.IsAllowed(new RegOp(RegOpType.DeleteKey, RegHive.LocalMachine, @"Software\Policies\Microsoft\Dsh")));

        Assert.False(TweakCatalog.IsAllowed(new RegOp(RegOpType.SetValue, RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", RegKind.DWord, "1")));
        Assert.False(TweakCatalog.IsAllowed(new RegOp(RegOpType.SetValue, RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisable8dot3NameCreation", RegKind.DWord, "1")));
        Assert.False(TweakCatalog.IsAllowed(new RegOp(RegOpType.DeleteKey, RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\DiagTrack")));
        Assert.False(TweakCatalog.IsAllowed(new RegOp(RegOpType.DeleteKey, RegHive.LocalMachine, @"Software\Policies")));
    }

    [Fact]
    public async Task Startup_entries_toggle_through_startup_approved()
    {
        using var t = new TempDir();
        var reg = new MemoryRegistry();
        reg.Set(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Demo", RegKind.String, @"C:\Demo\demo.exe --tray");
        var manager = new StartupManager(reg, reg);

        var entry = Assert.Single(manager.List(t.Sub("startup"), t.Sub("common")));
        Assert.True(entry.Enabled);

        await manager.SetEnabledAsync(entry, false);
        Assert.False(Assert.Single(manager.List(t.Root + "\\startup", t.Root + "\\common")).Enabled);
        Assert.NotNull(reg.Read(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Demo")); // la entrada no se borra

        await manager.SetEnabledAsync(entry, true);
        Assert.True(Assert.Single(manager.List(t.Root + "\\startup", t.Root + "\\common")).Enabled);
    }

    [Fact]
    public async Task Windows_security_startup_entry_is_protected_and_unexpanded_commands_stay_intact()
    {
        using var t = new TempDir();
        var reg = new MemoryRegistry();
        reg.Set(RegHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "SecurityHealth", RegKind.String, @"%windir%\system32\SecurityHealthSystray.exe");
        var manager = new StartupManager(reg, reg);
        var entry = Assert.Single(manager.List(t.Sub("s"), t.Sub("c")));
        Assert.True(entry.IsProtected);
        Assert.Equal(@"%windir%\system32\SecurityHealthSystray.exe", entry.Command);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SetEnabledAsync(entry, false));
        Assert.Empty(reg.Log);
    }

    [Fact]
    public void Debloat_list_is_safe_and_complete()
    {
        var all = AppxManager.Candidates;
        Assert.True(all.Count > 100, $"only {all.Count}");
        Assert.Equal(all.Count, all.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Nada esencial: Tienda, WinGet, códecs, marcos, seguridad ni idioma.
        string[] forbidden = ["WindowsStore", "DesktopAppInstaller", "Winget", "SecHealthUI", "VCLibs", "UI.Xaml", "NET.Native", "WindowsAppRuntime",
            "WinAppRuntime", "VideoExtension", "ImageExtension", "WebMediaExtensions", "LanguageExperiencePack", "StorePurchaseApp", "ShellExperienceHost", "RealtekAudio"];
        foreach (var c in all)
        {
            Assert.DoesNotContain(forbidden, f => c.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(c.Es) || string.IsNullOrWhiteSpace(c.En), c.Name);
            Assert.Contains(c.Group, new[] { "microsoft", "third", "oem" });
            if (c.Risk == 3) Assert.False(string.IsNullOrWhiteSpace(c.WarnEs), $"{c.Name} needs a warning");
            if (c.Recommended) Assert.Equal(1, c.Risk);
            if (c.IsWinGet) Assert.DoesNotContain('*', c.Name);
        }
        // Lo útil nunca viene marcado.
        foreach (var keep in new[] { "Microsoft.Windows.Photos", "Microsoft.WindowsCalculator", "Microsoft.ScreenSketch", "Microsoft.OneDrive", "SpotifyAB.SpotifyMusic", "Microsoft.Edge" })
            Assert.False(AppxManager.Find(keep)!.Recommended, keep);
        // Patrones para los paquetes que llevan un prefijo distinto en cada equipo.
        var march = AppxManager.Find("*MarchofEmpires*")!;
        Assert.True(AppxManager.Matches(march, "A278AB0D.MarchofEmpires"));
        Assert.False(AppxManager.Matches(AppxManager.Find("Microsoft.Paint")!, "Microsoft.PaintStudio"));
        var script = AppxManager.ProvisionedScript(["Microsoft.BingNews", "Microsoft.WindowsStore", "Microsoft.OneDrive", "bad'; rm"]);
        Assert.Contains("'Microsoft.BingNews'", script);
        Assert.DoesNotContain("WindowsStore", script);
        Assert.DoesNotContain("OneDrive", script); // va por WinGet, no es un paquete aprovisionado
        Assert.DoesNotContain("rm", script);
    }

    [Fact]
    public void Appx_list_only_accepts_known_packages()
    {
        var json = """[{"Name":"Microsoft.BingWeather","PackageFullName":"Microsoft.BingWeather_4.53.0.0_x64__8wekyb3d8bbwe","Version":"4.53.0.0"},{"Name":"Microsoft.WindowsStore","PackageFullName":"Microsoft.WindowsStore_1.0_x64__8wekyb3d8bbwe","Version":"1"},{"Name":"Microsoft.BingNews","PackageFullName":"bad name; rm","Version":"1"}]""";
        var list = AppxManager.Parse(json);
        var item = Assert.Single(list);
        Assert.Equal("Microsoft.BingWeather", item.Candidate.Name);
        Assert.Single(AppxManager.Parse("""{"Name":"Clipchamp.Clipchamp","PackageFullName":"Clipchamp.Clipchamp_2.0_neutral__yxz26nhyzhsrt","Version":"2"}"""));
        Assert.DoesNotContain(AppxManager.Candidates, c => c.Name is "Microsoft.WindowsStore" or "Microsoft.DesktopAppInstaller" or "Microsoft.SecHealthUI");
    }

    [Fact]
    public async Task Preferences_round_trip()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Root, TweakPreferences.FileName);
        var p = new TweakPreferences();
        p.Selections["explorer.show-extensions"] = "on";
        await p.SaveAsync(path);
        var loaded = await TweakPreferences.LoadAsync(path);
        Assert.Equal("on", loaded.Selections["EXPLORER.SHOW-EXTENSIONS"]);
    }
}
