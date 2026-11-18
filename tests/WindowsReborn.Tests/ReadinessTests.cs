using WindowsReborn.Backup;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Paths;
using WindowsReborn.Storage;

namespace WindowsReborn.Tests;

public class ReadinessTests
{
    private static BackupManifest Manifest(params CategoryInfo[] categories) => new()
    {
        CreatedUtc = DateTime.UtcNow,
        Format = "zip",
        Categories = [.. categories],
        Files =
        [
            new FileRecord { Path = "data/documents/a.txt", Section = Sections.Data, Size = 1 },
            new FileRecord { Path = "reborn/programs.json", Section = Sections.Config },
            new FileRecord { Path = "reborn/backup-selection.json", Section = Sections.Config },
        ],
    };

    [Fact]
    public void No_backup_is_not_ready()
    {
        var checks = FormatReadiness.Evaluate(new ReadinessInput());
        Assert.False(FormatReadiness.IsReady(checks));
        Assert.Equal(CheckState.Error, Assert.Single(checks).State);
    }

    [Fact]
    public void Backup_on_system_drive_blocks_and_unverified_is_pending()
    {
        var checks = FormatReadiness.Evaluate(new ReadinessInput
        {
            BackupPath = @"C:\Backups\b.wrbk.zip",
            Manifest = Manifest(),
            SystemDrive = @"C:\",
        });
        Assert.Equal(CheckState.Error, checks.Single(c => c.Key == "location").State);
        Assert.Equal(CheckState.Pending, checks.Single(c => c.Key == "integrity").State);
        Assert.False(FormatReadiness.IsReady(checks));
    }

    [Fact]
    public void Verified_backup_elsewhere_with_selected_folders_is_ready()
    {
        using var t = new TempDir();
        var folder = t.Sub("Partidas");
        var entry = CustomFolderEntry.FromPath(folder);
        var manifest = Manifest(new CategoryInfo { Id = "custom-partidas", Name = "Partidas", Kind = "customFolder", Root = entry.Root, SubPath = entry.SubPath, OriginalPath = folder });
        var checks = FormatReadiness.Evaluate(new ReadinessInput
        {
            BackupPath = @"Z:\Backups\b.wrbk.zip",
            Manifest = manifest,
            Verified = true,
            Selection = new BackupProfile { CustomFolders = [entry] },
            LibraryPrograms = 3,
            SystemDrive = @"C:\",
            AppCopiedNextToBackup = true,
        });
        Assert.Equal(CheckState.Ok, checks.Single(c => c.Key == "custom").State);
        Assert.Equal(CheckState.Ok, checks.Single(c => c.Key == "programs").State);
        Assert.True(FormatReadiness.IsReady(checks));
    }

    [Fact]
    public void Selected_folder_missing_from_backup_is_warned()
    {
        using var t = new TempDir();
        var entry = CustomFolderEntry.FromPath(t.Sub("NoIncluida"));
        var checks = FormatReadiness.Evaluate(new ReadinessInput
        {
            BackupPath = @"Z:\b.wrbk.zip",
            Manifest = Manifest(),
            Verified = true,
            Selection = new BackupProfile { CustomFolders = [entry] },
            SystemDrive = @"C:\",
        });
        var custom = checks.Single(c => c.Key == "custom");
        Assert.Equal(CheckState.Warning, custom.State);
        Assert.Contains("NoIncluida", (string)custom.Args[0]);
    }

    [Fact]
    public async Task Selection_survives_export_and_import_into_a_fresh_installation()
    {
        using var t = new TempDir();
        var before = new ConfigStore(t.Sub("antes"));
        var selection = new BackupProfile
        {
            Name = "current",
            Categories = ["documents"],
            CustomFolders = [CustomFolderEntry.FromPath(@"%APPDATA%\.minecraft")],
            Format = "folder",
        };
        await new ProfileStore(before.Directory).SaveSelectionAsync(selection);
        var file = Path.Combine(t.Root, "config.wrconfig");
        await before.ExportAsync(file, "test");

        // «Después de formatear»: configuración vacía.
        var after = new ConfigStore(t.Sub("despues"));
        var package = ConfigStore.ReadPackageFromZip(file);
        Assert.Contains(ProfileStore.SelectionFile, package.Files.Keys);
        await after.ApplyPackageAsync(package);

        var restored = await new ProfileStore(after.Directory).LoadSelectionAsync();
        var folder = Assert.Single(restored!.CustomFolders);
        Assert.True(folder.Selected);
        Assert.Equal(Path.Combine(KnownRoots.Resolve(KnownRoots.RoamingAppData)!, ".minecraft"), folder.Resolve());
        Assert.Equal("folder", restored.Format);
    }
}
