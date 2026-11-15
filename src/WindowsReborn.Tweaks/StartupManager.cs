using WindowsReborn.Core.Paths;

namespace WindowsReborn.Tweaks;

public enum StartupLocation
{
    UserRun,
    MachineRun,
    MachineRun32,
    UserStartupFolder,
    CommonStartupFolder,
}

public sealed record StartupEntry(string Name, string Command, StartupLocation Location, bool Enabled)
{
    /// <summary>Entradas de seguridad de Windows que no se ofrecen para desactivar.</summary>
    private static readonly string[] ProtectedNames = ["SecurityHealth", "Windows Defender", "WindowsDefender"];

    public bool RequiresAdmin => Location is StartupLocation.MachineRun or StartupLocation.MachineRun32 or StartupLocation.CommonStartupFolder;
    public bool IsProtected => ProtectedNames.Contains(Name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Aplicaciones que arrancan con Windows. Activa o desactiva con el mismo mecanismo
/// que el Administrador de tareas (<c>StartupApproved</c>): no borra la entrada,
/// así que siempre se puede volver a activar.
/// </summary>
public sealed class StartupManager(IRegistryReader registry, IRegistryWriter writer)
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static bool IsStartupApprovedKey(RegHive hive, string key) =>
        key.Equals(Approved + @"\Run", StringComparison.OrdinalIgnoreCase) ||
        key.Equals(Approved + @"\Run32", StringComparison.OrdinalIgnoreCase) ||
        key.Equals(Approved + @"\StartupFolder", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<StartupEntry> List(string? userStartupFolder = null, string? commonStartupFolder = null)
    {
        var list = new List<StartupEntry>();
        AddRegistry(list, RegHive.CurrentUser, Run, StartupLocation.UserRun);
        AddRegistry(list, RegHive.LocalMachine, Run, StartupLocation.MachineRun);
        AddRegistry(list, RegHive.LocalMachine, Run32, StartupLocation.MachineRun32);
        AddFolder(list, userStartupFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupLocation.UserStartupFolder);
        AddFolder(list, commonStartupFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupLocation.CommonStartupFolder);
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void AddRegistry(List<StartupEntry> list, RegHive hive, string key, StartupLocation location)
    {
        foreach (var name in registry.ValueNames(hive, key))
        {
            if (name.Length == 0) continue;
            var command = registry.Read(hive, key, name)?.Data ?? "";
            list.Add(new StartupEntry(name, Display(command), location, IsEnabled(location, name)));
        }
    }

    private void AddFolder(List<StartupEntry> list, string folder, StartupLocation location)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new StartupEntry(name, Display(file), location, IsEnabled(location, name)));
        }
    }

    /// <summary>Texto del comando sin el nombre de usuario (no interpreta la ruta: puede llevar argumentos o variables).</summary>
    internal static string Display(string command)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = command.Trim();
        return profile.Length > 3 ? text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase) : text;
    }

    private bool IsEnabled(StartupLocation location, string name)
    {
        var (hive, key) = ApprovedKey(location);
        var v = registry.Read(hive, key, name);
        if (v is null || v.Kind != RegKind.Binary || v.Data.Length < 2) return true;
        var first = Convert.ToByte(v.Data[..2], 16);
        return (first & 1) == 0; // 02/06 = activado, 03/07 = desactivado
    }

    private static (RegHive Hive, string Key) ApprovedKey(StartupLocation location) => location switch
    {
        StartupLocation.UserRun => (RegHive.CurrentUser, Approved + @"\Run"),
        StartupLocation.MachineRun => (RegHive.LocalMachine, Approved + @"\Run"),
        StartupLocation.MachineRun32 => (RegHive.LocalMachine, Approved + @"\Run32"),
        StartupLocation.UserStartupFolder => (RegHive.CurrentUser, Approved + @"\StartupFolder"),
        _ => (RegHive.LocalMachine, Approved + @"\StartupFolder"),
    };

    public async Task SetEnabledAsync(StartupEntry entry, bool enabled, CancellationToken ct = default)
    {
        if (entry.IsProtected)
            throw new InvalidOperationException("Windows security entries are not changed.");
        var (hive, key) = ApprovedKey(entry.Location);
        var data = enabled
            ? "020000000000000000000000"
            : "03000000" + Convert.ToHexString(BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()));
        await writer.WriteAsync([new RegOp(RegOpType.SetValue, hive, key, entry.Name, RegKind.Binary, data)], false, ct);
        Core.Logging.Log.Info($"Startup entry {(enabled ? "enabled" : "disabled")} ({entry.Location})");
    }
}
