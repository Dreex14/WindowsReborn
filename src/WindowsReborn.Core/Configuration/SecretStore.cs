using System.Security.Cryptography;
using System.Text;

namespace WindowsReborn.Core.Configuration;

/// <summary>
/// Cifra datos sensibles (como el token de GitHub) con DPAPI: solo el mismo usuario de
/// Windows en el mismo equipo puede descifrarlos. Nunca se guardan en claro ni se exportan.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsReborn.GitHubToken.v1");

    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>Devuelve el secreto, o null si no hay o ya no se puede descifrar (otro usuario u otro equipo).</summary>
    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
