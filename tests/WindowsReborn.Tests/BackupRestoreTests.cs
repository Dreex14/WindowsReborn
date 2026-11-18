using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WindowsReborn.Backup;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Restore;
using WindowsReborn.Storage;

namespace WindowsReborn.Tests;

public class BackupRestoreTests
{
    private static BackupPlan Plan(string dest, BackupFormat format, params BackupSource[] sources) => new()
    {
        Sources = sources,
        DestinationFolder = dest,
        Name = "test-backup",
        Format = format,
        AppVersion = "test",
    };

    private static async Task<BackupResult> RunAsync(BackupPlan plan, CancellationToken ct = default) =>
        await new BackupEngine().RunAsync(plan, null, null, ct);

    private static List<CategoryTarget> TargetsTo(BackupManifest manifest, string folder) =>
        manifest.Categories.Select(c => new CategoryTarget { Category = c, TargetFolder = Path.Combine(folder, c.Id) }).ToList();

    [Theory]
    [InlineData(BackupFormat.Zip)]
    [InlineData(BackupFormat.Folder)]
    public async Task Roundtrip_preserves_content_unicode_empty_dirs_and_long_paths(BackupFormat format)
    {
        using var t = new TempDir();
        var src = t.Sub("src");
        t.File("src/normal.txt", "hola");
        t.File("src/Ñandú ü 日本/ファイル 😀.txt", "unicode");
        t.File("src/empty-file.txt", "");
        Directory.CreateDirectory(Path.Combine(src, "vacía", "más"));
        var deep = string.Join('\\', Enumerable.Repeat("carpeta-con-nombre-largo-para-superar-limites", 7));
        t.File($"src/{deep}/archivo-profundo.txt", "deep");
        Assert.True(Path.Combine(src, deep, "archivo-profundo.txt").Length > 260);

        var dest = t.Sub("dest");
        var result = await RunAsync(Plan(dest, format, TempDir.Source("data", src)));
        Assert.Equal(BackupStatus.Success, result.Status);
        Assert.True(result.Verification!.IsValid);
        Assert.False(File.Exists(result.FinalPath + ".partial"));

        using var reader = BackupReader.Open(result.FinalPath!);
        var manifest = BackupReader.ReadManifest(reader);
        Assert.Equal(4, manifest.DataFiles.Count());
        Assert.Contains(manifest.EmptyDirectories, d => d.Relative == "vacía/más");

        var restoreRoot = t.Sub("restored");
        var plan = RestoreEngine.BuildPlan(manifest, TargetsTo(manifest, restoreRoot));
        var report = await RestoreEngine.ExecuteAsync(reader, plan, ConflictPolicy.Skip, "test", null, CancellationToken.None);
        Assert.Equal(4, report.Count(RestoreOutcome.Restored));
        Assert.Equal("unicode", File.ReadAllText(Path.Combine(restoreRoot, "data", "Ñandú ü 日本", "ファイル 😀.txt")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(restoreRoot, "data", deep, "archivo-profundo.txt")));
        Assert.True(Directory.Exists(Path.Combine(restoreRoot, "data", "vacía", "más")));
    }

    [Fact]
    public async Task Large_file_is_hashed_and_verified()
    {
        using var t = new TempDir();
        var src = t.Sub("src");
        var big = Path.Combine(src, "big.bin");
        var data = new byte[48 * 1024 * 1024 + 123];
        new Random(42).NextBytes(data);
        await File.WriteAllBytesAsync(big, data);

        var result = await RunAsync(Plan(t.Sub("dest"), BackupFormat.Zip, TempDir.Source("data", src)));
        Assert.Equal(BackupStatus.Success, result.Status);
        Assert.Equal(data.LongLength, result.BytesWritten);
        using var reader = BackupReader.Open(result.FinalPath!);
        var manifest = BackupReader.ReadManifest(reader);
        Assert.Equal(await Hashing.HashFileAsync(big), manifest.DataFiles.Single().Sha256);
    }

    [Fact]
    public async Task Destination_inside_source_is_rejected()
    {
        using var t = new TempDir();
        var src = t.Sub("src");
        t.File("src/a.txt", "a");
        var result = await RunAsync(Plan(Path.Combine(src, "backups"), BackupFormat.Zip, TempDir.Source("data", src)));
        Assert.Equal(BackupStatus.Failed, result.Status);
        Assert.Contains("dest.inside-source", result.Error);
    }

    [Fact]
    public async Task Cancellation_removes_temporary_output()
    {
        using var t = new TempDir();
        var src = t.Sub("src");
        for (var i = 0; i < 50; i++)
            await File.WriteAllBytesAsync(Path.Combine(src, $"f{i}.bin"), new byte[512 * 1024]);
        var dest = t.Sub("dest");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<BackupProgress>(p => { if (p.Phase == BackupPhase.Writing) cts.Cancel(); });

        var result = await new BackupEngine().RunAsync(Plan(dest, BackupFormat.Zip, TempDir.Source("data", src)), null, progress, cts.Token);
        Assert.Equal(BackupStatus.Cancelled, result.Status);
        Assert.Empty(Directory.GetFileSystemEntries(dest));
    }

    [Fact]
    public async Task Locked_file_is_skipped_and_reported()
    {
        using var t = new TempDir();
        var src = t.Sub("src");
        t.File("src/ok.txt", "ok");
        var locked = t.File("src/locked.txt", "locked");
        await using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await RunAsync(Plan(t.Sub("dest"), BackupFormat.Zip, TempDir.Source("data", src)));
        Assert.Equal(BackupStatus.SuccessWithSkips, result.Status);
        Assert.Contains(result.Report.Skipped, s => s.Reason == SkipReason.Locked);
        Assert.Equal(1, result.FilesWritten);
    }

    [Fact]
    public async Task Sensitive_files_are_excluded_by_default()
    {
        using var t = new TempDir();
        var src = t.Sub("src");
        t.File("src/notes.txt", "n");
        t.File("src/Login Data", "secret");
        t.File("src/.ssh/id_ed25519", "key");
        t.File("src/cert.pfx", "pfx");
        var result = await RunAsync(Plan(t.Sub("dest"), BackupFormat.Folder, TempDir.Source("data", src)));
        Assert.Equal(1, result.FilesWritten);
        Assert.Equal(3, result.Report.Skipped.Count(s => s.Reason == SkipReason.Sensitive));
    }

    [Fact]
    public async Task Exclusion_patterns_are_applied()
    {
        using var t = new TempDir();
        var src = t.Sub("src");
        t.File("src/app/index.js", "x");
        t.File("src/app/node_modules/lib/index.js", "x");
        t.File("src/~$draft.docx", "x");
        var result = await RunAsync(Plan(t.Sub("dest"), BackupFormat.Folder, TempDir.Source("data", src)));
        Assert.Equal(1, result.FilesWritten);
    }

    [Fact]
    public async Task Configuration_is_included_and_recoverable()
    {
        using var t = new TempDir();
        var src = t.Sub("src");
        t.File("src/a.txt", "a");
        var config = new ConfigStore(t.Sub("config"));
        await config.SaveSettingsAsync(new AppSettings { Language = "es", PreferredFormat = "folder" });
        t.File("config/programs.json", "{\"schemaVersion\":1,\"programs\":[{\"name\":\"Firefox\"}]}");
        t.File("config/profiles/semanal.json", "{\"name\":\"Semanal\"}");

        var extras = config.EnumerateFiles()
            .Select(f => new ExtraFile($"{Sections.Config}/{f.Relative}", Sections.Config, f.FullPath, null))
            .Append(new ExtraFile($"{Sections.Config}/{ConfigStore.ManifestName}", Sections.Config, null,
                JsonSerializer.SerializeToUtf8Bytes(config.CreateManifest("test"), Json.Options)))
            .ToList();
        var plan = new BackupPlan
        {
            Sources = [TempDir.Source("data", src)], DestinationFolder = t.Sub("dest"), Name = "cfg", Extras = extras,
        };
        var result = await RunAsync(plan);
        Assert.Equal(BackupStatus.Success, result.Status);

        using var reader = BackupReader.Open(result.FinalPath!);
        var manifest = BackupReader.ReadManifest(reader);
        Assert.True(manifest.HasConfiguration);
        var package = RestoreEngine.ReadConfiguration(reader, manifest)!;
        Assert.Equal(3, package.Files.Count);

        // Equipo recién formateado: configuración vacía.
        var fresh = new ConfigStore(t.Sub("fresh-config"));
        Assert.False(fresh.HasAnyConfiguration);
        await fresh.ApplyPackageAsync(package);
        var settings = await fresh.LoadSettingsAsync();
        Assert.Equal("es", settings.Language);
        Assert.True(File.Exists(Path.Combine(fresh.Directory, "profiles", "semanal.json")));
    }

    [Fact]
    public async Task Restore_conflicts_skip_rename_and_overwrite()
    {
        using var t = new TempDir();
        var src = t.Sub("src");
        t.File("src/doc.txt", "from-backup");
        var result = await RunAsync(Plan(t.Sub("dest"), BackupFormat.Zip, TempDir.Source("data", src)));
        using var reader = BackupReader.Open(result.FinalPath!);
        var manifest = BackupReader.ReadManifest(reader);
        var target = t.Sub("target");
        var existing = t.File("target/data/doc.txt", "local-version");

        var plan = RestoreEngine.BuildPlan(manifest, TargetsTo(manifest, target));
        Assert.Equal(1, plan.Conflicts);

        var skip = await RestoreEngine.ExecuteAsync(reader, plan, ConflictPolicy.Skip, "t", null, CancellationToken.None);
        Assert.Equal(1, skip.Count(RestoreOutcome.SkippedExisting));
        Assert.Equal("local-version", File.ReadAllText(existing));

        var rename = await RestoreEngine.ExecuteAsync(reader, plan, ConflictPolicy.Rename, "t", null, CancellationToken.None);
        Assert.Equal(1, rename.Count(RestoreOutcome.Renamed));
        Assert.Equal("local-version", File.ReadAllText(existing));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(target, "data")).Length);

        File.SetAttributes(existing, FileAttributes.ReadOnly);
        var overwrite = await RestoreEngine.ExecuteAsync(reader, plan, ConflictPolicy.Overwrite, "t", null, CancellationToken.None);
        Assert.Equal(1, overwrite.Count(RestoreOutcome.Overwritten));
        Assert.Equal("from-backup", File.ReadAllText(existing));
    }

    [Fact]
    public async Task Tampered_zip_entry_is_detected_and_not_restored()
    {
        using var t = new TempDir();
        var src = t.Sub("src");
        t.File("src/doc.txt", "original content");
        var result = await RunAsync(Plan(t.Sub("dest"), BackupFormat.Zip, TempDir.Source("data", src)));

        // Se reemplaza el contenido de la entrada sin actualizar el manifiesto.
        using (var zip = ZipFile.Open(result.FinalPath!, ZipArchiveMode.Update))
        {
            zip.GetEntry("data/data/doc.txt")!.Delete();
            using var w = new StreamWriter(zip.CreateEntry("data/data/doc.txt").Open());
            w.Write("tampered content");
        }

        using var reader = BackupReader.Open(result.FinalPath!);
        var manifest = BackupReader.ReadManifest(reader);
        var verification = await BackupVerifier.VerifyAsync(reader, manifest, null, null, CancellationToken.None);
        Assert.Single(verification.HashMismatch);

        var target = t.Sub("target");
        var plan = RestoreEngine.BuildPlan(manifest, TargetsTo(manifest, target));
        var report = await RestoreEngine.ExecuteAsync(reader, plan, ConflictPolicy.Skip, "t", null, CancellationToken.None);
        Assert.Equal(1, report.Count(RestoreOutcome.Failed));
        Assert.False(File.Exists(Path.Combine(target, "data", "doc.txt")));
        Assert.Empty(Directory.GetFiles(target, "*.wrtmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Manifest_with_traversal_path_is_rejected()
    {
        using var t = new TempDir();
        var zipPath = Path.Combine(t.Root, "evil.wrbk.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifest = new BackupManifest
            {
                Categories = [new CategoryInfo { Id = "c", Root = "{Documents}" }],
                Files = [new FileRecord { Path = "../../evil.txt", Category = "c", Relative = "../../evil.txt", Size = 1, Sha256 = "x" }],
            };
            using var s = zip.CreateEntry(BackupManifest.FileName).Open();
            JsonSerializer.Serialize(s, manifest, Json.Options);
        }

        using var reader = BackupReader.Open(zipPath);
        Assert.ThrowsAny<Exception>(() => BackupReader.ReadManifest(reader));
    }

    [Fact]
    public async Task Relative_traversal_in_category_is_blocked_at_plan_time()
    {
        using var t = new TempDir();
        var manifest = new BackupManifest
        {
            Categories = [new CategoryInfo { Id = "c", Root = "{Custom:x}" }],
            Files = [new FileRecord { Path = "data/c/ok.txt", Category = "c", Relative = "../../escape.txt", Size = 1, Sha256 = "x" }],
        };
        var plan = RestoreEngine.BuildPlan(manifest, [new CategoryTarget { Category = manifest.Categories[0], TargetFolder = t.Sub("target") }]);
        Assert.Empty(plan.Items);
        Assert.Single(plan.InvalidPaths);
        await Task.CompletedTask;
    }

    [Fact]
    public void Truncated_zip_is_reported_as_damaged()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Root, "broken.wrbk.zip");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("PK\u0003\u0004 this is not a complete zip"));
        Assert.Throws<InvalidDataException>(() => BackupReader.Open(path));
    }

    [Fact]
    public void Newer_format_is_refused_with_clear_error()
    {
        using var t = new TempDir();
        var folder = t.Sub("future");
        File.WriteAllText(Path.Combine(folder, BackupManifest.FileName), "{\"formatVersion\": 99, \"files\": []}");
        using var reader = BackupReader.Open(folder);
        Assert.Throws<NotSupportedException>(() => BackupReader.ReadManifest(reader));
    }

    [Fact]
    public void Older_manifest_without_new_sections_still_loads()
    {
        // Compatibilidad: un manifiesto v1 mínimo sin categorías nuevas, sin reborn/ ni installers/.
        using var t = new TempDir();
        var folder = t.Sub("old");
        File.WriteAllText(Path.Combine(folder, BackupManifest.FileName),
            "{\"formatVersion\":1,\"categories\":[{\"id\":\"documents\",\"root\":\"{Documents}\"}],\"files\":[],\"unknownFutureField\":true}");
        using var reader = BackupReader.Open(folder);
        var manifest = BackupReader.ReadManifest(reader);
        Assert.False(manifest.HasConfiguration);
        Assert.False(manifest.HasInstallers);
        Assert.Null(RestoreEngine.ReadConfiguration(reader, manifest));
    }

    [Fact]
    public async Task Restore_to_another_user_maps_known_roots()
    {
        // Un backup hecho con otro nombre de usuario: la raíz {Documents} se resuelve en este equipo.
        var manifest = new BackupManifest
        {
            UserName = "otro-usuario",
            Categories = [new CategoryInfo { Id = "documents", Root = "{Documents}", OriginalPath = @"C:\Users\otro-usuario\Documents" }],
        };
        var targets = RestoreEngine.SuggestTargets(manifest);
        var expected = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.Equal(expected, targets.Single().TargetFolder, ignoreCase: true);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task App_config_is_not_restored_while_app_is_running()
    {
        using var t = new TempDir();
        var src = t.Sub("src");
        t.File("src/settings.json", "{}");
        var currentProcess = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        var result = await RunAsync(Plan(t.Sub("dest"), BackupFormat.Zip,
            TempDir.Source("app", src, ["settings.json"], [currentProcess])));
        using var reader = BackupReader.Open(result.FinalPath!);
        var manifest = BackupReader.ReadManifest(reader);
        var plan = RestoreEngine.BuildPlan(manifest, TargetsTo(manifest, t.Sub("target")));
        var report = await RestoreEngine.ExecuteAsync(reader, plan, ConflictPolicy.Overwrite, "t", null, CancellationToken.None);
        Assert.Equal(1, report.Count(RestoreOutcome.SkippedAppRunning));
    }

    [Fact]
    public async Task Existing_final_name_is_never_overwritten()
    {
        using var t = new TempDir();
        var src = t.Sub("src");
        t.File("src/a.txt", "a");
        var dest = t.Sub("dest");
        File.WriteAllText(Path.Combine(dest, "test-backup.wrbk.zip"), "previous backup");
        var result = await RunAsync(Plan(dest, BackupFormat.Zip, TempDir.Source("data", src)));
        Assert.Equal(BackupStatus.Failed, result.Status);
        Assert.Equal("previous backup", File.ReadAllText(Path.Combine(dest, "test-backup.wrbk.zip")));
    }

    [Fact]
    public void Insufficient_space_blocks_folder_backups()
    {
        using var t = new TempDir();
        var plan = Plan(t.Sub("dest"), BackupFormat.Folder, TempDir.Source("data", t.Sub("src")));
        var check = DestinationValidator.Validate(plan, long.MaxValue / 4);
        Assert.Contains("dest.no-space", check.Errors);
    }
}

/// <summary>IProgress síncrono para pruebas (Progress&lt;T&gt; usa el contexto y llega tarde).</summary>
internal sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
