using WindowsReborn.Backup;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Paths;

namespace WindowsReborn.Tests;

/// <summary>Persistencia de carpetas personalizadas, perfiles y su adaptación a otro usuario.</summary>
public class ProfilePersistenceTests
{
    private static string Roaming => KnownRoots.Resolve(KnownRoots.RoamingAppData)!;

    [Fact]
    public void Env_variable_path_is_stored_relative_to_appdata()
    {
        var entry = CustomFolderEntry.FromPath(@"%APPDATA%\.minecraft");
        Assert.Equal(KnownRoots.RoamingAppData, entry.Root);
        Assert.Equal(".minecraft", entry.SubPath);
        Assert.Equal(".minecraft", entry.Name);
        Assert.Equal(@"%APPDATA%\.minecraft", entry.DisplayPath);
        Assert.Equal(Path.Combine(Roaming, ".minecraft"), entry.Resolve());
    }

    [Fact]
    public void Entry_from_another_user_resolves_to_current_user()
    {
        // Configuración importada de un equipo donde el usuario se llamaba «Otro».
        var entry = new CustomFolderEntry
        {
            Path = @"C:\Users\Otro\AppData\Roaming\.minecraft",
            Root = KnownRoots.RoamingAppData,
            SubPath = ".minecraft",
            Name = ".minecraft",
        };
        Assert.Equal(Path.Combine(Roaming, ".minecraft"), entry.Resolve());
    }

    [Fact]
    public void Folder_outside_user_profile_keeps_absolute_path()
    {
        using var t = new TempDir();
        var folder = t.Sub("Juegos", "Partidas");
        var entry = CustomFolderEntry.FromPath(folder);
        Assert.Equal(folder, entry.Resolve());
        Assert.True(entry.Exists);
    }

    [Fact]
    public async Task V1_profile_is_migrated_and_missing_folders_are_kept()
    {
        using var t = new TempDir();
        var config = t.Sub("config");
        var v1 = """
        { "schemaVersion": 1, "name": "Antiguo", "categories": ["documents"],
          "customFolders": [ { "path": "APPDATA_PLACEHOLDER", "isProjects": false },
                             { "path": "Z:\\NoExiste\\Carpeta", "isProjects": true } ] }
        """.Replace("APPDATA_PLACEHOLDER", Path.Combine(Roaming, ".minecraft").Replace("\\", "\\\\"));
        Directory.CreateDirectory(Path.Combine(config, ConfigStore.ProfilesFolder));
        await File.WriteAllTextAsync(Path.Combine(config, ConfigStore.ProfilesFolder, "antiguo.json"), v1);

        var profiles = await new ProfileStore(config).LoadAllAsync();
        var p = Assert.Single(profiles);
        Assert.Equal(BackupProfile.CurrentSchema, p.SchemaVersion);
        Assert.Equal(2, p.CustomFolders.Count);
        Assert.Equal(KnownRoots.RoamingAppData, p.CustomFolders[0].Root);
        Assert.Equal(".minecraft", p.CustomFolders[0].SubPath);
        Assert.False(p.CustomFolders[1].Exists);
        Assert.True(p.CustomFolders[1].IsProjects);

        // La migración se guarda en disco.
        var raw = await File.ReadAllTextAsync(Path.Combine(config, ConfigStore.ProfilesFolder, "antiguo.json"));
        Assert.Contains("\"root\"", raw);
    }

    [Fact]
    public async Task Selection_round_trips_with_exclusions_and_state()
    {
        using var t = new TempDir();
        var store = new ProfileStore(t.Sub("config"));
        var folder = CustomFolderEntry.FromPath(@"%APPDATA%\.minecraft");
        folder.Selected = false;
        folder.Exclusions = ["logs/", "*.log"];
        folder.RestoreTarget = @"D:\Juegos\.minecraft";
        await store.SaveSelectionAsync(new BackupProfile { Name = "current", CustomFolders = [folder], Format = "7z", Compression = "maximum", IncludeWinGetList = false });

        var loaded = await store.LoadSelectionAsync();
        Assert.NotNull(loaded);
        var f = Assert.Single(loaded!.CustomFolders);
        Assert.False(f.Selected);
        Assert.Equal(["logs/", "*.log"], f.Exclusions);
        Assert.Equal(@"D:\Juegos\.minecraft", f.RestoreTarget);
        Assert.Equal("7z", loaded.Format);
        Assert.False(loaded.IncludeWinGetList);
    }

    [Fact]
    public void Selection_file_travels_in_configuration_packages()
    {
        Assert.True(ConfigStore.IsAllowedRelative(ProfileStore.SelectionFile));
        Assert.True(ConfigStore.IsAllowedRelative("tweaks.json"));
        Assert.False(ConfigStore.IsAllowedRelative("tweak-journal.json"));
    }

    [Fact]
    public void Custom_folder_source_uses_symbolic_root_and_own_exclusions()
    {
        using var t = new TempDir();
        var folder = t.Sub("Proyecto Final");
        var entry = CustomFolderEntry.FromPath(folder, isProjects: true);
        entry.Exclusions = ["*.bak"];
        var source = SourceFactory.ForCustomFolder(entry, []);
        Assert.NotNull(source);
        Assert.Contains("*.bak", source!.ExtraExclusions);
        Assert.Contains("bin/", source.ExtraExclusions);
        Assert.Equal(folder, source.AbsolutePath);
        Assert.Null(SourceFactory.ForCustomFolder(CustomFolderEntry.FromPath(Path.Combine(t.Root, "no-existe")), []));
    }
}
