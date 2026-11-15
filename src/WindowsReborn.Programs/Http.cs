using System.Net.Http.Headers;

namespace WindowsReborn.Programs;

/// <summary>
/// Cliente HTTP compartido. Solo se usa cuando el usuario pulsa un botón que lo
/// requiere (buscar versiones, descargar). No envía cookies ni datos del equipo: el
/// agente de usuario es genérico.
/// </summary>
public static class Http
{
    public static HttpClient CreateDefault()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WindowsReborn", "0.1"));
        return client;
    }

    /// <summary>Solo se aceptan URL HTTPS.</summary>
    public static Uri RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Only HTTPS URLs are allowed.");
        return uri;
    }

    /// <summary>
    /// Dominio registrable aproximado (<c>download.mozilla.org</c> → <c>mozilla.org</c>),
    /// con soporte para sufijos de dos niveles comunes (<c>co.uk</c>, <c>com.es</c>…).
    /// </summary>
    public static string RegistrableDomain(string host)
    {
        var parts = host.ToLowerInvariant().TrimEnd('.').Split('.');
        if (parts.Length <= 2) return string.Join('.', parts);
        var twoLevel = parts[^2] is "co" or "com" or "org" or "net" or "gov" or "ac" or "edu" && parts[^1].Length == 2;
        return string.Join('.', parts[^(twoLevel ? 3 : 2)..]);
    }

    public static bool SameSite(string? urlA, string? urlB)
    {
        if (!Uri.TryCreate(urlA, UriKind.Absolute, out var a) || !Uri.TryCreate(urlB, UriKind.Absolute, out var b))
            return false;
        return RegistrableDomain(a.Host) == RegistrableDomain(b.Host);
    }
}
