using System.Diagnostics;
using System.Text.RegularExpressions;
using WindowsReborn.Core.Logging;

namespace WindowsReborn.Programs;

public enum InstallOutcome
{
    Installed,
    Updated,
    AlreadyInstalled,
    /// <summary>Se lanzó el instalador oficial; el usuario termina el asistente.</summary>
    InstallerLaunched,
    /// <summary>Se abrió la web oficial (el fabricante no permite descargas automáticas).</summary>
    PageOpened,
    Failed,
    Cancelled,
}

public sealed record InstallResult(InstallOutcome Outcome, string? Detail = null);

/// <summary>Estado de un programa del catálogo en este equipo.</summary>
public sealed record InstalledInfo(bool Installed, string? Version, string? AvailableUpdate);

/// <summary>
/// Instala los programas del catálogo sin que el usuario tenga que configurar nada:
/// WinGet trabaja en segundo plano con la última versión, y los programas que no
/// están en WinGet se descargan de su web oficial (comprobando la firma digital).
/// </summary>
public sealed class SoftwareInstaller(IProcessRunner runner, HttpClient http, Downloader downloader)
{
    /// <summary>WinGet: no hay versión más nueva (ya instalado y al día).</summary>
    public const int NoApplicableUpgrade = unchecked((int)0x8A15002B);
    /// <summary>WinGet: el paquete ya está instalado.</summary>
    public const int PackageAlreadyInstalled = unchecked((int)0x8A150061);
    public const int InstallerRebootRequired = unchecked((int)0x8A150109);

    private string? _newestPythonId;

    /// <summary>
    /// Lee de una vez los programas instalados (<c>winget list</c>) y el registro de
    /// desinstalación. Devuelve el estado de cada programa del catálogo.
    /// </summary>
    public async Task<Dictionary<string, InstalledInfo>> DetectAsync(IEnumerable<CatalogProgram> programs, CancellationToken ct)
    {
        var rows = new List<WinGetListRow>();
        try
        {
            var r = await runner.RunHiddenAsync(WinGetClient.Exe,
                ["list", "--disable-interactivity", "--accept-source-agreements"], ct).ConfigureAwait(false);
            rows.AddRange(ParseList(r.Output));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("winget list failed: " + ex.Message);
        }

        var uninstall = InstalledDetector.ReadUninstallKeys();
        var result = new Dictionary<string, InstalledInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in programs)
        {
            WinGetListRow? row = null;
            if (p.Source is CatalogSource.WinGet or CatalogSource.MsStore && p.WinGetId is not null)
                row = rows.FirstOrDefault(x => x.Id.Equals(p.WinGetId, StringComparison.OrdinalIgnoreCase));
            else if (p.Source == CatalogSource.WinGetNewest && p.WinGetId is not null)
                row = rows.Where(x => x.Id.StartsWith(p.WinGetId + ".", StringComparison.OrdinalIgnoreCase))
                          .OrderByDescending(x => MinorOf(x.Id, p.WinGetId)).FirstOrDefault();

            if (row is not null)
            {
                result[p.Id] = new InstalledInfo(true, row.Version, string.IsNullOrWhiteSpace(row.Available) ? null : row.Available);
                continue;
            }

            // Sin coincidencia en WinGet: se busca en «Programas instalados» (nombre explícito o
            // el nombre del programa como primera palabra, para no confundir «Git» con «GitHub Desktop»).
            var byName = new Regex("^" + Regex.Escape(p.Name) + @"\b", RegexOptions.IgnoreCase);
            var app = p.DetectName is not null
                ? uninstall.FirstOrDefault(a => a.Name.Contains(p.DetectName, StringComparison.OrdinalIgnoreCase))
                : uninstall.FirstOrDefault(a => byName.IsMatch(a.Name));
            result[p.Id] = new InstalledInfo(app is not null, app?.Version, null);
        }
        return result;
    }

    public async Task<InstallResult> InstallAsync(CatalogProgram p, bool isUpdate, IProgress<string>? status, CancellationToken ct)
    {
        try
        {
            switch (p.Source)
            {
                case CatalogSource.WinGet:
                    return await WinGetInstallAsync(p.WinGetId!, p.CustomArgs, isUpdate, ct).ConfigureAwait(false);

                case CatalogSource.MsStore:
                    return await WinGetInstallAsync(p.WinGetId!, null, isUpdate, ct, "msstore").ConfigureAwait(false);

                case CatalogSource.WinGetNewest:
                    var id = await NewestIdAsync(p.WinGetId!, ct).ConfigureAwait(false)
                             ?? throw new SourceException("source.winget-not-found");
                    return await WinGetInstallAsync(id, p.CustomArgs, isUpdate, ct).ConfigureAwait(false);

                case CatalogSource.OfficialPageLink:
                case CatalogSource.DirectLatest:
                    var url = p.Source == CatalogSource.DirectLatest ? p.DownloadUrl! : await FindLinkAsync(p, ct).ConfigureAwait(false);
                    var entry = new ProgramEntry { Id = p.Id, Name = p.Name, ExpectedPublisher = p.Publisher };
                    var release = new ReleaseInfo { DownloadUrl = url, Origin = new Uri(url).Host, ExpectedPublisher = p.Publisher };
                    var folder = Path.Combine(Path.GetTempPath(), "WindowsReborn", "installers");
                    var progress = new Progress<DownloadProgress>(d =>
                        status?.Report(d.BytesTotal is > 0 ? $"{d.BytesDone * 100 / d.BytesTotal.Value}%" : ""));
                    var file = await downloader.DownloadAsync(entry, release, folder, progress, ct).ConfigureAwait(false);
                    status?.Report("");
                    await runner.RunVisibleAsync(file.Path, "", ct).ConfigureAwait(false);
                    return new InstallResult(InstallOutcome.InstallerLaunched);

                default:
                    Process.Start(new ProcessStartInfo(p.Homepage!) { UseShellExecute = true });
                    return new InstallResult(InstallOutcome.PageOpened);
            }
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
            Log.Error($"Install {p.Id} failed", ex);
            return new InstallResult(InstallOutcome.Failed, ex.Message);
        }
    }

    private async Task<InstallResult> WinGetInstallAsync(string id, string? custom, bool isUpdate, CancellationToken ct, string source = "winget")
    {
        var args = new List<string>
        {
            isUpdate ? "upgrade" : "install", "--id", id, "--exact", "--source", source, "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
        };
        if (!string.IsNullOrWhiteSpace(custom))
        {
            args.Add("--custom");
            args.Add(custom);
        }
        var r = await runner.RunHiddenAsync(WinGetClient.Exe, args, ct).ConfigureAwait(false);
        Log.Info($"winget {args[0]} {id} -> 0x{r.ExitCode:X8}");
        return r.ExitCode switch
        {
            0 => new InstallResult(isUpdate ? InstallOutcome.Updated : InstallOutcome.Installed),
            InstallerRebootRequired => new InstallResult(isUpdate ? InstallOutcome.Updated : InstallOutcome.Installed, "reboot"),
            NoApplicableUpgrade or PackageAlreadyInstalled => new InstallResult(InstallOutcome.AlreadyInstalled),
            _ => new InstallResult(InstallOutcome.Failed, $"0x{r.ExitCode:X8}"),
        };
    }

    /// <summary>Id de WinGet con la versión menor más alta (p. ej. Python.Python.3.15).</summary>
    public async Task<string?> NewestIdAsync(string prefix, CancellationToken ct)
    {
        if (_newestPythonId is not null && _newestPythonId.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            return _newestPythonId;
        var r = await runner.RunHiddenAsync(WinGetClient.Exe,
            ["search", "--id", prefix + ".", "--source", "winget", "--disable-interactivity", "--accept-source-agreements"], ct).ConfigureAwait(false);
        // Solo versiones estables: una 3.x que aún está en alfa, beta o rc (3.15.0b2…) no cuenta.
        var best = WinGetClient.ParseTable(r.Output)
            .Where(x => string.IsNullOrWhiteSpace(x.Version) || IsStableVersion(x.Version))
            .Select(x => x.Id)
            .Where(x => Regex.IsMatch(x, "^" + Regex.Escape(prefix) + @"\.\d+$", RegexOptions.IgnoreCase))
            .OrderByDescending(x => MinorOf(x, prefix))
            .FirstOrDefault();
        return _newestPythonId = best;
    }

    internal static bool IsStableVersion(string version) => Regex.IsMatch(version.Trim(), @"^\d+(\.\d+)*$");

    private static int MinorOf(string id, string prefix) =>
        int.TryParse(id.AsSpan(prefix.Length + 1), out var n) ? n : -1;

    /// <summary>Busca en la web oficial el enlace de descarga más reciente.</summary>
    public async Task<string> FindLinkAsync(CatalogProgram p, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, p.Homepage);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var link = Regex.Matches(html, p.LinkPattern!).Select(m => m.Value).Distinct()
            .OrderByDescending(VersionKey).FirstOrDefault();
        return link ?? throw new SourceException("source.no-url");
    }

    private static Version VersionKey(string url)
    {
        var m = Regex.Match(url, @"\d+(\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : new Version(0, 0);
    }

    public sealed record WinGetListRow(string Name, string Id, string Version, string? Available);

    /// <summary>
    /// Interpreta la tabla de <c>winget list</c> (Nombre, Id, Versión, [Disponible], Origen).
    /// Las cabeceras pueden estar traducidas; se usan las posiciones.
    /// </summary>
    public static IReadOnlyList<WinGetListRow> ParseList(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
            .ToList();
        var sep = lines.FindIndex(l => l.Length > 10 && l.Trim().All(c => c == '-'));
        if (sep <= 0) return [];
        var header = lines[sep - 1];
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
                starts.Add(i);
        if (starts.Count < 3) return [];

        var hasAvailable = starts.Count >= 5;
        var rows = new List<WinGetListRow>();
        foreach (var line in lines.Skip(sep + 1))
        {
            if (line.Length <= starts[1]) continue;
            string Col(int idx)
            {
                if (idx >= starts.Count) return "";
                var start = starts[idx];
                var end = idx + 1 < starts.Count ? Math.Min(starts[idx + 1], line.Length) : line.Length;
                return start >= line.Length ? "" : line[start..end].Trim();
            }
            var id = Col(1);
            if (id.Length == 0 || id.Contains(' ') || id.EndsWith('…')) continue;
            rows.Add(new WinGetListRow(Col(0), id, Col(2), hasAvailable ? Col(3) : null));
        }
        return rows;
    }
}
