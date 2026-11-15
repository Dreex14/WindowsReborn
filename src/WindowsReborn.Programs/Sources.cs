using System.IO.Enumeration;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsReborn.Programs;

/// <summary>Error esperable al consultar una fuente (se muestra al usuario con su clave).</summary>
public sealed class SourceException(string key, string? detail = null) : Exception(detail ?? key)
{
    /// <summary>Clave de localización (p. ej. <c>source.no-asset</c>).</summary>
    public string Key { get; } = key;
    public string? Detail { get; } = detail;
}

/// <summary>Consulta la última versión estable en la fuente elegida por el usuario.</summary>
public interface ISourceAdapter
{
    bool CanHandle(ProgramEntry entry);
    Task<ReleaseInfo> GetLatestAsync(ProgramEntry entry, CancellationToken ct);
}

/// <summary>Elige el adaptador según la fuente guardada (nunca la cambia por otra).</summary>
public sealed class SourceResolver(IReadOnlyList<ISourceAdapter> adapters)
{
    public ISourceAdapter? For(ProgramEntry entry) => adapters.FirstOrDefault(a => a.CanHandle(entry));

    public Task<ReleaseInfo> GetLatestAsync(ProgramEntry entry, CancellationToken ct) =>
        (For(entry) ?? throw new SourceException("source.none")).GetLatestAsync(entry, ct);
}

/// <summary>
/// GitHub Releases: <c>releases/latest</c> nunca devuelve borradores ni versiones
/// preliminares. El recurso se elige con el patrón definido por el usuario y se
/// verifica con el <c>digest</c> SHA-256 que publica GitHub.
/// </summary>
public sealed class GitHubReleasesAdapter(HttpClient http) : ISourceAdapter
{
    private static readonly Regex RepoPattern = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);

    public bool CanHandle(ProgramEntry entry) => entry.Source.Kind == SourceKind.GitHub && entry.Source.GitHubRepo is not null;

    public async Task<ReleaseInfo> GetLatestAsync(ProgramEntry entry, CancellationToken ct)
    {
        var repo = entry.Source.GitHubRepo!;
        if (!RepoPattern.IsMatch(repo))
            throw new SourceException("source.bad-repo");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new SourceException("source.no-release");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new SourceException("source.rate-limited");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var assets = root.GetProperty("assets").EnumerateArray()
            .Select(a => new
            {
                Name = a.GetProperty("name").GetString() ?? "",
                Url = a.GetProperty("browser_download_url").GetString() ?? "",
                Digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            })
            .ToList();

        var info = new ReleaseInfo
        {
            Version = tag.TrimStart('v', 'V'),
            Origin = $"GitHub Releases · {repo}",
            ExpectedPublisher = entry.ExpectedPublisher,
        };

        var pattern = entry.Source.AssetPattern;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            // Sin patrón solo se acepta si hay exactamente un instalador candidato.
            var installers = assets.Where(a => IsInstallerName(a.Name)).ToList();
            if (installers.Count != 1)
                throw new SourceException("source.pattern-required", string.Join(", ", assets.Select(a => a.Name)));
            pattern = installers[0].Name;
        }

        var matches = assets.Where(a => FileSystemName.MatchesSimpleExpression(pattern, a.Name, ignoreCase: true)).ToList();
        if (matches.Count == 0)
            throw new SourceException("source.no-asset", string.Join(", ", assets.Select(a => a.Name)));
        if (matches.Count > 1)
            throw new SourceException("source.ambiguous-asset", string.Join(", ", matches.Select(a => a.Name)));

        var asset = matches[0];
        if (!asset.Url.StartsWith($"https://github.com/{repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new SourceException("source.unexpected-url");

        info.DownloadUrl = asset.Url;
        info.FileName = asset.Name;
        if (asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
            info.ExpectedSha256 = asset.Digest[7..];
        return info;
    }

    internal static bool IsInstallerName(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Adaptador oficial de Mozilla Firefox: versión desde <c>product-details.mozilla.org</c>,
/// instalador y <c>SHA256SUMS</c> desde el servidor de publicaciones de Mozilla, y
/// firma esperada «Mozilla Corporation».
/// </summary>
public sealed class MozillaFirefoxAdapter(HttpClient http, Func<string> languageProvider) : ISourceAdapter
{
    public const string Id = "mozilla-firefox";
    private const string Releases = "https://download-installer.cdn.mozilla.net/pub/firefox/releases/";

    public bool CanHandle(ProgramEntry entry) => entry.Source.Kind == SourceKind.Vendor && entry.Source.VendorAdapter == Id;

    public async Task<ReleaseInfo> GetLatestAsync(ProgramEntry entry, CancellationToken ct)
    {
        using var versions = JsonDocument.Parse(await http.GetStringAsync("https://product-details.mozilla.org/1.0/firefox_versions.json", ct).ConfigureAwait(false));
        var version = versions.RootElement.GetProperty("LATEST_FIREFOX_VERSION").GetString();
        if (string.IsNullOrWhiteSpace(version) || !Regex.IsMatch(version, @"^\d+(\.\d+)*$"))
            throw new SourceException("source.version-unknown");

        var platform = entry.Architecture switch { "arm64" => "win64-aarch64", "x86" => "win32", _ => "win64" };
        var lang = languageProvider() == "es" ? "es-ES" : "en-US";
        var fileName = $"Firefox Setup {version}.exe";
        var relative = $"{platform}/{lang}/{fileName}";

        var sums = await http.GetStringAsync($"{Releases}{version}/SHA256SUMS", ct).ConfigureAwait(false);
        string? sha = null;
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim() == relative)
                sha = parts[0];
        }
        if (sha is null)
            throw new SourceException("source.no-hash");

        return new ReleaseInfo
        {
            Version = version,
            DownloadUrl = $"{Releases}{version}/{platform}/{lang}/{Uri.EscapeDataString(fileName)}",
            FileName = fileName,
            ExpectedSha256 = sha,
            ExpectedPublisher = "Mozilla Corporation",
            Origin = "Mozilla · product-details + SHA256SUMS",
        };
    }
}

/// <summary>WinGet: la versión se consulta con <c>winget search --exact</c> y la descarga la hace WinGet.</summary>
public sealed class WinGetAdapter(WinGetClient winget) : ISourceAdapter
{
    public bool CanHandle(ProgramEntry entry) => entry.Source.Kind == SourceKind.WinGet && !string.IsNullOrEmpty(entry.WinGetId);

    public async Task<ReleaseInfo> GetLatestAsync(ProgramEntry entry, CancellationToken ct)
    {
        var version = await winget.GetLatestVersionAsync(entry.WinGetId!, ct).ConfigureAwait(false)
                      ?? throw new SourceException("source.winget-not-found");
        return new ReleaseInfo
        {
            Version = version,
            ViaWinGet = true,
            Origin = $"WinGet · {entry.WinGetId}",
            ExpectedPublisher = entry.ExpectedPublisher,
        };
    }
}

/// <summary>
/// URL directa elegida por el usuario. Se puede descargar, pero no se puede saber
/// con seguridad si es la última versión: se indica como «versión desconocida».
/// </summary>
public sealed class DirectUrlAdapter : ISourceAdapter
{
    public bool CanHandle(ProgramEntry entry) => entry.Source.Kind == SourceKind.DirectUrl && !string.IsNullOrEmpty(entry.Source.DirectUrl);

    public Task<ReleaseInfo> GetLatestAsync(ProgramEntry entry, CancellationToken ct)
    {
        var uri = Http.RequireHttps(entry.Source.DirectUrl!);
        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        return Task.FromResult(new ReleaseInfo
        {
            Version = null,
            DownloadUrl = uri.ToString(),
            FileName = string.IsNullOrWhiteSpace(name) ? null : name,
            ExpectedPublisher = entry.ExpectedPublisher,
            Origin = uri.Host,
        });
    }
}

/// <summary>Adaptadores oficiales disponibles (para la interfaz y la identificación).</summary>
public static class VendorAdapters
{
    /// <summary>Id de WinGet → adaptador oficial del desarrollador.</summary>
    public static IReadOnlyDictionary<string, string> ByWinGetId { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Mozilla.Firefox"] = MozillaFirefoxAdapter.Id,
    };

    public static IReadOnlyList<string> All { get; } = [MozillaFirefoxAdapter.Id];
}
