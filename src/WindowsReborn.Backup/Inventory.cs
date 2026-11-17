using System.IO.Enumeration;
using WindowsReborn.Core.Catalog;
using WindowsReborn.Core.Paths;
using WindowsReborn.Storage;

namespace WindowsReborn.Backup;

public sealed record InventoryItem(int SourceIndex, string FullPath, string Relative, long Size, DateTime LastWriteUtc, FileAttributes Attributes);

public sealed class InventoryResult
{
    public List<InventoryItem> Files { get; } = [];
    public List<(int SourceIndex, string Relative)> EmptyDirectories { get; } = [];
    public List<SkippedItem> Skipped { get; } = [];
    public List<string> Warnings { get; } = [];
    public long TotalBytes => Files.Sum(f => f.Size);

    public long BytesForSource(int index) => Files.Where(f => f.SourceIndex == index).Sum(f => f.Size);
    public int CountForSource(int index) => Files.Count(f => f.SourceIndex == index);
}

/// <summary>
/// Recorre las fuentes (solo lectura) y decide qué se copiará. No sigue uniones ni
/// enlaces simbólicos (evita bucles y duplicados), omite archivos solo en la nube
/// para no descargarlos sin aviso y aparta los archivos sensibles.
/// </summary>
public static class Inventory
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    public static Task<InventoryResult> ScanAsync(
        IReadOnlyList<BackupSource> sources, IReadOnlyList<string> exclusions, bool includeSensitive,
        IProgress<BackupProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Scan(sources, exclusions, includeSensitive, progress, ct), ct);

    public static InventoryResult Scan(
        IReadOnlyList<BackupSource> sources, IReadOnlyList<string> exclusions, bool includeSensitive,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var result = new InventoryResult();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        var lastReport = Environment.TickCount64;

        for (var s = 0; s < sources.Count; s++)
        {
            var source = sources[s];
            if (!Directory.Exists(source.AbsolutePath))
            {
                result.Skipped.Add(new SkippedItem { Path = Anon(source.AbsolutePath), Reason = SkipReason.NotFound });
                continue;
            }

            // Fuentes solapadas: la segunda vez que aparezca un archivo se omite (se avisa).
            for (var o = 0; o < s; o++)
            {
                if (SafePath.IsSameOrInside(source.AbsolutePath, sources[o].AbsolutePath) && sources[o].Include.Count == 0)
                    result.Warnings.Add($"overlap:{source.Name}|{sources[o].Name}");
            }

            var allExclusions = exclusions.Concat(source.ExtraExclusions).ToList();

            if (source.Include.Count > 0)
            {
                foreach (var include in source.Include)
                {
                    var path = Path.Combine(source.AbsolutePath, include.TrimEnd('/').Replace('/', '\\'));
                    if (include.EndsWith('/'))
                    {
                        if (Directory.Exists(path))
                            Walk(s, source, path, allExclusions, includeSensitive, result, seen, ref bytes, ref lastReport, progress, ct);
                    }
                    else if (File.Exists(path))
                    {
                        AddFile(s, source, new FileInfo(path), allExclusions, includeSensitive, result, seen, ref bytes);
                    }
                }
            }
            else
            {
                Walk(s, source, source.AbsolutePath, allExclusions, includeSensitive, result, seen, ref bytes, ref lastReport, progress, ct);
            }
        }

        progress?.Report(new BackupProgress(BackupPhase.Inventory, bytes, bytes, result.Files.Count, result.Files.Count, null));
        return result;
    }

    private static void Walk(int index, BackupSource source, string start, List<string> exclusions, bool includeSensitive,
        InventoryResult result, HashSet<string> seen, ref long bytes, ref long lastReport,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(start));
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            FileSystemInfo[] children;
            try
            {
                children = dir.GetFileSystemInfos("*", options);
            }
            catch (UnauthorizedAccessException)
            {
                result.Skipped.Add(new SkippedItem { Path = Anon(dir.FullName), Reason = SkipReason.AccessDenied });
                continue;
            }
            catch (IOException ex)
            {
                result.Skipped.Add(new SkippedItem { Path = Anon(dir.FullName), Reason = SkipReason.Error, Detail = ex.Message });
                continue;
            }

            if (children.Length == 0 && !dir.FullName.Equals(source.AbsolutePath, StringComparison.OrdinalIgnoreCase))
            {
                result.EmptyDirectories.Add((index, Rel(source, dir.FullName)));
                continue;
            }

            foreach (var child in children)
            {
                if (child is DirectoryInfo sub)
                {
                    if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint) && sub.LinkTarget is not null)
                    {
                        result.Skipped.Add(new SkippedItem { Path = Anon(sub.FullName), Reason = SkipReason.ReparsePoint });
                        continue;
                    }
                    if (IsExcludedDirectory(sub.Name, exclusions))
                        continue;
                    if (!includeSensitive && SensitivePatterns.DirectoryNames.Contains(sub.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Skipped.Add(new SkippedItem { Path = Anon(sub.FullName), Reason = SkipReason.Sensitive });
                        continue;
                    }
                    stack.Push(sub);
                }
                else if (child is FileInfo file)
                {
                    AddFile(index, source, file, exclusions, includeSensitive, result, seen, ref bytes);
                    var now = Environment.TickCount64;
                    if (now - lastReport > 150)
                    {
                        lastReport = now;
                        progress?.Report(new BackupProgress(BackupPhase.Inventory, bytes, 0, result.Files.Count, 0, Anon(file.FullName)));
                    }
                }
            }
        }
    }

    private static void AddFile(int index, BackupSource source, FileInfo file, List<string> exclusions, bool includeSensitive,
        InventoryResult result, HashSet<string> seen, ref long bytes)
    {
        if (IsExcludedFile(file.Name, exclusions))
            return;

        var attrs = file.Attributes;
        if ((attrs & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0)
        {
            result.Skipped.Add(new SkippedItem { Path = Anon(file.FullName), Reason = SkipReason.CloudOnly });
            return;
        }
        if (attrs.HasFlag(FileAttributes.ReparsePoint) && file.LinkTarget is not null)
        {
            result.Skipped.Add(new SkippedItem { Path = Anon(file.FullName), Reason = SkipReason.ReparsePoint });
            return;
        }
        if (!includeSensitive && IsSensitive(file.Name))
        {
            result.Skipped.Add(new SkippedItem { Path = Anon(file.FullName), Reason = SkipReason.Sensitive });
            return;
        }
        if (!seen.Add(file.FullName))
            return;

        result.Files.Add(new InventoryItem(index, file.FullName, Rel(source, file.FullName), file.Length, file.LastWriteTimeUtc, attrs));
        bytes += file.Length;
    }

    public static bool IsSensitive(string fileName) =>
        SensitivePatterns.FileNames.Any(p => FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true));

    public static bool IsExcludedFile(string name, IEnumerable<string> patterns) =>
        patterns.Where(p => !p.EndsWith('/') && !p.EndsWith('\\'))
            .Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));

    public static bool IsExcludedDirectory(string name, IEnumerable<string> patterns) =>
        patterns.Where(p => p.EndsWith('/') || p.EndsWith('\\'))
            .Any(p => FileSystemName.MatchesSimpleExpression(p.TrimEnd('/', '\\'), name, ignoreCase: true));

    private static string Rel(BackupSource source, string full) =>
        Path.GetRelativePath(source.AbsolutePath, full).Replace('\\', '/');

    private static string Anon(string path) => KnownRoots.Anonymize(path);
}
