using System.Text.Json;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Logging;

namespace WindowsReborn.Programs;

/// <summary>Resultado de importar una lista de programas.</summary>
public sealed record ImportOutcome(int Added, IReadOnlyList<string> ConflictingSources, IReadOnlyList<string> Duplicates);

/// <summary>Carga y guarda <c>programs.json</c> e importa/exporta listas portables.</summary>
public sealed class ProgramLibraryStore(string configDirectory)
{
    public string FilePath => Path.Combine(configDirectory, ProgramLibrary.FileName);

    public async Task<ProgramLibrary> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return new ProgramLibrary();
        try
        {
            return Normalize(await Json.ReadFileAsync<ProgramLibrary>(FilePath) ?? new ProgramLibrary());
        }
        catch (Exception ex)
        {
            Log.Error("programs.json could not be read", ex);
            // No se sobrescribe: se conserva una copia del archivo dañado.
            File.Copy(FilePath, FilePath + ".damaged", overwrite: true);
            return new ProgramLibrary();
        }
    }

    public Task SaveAsync(ProgramLibrary library) => Json.WriteFileAtomicAsync(FilePath, library);

    /// <summary>Exporta la biblioteca a un archivo portable (sin estado local de descargas).</summary>
    public static async Task ExportAsync(ProgramLibrary library, string path)
    {
        var copy = new ProgramLibrary
        {
            Programs = library.Programs.Select(p =>
            {
                var clone = JsonSerializer.Deserialize<ProgramEntry>(JsonSerializer.Serialize(p, Json.Options), Json.Options)!;
                clone.LastDownloadPath = null;
                return clone;
            }).ToList(),
        };
        await Json.WriteFileAtomicAsync(path, copy);
    }

    public static async Task<ProgramLibrary> ReadListAsync(string path)
    {
        var lib = await Json.ReadFileAsync<ProgramLibrary>(path) ?? throw new InvalidDataException("Empty list.");
        if (lib.SchemaVersion > ProgramLibrary.CurrentSchema)
            throw new NotSupportedException("The list was created by a newer version of Windows Reborn.");
        return Normalize(lib);
    }

    /// <summary>
    /// Añade a <paramref name="target"/> los programas de <paramref name="incoming"/>.
    /// Los que ya existen (mismo Id de WinGet o mismo nombre) no se tocan: si su
    /// fuente difiere, se informa para que el usuario decida, pero nunca se sustituye.
    /// </summary>
    public static ImportOutcome Merge(ProgramLibrary target, ProgramLibrary incoming)
    {
        var added = 0;
        var conflicts = new List<string>();
        var duplicates = new List<string>();
        foreach (var entry in incoming.Programs)
        {
            var existing = target.Programs.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(p.WinGetId) && string.Equals(p.WinGetId, entry.WinGetId, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(p.Name, entry.Name, StringComparison.CurrentCultureIgnoreCase));
            if (existing is null)
            {
                entry.LastDownloadPath = null;
                if (target.Programs.Any(p => p.Id == entry.Id))
                    entry.Id = Guid.NewGuid().ToString("N");
                target.Programs.Add(entry);
                added++;
            }
            else if (!SameSource(existing.Source, entry.Source))
            {
                conflicts.Add(existing.Name);
            }
            else
            {
                duplicates.Add(existing.Name);
            }
        }
        return new ImportOutcome(added, conflicts, duplicates);
    }

    private static bool SameSource(ProgramSource a, ProgramSource b) =>
        a.Kind == b.Kind &&
        string.Equals(a.GitHubRepo, b.GitHubRepo, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.DirectUrl, b.DirectUrl, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.VendorAdapter, b.VendorAdapter, StringComparison.OrdinalIgnoreCase);

    private static ProgramLibrary Normalize(ProgramLibrary lib)
    {
        lib.Programs ??= [];
        foreach (var p in lib.Programs)
        {
            p.Source ??= new ProgramSource();
            p.ConfigAdapters ??= [];
            if (string.IsNullOrWhiteSpace(p.Id))
                p.Id = Guid.NewGuid().ToString("N");
        }
        lib.Programs.RemoveAll(p => string.IsNullOrWhiteSpace(p.Name));
        return lib;
    }
}
