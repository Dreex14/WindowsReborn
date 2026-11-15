using System.IO.Compression;
using WindowsReborn.Core.Paths;

namespace WindowsReborn.Storage;

/// <summary>
/// Escritor de un backup. Todo se escribe primero en una ubicación temporal
/// (<see cref="TempPath"/>); solo <see cref="Commit"/> la convierte en el resultado
/// definitivo, y únicamente debe llamarse después de verificarla.
/// </summary>
public interface IBackupWriter : IAsyncDisposable
{
    string FinalPath { get; }
    string TempPath { get; }

    /// <summary>Añade un archivo calculando su SHA-256 mientras se escribe.</summary>
    Task<(long Bytes, string Sha256)> AddFileAsync(string archivePath, Stream source, DateTime lastWriteUtc, Action<long>? onBytes, CancellationToken ct);

    Task AddDirectoryAsync(string archivePath, CancellationToken ct);

    /// <summary>Termina la escritura (cierra el ZIP, ejecuta 7-Zip…). Tras esto el temporal es legible.</summary>
    Task CompleteAsync(Action<string>? status, CancellationToken ct);

    /// <summary>Abre el resultado temporal para verificarlo.</summary>
    IBackupReader OpenTempForVerification(string? password);

    /// <summary>Renombra el temporal al nombre definitivo.</summary>
    void Commit();

    /// <summary>Elimina todo lo temporal (cancelación o fallo).</summary>
    void Abort();
}

public static class BackupWriterFactory
{
    public static IBackupWriter Create(BackupFormat format, string finalPath, CompressionPreset preset, string? sevenZipExe, string? password) => format switch
    {
        BackupFormat.Zip => new ZipBackupWriter(finalPath, preset),
        BackupFormat.Folder => new FolderBackupWriter(finalPath),
        BackupFormat.SevenZip => new SevenZipBackupWriter(finalPath, preset,
            sevenZipExe ?? throw new InvalidOperationException("7-Zip not available"), password),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

/// <summary>ZIP con <see cref="System.IO.Compression"/> (ZIP64 automático para archivos grandes).</summary>
public sealed class ZipBackupWriter : IBackupWriter
{
    private readonly CompressionLevel _level;
    private FileStream? _stream;
    private ZipArchive? _zip;

    public ZipBackupWriter(string finalPath, CompressionPreset preset)
    {
        FinalPath = finalPath;
        TempPath = finalPath + ".partial";
        _level = preset switch
        {
            CompressionPreset.Fast => CompressionLevel.Fastest,
            CompressionPreset.Maximum => CompressionLevel.SmallestSize,
            CompressionPreset.Store => CompressionLevel.NoCompression,
            _ => CompressionLevel.Optimal,
        };
        _stream = new FileStream(TempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        _zip = new ZipArchive(_stream, ZipArchiveMode.Create, leaveOpen: false);
    }

    public string FinalPath { get; }
    public string TempPath { get; }

    public async Task<(long Bytes, string Sha256)> AddFileAsync(string archivePath, Stream source, DateTime lastWriteUtc, Action<long>? onBytes, CancellationToken ct)
    {
        var entry = _zip!.CreateEntry(archivePath, _level);
        entry.LastWriteTime = ClampZipDate(lastWriteUtc);
        await using var es = entry.Open();
        return await Hashing.CopyAndHashAsync(source, es, onBytes, ct).ConfigureAwait(false);
    }

    public Task AddDirectoryAsync(string archivePath, CancellationToken ct)
    {
        _zip!.CreateEntry(archivePath.TrimEnd('/') + "/");
        return Task.CompletedTask;
    }

    public Task CompleteAsync(Action<string>? status, CancellationToken ct)
    {
        _zip?.Dispose();
        _zip = null;
        _stream = null;
        return Task.CompletedTask;
    }

    public IBackupReader OpenTempForVerification(string? password) => new ZipBackupReader(TempPath);

    public void Commit() => File.Move(TempPath, FinalPath, overwrite: false);

    public void Abort()
    {
        try { _zip?.Dispose(); } catch { /* El ZIP puede estar incompleto. */ }
        _zip = null;
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        TryDelete(TempPath);
    }

    public ValueTask DisposeAsync()
    {
        if (_zip is not null)
            Abort();
        return ValueTask.CompletedTask;
    }

    // ZIP solo admite fechas entre 1980 y 2107.
    private static DateTimeOffset ClampZipDate(DateTime utc)
    {
        var local = utc.ToLocalTime();
        if (local.Year < 1980) local = new DateTime(1980, 1, 1);
        if (local.Year > 2107) local = new DateTime(2107, 12, 31);
        return new DateTimeOffset(local);
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>Copia sin comprimir en una carpeta.</summary>
public sealed class FolderBackupWriter : IBackupWriter
{
    private bool _committed;

    public FolderBackupWriter(string finalPath)
    {
        FinalPath = finalPath;
        TempPath = finalPath + ".partial";
        if (Directory.Exists(TempPath) || File.Exists(TempPath))
            throw new IOException($"Temporary path already exists: {TempPath}");
        Directory.CreateDirectory(TempPath);
    }

    public string FinalPath { get; }
    public string TempPath { get; }

    public async Task<(long Bytes, string Sha256)> AddFileAsync(string archivePath, Stream source, DateTime lastWriteUtc, Action<long>? onBytes, CancellationToken ct)
    {
        var target = SafePath.Combine(TempPath, archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        (long, string) result;
        await using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
        {
            result = await Hashing.CopyAndHashAsync(source, fs, onBytes, ct).ConfigureAwait(false);
        }
        File.SetLastWriteTimeUtc(target, lastWriteUtc);
        return result;
    }

    public Task AddDirectoryAsync(string archivePath, CancellationToken ct)
    {
        Directory.CreateDirectory(SafePath.Combine(TempPath, archivePath.TrimEnd('/')));
        return Task.CompletedTask;
    }

    public Task CompleteAsync(Action<string>? status, CancellationToken ct) => Task.CompletedTask;

    public IBackupReader OpenTempForVerification(string? password) => new FolderBackupReader(TempPath);

    public void Commit()
    {
        Directory.Move(TempPath, FinalPath);
        _committed = true;
    }

    public void Abort()
    {
        try { if (Directory.Exists(TempPath)) Directory.Delete(TempPath, recursive: true); } catch { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public bool Committed => _committed;
}

/// <summary>
/// 7z mediante el <c>7z.exe</c> instalado por el usuario. Prepara los archivos en una
/// carpeta temporal junto al destino y después la comprime (con AES-256 y nombres
/// cifrados si hay contraseña). La contraseña se pasa por la entrada estándar, nunca
/// en la línea de comandos.
/// </summary>
public sealed class SevenZipBackupWriter : IBackupWriter
{
    private readonly FolderBackupWriter _staging;
    private readonly CompressionPreset _preset;
    private readonly string _sevenZip;
    private readonly string? _password;

    public SevenZipBackupWriter(string finalPath, CompressionPreset preset, string sevenZipExe, string? password)
    {
        FinalPath = finalPath;
        TempPath = finalPath + ".partial";
        _staging = new FolderBackupWriter(finalPath + ".staging");
        _preset = preset;
        _sevenZip = sevenZipExe;
        _password = string.IsNullOrEmpty(password) ? null : password;
    }

    public string FinalPath { get; }
    public string TempPath { get; }

    /// <summary>Carpeta de preparación (necesita espacio adicional igual al tamaño original).</summary>
    public string StagingPath => _staging.TempPath;

    public Task<(long Bytes, string Sha256)> AddFileAsync(string archivePath, Stream source, DateTime lastWriteUtc, Action<long>? onBytes, CancellationToken ct) =>
        _staging.AddFileAsync(archivePath, source, lastWriteUtc, onBytes, ct);

    public Task AddDirectoryAsync(string archivePath, CancellationToken ct) => _staging.AddDirectoryAsync(archivePath, ct);

    public async Task CompleteAsync(Action<string>? status, CancellationToken ct)
    {
        var level = _preset switch
        {
            CompressionPreset.Fast => "-mx=1",
            CompressionPreset.Maximum => "-mx=9",
            CompressionPreset.Store => "-mx=0",
            _ => "-mx=5",
        };
        var args = new List<string> { "a", "-t7z", level, "-mmt=on", "-bsp1", "-y", TempPath, ".\\*" };
        if (_password is not null)
        {
            args.Add("-mhe=on");
            args.Add("-p");
        }

        var result = await SevenZipRunner.RunAsync(_sevenZip, args, _staging.TempPath,
            _password is null ? null : [_password, _password], status, ct).ConfigureAwait(false);
        if (result.ExitCode is not 0 and not 1)
            throw new IOException($"7-Zip failed (exit code {result.ExitCode}).");

        _staging.Abort();
    }

    public IBackupReader OpenTempForVerification(string? password) =>
        SevenZipBackupReader.Open(_sevenZip, TempPath, password ?? _password);

    public void Commit() => File.Move(TempPath, FinalPath, overwrite: false);

    public void Abort()
    {
        _staging.Abort();
        ZipBackupWriter.TryDelete(TempPath);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
