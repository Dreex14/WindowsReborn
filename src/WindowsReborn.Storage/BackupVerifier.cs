namespace WindowsReborn.Storage;

/// <summary>Resultado de verificar un backup contra su manifiesto.</summary>
public sealed class VerificationResult
{
    public int CheckedFiles { get; set; }
    public long CheckedBytes { get; set; }
    public List<string> Missing { get; } = [];
    public List<string> HashMismatch { get; } = [];
    public List<string> Unreadable { get; } = [];

    public bool IsValid => Missing.Count == 0 && HashMismatch.Count == 0 && Unreadable.Count == 0;
}

public sealed record VerificationProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, string? Current);

/// <summary>Recalcula el SHA-256 de cada entrada y lo compara con el manifiesto.</summary>
public static class BackupVerifier
{
    public static async Task<VerificationResult> VerifyAsync(
        IBackupReader reader, BackupManifest manifest, IEnumerable<FileRecord>? only,
        IProgress<VerificationProgress>? progress, CancellationToken ct)
    {
        var files = (only ?? manifest.Files).ToList();
        var total = files.Sum(f => f.Size);
        var result = new VerificationResult();
        long done = 0;
        var lastReport = Environment.TickCount64;

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            if (!reader.Contains(file.Path))
            {
                result.Missing.Add(file.Path);
                done += file.Size;
                continue;
            }

            try
            {
                await using var stream = reader.OpenEntry(file.Path);
                var (bytes, sha) = await Hashing.CopyAndHashAsync(stream, null, n =>
                {
                    done += n;
                    var now = Environment.TickCount64;
                    if (now - lastReport > 100)
                    {
                        lastReport = now;
                        progress?.Report(new VerificationProgress(done, total, i, files.Count, file.Path));
                    }
                }, ct).ConfigureAwait(false);

                if (bytes != file.Size || !string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    result.HashMismatch.Add(file.Path);
                result.CheckedBytes += bytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                result.Unreadable.Add(file.Path);
            }

            result.CheckedFiles++;
        }

        progress?.Report(new VerificationProgress(total, total, files.Count, files.Count, null));
        return result;
    }
}
