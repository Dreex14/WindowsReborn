using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsReborn.Programs;

namespace WindowsReborn.Tweaks;

/// <summary>Aplicación preinstalada conocida que se puede quitar para el usuario actual.</summary>
public sealed class AppxCandidate
{
    public string Name { get; set; } = "";
    public int Risk { get; set; } = 2;
    public string Es { get; set; } = "";
    public string En { get; set; } = "";
    public string? WarnEs { get; set; }
    public string? WarnEn { get; set; }

    public string Title(string lang) => lang == "es" ? Es : En;
    public string? Warning(string lang) => lang == "es" ? WarnEs : WarnEn;
}

public sealed record InstalledAppx(AppxCandidate Candidate, string PackageFullName, string Version);

/// <summary>
/// Aplicaciones preinstaladas. Solo trabaja con una lista cerrada (<c>appx.json</c>)
/// de aplicaciones de consumo: nunca toca la Tienda, el Instalador de aplicaciones
/// (WinGet), marcos de ejecución ni componentes del sistema. Quita el paquete solo
/// para el usuario actual; se puede volver a instalar desde la Microsoft Store.
/// </summary>
public sealed partial class AppxManager(IProcessRunner runner)
{
    private static readonly Lazy<IReadOnlyList<AppxCandidate>> Loaded = new(() =>
    {
        using var s = typeof(AppxManager).Assembly.GetManifestResourceStream("WindowsReborn.Tweaks.appx.json")!;
        return JsonSerializer.Deserialize<List<AppxCandidate>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip }) ?? [];
    });

    public static IReadOnlyList<AppxCandidate> Candidates => Loaded.Value;

    [GeneratedRegex(@"^[A-Za-z0-9.\-]+$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^[A-Za-z0-9._\-~]+$")]
    private static partial Regex SafeFullName();

    private static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    public async Task<IReadOnlyList<InstalledAppx>> ListInstalledAsync(CancellationToken ct = default)
    {
        var names = string.Join(",", Candidates.Where(c => SafeName().IsMatch(c.Name)).Select(c => $"'{c.Name}'"));
        var script = $"$n=@({names}); Get-AppxPackage | Where-Object {{ $n -contains $_.Name -and -not $_.IsFramework -and -not $_.NonRemovable }} | Select-Object Name,PackageFullName,Version | ConvertTo-Json -Compress";
        var result = await runner.RunHiddenAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], ct);
        return Parse(result.Output);
    }

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
                var candidate = Candidates.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (candidate is not null && SafeFullName().IsMatch(full) && list.All(x => x.Candidate != candidate))
                    list.Add(new InstalledAppx(candidate, full, version));
            }
        }
        catch (JsonException)
        {
        }
        return list.OrderBy(x => x.Candidate.Risk).ThenBy(x => x.Candidate.En).ToList();
    }

    /// <summary>Quita un paquete para el usuario actual. Devuelve <c>true</c> si ya no está instalado.</summary>
    public async Task<bool> RemoveAsync(InstalledAppx app, CancellationToken ct = default)
    {
        if (!SafeFullName().IsMatch(app.PackageFullName) || Candidates.All(c => c != app.Candidate))
            throw new InvalidOperationException("Package not allowed.");
        var script = $"Remove-AppxPackage -Package '{app.PackageFullName}' -ErrorAction Stop";
        var result = await runner.RunHiddenAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], ct);
        Core.Logging.Log.Info($"Appx {app.Candidate.Name} removal exit code {result.ExitCode}");
        return result.ExitCode == 0;
    }

    /// <summary>Búsqueda en la Microsoft Store para reinstalar.</summary>
    public static string StoreSearchUri(AppxCandidate c) => "ms-windows-store://search/?query=" + Uri.EscapeDataString(c.En);
}
