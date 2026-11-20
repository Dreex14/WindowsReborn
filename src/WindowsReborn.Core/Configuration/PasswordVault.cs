using System.Security.Cryptography;
using System.Text;

namespace WindowsReborn.Core.Configuration;

/// <summary>Secreto cifrado con una contraseña (para llevarlo dentro de «Mi configuración»).</summary>
public sealed class PasswordSecret
{
    public string Kdf { get; set; } = PasswordVault.KdfName;
    public int Iterations { get; set; }
    public string Salt { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Data { get; set; } = "";
    /// <summary>Cuenta de GitHub a la que pertenece (no es secreto).</summary>
    public string? User { get; set; }
}

/// <summary>
/// Cifra con AES-256-GCM y una clave derivada de la contraseña con PBKDF2-SHA256.
/// Sin la contraseña el secreto no se puede leer; si se altera el archivo, falla al descifrar.
/// </summary>
public static class PasswordVault
{
    public const string KdfName = "pbkdf2-sha256";
    public const int MinPasswordLength = 8;
    private const int DefaultIterations = 600_000;
    private const int MaxIterations = 5_000_000;
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("WindowsReborn.ExportedSecret.v1");

    public static PasswordSecret Protect(string secret, string password, string? user = null, int iterations = DefaultIterations)
    {
        if (password.Length < MinPasswordLength) throw new ArgumentException("password-too-short", nameof(password));
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = DeriveKey(password, salt, iterations);
        var plain = Encoding.UTF8.GetBytes(secret);
        var data = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plain, data, tag, Aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
        return new PasswordSecret
        {
            Iterations = iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(data),
            User = user,
        };
    }

    /// <summary>El secreto, o null si la contraseña no es correcta o el archivo está dañado.</summary>
    public static string? Unprotect(PasswordSecret? secret, string password)
    {
        if (secret is null || secret.Kdf != KdfName || secret.Iterations is < 100_000 or > MaxIterations) return null;
        byte[]? key = null;
        try
        {
            var salt = Convert.FromBase64String(secret.Salt);
            var nonce = Convert.FromBase64String(secret.Nonce);
            var tag = Convert.FromBase64String(secret.Tag);
            var data = Convert.FromBase64String(secret.Data);
            if (salt.Length < 16 || nonce.Length != 12 || tag.Length != 16 || data.Length > 4096) return null;
            key = DeriveKey(password, salt, secret.Iterations);
            var plain = new byte[data.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, data, tag, plain, Aad);
            var result = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return result;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
}
