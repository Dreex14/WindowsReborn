using System.Buffers;
using System.Security.Cryptography;

namespace WindowsReborn.Storage;

/// <summary>Copia de flujos calculando SHA-256 al vuelo y notificando bytes reales.</summary>
public static class Hashing
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Copia <paramref name="source"/> en <paramref name="destination"/> (si no es nulo)
    /// y devuelve el número de bytes y el SHA-256 en hexadecimal en minúsculas.
    /// </summary>
    public static async Task<(long Bytes, string Sha256)> CopyAndHashAsync(
        Stream source, Stream? destination, Action<long>? onBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                if (destination is not null)
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                onBytes?.Invoke(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var (_, sha) = await CopyAndHashAsync(fs, null, null, ct).ConfigureAwait(false);
        return sha;
    }

    public static string HashBytes(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
