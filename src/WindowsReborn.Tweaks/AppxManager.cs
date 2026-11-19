using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsReborn.Programs;

namespace WindowsReborn.Tweaks;

/// <summary>Programa preinstalado conocido que se puede quitar.</summary>
public sealed class AppxCandidate
{
    /// <summary>Nombre del paquete (Appx) o Id de WinGet (si <see cref="Method"/> es <c>winget</c>).</summary>
    public string Name { get; set; } = "";
    public int Risk { get; set; } = 2;
    /// <summary>Viene marcado para quitar (basura sin utilidad para casi nadie).</summary>
    public bool Recommended { get; set; }
    /// <summary><c>appx</c> (paquete de la Store) o <c>winget</c> (programa de escritorio como OneDrive).</summary>
    public string Method { get; set; } = "appx";
    /// <summary><c>microsoft</c>, <c>third</c> (patrocinadas) u <c>oem</c> (del fabricante del PC).</summary>
    public string Group { get; set; } = "microsoft";
    public string Es { get; set; } = "";
    public string En { get; set; } = "";
    public string? WarnEs { get; set; }
    public string? WarnEn { get; set; }

    public bool IsWinGet => Method.Equals("winget", StringComparison.OrdinalIgnoreCase);
    public string Title(string lang) => lang == "es" ? Es : En;
    public string? Warning(string lang) => lang == "es" ? WarnEs : WarnEn;
}

public sealed record InstalledAppx(AppxCandidate Candidate, string PackageFullName, string Version);

/// <summary>
/// Desinstalador de programas preinstalados. Solo trabaja con una lista cerrada
/// (<c>appx.json</c>): nunca toca la Tienda, el Instalador de aplicaciones (WinGet),
/// marcos de ejecución, códecs ni componentes del sistema. Las apps de la Store se quitan
/// para el usuario actual (y, si se pide, también para usuarios nuevos); los programas de
/// escritorio como OneDrive se desinstalan con WinGet. Todo se puede volver a instalar.
/// </summary>
public sealed partial class AppxManager(IProcessRunner runner)
{
    private static readonly Lazy<IReadOnlyList<AppxCandidate>> Loaded = new(() =>
    {
        using var s = typeof(AppxManager).Assembly.GetManifestResourceStream("WindowsReborn.Tweaks.appx.json")!;
        return JsonSerializer.Deserialize<List<AppxCandidate>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? [];
    });

    public static IReadOnlyList<AppxCandidate> Candidates => Loaded.Value;

    public static AppxCandidate? Find(string name) => Candidates.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Nombres de paquete; admiten «*» para los que llevan un prefijo distinto en cada equipo.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9.\-*]+$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^[A-Za-z0-9.\-]+$")]
    private static partial Regex SafeWinGetId();

    /// <summary>¿El paquete instalado corresponde a este programa de la lista?</summary>
    public static bool Matches(AppxCandidate c, string packageName) =>
        c.Name.Contains('*')
            ? System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(c.Name, packageName, ignoreCase: true)
            : c.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9._\-~]+$")]
    private static partial Regex SafeFullName();

    private static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>Programas de la lista que están instalados en este equipo.</summary>
    public async Task<IReadOnlyList<InstalledAppx>> ListInstalledAsync(CancellationToken ct = default)
    {
        // Se leen todos los paquetes quitables y se filtran aquí con la lista (admite patrones).
        const string script = "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable } | Select-Object Name,PackageFullName,Version | ConvertTo-Json -Compress";
        var result = await runner.RunHiddenAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], ct);
        var list = Parse(result.Output).ToList();

        if (Candidates.Any(c => c.IsWinGet))
        {
            try
            {
                var r = await runner.RunHiddenAsync(WinGetClient.Exe, ["list", "--disable-interactivity", "--accept-source-agreements"], ct);
                var rows = SoftwareInstaller.ParseList(r.Output);
                foreach (var c in Candidates.Where(c => c.IsWinGet))
                {
                    var row = rows.FirstOrDefault(x => x.Id.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
                    if (row is not null) list.Add(new InstalledAppx(c, c.Name, row.Version));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Core.Logging.Log.Warn("winget list failed: " + ex.Message);
            }
        }
        return Sort(list);
    }

    private static IReadOnlyList<InstalledAppx> Sort(IEnumerable<InstalledAppx> list) =>
        list.OrderByDescending(x => x.Candidate.Recommended).ThenBy(x => x.Candidate.Risk).ThenBy(x => x.Candidate.Es, StringComparer.CurrentCultureIgnoreCase).ToList();

    internal static IReadOnlyList<InstalledAppx> Parse(string json)
    {
        var list = new List<InstalledAppx>();
        json = json.Trim();
        if (json.Length == 0) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            foreach (var e in items)
            {
                var name = e.GetProperty("Name").GetString() ?? "";
                var full = e.GetProperty("PackageFullName").GetString() ?? "";
                var version = e.TryGetProperty("Version", out var v) ? v.ToString() : "";
                var candidate = Candidates.FirstOrDefault(c => !c.IsWinGet && Matches(c, name));
                if (candidate is not null && SafeFullName().IsMatch(full) && list.All(x => x.Candidate != candidate))
                    list.Add(new InstalledAppx(candidate, full, version));
            }
        }
        catch (JsonException)
        {
        }
        return Sort(list);
    }

    /// <summary>Quita un programa. Devuelve <c>true</c> si ya no está instalado.</summary>
    public async Task<bool> RemoveAsync(InstalledAppx app, CancellationToken ct = default)
    {
        if (Candidates.All(c => c != app.Candidate))
            throw new InvalidOperationException("Package not allowed.");

        if (app.Candidate.IsWinGet)
        {
            if (!SafeWinGetId().IsMatch(app.Candidate.Name)) throw new InvalidOperationException("Package not allowed.");
            var r = await runner.RunHiddenAsync(WinGetClient.Exe,
                ["uninstall", "--id", app.Candidate.Name, "--exact", "--silent", "--accept-source-agreements", "--disable-interactivity"], ct);
            Core.Logging.Log.Info($"winget uninstall {app.Candidate.Name} exit code 0x{r.ExitCode:X8}");
            return r.ExitCode == 0;
        }

        if (!SafeFullName().IsMatch(app.PackageFullName))
            throw new InvalidOperationException("Package not allowed.");
        var script = $"Remove-AppxPackage -Package '{app.PackageFullName}' -ErrorAction Stop";
        var result = await runner.RunHiddenAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], ct);
        Core.Logging.Log.Info($"Appx {app.Candidate.Name} removal exit code {result.ExitCode}");
        return result.ExitCode == 0;
    }

    /// <summary>Script que quita los paquetes «aprovisionados» (los que Windows instala a cada usuario nuevo).</summary>
    public static string ProvisionedScript(IEnumerable<string> names)
    {
        var safe = names.Where(n => SafeName().IsMatch(n) && Candidates.Any(c => !c.IsWinGet && c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(n => $"'{n}'").ToList();
        if (safe.Count == 0) return "";
        return $"$n=@({string.Join(",", safe)}); $all = Get-AppxProvisionedPackage -Online; " +
               "foreach ($p in $n) { $all | Where-Object { $_.DisplayName -like $p } | " +
               "ForEach-Object { Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -ErrorAction SilentlyContinue | Out-Null } }";
    }

    /// <summary>
    /// Evita que estos programas vuelvan a instalarse para usuarios nuevos o tras una
    /// actualización grande de Windows. Pide permiso de administrador una vez.
    /// </summary>
    public static async Task<bool> RemoveProvisionedAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        var script = ProvisionedScript(names);
        if (script.Length == 0) return true;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo(PowerShell)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded },
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync(ct);
            Core.Logging.Log.Info($"Provisioned packages removal exit code {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new ElevationCancelledException();
        }
    }

    /// <summary>Para reinstalar: búsqueda en la Microsoft Store (o la web de WinGet).</summary>
    public static string StoreSearchUri(AppxCandidate c) => "ms-windows-store://search/?query=" + Uri.EscapeDataString(c.En);
}
