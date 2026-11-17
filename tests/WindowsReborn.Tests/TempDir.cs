using WindowsReborn.Backup;
using WindowsReborn.Core.Paths;

namespace WindowsReborn.Tests;

/// <summary>Carpeta temporal aislada por prueba; nunca se tocan carpetas reales del usuario.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "WindowsReborn.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Sub(params string[] parts)
    {
        var path = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    /// <summary>Fuente personalizada que apunta a una carpeta de prueba.</summary>
    public static BackupSource Source(string id, string path, IReadOnlyList<string>? include = null, IReadOnlyList<string>? processes = null) => new()
    {
        Id = id,
        Name = id,
        Kind = include is null ? SourceKind.CustomFolder : SourceKind.AppConfig,
        Root = KnownRoots.MakeCustom(id),
        AbsolutePath = path,
        Include = include ?? [],
        ProcessNames = processes ?? [],
    };
}
