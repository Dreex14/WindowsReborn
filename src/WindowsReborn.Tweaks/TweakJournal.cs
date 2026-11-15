using WindowsReborn.Core.Configuration;

namespace WindowsReborn.Tweaks;

/// <summary>Valor original de un ajuste antes de que Windows Reborn lo cambiara.</summary>
public sealed class OriginalValue
{
    public RegHive Hive { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public RegKind Kind { get; set; }
    public string? Data { get; set; }
    /// <summary>Primera clave que no existía y que creó el cambio (se borra al revertir).</summary>
    public string? CreatedKey { get; set; }
}

public sealed class JournalEntry
{
    public string TweakId { get; set; } = "";
    public string AppliedOption { get; set; } = "";
    public DateTime AppliedUtc { get; set; }
    public int WindowsBuild { get; set; }
    public List<OriginalValue> Originals { get; set; } = [];
}

/// <summary>
/// Diario local de cambios (<c>state/tweak-journal.json</c>). Guarda el valor que
/// había antes del <b>primer</b> cambio de cada ajuste, para revertir al estado
/// real del equipo y no a un valor «por defecto» supuesto. Es propio de esta
/// instalación de Windows y no se copia en los backups.
/// </summary>
public sealed class TweakJournal
{
    public const string FileName = "tweak-journal.json";
    private readonly string? _path;
    private Dictionary<string, JournalEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public TweakJournal(string? path = null)
    {
        _path = path;
    }

    public static async Task<TweakJournal> LoadAsync(string path)
    {
        var journal = new TweakJournal(path);
        if (File.Exists(path))
        {
            try
            {
                var list = await Json.ReadFileAsync<List<JournalEntry>>(path) ?? [];
                journal._entries = list.ToDictionary(e => e.TweakId, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Core.Logging.Log.Error("Tweak journal could not be read", ex);
            }
        }
        return journal;
    }

    public JournalEntry? Get(string tweakId) => _entries.GetValueOrDefault(tweakId);

    public IReadOnlyCollection<JournalEntry> Entries => _entries.Values;

    public void Set(JournalEntry entry) => _entries[entry.TweakId] = entry;

    public void Remove(string tweakId) => _entries.Remove(tweakId);

    public Task SaveAsync() =>
        _path is null ? Task.CompletedTask : Json.WriteFileAtomicAsync(_path, _entries.Values.OrderBy(e => e.TweakId).ToList());
}

/// <summary>
/// Preferencias de ajustes elegidas por el usuario (<c>tweaks.json</c>, viaja en
/// los backups). Tras formatear se ofrecen para aplicarlas de nuevo.
/// </summary>
public sealed class TweakPreferences
{
    public const string FileName = "tweaks.json";
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Id del ajuste → opción elegida.</summary>
    public Dictionary<string, string> Selections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<TweakPreferences> LoadAsync(string path)
    {
        if (!File.Exists(path)) return new TweakPreferences();
        try
        {
            var p = await Json.ReadFileAsync<TweakPreferences>(path) ?? new TweakPreferences();
            p.Selections = new Dictionary<string, string>(p.Selections, StringComparer.OrdinalIgnoreCase);
            return p;
        }
        catch
        {
            return new TweakPreferences();
        }
    }

    public Task SaveAsync(string path) => Json.WriteFileAtomicAsync(path, this);
}
