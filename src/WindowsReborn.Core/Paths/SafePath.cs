namespace WindowsReborn.Core.Paths;

/// <summary>
/// Utilidades para combinar rutas de forma segura. Toda ruta que proceda de un
/// archivo de backup (y por tanto pueda estar manipulada) debe pasar por aquí antes
/// de escribirse en disco.
/// </summary>
public static class SafePath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Normaliza una ruta relativa de archivo (separadores '/' o '\') y comprueba que no
    /// contiene segmentos peligrosos. Devuelve la ruta con separadores '\'.
    /// </summary>
    /// <exception cref="UnsafePathException">Si la ruta es absoluta, sale de su raíz o usa nombres no válidos.</exception>
    public static string NormalizeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new UnsafePathException(relative, "empty");

        var unified = relative.Replace('/', '\\');
        if (unified.StartsWith('\\') || Path.IsPathRooted(unified) || unified.Contains(':'))
            throw new UnsafePathException(relative, "rooted-or-stream");

        var segments = unified.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new UnsafePathException(relative, "empty");

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                throw new UnsafePathException(relative, "traversal");
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new UnsafePathException(relative, "trailing-dot-or-space");
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new UnsafePathException(relative, "invalid-chars");

            var stem = segment.Split('.')[0];
            if (ReservedNames.Contains(stem))
                throw new UnsafePathException(relative, "reserved-name");
        }

        return string.Join('\\', segments);
    }

    /// <summary>
    /// Combina una raíz autorizada con una ruta relativa no fiable y garantiza que el
    /// resultado queda dentro de la raíz.
    /// </summary>
    public static string Combine(string authorizedRoot, string untrustedRelative)
    {
        var root = Path.GetFullPath(authorizedRoot);
        var rootWithSep = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var relative = NormalizeRelative(untrustedRelative);
        var full = Path.GetFullPath(Path.Combine(root, relative));

        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new UnsafePathException(untrustedRelative, "outside-root");

        return full;
    }

    /// <summary>Indica si <paramref name="candidate"/> es igual o está dentro de <paramref name="folder"/>.</summary>
    public static bool IsSameOrInside(string candidate, string folder)
    {
        var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)) + Path.DirectorySeparatorChar;
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return c.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Convierte una ruta en relativa usando siempre '/' (formato interno del archivo).</summary>
    public static string ToArchivePath(string relative) => relative.Replace('\\', '/');
}

/// <summary>Se lanza cuando una ruta no es segura para escribirse.</summary>
public sealed class UnsafePathException(string path, string reason)
    : Exception($"Unsafe path '{path}': {reason}")
{
    public string UnsafePath { get; } = path;
    public string Reason { get; } = reason;
}
