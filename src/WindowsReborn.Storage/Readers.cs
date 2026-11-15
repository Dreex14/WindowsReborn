using System.IO.Compression;
using System.Text.Json;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Paths;

namespace WindowsReborn.Storage;

/// <summary>Lector de un backup en cualquiera de sus formatos.</summary>
public interface IBackupReader : IDisposable
{
    /// <summary>Ruta del backup que se está leyendo.</summary>
    string Source { get; }

    bool Contains(string archivePath);

    IEnumerable<string> EntryNames { get; }

    Stream OpenEntry(string archivePath);
}

public static class BackupReader
{
    /// <summary>Abre un backup detectando su formato.</summary>
    public static IBackupReader Open(string path, string? sevenZipExe = null, string? password = null)
    {
        if (Path.GetFileName(path).Equals(BackupManifest.FileName, StringComparison.OrdinalIgnoreCase))
            path = Path.GetDirectoryName(path)!;

        return BackupFormatExtensions.Detect(path) switch
        {
            BackupFormat.Zip => new ZipBackupReader(path),
            BackupFormat.Folder => new FolderBackupReader(path),
            BackupFormat.SevenZip => SevenZipBackupReader.Open(
                sevenZipExe ?? throw new InvalidOperationException("7-Zip not available"), path, password),
            _ => throw new InvalidDataException("Unrecognized backup format."),
        };
    }

    /// <summary>Lee y valida el manifiesto.</summary>
    public static BackupManifest ReadManifest(IBackupReader reader)
    {
        if (!reader.Contains(BackupManifest.FileName))
            throw new InvalidDataException("manifest.json not found: this is not a Windows Reborn backup or it is incomplete.");

        using var stream = reader.OpenEntry(BackupManifest.FileName);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, Json.Options)
                       ?? throw new InvalidDataException("manifest.json is empty.");

        if (manifest.FormatVersion < 1)
            throw new InvalidDataException("Invalid manifest format version.");
        if (manifest.FormatVersion > BackupManifest.CurrentFormatVersion)
            throw new NotSupportedException($"This backup uses format {manifest.FormatVersion}; this version of Windows Reborn supports up to {BackupManifest.CurrentFormatVersion}.");

        // Toda ruta del manifiesto debe ser segura antes de usarse.
        foreach (var file in manifest.Files)
            SafePath.NormalizeRelative(file.Path);

        return manifest;
    }

    public static CreationReport? ReadReport(IBackupReader reader)
    {
        if (!reader.Contains(BackupManifest.ReportFileName))
            return null;
        try
        {
            using var stream = reader.OpenEntry(BackupManifest.ReportFileName);
            return JsonSerializer.Deserialize<CreationReport>(stream, Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class ZipBackupReader : IBackupReader
{
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;

    public ZipBackupReader(string path)
    {
        Source = path;
        try
        {
            _zip = ZipFile.OpenRead(path);
            _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _zip.Entries)
            {
                if (e.FullName.EndsWith('/'))
                    continue;
                _entries[e.FullName.Replace('\\', '/')] = e;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The ZIP file is damaged or incomplete.", ex);
        }
    }

    public string Source { get; }
    public IEnumerable<string> EntryNames => _entries.Keys;
    public bool Contains(string archivePath) => _entries.ContainsKey(archivePath);

    public Stream OpenEntry(string archivePath) =>
        _entries.TryGetValue(archivePath, out var e) ? e.Open() : throw new FileNotFoundException(archivePath);

    public void Dispose() => _zip.Dispose();
}

public sealed class FolderBackupReader : IBackupReader
{
    private readonly bool _deleteOnDispose;

    public FolderBackupReader(string root, bool deleteOnDispose = false)
    {
        Source = root;
        _deleteOnDispose = deleteOnDispose;
    }

    public string Source { get; }

    public IEnumerable<string> EntryNames =>
        Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Source, f).Replace('\\', '/'));

    public bool Contains(string archivePath)
    {
        try { return File.Exists(SafePath.Combine(Source, archivePath)); }
        catch (UnsafePathException) { return false; }
    }

    public Stream OpenEntry(string archivePath) =>
        new FileStream(SafePath.Combine(Source, archivePath), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);

    public void Dispose()
    {
        if (_deleteOnDispose)
        {
            try { Directory.Delete(Source, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Lee un 7z extrayéndolo con 7-Zip a una carpeta temporal que se borra al cerrar.
/// </summary>
public static class SevenZipBackupReader
{
    public static IBackupReader Open(string sevenZipExe, string archive, string? password)
    {
        var temp = Path.Combine(Path.GetTempPath(), "WindowsReborn", "7z-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var args = new List<string> { "x", "-y", "-bsp0", archive, "-o" + temp };
        // Con contraseña, «-p» sin valor hace que 7-Zip la lea de la entrada estándar.
        // Sin contraseña, la entrada estándar se cierra y un 7z cifrado falla en vez de esperar.
        if (password is not null)
            args.Add("-p");

        var result = SevenZipRunner.RunAsync(sevenZipExe, args, temp,
            password is null ? null : [password], null, CancellationToken.None).GetAwaiter().GetResult();
        if (result.ExitCode != 0)
        {
            try { Directory.Delete(temp, true); } catch { }
            throw new InvalidDataException(result.StdErr.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? "Wrong password or damaged 7z archive."
                : $"The 7z archive could not be read (7-Zip exit code {result.ExitCode}).");
        }
        return new FolderBackupReader(temp, deleteOnDispose: true);
    }
}
