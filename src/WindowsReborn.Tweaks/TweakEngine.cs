using WindowsReborn.Core.Logging;

namespace WindowsReborn.Tweaks;

/// <summary>
/// Motor de ajustes. Para cada cambio: comprueba la compatibilidad con la
/// compilación de Windows, lee el estado real, guarda los valores originales en el
/// diario, escribe, vuelve a leer para verificar y, si la verificación falla,
/// deshace lo escrito. Revertir devuelve los valores originales registrados.
/// </summary>
public sealed class TweakEngine(IRegistryReader registry, IRegistryWriter writer, TweakJournal journal, int windowsBuild)
{
    public TweakJournal Journal => journal;

    public TweakStatus Evaluate(TweakDefinition t)
    {
        if (t.Kind == TweakKind.Info)
            return new TweakStatus(Support.InfoOnly, null, false);
        if (!t.SupportsBuild(windowsBuild))
            return new TweakStatus(Support.WrongBuild, null, false);
        if (t.Settings.Any(s => s.RequireKey && !registry.KeyExists(s.Hive, s.Key)))
            return new TweakStatus(Support.Missing, null, false);
        return new TweakStatus(Support.Supported, CurrentOption(t), journal.Get(t.Id) is not null,
            t.Settings.All(s => registry.Read(s.Hive, s.Key, s.Name) is null));
    }

    private string? CurrentOption(TweakDefinition t)
    {
        foreach (var option in t.Options)
        {
            if (t.Settings.All(s => Matches(s, option)))
                return option;
        }
        return null;
    }

    private bool Matches(RegSetting s, string option)
    {
        if (!s.Values.TryGetValue(option, out var expected))
            return true; // la opción no controla este valor
        var actual = registry.Read(s.Hive, s.Key, s.Name);
        if (actual is null && s.Default is not null) return expected is not null && Same(s.Kind, s.Default, expected);
        if (expected is null) return actual is null;
        // Sin valor = lo que trae Windows, que en un conmutador es «desactivado».
        if (actual is null && option == TweakDefinition.Off) return true;
        return actual is not null && Same(s.Kind, actual.Data, expected);
    }

    private static bool Same(RegKind kind, string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Aplica varias opciones en un solo lote (una sola petición de elevación).</summary>
    public async Task<IReadOnlyList<TweakResult>> ApplyAsync(
        IReadOnlyList<(TweakDefinition Tweak, string Option)> changes, bool createRestorePoint, CancellationToken ct = default)
    {
        var results = new List<TweakResult>();
        var pending = new List<(TweakDefinition Tweak, string Option, List<OriginalValue> Originals)>();
        var ops = new List<RegOp>();

        foreach (var (t, option) in changes)
        {
            var status = Evaluate(t);
            if (!status.IsSupported || !t.Options.Contains(option))
            {
                results.Add(new TweakResult(t.Id, TweakOutcome.Unsupported));
                continue;
            }
            if (status.CurrentOption == option)
            {
                results.Add(new TweakResult(t.Id, TweakOutcome.Unchanged));
                continue;
            }
            var originals = Capture(t);
            pending.Add((t, option, originals));
            ops.AddRange(OpsFor(t, option));
        }

        if (ops.Count == 0)
            return results;

        await writer.WriteAsync(ops, createRestorePoint, ct);

        foreach (var (t, option, originals) in pending)
        {
            if (CurrentOption(t) == option)
            {
                if (journal.Get(t.Id) is { } existing)
                {
                    existing.AppliedOption = option;
                    existing.AppliedUtc = DateTime.UtcNow;
                }
                else
                {
                    journal.Set(new JournalEntry
                    {
                        TweakId = t.Id, AppliedOption = option, AppliedUtc = DateTime.UtcNow,
                        WindowsBuild = windowsBuild, Originals = originals,
                    });
                }
                results.Add(new TweakResult(t.Id, TweakOutcome.Applied));
                Log.Info($"Tweak {t.Id} set to {option}");
            }
            else
            {
                // Windows rechazó o ignoró el cambio: se deja como estaba.
                try { await writer.WriteAsync(RestoreOps(originals), false, ct); } catch { }
                results.Add(new TweakResult(t.Id, TweakOutcome.Failed, "verify"));
                Log.Warn($"Tweak {t.Id} could not be verified and was rolled back");
            }
        }

        await journal.SaveAsync();
        return results;
    }

    public async Task<TweakResult> ApplyAsync(TweakDefinition t, string option, bool createRestorePoint, CancellationToken ct = default) =>
        (await ApplyAsync([(t, option)], createRestorePoint, ct)).Single();

    /// <summary>
    /// Devuelve el ajuste a los valores originales guardados. Si no hay registro (el
    /// cambio lo hizo otra herramienta), un conmutador vuelve a su valor de Windows (<c>off</c>).
    /// </summary>
    public async Task<TweakResult> RevertAsync(TweakDefinition t, bool createRestorePoint, CancellationToken ct = default)
    {
        var status = Evaluate(t);
        if (!status.IsSupported)
            return new TweakResult(t.Id, TweakOutcome.Unsupported);

        var entry = journal.Get(t.Id);
        if (entry is null)
        {
            if (t.Kind != TweakKind.Toggle)
                return new TweakResult(t.Id, TweakOutcome.Unchanged);
            var r = await ApplyAsync(t, TweakDefinition.Off, createRestorePoint, ct);
            return r with { Outcome = r.Outcome == TweakOutcome.Applied ? TweakOutcome.Reverted : r.Outcome };
        }

        await writer.WriteAsync(RestoreOps(entry.Originals), createRestorePoint, ct);
        var ok = entry.Originals.All(o =>
        {
            var v = registry.Read(o.Hive, o.Key, o.Name);
            return o.Existed ? v is not null && Same(o.Kind, v.Data, o.Data ?? "") : v is null;
        });
        if (!ok)
        {
            Log.Warn($"Tweak {t.Id} could not be fully reverted");
            return new TweakResult(t.Id, TweakOutcome.Failed, "verify");
        }
        journal.Remove(t.Id);
        await journal.SaveAsync();
        Log.Info($"Tweak {t.Id} reverted to original values");
        return new TweakResult(t.Id, TweakOutcome.Reverted);
    }

    private List<OriginalValue> Capture(TweakDefinition t)
    {
        var list = new List<OriginalValue>();
        foreach (var s in t.Settings)
        {
            var v = registry.Read(s.Hive, s.Key, s.Name);
            list.Add(new OriginalValue
            {
                Hive = s.Hive, Key = s.Key, Name = s.Name,
                Existed = v is not null, Kind = v?.Kind ?? s.Kind, Data = v?.Data,
                CreatedKey = FirstMissingKey(s.Hive, s.Key),
            });
        }
        return list;
    }

    private string? FirstMissingKey(RegHive hive, string key)
    {
        var parts = key.Split('\\');
        for (var i = 1; i <= parts.Length; i++)
        {
            var path = string.Join('\\', parts[..i]);
            if (!registry.KeyExists(hive, path))
                return i >= 3 ? path : null; // nunca se borrará una clave de primer o segundo nivel
        }
        return null;
    }

    internal static IEnumerable<RegOp> OpsFor(TweakDefinition t, string option)
    {
        foreach (var s in t.Settings)
        {
            if (!s.Values.TryGetValue(option, out var value))
                continue;
            yield return value is null
                ? new RegOp(RegOpType.DeleteValue, s.Hive, s.Key, s.Name)
                : new RegOp(RegOpType.SetValue, s.Hive, s.Key, s.Name, s.Kind, value);
        }
        if (option == TweakDefinition.Off && t.OffDeletesKey is { } key)
            yield return new RegOp(RegOpType.DeleteKey, t.Settings[0].Hive, key);
    }

    internal static List<RegOp> RestoreOps(IEnumerable<OriginalValue> originals)
    {
        var ops = new List<RegOp>();
        var deleted = new List<(RegHive, string)>();
        foreach (var o in originals)
        {
            if (o.CreatedKey is not null)
            {
                if (!deleted.Contains((o.Hive, o.CreatedKey)))
                {
                    ops.Add(new RegOp(RegOpType.DeleteKey, o.Hive, o.CreatedKey));
                    deleted.Add((o.Hive, o.CreatedKey));
                }
                continue;
            }
            ops.Add(o.Existed
                ? new RegOp(RegOpType.SetValue, o.Hive, o.Key, o.Name, o.Kind, o.Data)
                : new RegOp(RegOpType.DeleteValue, o.Hive, o.Key, o.Name));
        }
        return ops;
    }
}
