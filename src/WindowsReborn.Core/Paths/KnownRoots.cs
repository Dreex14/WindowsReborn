using System.Runtime.InteropServices;

namespace WindowsReborn.Core.Paths;

/// <summary>
/// Raíces simbólicas que se guardan en el manifiesto en lugar de rutas absolutas.
/// Permiten restaurar en otro nombre de usuario, otra unidad o con carpetas
/// redirigidas (OneDrive), porque se resuelven de nuevo en el equipo de destino.
/// </summary>
public static class KnownRoots
{
    public const string Desktop = "{Desktop}";
    public const string Documents = "{Documents}";
    public const string Downloads = "{Downloads}";
    public const string Pictures = "{Pictures}";
    public const string Videos = "{Videos}";
    public const string Music = "{Music}";
    public const string SavedGames = "{SavedGames}";
    public const string UserProfile = "{UserProfile}";
    public const string RoamingAppData = "{RoamingAppData}";
    public const string LocalAppData = "{LocalAppData}";

    /// <summary>Prefijo de las raíces personalizadas: <c>{Custom:Nombre}</c>.</summary>
    public const string CustomPrefix = "{Custom:";

    private static readonly Dictionary<string, Guid> FolderIds = new(StringComparer.OrdinalIgnoreCase)
    {
        [Desktop] = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
        [Documents] = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        [Downloads] = new("374DE290-123F-4565-9164-39C4925E467B"),
        [Pictures] = new("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        [Videos] = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
        [Music] = new("4BD8D571-6D19-48D3-BE97-422220080E43"),
        [SavedGames] = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"),
        [UserProfile] = new("5E6C858F-0E22-4760-9AFE-EA3317B67173"),
        [RoamingAppData] = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"),
        [LocalAppData] = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"),
    };

    /// <summary>Raíces estándar, en el orden en que se muestran.</summary>
    public static IReadOnlyList<string> Standard { get; } =
        [Desktop, Documents, Downloads, Pictures, Videos, Music, SavedGames, UserProfile, RoamingAppData, LocalAppData];

    public static bool IsCustom(string token) => token.StartsWith(CustomPrefix, StringComparison.Ordinal) && token.EndsWith('}');

    public static string MakeCustom(string name)
    {
        var clean = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_').ToArray()).Trim();
        return CustomPrefix + (clean.Length == 0 ? "Folder" : clean) + "}";
    }

    public static string CustomName(string token) => IsCustom(token) ? token[CustomPrefix.Length..^1] : token;

    /// <summary>
    /// Resuelve una raíz estándar a la ruta real del usuario actual. Devuelve
    /// <c>null</c> si la raíz es personalizada o la carpeta no existe en este equipo.
    /// </summary>
    public static string? Resolve(string token)
    {
        if (!FolderIds.TryGetValue(token, out var id))
            return null;

        var hr = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pathPtr);
        try
        {
            if (hr != 0 || pathPtr == IntPtr.Zero)
                return null;
            return Marshal.PtrToStringUni(pathPtr);
        }
        finally
        {
            if (pathPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    /// <summary>
    /// Sustituye el principio de una ruta por la raíz conocida más específica, para
    /// registrar operaciones sin exponer el nombre de usuario.
    /// </summary>
    public static string Anonymize(string path)
    {
        string? bestToken = null;
        string? bestPath = null;
        foreach (var (token, resolved) in ResolvedCache.Value)
        {
            if (!SafePath.IsSameOrInside(path, resolved))
                continue;
            if (bestPath is null || resolved.Length > bestPath.Length)
            {
                bestToken = token;
                bestPath = resolved;
            }
        }

        if (bestToken is null || bestPath is null)
            return path;
        var rest = Path.GetRelativePath(bestPath, path);
        return rest == "." ? bestToken : bestToken + "\\" + rest;
    }

    /// <summary>
    /// Ruta legible para mostrar en pantalla sin exponer el nombre de usuario:
    /// <c>C:\Users\Ana\Documents</c> → <c>%USERPROFILE%\Documents</c>.
    /// </summary>
    public static string Friendly(string path)
    {
        var profile = Resolve(UserProfile);
        if (profile is null || !SafePath.IsSameOrInside(path, profile))
            return path;
        var rest = System.IO.Path.GetRelativePath(profile, path);
        return rest == "." ? "%USERPROFILE%" : "%USERPROFILE%\\" + rest;
    }

    private static readonly Lazy<List<(string Token, string Path)>> ResolvedCache = new(() =>
        Standard.Select(t => (t, Resolve(t))).Where(x => x.Item2 is not null).Select(x => (x.t, x.Item2!)).ToList());

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
