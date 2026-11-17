using System.Diagnostics;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Logging;
using WindowsReborn.Core.Paths;
using WindowsReborn.Storage;

namespace WindowsReborn.Restore;

public enum ConflictPolicy
{
    /// <summary>No tocar el archivo existente (por defecto).</summary>
    Skip,
    /// <summary>Restaurar con otro nombre junto al existente.</summary>
    Rename,
    /// <summary>Reemplazar el existente. Requiere confirmación expresa en la interfaz.</summary>
    Overwrite,
}

/// <summary>Destino elegido para una categoría del backup.</summary>
public sealed class CategoryTarget
{
    public required CategoryInfo Category { get; init; }
    public string? TargetFolder { get; set; }
    public bool Selected { get; set; } = true;
    /// <summary>Si es null se restaura toda la categoría; si no, solo estas rutas relativas.</summary>
    public HashSet<string>? OnlyRelative { get; set; }
}

public sealed record RestorePlanItem(FileRecord File, CategoryInfo Category, string TargetPath, bool Exists);

public sealed class RestorePlan
{
    public List<RestorePlanItem> Items { get; } = [];
    public List<(CategoryInfo Category, string TargetPath)> Directories { get; } = [];
    public List<string> InvalidPaths { get; } = [];
    public int Conflicts => Items.Count(i => i.Exists);
    public long TotalBytes => Items.Sum(i => i.File.Size);
}

public enum RestoreOutcome
{
    Restored,
    Renamed,
    Overwritten,
    SkippedExisting,
    SkippedAppRunning,
    Failed,
}

public sealed class RestoreItemResult
{
    public string Path { get; set; } = "";
    public RestoreOutcome Outcome { get; set; }
    public string? Detail { get; set; }
}

public sealed class RestoreReport
{
    public DateTime StartedUtc { get; set; }
    public TimeSpan Duration { get; set; }
    public string BackupName { get; set; } = "";
    public bool Cancelled { get; set; }
    public List<RestoreItemResult> Items { get; } = [];

    public int Count(RestoreOutcome outcome) => Items.Count(i => i.Outcome == outcome);
    public bool HasFailures => Items.Any(i => i.Outcome == RestoreOutcome.Failed);
}

public sealed record RestoreProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, string? Current);

/// <summary>
/// Restauración selectiva: resuelve destinos (con remapeo de usuario/unidad),
/// detecta conflictos, escribe en temporal, verifica el SHA-256 y solo entonces
/// coloca el archivo en su sitio. Ninguna ruta del backup puede escribir fuera de
/// la carpeta de destino autorizada.
/// </summary>
public static class RestoreEngine
{
    /// <summary>Propone un destino para cada categoría en el equipo actual.</summary>
    public static List<CategoryTarget> SuggestTargets(BackupManifest manifest)
    {
        var list = new List<CategoryTarget>();
        foreach (var category in manifest.Categories)
        {
            string? target = null;
            var root = KnownRoots.Resolve(category.Root);
            if (root is not null)
            {
                target = string.IsNullOrEmpty(category.SubPath) ? root : SafeCombineOrNull(root, category.SubPath);
            }
            else if (category.OriginalPath is not null)
            {
                // Raíz personalizada: se propone la ruta original solo si su unidad existe.
                var drive = Path.GetPathRoot(category.OriginalPath);
                if (drive is not null && Directory.Exists(drive))
                    target = category.OriginalPath;
            }
            list.Add(new CategoryTarget { Category = category, TargetFolder = target });
        }
        return list;
    }

    public static RestorePlan BuildPlan(BackupManifest manifest, IEnumerable<CategoryTarget> targets)
    {
        var plan = new RestorePlan();
        var byId = targets.Where(t => t.Selected && !string.IsNullOrWhiteSpace(t.TargetFolder))
            .ToDictionary(t => t.Category.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var file in manifest.DataFiles)
        {
            if (file.Category is null || file.Relative is null || !byId.TryGetValue(file.Category, out var target))
                continue;
            if (target.OnlyRelative is not null && !target.OnlyRelative.Contains(file.Relative))
                continue;
            try
            {
                var path = SafePath.Combine(target.TargetFolder!, file.Relative);
                plan.Items.Add(new RestorePlanItem(file, target.Category, path, File.Exists(path)));
            }
            catch (UnsafePathException)
            {
                plan.InvalidPaths.Add(file.Path);
            }
        }

        foreach (var dir in manifest.EmptyDirectories)
        {
            if (!byId.TryGetValue(dir.Category, out var target) || target.OnlyRelative is not null)
                continue;
            try { plan.Directories.Add((target.Category, SafePath.Combine(target.TargetFolder!, dir.Relative))); }
            catch (UnsafePathException) { plan.InvalidPaths.Add(dir.Relative); }
        }
        return plan;
    }

    /// <summary>Procesos que impiden restaurar ahora (la app debe estar cerrada).</summary>
    public static IReadOnlyList<string> RunningBlockers(IEnumerable<CategoryInfo> categories)
    {
        var running = new List<string>();
        foreach (var name in categories.SelectMany(c => c.ProcessNames).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0) running.Add(name);
                foreach (var p in procs) p.Dispose();
            }
            catch { }
        }
        return running;
    }

    public static async Task<RestoreReport> ExecuteAsync(IBackupReader reader, RestorePlan plan, ConflictPolicy policy,
        string backupName, IProgress<RestoreProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var report = new RestoreReport { StartedUtc = DateTime.UtcNow, BackupName = backupName };
        foreach (var invalid in plan.InvalidPaths)
            report.Items.Add(new RestoreItemResult { Path = invalid, Outcome = RestoreOutcome.Failed, Detail = "unsafe-path" });

        long total = plan.TotalBytes, done = 0;
        var filesDone = 0;
        var lastReport = 0L;
        var blockedCategories = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var (_, dirPath) in plan.Directories)
                Directory.CreateDirectory(dirPath);

            foreach (var item in plan.Items)
            {
                ct.ThrowIfCancellationRequested();
                var display = $"{item.Category.Name}/{item.File.Relative}";

                // Configuración de apps: solo con la aplicación cerrada para no pisar un perfil en uso.
                if (item.Category.ProcessNames.Count > 0)
                {
                    if (!blockedCategories.TryGetValue(item.Category.Id, out var blocked))
                    {
                        blocked = RunningBlockers([item.Category]).Count > 0;
                        blockedCategories[item.Category.Id] = blocked;
                    }
                    if (blocked)
                    {
                        report.Items.Add(new RestoreItemResult { Path = display, Outcome = RestoreOutcome.SkippedAppRunning });
                        done += item.File.Size;
                        filesDone++;
                        continue;
                    }
                }

                var result = await RestoreOneAsync(reader, item, policy, n =>
                {
                    done += n;
                    var now = Environment.TickCount64;
                    if (now - lastReport > 100)
                    {
                        lastReport = now;
                        progress?.Report(new RestoreProgress(done, total, filesDone, plan.Items.Count, display));
                    }
                }, ct).ConfigureAwait(false);
                result.Path = display;
                report.Items.Add(result);
                filesDone++;
            }
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
            Log.Warn("Restore cancelled by the user");
        }

        progress?.Report(new RestoreProgress(total, total, filesDone, plan.Items.Count, null));
        report.Duration = sw.Elapsed;
        Log.Info($"Restore finished: {report.Count(RestoreOutcome.Restored)} restored, {report.Count(RestoreOutcome.Failed)} failed, cancelled={report.Cancelled}");
        return report;
    }

    private static async Task<RestoreItemResult> RestoreOneAsync(IBackupReader reader, RestorePlanItem item, ConflictPolicy policy,
        Action<long> onBytes, CancellationToken ct)
    {
        var target = item.TargetPath;
        var exists = File.Exists(target);
        var outcome = RestoreOutcome.Restored;

        if (exists)
        {
            switch (policy)
            {
                case ConflictPolicy.Skip:
                    onBytes(item.File.Size);
                    return new RestoreItemResult { Outcome = RestoreOutcome.SkippedExisting };
                case ConflictPolicy.Rename:
                    target = NextFreeName(target);
                    outcome = RestoreOutcome.Renamed;
                    break;
                default:
                    outcome = RestoreOutcome.Overwritten;
                    break;
            }
        }

        if (!reader.Contains(item.File.Path))
            return new RestoreItemResult { Outcome = RestoreOutcome.Failed, Detail = "missing-in-backup" };

        var temp = target + ".wrtmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string sha;
            long bytes;
            await using (var input = reader.OpenEntry(item.File.Path))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                (bytes, sha) = await Hashing.CopyAndHashAsync(input, output, onBytes, ct).ConfigureAwait(false);
            }

            if (bytes != item.File.Size || !string.Equals(sha, item.File.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                return new RestoreItemResult { Outcome = RestoreOutcome.Failed, Detail = "hash-mismatch" };
            }

            if (outcome == RestoreOutcome.Overwritten && File.Exists(target))
            {
                // Un archivo de solo lectura no se puede reemplazar sin quitar el atributo.
                var attrs = File.GetAttributes(target);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(target, attrs & ~FileAttributes.ReadOnly);
            }
            File.Move(temp, target, overwrite: outcome == RestoreOutcome.Overwritten);
            File.SetLastWriteTimeUtc(target, item.File.LastWriteUtc);
            var keep = item.File.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden);
            if (keep != 0)
                File.SetAttributes(target, File.GetAttributes(target) | keep);
            return new RestoreItemResult { Outcome = outcome };
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(temp);
            return new RestoreItemResult { Outcome = RestoreOutcome.Failed, Detail = ex is UnauthorizedAccessException ? "access-denied" : ex.Message };
        }
    }

    /// <summary>«Informe.docx» → «Informe (restaurado).docx», «Informe (restaurado 2).docx»…</summary>
    internal static string NextFreeName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var suffix = Core.Localization.Loc.T("restore.renamed-suffix");
        if (suffix == "restore.renamed-suffix") suffix = "restored";
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, i == 1 ? $"{stem} ({suffix}){ext}" : $"{stem} ({suffix} {i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>Lee la carpeta <c>reborn/</c> del backup como paquete de configuración validado.</summary>
    public static ConfigPackage? ReadConfiguration(IBackupReader reader, BackupManifest manifest)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files.Where(f => f.Section == Sections.Config))
        {
            if (!reader.Contains(file.Path) || file.Size > 16 * 1024 * 1024)
                continue;
            using var s = reader.OpenEntry(file.Path);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            if (!string.Equals(Hashing.HashBytes(bytes), file.Sha256, StringComparison.OrdinalIgnoreCase))
                continue;
            files[file.Path[(Sections.Config.Length + 1)..]] = bytes;
        }
        return files.Count == 0 ? null : ConfigStore.ReadPackage(files);
    }

    public static async Task SaveReportAsync(RestoreReport report, string reportsDirectory)
    {
        Directory.CreateDirectory(reportsDirectory);
        await Json.WriteFileAtomicAsync(Path.Combine(reportsDirectory, $"restore-{DateTime.Now:yyyyMMdd-HHmmss}.json"), new
        {
            report.StartedUtc,
            report.Duration,
            report.BackupName,
            report.Cancelled,
            Summary = Enum.GetValues<RestoreOutcome>().ToDictionary(o => o.ToString(), report.Count),
            Items = report.Items.Where(i => i.Outcome is not RestoreOutcome.Restored),
        });
    }

    private static string? SafeCombineOrNull(string root, string sub)
    {
        try { return SafePath.Combine(root, sub); }
        catch (UnsafePathException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
