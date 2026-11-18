using System.Diagnostics;
using System.Text.Json;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Paths;
using WindowsReborn.Storage;

namespace WindowsReborn.Programs;

/// <summary>Índice de instaladores guardados (<c>installers/index.json</c> o carpeta de recuperación).</summary>
public sealed class InstallerIndex
{
    public const string FileName = "index.json";
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }
    public List<InstallerIndexEntry> Installers { get; set; } = [];
}

public sealed class InstallerIndexEntry
{
    public string ProgramId { get; set; } = "";
    public string ProgramName { get; set; } = "";
    public string? Version { get; set; }
    /// <summary>Ruta relativa dentro de la carpeta de instaladores (con '/').</summary>
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool HashVerified { get; set; }
    public SignatureStatus Signature { get; set; }
    public string? Signer { get; set; }
    public string Origin { get; set; } = "";
    public DateTime DownloadedUtc { get; set; }
}

/// <summary>
/// Gestiona los instaladores preparados para formatear: los incorpora a un backup
/// (sección <c>installers/</c>) o a una carpeta de recuperación, y los recupera
/// verificando de nuevo hash y firma antes de ejecutarlos.
/// </summary>
public static class InstallerStore
{
    /// <summary>Crea los archivos extra para incluir los instaladores en un backup.</summary>
    public static (List<(string ArchivePath, string SourcePath)> Files, byte[] IndexJson) PrepareForBackup(IEnumerable<DownloadedInstaller> installers)
    {
        var index = new InstallerIndex { CreatedUtc = DateTime.UtcNow };
        var files = new List<(string, string)>();
        foreach (var inst in installers)
        {
            var folder = Downloader.Sanitize(inst.ProgramName) + "-" + inst.ProgramId[..Math.Min(8, inst.ProgramId.Length)];
            var relative = $"{folder}/{Path.GetFileName(inst.Path)}";
            files.Add(($"{Sections.Installers}/{relative}", inst.Path));
            index.Installers.Add(ToIndex(inst, relative));
        }
        return (files, JsonSerializer.SerializeToUtf8Bytes(index, Json.Options));
    }

    /// <summary>Copia los instaladores a una carpeta de recuperación elegida por el usuario.</summary>
    public static async Task<string> CopyToFolderAsync(IEnumerable<DownloadedInstaller> installers, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var indexPath = Path.Combine(folder, FileName);
        var index = File.Exists(indexPath) ? await Json.ReadFileAsync<InstallerIndex>(indexPath, ct) ?? new() : new InstallerIndex();
        index.CreatedUtc = DateTime.UtcNow;
        foreach (var inst in installers)
        {
            var sub = Downloader.Sanitize(inst.ProgramName) + "-" + inst.ProgramId[..Math.Min(8, inst.ProgramId.Length)];
            var relative = $"{sub}/{Path.GetFileName(inst.Path)}";
            var target = SafePath.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(inst.Path), StringComparison.OrdinalIgnoreCase))
                File.Copy(inst.Path, target, overwrite: true);
            if (await Hashing.HashFileAsync(target, ct) != inst.Sha256)
                throw new IOException("Copy verification failed.");
            index.Installers.RemoveAll(i => i.ProgramId == inst.ProgramId);
            index.Installers.Add(ToIndex(inst, relative));
        }
        await Json.WriteFileAtomicAsync(indexPath, index, ct);
        return indexPath;
    }

    public const string FileName = InstallerIndex.FileName;

    /// <summary>Lee los instaladores verificados que hay en una carpeta (según su índice).</summary>
    public static async Task<List<DownloadedInstaller>> LoadFolderAsync(string folder, CancellationToken ct = default)
    {
        var indexPath = Path.Combine(folder, FileName);
        if (!File.Exists(indexPath)) return [];
        InstallerIndex? index;
        try { index = await Json.ReadFileAsync<InstallerIndex>(indexPath, ct); }
        catch (JsonException) { return []; }
        var list = new List<DownloadedInstaller>();
        foreach (var e in index?.Installers ?? [])
        {
            string path;
            try { path = SafePath.Combine(folder, e.File); }
            catch (UnsafePathException) { continue; }
            if (!File.Exists(path)) continue;
            list.Add(new DownloadedInstaller
            {
                ProgramId = e.ProgramId, ProgramName = e.ProgramName, Version = e.Version, Path = path, Sha256 = e.Sha256,
                HashVerified = e.HashVerified, Signature = e.Signature, Signer = e.Signer, PublisherMatches = true,
                Origin = e.Origin, DownloadedUtc = e.DownloadedUtc,
            });
        }
        return list;
    }

    public static InstallerIndex? ReadIndexFromBackup(IBackupReader reader)
    {
        var path = $"{Sections.Installers}/{InstallerIndex.FileName}";
        if (!reader.Contains(path)) return null;
        using var s = reader.OpenEntry(path);
        return JsonSerializer.Deserialize<InstallerIndex>(s, Json.Options);
    }

    /// <summary>
    /// Extrae un instalador del backup a una carpeta temporal y vuelve a comprobar hash
    /// y firma. Devuelve la ruta lista para ejecutarse tras la confirmación del usuario.
    /// </summary>
    public static async Task<(string Path, SignatureInfo Signature)> ExtractFromBackupAsync(IBackupReader reader, InstallerIndexEntry entry,
        string tempFolder, CancellationToken ct)
    {
        var archivePath = $"{Sections.Installers}/{SafePath.NormalizeRelative(entry.File).Replace('\\', '/')}";
        var target = SafePath.Combine(tempFolder, entry.File);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using (var input = reader.OpenEntry(archivePath))
        await using (var output = File.Create(target))
        {
            var (_, sha) = await Hashing.CopyAndHashAsync(input, output, null, ct);
            if (!string.Equals(sha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                output.Close();
                File.Delete(target);
                throw new SourceException("download.hash-mismatch");
            }
        }
        var signature = Authenticode.Verify(target);
        if (signature.Status == SignatureStatus.Invalid)
        {
            File.Delete(target);
            throw new SourceException("download.signature-invalid", signature.Signer);
        }
        return (target, signature);
    }

    /// <summary>
    /// Abre un instalador para que el usuario lo complete. Solo debe llamarse después de
    /// una confirmación explícita en la interfaz. Devuelve el código de salida.
    /// </summary>
    public static async Task<int> RunInstallerAsync(string path, CancellationToken ct)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        ProcessStartInfo psi = ext == ".msi"
            ? new ProcessStartInfo("msiexec.exe") { ArgumentList = { "/i", path }, UseShellExecute = true }
            : new ProcessStartInfo(path) { UseShellExecute = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Installer could not be started.");
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    public static bool IsSuccessExitCode(int code) => code is 0 or 3010 or 1641;

    private static InstallerIndexEntry ToIndex(DownloadedInstaller inst, string relative) => new()
    {
        ProgramId = inst.ProgramId,
        ProgramName = inst.ProgramName,
        Version = inst.Version,
        File = relative,
        Sha256 = inst.Sha256,
        HashVerified = inst.HashVerified,
        Signature = inst.Signature,
        Signer = inst.Signer,
        Origin = inst.Origin,
        DownloadedUtc = inst.DownloadedUtc,
    };
}
