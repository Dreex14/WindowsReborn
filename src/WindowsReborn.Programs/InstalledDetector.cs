using Microsoft.Win32;

namespace WindowsReborn.Programs;

public sealed record InstalledApp(string Name, string? Version, string? Publisher);

/// <summary>
/// Detecta programas instalados leyendo (solo lectura) las claves «Uninstall» y,
/// si hay Id de WinGet, preguntando a <c>winget list</c>.
/// </summary>
public sealed class InstalledDetector(WinGetClient winget)
{
    private IReadOnlyList<InstalledApp>? _cache;

    public void Invalidate() => _cache = null;

    public IReadOnlyList<InstalledApp> InstalledApps => _cache ??= ReadUninstallKeys();

    public async Task<(InstallState State, string? Version)> DetectAsync(ProgramEntry entry, bool useWinGet, CancellationToken ct)
    {
        var needle = string.IsNullOrWhiteSpace(entry.DetectName) ? entry.Name : entry.DetectName!;
        var match = Match(InstalledApps, needle);

        if (useWinGet && !string.IsNullOrEmpty(entry.WinGetId))
        {
            try
            {
                var installed = await winget.IsInstalledAsync(entry.WinGetId!, ct).ConfigureAwait(false);
                if (installed == true) return (InstallState.Installed, match?.Version);
                if (installed == false && match is null) return (InstallState.NotInstalled, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // WinGet no disponible: se usa solo el Registro.
            }
        }

        return match is null ? (InstallState.NotInstalled, null) : (InstallState.ProbablyInstalled, match.Version);
    }

    internal static InstalledApp? Match(IEnumerable<InstalledApp> apps, string needle)
    {
        needle = needle.Trim();
        if (needle.Length < 2) return null;
        return apps.FirstOrDefault(a => a.Name.Equals(needle, StringComparison.OrdinalIgnoreCase))
               ?? apps.FirstOrDefault(a => a.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
               ?? apps.FirstOrDefault(a => a.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<InstalledApp> ReadUninstallKeys()
    {
        var list = new List<InstalledApp>();
        void Read(RegistryKey hive, RegistryView view)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
                using var key = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key is null) return;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub?.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display)) continue;
                    if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    list.Add(new InstalledApp(display.Trim(), sub.GetValue("DisplayVersion") as string, sub.GetValue("Publisher") as string));
                }
            }
            catch
            {
                // Lectura opcional.
            }
        }

        Read(Registry.LocalMachine, RegistryView.Registry64);
        Read(Registry.LocalMachine, RegistryView.Registry32);
        Read(Registry.CurrentUser, RegistryView.Default);
        return list.DistinctBy(a => (a.Name, a.Version)).ToList();
    }
}
