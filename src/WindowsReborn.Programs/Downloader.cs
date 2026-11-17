using WindowsReborn.Core.Logging;
using WindowsReborn.Core.Paths;
using WindowsReborn.Storage;

namespace WindowsReborn.Programs;

public sealed record DownloadProgress(long BytesDone, long? BytesTotal);

/// <summary>Instalador descargado y verificado.</summary>
public sealed class DownloadedInstaller
{
    public string ProgramId { get; set; } = "";
    public string ProgramName { get; set; } = "";
    public string? Version { get; set; }
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>El hash coincide con el publicado por la fuente (o lo comprobó WinGet).</summary>
    public bool HashVerified { get; set; }
    public SignatureStatus Signature { get; set; }
    public string? Signer { get; set; }
    public bool PublisherMatches { get; set; }
    public string Origin { get; set; } = "";
    public DateTime DownloadedUtc { get; set; }
}

/// <summary>
/// Descarga instaladores solo por HTTPS a un archivo temporal, comprueba el SHA-256
/// publicado y la firma Authenticode, y solo entonces lo deja con su nombre final.
/// Un archivo con hash distinto o firma inválida se elimina. Nunca ejecuta nada.
/// </summary>
public sealed class Downloader(HttpClient http, WinGetClient winget)
{
    public async Task<DownloadedInstaller> DownloadAsync(ProgramEntry entry, ReleaseInfo release, string folder,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var programFolder = SafePath.Combine(folder, Sanitize(entry.Name));
        Directory.CreateDirectory(programFolder);

        string path;
        string sha;
        bool hashVerified;
        var version = release.Version;

        if (release.ViaWinGet)
        {
            progress?.Report(new DownloadProgress(0, null));
            var (file, wingetVersion) = await winget.DownloadAsync(entry.WinGetId!, entry.Architecture, entry.InstallerType, programFolder, ct).ConfigureAwait(false);
            path = file;
            version = wingetVersion ?? version;
            sha = await Hashing.HashFileAsync(path, ct).ConfigureAwait(false);
            hashVerified = true; // WinGet aborta si el hash no coincide con su manifiesto.
        }
        else
        {
            var uri = Http.RequireHttps(release.DownloadUrl ?? throw new SourceException("source.no-url"));
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                throw new SourceException("source.insecure-redirect");

            var fileName = release.FileName ?? response.Content.Headers.ContentDisposition?.FileNameStar
                           ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                           ?? System.IO.Path.GetFileName(response.RequestMessage?.RequestUri?.AbsolutePath ?? "")
                           ?? "installer.bin";
            fileName = Sanitize(fileName);
            if (string.IsNullOrWhiteSpace(System.IO.Path.GetExtension(fileName)))
                fileName += ".bin";
            path = SafePath.Combine(programFolder, fileName);
            var temp = path + ".partial";
            var total = response.Content.Headers.ContentLength;

            try
            {
                await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
                {
                    long done = 0;
                    var last = 0L;
                    (_, sha) = await Hashing.CopyAndHashAsync(input, output, n =>
                    {
                        done += n;
                        var now = Environment.TickCount64;
                        if (now - last > 100) { last = now; progress?.Report(new DownloadProgress(done, total)); }
                    }, ct).ConfigureAwait(false);
                }

                if (release.ExpectedSha256 is not null && !string.Equals(sha, release.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new SourceException("download.hash-mismatch");
                hashVerified = release.ExpectedSha256 is not null;
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }

        var signature = Authenticode.Verify(path);
        if (signature.Status == SignatureStatus.Invalid)
        {
            try { File.Delete(path); } catch { }
            throw new SourceException("download.signature-invalid", signature.Signer);
        }

        var expectedPublisher = release.ExpectedPublisher ?? entry.ExpectedPublisher;
        var result = new DownloadedInstaller
        {
            ProgramId = entry.Id,
            ProgramName = entry.Name,
            Version = version,
            Path = path,
            Sha256 = sha,
            HashVerified = hashVerified,
            Signature = signature.Status,
            Signer = signature.Signer,
            PublisherMatches = Authenticode.PublisherMatches(signature, expectedPublisher),
            Origin = release.Origin,
            DownloadedUtc = DateTime.UtcNow,
        };

        if (!result.PublisherMatches && !string.IsNullOrWhiteSpace(expectedPublisher))
        {
            try { File.Delete(path); } catch { }
            throw new SourceException("download.publisher-mismatch", signature.Signer);
        }

        Log.Info($"Downloaded {entry.Name} {version}: hashVerified={hashVerified}, signature={signature.Status}");
        return result;
    }

    internal static string Sanitize(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "program";
        try { SafePath.NormalizeRelative(clean); }
        catch (UnsafePathException) { clean = "_" + clean; }
        return clean.Length > 80 ? clean[..80] : clean;
    }
}
