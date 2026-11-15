using WindowsReborn.Core.Paths;

namespace WindowsReborn.Core.Logging;

/// <summary>
/// Registro local de operaciones. Solo escribe en disco local; las rutas se
/// anonimizan con <see cref="KnownRoots.Anonymize"/> antes de escribirse.
/// Nunca se envía nada por red.
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static string? _directory;

    public static void Initialize(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        _directory = logsDirectory;
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    /// <summary>Anonimiza una ruta para el registro.</summary>
    public static string P(string path)
    {
        try { return KnownRoots.Anonymize(path); }
        catch { return "<path>"; }
    }

    private static void Write(string level, string message)
    {
        if (_directory is null)
            return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                File.AppendAllText(Path.Combine(_directory, $"{DateTime.Now:yyyy-MM-dd}.log"), line);
            }
            catch
            {
                // El registro nunca debe interrumpir una operación.
            }
        }
    }
}
