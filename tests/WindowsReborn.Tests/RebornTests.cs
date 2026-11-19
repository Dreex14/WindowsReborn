using System.Text.Json.Nodes;
using WindowsReborn.Backup;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Programs;
using WindowsReborn.Restore;
using WindowsReborn.Storage;
using WindowsReborn.Tweaks;

namespace WindowsReborn.Tests;

/// <summary>Catálogos, presets y formatos de la versión 1.0.</summary>
public class RebornTests
{
    [Fact]
    public void Presets_only_reference_existing_toggles()
    {
        foreach (var id in TweakPresets.Privacy.Concat(TweakPresets.Optimize))
        {
            var t = TweakCatalog.Find(id);
            Assert.True(t is not null, $"preset references unknown tweak {id}");
            Assert.Equal(TweakKind.Toggle, t!.Kind);
        }
    }

    [Fact]
    public void Privacy_preset_keeps_camera_microphone_and_location_working()
    {
        Assert.DoesNotContain(TweakPresets.Privacy, id => id.StartsWith("permissions.", StringComparison.Ordinal));
        Assert.DoesNotContain(TweakPresets.Privacy, id => TweakCatalog.Find(id)!.Risk == RiskLevel.Advanced && !id.StartsWith("privacy.diag", StringComparison.Ordinal));
    }

    [Fact]
    public void Software_catalog_is_consistent()
    {
        var all = SoftwareCatalog.All;
        Assert.Equal(all.Count, all.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var p in all)
        {
            switch (p.Source)
            {
                case CatalogSource.WinGet:
                case CatalogSource.WinGetNewest:
                case CatalogSource.MsStore:
                    Assert.False(string.IsNullOrWhiteSpace(p.WinGetId), p.Id);
                    break;
                case CatalogSource.OfficialPageLink:
                    Assert.False(string.IsNullOrWhiteSpace(p.LinkPattern), p.Id);
                    Assert.StartsWith("https://", p.Homepage);
                    break;
                case CatalogSource.DirectLatest:
                    Assert.StartsWith("https://", p.DownloadUrl);
                    break;
                case CatalogSource.StorePage:
                    Assert.StartsWith("ms-windows-store://", p.Homepage);
                    break;
                default:
                    Assert.StartsWith("https://", p.Homepage);
                    break;
            }
            Assert.False(string.IsNullOrWhiteSpace(p.Monogram), p.Id);
        }

        // La lista con la que se formatea habitualmente viene marcada.
        string[] usual = ["discord", "nvidia-app", "brave", "chrome", "helium", "epic", "steam", "spotify", "obs", "protonvpn", "warp",
            "vscode", "eclipse", "veracrypt", "eartrumpet", "hoyoplay", "audacity", "winrar", "git", "python", "davinci", "malwarebytes", "whatsapp", "minecraft", "mullvad-browser", "claude", "chatgpt"];
        Assert.All(SoftwareCatalog.AddedPreselected, id => Assert.True(SoftwareCatalog.Find(id)?.Preselected == true));
        Assert.Null(SoftwareCatalog.Find("telegram"));

        // Todos los programas llevan su icono dentro del ejecutable.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "WindowsReborn.slnx"))) dir = dir.Parent;
        var icons = Path.Combine(dir!.FullName, "src", "WindowsReborn.App", "Assets", "Programs");
        foreach (var p in all)
            Assert.True(System.IO.File.Exists(Path.Combine(icons, p.Id + ".png")), $"{p.Id} has no icon");
        foreach (var id in usual)
            Assert.True(SoftwareCatalog.Find(id)?.Preselected == true, $"{id} should be preselected");
    }

    [Fact]
    public void Nvidia_link_pattern_matches_official_link()
    {
        var p = SoftwareCatalog.Find("nvidia-app")!;
        const string html = "<a href=\"https://us.download.nvidia.com/nvapp/client/11.0.9.585/x64/NVIDIA_app_v11.0.9.585_x64.exe\">x64</a>" +
                            "<a href=\"https://us.download.nvidia.com/nvapp/client/11.0.9.585/arm64/NVIDIA_app_v11.0.9.585_arm64.exe\">arm</a>";
        var matches = System.Text.RegularExpressions.Regex.Matches(html, p.LinkPattern!);
        Assert.Single(matches);
        Assert.EndsWith("_x64.exe", matches[0].Value);
    }

    [Fact]
    public void Winget_list_is_parsed_with_and_without_updates()
    {
        const string withUpdates = """
            Nombre            Id                     Versión   Disponible  Origen
            ---------------------------------------------------------------------
            Brave             Brave.Brave            156.1.0   157.1.99    winget
            Git               Git.Git                2.56.0.4              winget
            Python 3.15       Python.Python.3.15     3.15.2                winget
            """;
        var rows = SoftwareInstaller.ParseList(withUpdates);
        Assert.Equal(3, rows.Count);
        Assert.Equal("157.1.99", rows[0].Available);
        Assert.Equal("", rows[1].Available);
        Assert.Equal("Python.Python.3.15", rows[2].Id);

        const string noUpdates = """
            Name     Id         Version  Source
            -----------------------------------
            Git      Git.Git    2.56.0   winget
            """;
        var single = Assert.Single(SoftwareInstaller.ParseList(noUpdates));
        Assert.Null(single.Available);
    }

    [Fact]
    public void GitHub_asset_kind_is_detected()
    {
        Assert.False(new GitHubApp("a/b", "B", null, "1", "CalendarioAnime.exe", "https://x", null).IsInstaller);
        Assert.True(new GitHubApp("a/b", "B", null, "1", "Tool-Setup.exe", "https://x", null).IsInstaller);
        Assert.True(new GitHubApp("a/b", "B", null, "1", "tool.msi", "https://x", null).IsInstaller);
        Assert.True(new GitHubApp("a/b", "B", null, "1", "Koperfield-v1.0-windows.zip", "https://x", null).IsZip);
        Assert.True(GitHubAppsClient.IsValidRepo("Dreex14/calendario-anime"));
        Assert.False(GitHubAppsClient.IsValidRepo("../etc"));
    }

    [Fact]
    public async Task MyConfig_round_trips_through_zip()
    {
        using var tmp = new TempDir();
        var settings = new AppSettings { GitHubUser = "someone", SelectedRepos = ["someone/tool"], CustomFolders = [@"D:\Proyectos"] };
        var tweaks = new Dictionary<string, string> { ["explorer.show-extensions"] = "on", ["taskbar.search"] = "icon" };
        var config = MyConfig.FromSettings(settings, tweaks, ["brave", "git"], ["brave", "pictures"], "1.0.0");
        var zip = Path.Combine(tmp.Root, "config.zip");
        await config.SaveZipAsync(zip);

        Assert.True(MyConfig.IsConfigZip(zip));
        var loaded = await MyConfig.LoadAsync(zip);
        Assert.Equal("on", loaded.Tweaks["EXPLORER.SHOW-EXTENSIONS"]);
        Assert.Equal(["brave", "git"], loaded.Programs);

        // Un archivo de una sola sección no pisa las demás.
        var partial = new MyConfig { Parts = [MyConfig.PartPrivacy], Tweaks = new() { ["privacy.tips"] = "on" } };
        var keep = new AppSettings { SelectedPrograms = ["steam"], BackupItems = ["brave"] };
        partial.ApplyTo(keep);
        Assert.Equal(["steam"], keep.SelectedPrograms);
        Assert.Equal(["brave"], keep.BackupItems);
        Assert.False(partial.Has(MyConfig.PartPrograms));

        var target = new AppSettings();
        loaded.ApplyTo(target);
        Assert.Equal("someone", target.GitHubUser);
        Assert.Equal(["brave", "pictures"], target.BackupItems);
        Assert.Equal([@"D:\Proyectos"], target.CustomFolders);
    }

    [Fact]
    public void Portable_paths_hide_the_user_folder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(@"%APPDATA%\.minecraft", MyConfig.ToPortable(Path.Combine(appData, ".minecraft")));
        Assert.Equal(@"D:\Juegos", MyConfig.ToPortable(@"D:\Juegos"));
    }

    [Fact]
    public void Chromium_profiles_are_found_and_merged_without_overwriting()
    {
        using var tmp = new TempDir();
        var userData = tmp.Sub("User Data");
        tmp.File(@"User Data\Default\Bookmarks", "{}");
        tmp.File(@"User Data\Profile 1\Preferences", "{}");
        tmp.Sub("User Data", "System Profile");
        Assert.Equal(["Default", "Profile 1"], BackupCatalog.ChromiumProfiles(userData));

        // El navegador recién instalado ya tiene Local State con su clave nueva y el perfil Default.
        System.IO.File.WriteAllText(Path.Combine(userData, "Local State"),
            """{"os_crypt":{"encrypted_key":"NEW"},"profile":{"info_cache":{"Default":{"name":"Nuevo"}}}}""");
        var saved = System.Text.Encoding.UTF8.GetBytes("""{"Default":{"name":"Personal"},"Profile 1":{"name":"Trabajo"}}""");
        BackupCatalog.MergeChromiumProfileList(userData, saved);

        var state = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(userData, "Local State")))!;
        Assert.Equal("NEW", state["os_crypt"]!["encrypted_key"]!.GetValue<string>());
        Assert.Equal("Nuevo", state["profile"]!["info_cache"]!["Default"]!["name"]!.GetValue<string>());
        Assert.Equal("Trabajo", state["profile"]!["info_cache"]!["Profile 1"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void App_permission_paths_hide_the_user_folder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '#');
        var exe = profile + "#AppData#Local#Discord#app-1.0#Discord.exe";
        var portable = AppPermissions.ToPortable(exe);
        Assert.StartsWith("%USERPROFILE%#AppData", portable);
        Assert.Equal(exe, AppPermissions.FromPortable(portable));
        Assert.Equal("C:#Program Files#OBS#obs64.exe", AppPermissions.ToPortable("C:#Program Files#OBS#obs64.exe"));
        Assert.Contains("webcam", AppPermissions.Capabilities);
        Assert.Contains("microphone", AppPermissions.Capabilities);
        Assert.Equal(2, AppPermissions.CountApps(new Dictionary<string, Dictionary<string, string>>
        {
            ["webcam"] = new() { ["App.A"] = "Allow", ["App.B"] = "Deny" },
            ["microphone"] = new() { ["App.A"] = "Allow" },
        }));
    }

    [Fact]
    public void Permission_keys_exist_in_windows()
    {
        // Cada ajuste de permisos apunta a un permiso real de ConsentStore (evita erratas como «appDiagnostic»).
        string[] known = [.. AppPermissions.Capabilities, "appointments"];
        foreach (var t in TweakCatalog.All.Where(t => t.Category == TweakCategory.Permissions))
            foreach (var s in t.Settings)
                Assert.Contains(s.Key.Split('\\').Last(), known);
    }

    [Fact]
    public void Firefox_profiles_are_found()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("Mozilla", "Firefox");
        tmp.File(@"Mozilla\Firefox\profiles.ini", "[Profile0]");
        tmp.File(@"Mozilla\Firefox\Profiles\abc.default-release\prefs.js", "");
        tmp.Sub("Mozilla", "Firefox", "Profiles", "vacio");
        Assert.Equal(["abc.default-release"], BackupCatalog.GeckoProfiles(root));
    }

    [Fact]
    public void Chromium_backup_never_includes_secrets()
    {
        foreach (var f in BackupCatalog.ChromiumProfileFiles)
            Assert.DoesNotContain(f.TrimEnd('/'), new[] { "Login Data", "Cookies", "Web Data", "Local State", "History" });
        foreach (var f in BackupCatalog.GeckoProfileFiles)
            Assert.DoesNotContain(f.TrimEnd('/'), new[] { "logins.json", "key4.db", "cookies.sqlite", "cert9.db", "sessionstore.jsonlz4" });
    }

    [Fact]
    public void Policy_writes_under_hkcu_go_through_elevation()
    {
        Assert.True(SystemRegistryWriter.NeedsElevation(new RegOp(RegOpType.SetValue, RegHive.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "X")));
        Assert.False(SystemRegistryWriter.NeedsElevation(new RegOp(RegOpType.SetValue, RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "X")));
        Assert.True(TweakCatalog.Find("privacy.office-telemetry")!.RequiresAdmin);
        Assert.True(TweakCatalog.Find("explorer.no-store-open-with")!.RequiresAdmin);
        Assert.True(TweakCatalog.Find("permissions.webcam")!.RequiresAdmin);
    }

    [Fact]
    public async Task Browser_profile_backup_restores_only_safe_files_with_profile_list()
    {
        using var tmp = new TempDir();
        tmp.File(@"src\User Data\Default\Bookmarks", "{\"roots\":{}}");
        tmp.File(@"src\User Data\Default\Preferences", "{}");
        tmp.File(@"src\User Data\Default\Cookies", "secret");
        tmp.File(@"src\User Data\Default\Login Data", "secret");
        tmp.File(@"src\User Data\Default\Extensions\abc\1.0\manifest.json", "{}");
        var profile = Path.Combine(tmp.Root, "src", "User Data", "Default");

        var source = new BackupSource
        {
            Id = "brave-default", Name = "Brave · Default", Kind = Backup.SourceKind.AppConfig, Root = Core.Paths.KnownRoots.LocalAppData,
            SubPath = @"BraveSoftware\Brave-Browser\User Data\Default", AbsolutePath = profile,
            Include = BackupCatalog.ChromiumProfileFiles, AdapterId = "brave",
        };
        var extra = new ExtraFile("browsers/brave.json", Sections.Apps, null, System.Text.Encoding.UTF8.GetBytes("""{"Default":{"name":"Yo"}}"""));
        var plan = new BackupPlan { Sources = [source], DestinationFolder = tmp.Sub("out"), Name = "Configuración de apps", Extras = [extra] };
        var result = await new BackupEngine().RunAsync(plan, null, null, CancellationToken.None);
        Assert.Equal(BackupStatus.Success, result.Status);

        using var reader = BackupReader.Open(result.FinalPath!);
        var manifest = BackupReader.ReadManifest(reader);
        Assert.Equal("brave", manifest.Categories.Single().Adapter);
        Assert.True(reader.Contains("browsers/brave.json"));
        Assert.DoesNotContain(manifest.Files, f => f.Path.EndsWith("Cookies") || f.Path.EndsWith("Login Data"));

        // Se restaura en otra carpeta (en la app, la ruta original resuelta en el equipo nuevo).
        var targets = RestoreEngine.SuggestTargets(manifest);
        var restored = tmp.Sub("restored", "User Data", "Default");
        targets[0].TargetFolder = restored;
        var report = await RestoreEngine.ExecuteAsync(reader, RestoreEngine.BuildPlan(manifest, targets), ConflictPolicy.Overwrite, "t", null, CancellationToken.None);
        Assert.False(report.HasFailures);
        Assert.True(System.IO.File.Exists(Path.Combine(restored, "Bookmarks")));
        Assert.True(System.IO.File.Exists(Path.Combine(restored, "Extensions", "abc", "1.0", "manifest.json")));
        Assert.False(System.IO.File.Exists(Path.Combine(restored, "Cookies")));
    }
}
