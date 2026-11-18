using System.Text.RegularExpressions;

namespace WindowsReborn.Programs;

/// <summary>Propuesta de fuente para un programa nuevo. El usuario la confirma o la cambia.</summary>
public sealed class SourceProposal
{
    public ProgramSource Source { get; set; } = new();
    public string? Homepage { get; set; }
    public string? PublisherUrl { get; set; }
    public string? WinGetId { get; set; }
    public string? LatestVersion { get; set; }
    public List<string> Notes { get; } = [];
}

/// <summary>
/// Intenta identificar la fuente oficial y el método de descarga más fiable de un
/// programa, con este orden: adaptador oficial del desarrollador → GitHub Releases
/// enlazado desde el manifiesto de WinGet → WinGet → página oficial (solo abrir).
/// Solo consulta fuentes públicas y solo cuando el usuario lo pide.
/// </summary>
public sealed partial class SourceIdentifier(HttpClient http, WinGetClient winget)
{
    private const string ManifestsRaw = "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/";

    public Task<IReadOnlyList<WinGetPackage>> SearchAsync(string name, CancellationToken ct) => winget.SearchAsync(name, ct);

    /// <summary>Analiza un Id de WinGet elegido por el usuario y propone la mejor fuente.</summary>
    public async Task<SourceProposal> ProposeAsync(string winGetId, string? userHomepage, CancellationToken ct)
    {
        var proposal = new SourceProposal { WinGetId = winGetId, Homepage = userHomepage };
        proposal.LatestVersion = await winget.GetLatestVersionAsync(winGetId, ct).ConfigureAwait(false);
        if (proposal.LatestVersion is null)
            throw new SourceException("source.winget-not-found");

        var manifest = await TryReadManifestAsync(winGetId, proposal.LatestVersion, ct).ConfigureAwait(false);
        if (manifest is not null)
        {
            proposal.PublisherUrl = manifest.PublisherUrl;
            proposal.Homepage ??= manifest.PackageUrl ?? manifest.PublisherUrl;
        }
        else
        {
            proposal.Notes.Add("identify.manifest-unavailable");
        }

        // 1. Adaptador oficial del desarrollador.
        if (VendorAdapters.ByWinGetId.TryGetValue(winGetId, out var vendor))
        {
            proposal.Source = new ProgramSource
            {
                Kind = SourceKind.Vendor,
                VendorAdapter = vendor,
                Trust = SourceTrust.Verified,
                TrustReason = "trust.vendor-adapter",
            };
            return proposal;
        }

        // 2. Repositorio de GitHub enlazado desde el manifiesto (instalador, web o publicador).
        var repo = manifest?.InstallerUrls.Select(GitHubRepoFromReleaseUrl).FirstOrDefault(r => r is not null)
                   ?? GitHubRepoFromUrl(manifest?.PackageUrl) ?? GitHubRepoFromUrl(manifest?.PublisherUrl);
        if (repo is not null && manifest!.InstallerUrls.Any(u => GitHubRepoFromReleaseUrl(u) == repo))
        {
            proposal.Source = new ProgramSource
            {
                Kind = SourceKind.GitHub,
                GitHubRepo = repo,
                Trust = SourceTrust.Verified,
                TrustReason = "trust.github-in-manifest",
            };
            proposal.Notes.Add("identify.github-pattern-needed");
            return proposal;
        }

        // 3. WinGet.
        var verified = manifest is not null && userHomepage is not null &&
                       (Http.SameSite(userHomepage, manifest.PackageUrl) || Http.SameSite(userHomepage, manifest.PublisherUrl));
        proposal.Source = new ProgramSource
        {
            Kind = SourceKind.WinGet,
            Trust = verified ? SourceTrust.Verified : SourceTrust.UserSelected,
            TrustReason = verified ? "trust.winget-homepage-match" : "trust.winget-selected",
        };
        return proposal;
    }

    /// <summary>Clasifica una URL escrita por el usuario. Nunca la sustituye.</summary>
    public static ProgramSource FromUserUrl(string url, string? homepage)
    {
        var uri = Http.RequireHttps(url);
        var repo = GitHubRepoFromUrl(url);
        if (repo is not null)
        {
            return new ProgramSource
            {
                Kind = SourceKind.GitHub,
                GitHubRepo = repo,
                Trust = SourceTrust.UserSelected,
                TrustReason = "trust.user-url",
            };
        }
        var sameSite = homepage is not null && Http.SameSite(uri.ToString(), homepage);
        return new ProgramSource
        {
            Kind = SourceKind.DirectUrl,
            DirectUrl = uri.ToString(),
            Trust = sameSite ? SourceTrust.UserSelected : SourceTrust.Unverified,
            TrustReason = sameSite ? "trust.user-url-homepage" : "trust.user-url-other-domain",
        };
    }

    internal sealed record WinGetManifestInfo(string? PublisherUrl, string? PackageUrl, IReadOnlyList<string> InstallerUrls);

    private async Task<WinGetManifestInfo?> TryReadManifestAsync(string id, string version, CancellationToken ct)
    {
        if (!IdPattern().IsMatch(id) || !VersionPattern().IsMatch(version))
            return null;
        var folder = $"{ManifestsRaw}{char.ToLowerInvariant(id[0])}/{id.Replace('.', '/')}/{version}/";
        try
        {
            var root = await http.GetStringAsync($"{folder}{id}.yaml", ct).ConfigureAwait(false);
            var locale = YamlValue(root, "DefaultLocale") ?? "en-US";
            var localeYaml = await http.GetStringAsync($"{folder}{id}.locale.{locale}.yaml", ct).ConfigureAwait(false);
            var installerYaml = await http.GetStringAsync($"{folder}{id}.installer.yaml", ct).ConfigureAwait(false);
            var installerUrls = InstallerUrlPattern().Matches(installerYaml).Select(m => m.Groups[1].Value.Trim('\'', '"')).ToList();
            return new WinGetManifestInfo(YamlValue(localeYaml, "PublisherUrl"), YamlValue(localeYaml, "PackageUrl"), installerUrls);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    internal static string? YamlValue(string yaml, string key)
    {
        var m = Regex.Match(yaml, $@"^{Regex.Escape(key)}:\s*(.+?)\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim('\'', '"') : null;
    }

    internal static string? GitHubRepoFromReleaseUrl(string url)
    {
        var m = GitHubReleaseUrl().Match(url);
        return m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : null;
    }

    internal static string? GitHubRepoFromUrl(string? url)
    {
        if (url is null) return null;
        var m = GitHubAnyUrl().Match(url);
        return m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value.Replace(".git", "")}" : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]*$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"InstallerUrl:\s*(\S+)")]
    private static partial Regex InstallerUrlPattern();

    [GeneratedRegex(@"^https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/releases/download/", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubReleaseUrl();

    [GeneratedRegex(@"^https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubAnyUrl();
}
