using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsReborn.Core.Logging;
using WindowsReborn.Core.Paths;
using WindowsReborn.Storage;

namespace WindowsReborn.Programs;

/// <summary>Programa publicado en GitHub Releases (los .exe del usuario).</summary>
public sealed record GitHubApp(
    string Repo,
    string Name,
    string? Description,
    string Version,
    string AssetName,
    string DownloadUrl,
    string? Sha256)
{
    public bool IsInstaller =>
        AssetName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(AssetName, "setup|install", RegexOptions.IgnoreCase);

    public bool IsZip => AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Registro local de las apps de GitHub instaladas por Windows Reborn.</summary>
public sealed class GitHubInstall
{
    public string Version { get; set; } = "";
    public string? ExePath { get; set; }
}

/// <summary>
/// Lista las releases de un usuario de GitHub y descarga siempre la última versión.
/// Los programas portables se guardan en <c>%LOCALAPPDATA%\Programs\Windows Reborn Apps</c>
/// con acceso directo en el menú Inicio; los instaladores se ejecutan.
/// </summary>
public sealed class GitHubAppsClient(HttpClient http, string stateDirectory)
{
    private static readonly Regex RepoPattern = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);
    private static readonly Regex UserPattern = new(@"^[A-Za-z0-9-]{1,39}$", RegexOptions.Compiled);

    private string StatePath => Path.Combine(stateDirectory, "github-apps.json");

    public static string AppsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Windows Reborn Apps");

    public static bool IsValidUser(string? user) => user is not null && UserPattern.IsMatch(user);
    public static bool IsValidRepo(string? repo) =>
        repo is not null && RepoPattern.IsMatch(repo) && repo.Split('/').All(part => part.Trim('.').Length > 0);

    /// <summary>Repositorios públicos del usuario con una release que tenga un .exe/.zip/.msi.</summary>
    public async Task<IReadOnlyList<GitHubApp>> ListUserAppsAsync(string user, IEnumerable<string> extraRepos, CancellationToken ct)
    {
        var repos = new List<(string FullName, string? Description)>();
        if (IsValidUser(user))
        {
            using var doc = await GetJsonAsync($"https://api.github.com/users/{user}/repos?per_page=100&sort=updated", ct).ConfigureAwait(false);
            if (doc is not null)
            {
                foreach (var r in doc.RootElement.EnumerateArray())
                {
                    if (r.GetProperty("fork").GetBoolean() || r.GetProperty("archived").GetBoolean()) continue;
                    repos.Add((r.GetProperty("full_name").GetString()!,
                        r.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null));
                }
            }
        }
        foreach (var extra in extraRepos.Where(IsValidRepo))
            if (!repos.Any(r => r.FullName.Equals(extra, StringComparison.OrdinalIgnoreCase)))
                repos.Add((extra, null));

        var tasks = repos.Select(r => LatestAsync(r.FullName, r.Description, ct)).ToList();
        var apps = await Task.WhenAll(tasks).ConfigureAwait(false);
        return apps.Where(a => a is not null).Select(a => a!).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<GitHubApp?> LatestAsync(string repo, string? description, CancellationToken ct)
    {
        if (!IsValidRepo(repo)) return null;
        using var doc = await GetJsonAsync($"https://api.github.com/repos/{repo}/releases/latest", ct).ConfigureAwait(false);
        if (doc is null) return null;
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var releaseName = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;

        var assets = root.GetProperty("assets").EnumerateArray().Select(a => new
        {
            Name = a.GetProperty("name").GetString() ?? "",
            Url = a.GetProperty("browser_download_url").GetString() ?? "",
            Digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
        }).ToList();

        // Preferencia: .exe x64 → .exe → .msi → .zip de Windows → .zip.
        var asset = assets.Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(a => a.Name.Contains("x64", StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
                    ?? assets.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    ?? assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(a => Regex.IsMatch(a.Name, "win", RegexOptions.IgnoreCase)).FirstOrDefault();
        if (asset is null) return null;
        if (!asset.Url.StartsWith($"https://github.com/{repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            return null;

        var version = tag.TrimStart('v', 'V');
        var name = CleanName(releaseName, version) ?? repo.Split('/')[1].Replace('-', ' ');
        var sha = asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? asset.Digest[7..] : null;
        return new GitHubApp(repo, name, description, version, asset.Name, asset.Url, sha);
    }

    private static string? CleanName(string? releaseName, string version)
    {
        if (string.IsNullOrWhiteSpace(releaseName)) return null;
        var clean = Regex.Replace(releaseName, @"\s*v?" + Regex.Escape(version) + @"\s*$", "", RegexOptions.IgnoreCase).Trim();
        return clean.Length == 0 ? null : clean;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new SourceException("source.rate-limited");
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
    }

    public Dictionary<string, GitHubInstall> LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<Dictionary<string, GitHubInstall>>(File.ReadAllText(StatePath))
                       ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Log.Warn("github-apps.json unreadable: " + ex.Message); }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveState(Dictionary<string, GitHubInstall> state)
    {
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Descarga la última versión y la instala (portable) o ejecuta su instalador.</summary>
    public async Task<InstallResult> InstallAsync(GitHubApp app, bool desktopShortcut, IProgress<string>? status, CancellationToken ct)
    {
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), "WindowsReborn", "github", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var file = Path.Combine(temp, Downloader.Sanitize(app.AssetName));
            await DownloadAsync(app, file, status, ct).ConfigureAwait(false);

            var state = LoadState();
            if (app.IsInstaller)
            {
                var sig = Authenticode.Verify(file);
                if (sig.Status == SignatureStatus.Invalid) throw new SourceException("download.signature-invalid");
                using var p = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                if (p is not null) await p.WaitForExitAsync(ct).ConfigureAwait(false);
                state[app.Repo] = new GitHubInstall { Version = app.Version };
                SaveState(state);
                return new InstallResult(InstallOutcome.InstallerLaunched);
            }

            var folder = SafePath.Combine(AppsRoot, Downloader.Sanitize(app.Repo.Split('/')[1]));
            var wasInstalled = state.ContainsKey(app.Repo);
            CloseRunning(folder);
            if (app.IsZip)
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                Directory.CreateDirectory(folder);
                ZipFile.ExtractToDirectory(file, folder, overwriteFiles: true);
            }
            else
            {
                Directory.CreateDirectory(folder);
                File.Copy(file, Path.Combine(folder, Downloader.Sanitize(app.AssetName)), overwrite: true);
            }
            try { Directory.Delete(temp, true); } catch { }

            var exe = Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories)
                .OrderByDescending(f => Path.GetFileNameWithoutExtension(f).Replace(" ", "")
                    .Equals(app.Name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                .ThenBy(f => f.Count(c => c == '\\'))
                .ThenByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
            if (exe is not null)
            {
                Shortcuts.Create(Path.Combine(Shortcuts.StartMenuPrograms, app.Name + ".lnk"), exe);
                if (desktopShortcut)
                    Shortcuts.Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), app.Name + ".lnk"), exe);
            }
            state[app.Repo] = new GitHubInstall { Version = app.Version, ExePath = exe };
            SaveState(state);
            Log.Info($"GitHub app {app.Repo} {app.Version} installed");
            return new InstallResult(wasInstalled ? InstallOutcome.Updated : InstallOutcome.Installed);
        }
        catch (OperationCanceledException)
        {
            return new InstallResult(InstallOutcome.Cancelled);
        }
        catch (SourceException ex)
        {
            return new InstallResult(InstallOutcome.Failed, ex.Key);
        }
        catch (Exception ex)
        {
            Log.Error($"GitHub install {app.Repo} failed", ex);
            return new InstallResult(InstallOutcome.Failed, ex.Message);
        }
    }

    private async Task DownloadAsync(GitHubApp app, string path, IProgress<string>? status, CancellationToken ct)
    {
        using var response = await http.GetAsync(Http.RequireHttps(app.DownloadUrl), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        string sha;
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
        {
            long done = 0;
            (_, sha) = await Hashing.CopyAndHashAsync(input, output, nBytes =>
            {
                done += nBytes;
                if (total is > 0) status?.Report($"{done * 100 / total.Value}%");
            }, ct).ConfigureAwait(false);
        }
        if (app.Sha256 is not null && !sha.Equals(app.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new SourceException("download.hash-mismatch");
        }
        status?.Report("");
    }

    /// <summary>Cierra la versión antigua si está abierta, para poder reemplazarla.</summary>
    private static void CloseRunning(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var exe in Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories))
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                    {
                        p.CloseMainWindow();
                        if (!p.WaitForExit(5000)) p.Kill();
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
    }
}

/// <summary>Accesos directos .lnk mediante el objeto COM de Windows Script Host.</summary>
public static class Shortcuts
{
    public static string StartMenuPrograms =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs));

    public static void Create(string lnkPath, string target)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(lnkPath);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("Shortcut could not be created: " + ex.Message);
        }
    }
}
