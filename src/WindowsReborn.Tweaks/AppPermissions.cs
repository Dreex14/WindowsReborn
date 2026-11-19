using Microsoft.Win32;
using WindowsReborn.Core.Logging;

namespace WindowsReborn.Tweaks;

/// <summary>
/// Permisos de cada app (cámara, micrófono, ubicación…) tal como los guarda Windows en
/// <c>HKCU\…\CapabilityAccessManager\ConsentStore</c>. Se copian para dejarlos igual tras
/// formatear. Solo se guardan los valores «Allow»/«Deny», nunca el historial de uso.
/// </summary>
public static class AppPermissions
{
    public const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string NonPackaged = "NonPackaged";
    private const string ProfileToken = "%USERPROFILE%";

    /// <summary>Permisos que se copian app por app.</summary>
    public static IReadOnlyList<string> Capabilities { get; } =
    [
        "webcam", "microphone", "location", "contacts", "appointments", "userAccountInformation", "appDiagnostics",
        "documentsLibrary", "picturesLibrary", "videosLibrary", "musicLibrary", "downloadsFolder", "broadFileSystemAccess",
        "userNotificationListener", "phoneCall", "phoneCallHistory", "chat", "email", "userDataTasks", "radios",
        "bluetoothSync", "graphicsCaptureProgrammatic", "graphicsCaptureWithoutBorder", "activity", "humanPresence", "systemAIModels",
    ];

    /// <summary>Permiso → (app → Allow/Deny). Las apps de escritorio van como <c>NonPackaged\ruta</c>.</summary>
    public static Dictionary<string, Dictionary<string, string>> Capture()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        using var store = Registry.CurrentUser.OpenSubKey(ConsentStore);
        if (store is null) return result;
        foreach (var capability in Capabilities)
        {
            using var cap = store.OpenSubKey(capability);
            if (cap is null) continue;
            var apps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in cap.GetSubKeyNames())
            {
                if (app.Equals(NonPackaged, StringComparison.OrdinalIgnoreCase))
                {
                    using var np = cap.OpenSubKey(app);
                    if (np is null) continue;
                    foreach (var exe in np.GetSubKeyNames())
                        if (ReadValue(np, exe) is { } v) apps[NonPackaged + "\\" + ToPortable(exe)] = v;
                }
                else if (ReadValue(cap, app) is { } v)
                {
                    apps[app] = v;
                }
            }
            if (apps.Count > 0) result[capability] = apps;
        }
        return result;
    }

    /// <summary>Escribe los permisos guardados. Devuelve cuántos se aplicaron.</summary>
    public static int Apply(IReadOnlyDictionary<string, Dictionary<string, string>> permissions)
    {
        var count = 0;
        foreach (var (capability, apps) in permissions)
        {
            if (!Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var (app, value) in apps)
            {
                if (value is not ("Allow" or "Deny") || !IsSafeName(app)) continue;
                var relative = app.StartsWith(NonPackaged + "\\", StringComparison.OrdinalIgnoreCase)
                    ? NonPackaged + "\\" + FromPortable(app[(NonPackaged.Length + 1)..])
                    : app;
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey($@"{ConsentStore}\{capability}\{relative}");
                    key.SetValue("Value", value, RegistryValueKind.String);
                    count++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"App permission {capability} could not be restored: {ex.Message}");
                }
            }
        }
        return count;
    }

    public static int CountApps(IReadOnlyDictionary<string, Dictionary<string, string>>? permissions) =>
        permissions?.Values.SelectMany(a => a.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;

    private static string? ReadValue(RegistryKey parent, string name)
    {
        try
        {
            using var k = parent.OpenSubKey(name);
            return k?.GetValue("Value") as string is "Allow" or "Deny" ? (string)k.GetValue("Value")! : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Un nombre de app nunca puede salir de su permiso (sin «..» ni barras extra).</summary>
    private static bool IsSafeName(string app)
    {
        var parts = app.Split('\\');
        return parts.Length is 1 or 2 && parts.All(p => p.Length > 0 && p != "." && p != ".." && p.IndexOfAny(['/', '*', '?', '"', '<', '>', '|']) < 0) &&
               (parts.Length == 1 || parts[0].Equals(NonPackaged, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Las rutas de las apps de escritorio usan «#» en lugar de «\» (C:#Users#Ana#…).</summary>
    private static string ProfileHashed => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '#');

    internal static string ToPortable(string exe) =>
        exe.StartsWith(ProfileHashed + "#", StringComparison.OrdinalIgnoreCase) ? ProfileToken + exe[ProfileHashed.Length..] : exe;

    internal static string FromPortable(string exe) =>
        exe.StartsWith(ProfileToken, StringComparison.OrdinalIgnoreCase) ? ProfileHashed + exe[ProfileToken.Length..] : exe;
}
