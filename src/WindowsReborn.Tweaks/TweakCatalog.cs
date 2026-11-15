using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsReborn.Tweaks;

/// <summary>
/// Catálogo de ajustes, definido como datos (<c>catalog.json</c>) para revisarlo y
/// ampliarlo sin tocar el motor. Cada ajuste trae sus textos en español e inglés.
/// </summary>
public static class TweakCatalog
{
    private static readonly Dictionary<string, string> Macros = new(StringComparer.Ordinal)
    {
        ["@adv"] = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        ["@explorer"] = @"Software\Microsoft\Windows\CurrentVersion\Explorer",
        ["@cdm"] = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
        ["@search"] = @"Software\Microsoft\Windows\CurrentVersion\Search",
        ["@searchsettings"] = @"Software\Microsoft\Windows\CurrentVersion\SearchSettings",
        ["@personalize"] = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
        ["@consent"] = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore",
        ["@policies"] = @"Software\Policies\Microsoft\Windows",
        ["@services"] = @"SYSTEM\CurrentControlSet\Services",
    };

    private static readonly Lazy<IReadOnlyList<TweakDefinition>> Loaded = new(() =>
    {
        using var s = typeof(TweakCatalog).Assembly.GetManifestResourceStream("WindowsReborn.Tweaks.catalog.json")
                      ?? throw new InvalidOperationException("catalog.json missing");
        return Parse(s);
    });

    public static IReadOnlyList<TweakDefinition> All => Loaded.Value;

    public static TweakDefinition? Find(string id) => All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<TweakDefinition> Parse(Stream json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        var dtos = JsonSerializer.Deserialize<List<TweakDto>>(json, options) ?? [];
        return dtos.Select(Convert).ToList();
    }

    private static TweakDefinition Convert(TweakDto d)
    {
        var kind = d.Kind ?? (d.Choices is { Count: > 0 } ? TweakKind.Choice : TweakKind.Toggle);
        var optionIds = kind == TweakKind.Choice ? d.Choices! : [TweakDefinition.On, TweakDefinition.Off];
        var settings = (d.Settings ?? []).Select(s => new RegSetting
        {
            Hive = s.Hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase) ? RegHive.LocalMachine : RegHive.CurrentUser,
            Key = ExpandKey(s.Key),
            Name = s.Name ?? "",
            Kind = s.Type,
            RequireKey = s.RequireKey,
            Values = kind == TweakKind.Choice
                ? (s.Values ?? []).ToDictionary(kv => kv.Key, kv => Str(kv.Value))
                : new Dictionary<string, string?> { [TweakDefinition.On] = Str(s.On), [TweakDefinition.Off] = Str(s.Off) },
        }).ToList();

        var texts = new Dictionary<string, TweakText>(StringComparer.OrdinalIgnoreCase);
        foreach (var (lang, t) in new[] { ("es", d.Es), ("en", d.En) })
        {
            if (t is null) continue;
            texts[lang] = new TweakText(t.Title, t.Desc ?? "", t.Warn, t.Choices ?? new Dictionary<string, string>());
        }

        return new TweakDefinition
        {
            Id = d.Id,
            Category = d.Category,
            Risk = (RiskLevel)Math.Clamp(d.Risk, 1, 3),
            Kind = kind,
            Restart = d.Restart,
            Builds = d.Builds?.Select(b => new BuildRange(b.ElementAtOrDefault(0), b.ElementAtOrDefault(1))).ToList()
                     ?? (d.MinBuild > 0 || d.MaxBuild > 0 ? [new BuildRange(d.MinBuild, d.MaxBuild)] : []),
            Settings = settings,
            Options = optionIds,
            OffDeletesKey = d.OffDeletesKey is null ? null : ExpandKey(d.OffDeletesKey),
            OpenUri = d.Open,
            Tags = d.Tags ?? [],
            Texts = texts,
        };
    }

    private static string ExpandKey(string key)
    {
        var k = key.Replace('/', '\\');
        foreach (var (macro, value) in Macros)
        {
            if (k.StartsWith(macro, StringComparison.Ordinal))
                return value + k[macro.Length..];
        }
        return k;
    }

    private static string? Str(JsonElement? e) => e switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.Number } n => n.GetInt64().ToString(CultureInfo.InvariantCulture),
        { } v => v.GetString(),
    };

    /// <summary>
    /// Comprueba que una operación pedida al proceso elevado corresponde a un ajuste
    /// del catálogo (o a la lista de inicio), para que el proceso con permisos de
    /// administrador nunca escriba fuera de lo que la aplicación conoce.
    /// </summary>
    public static bool IsAllowed(RegOp op)
    {
        if (StartupManager.IsStartupApprovedKey(op.Hive, op.Key))
            return op.Type != RegOpType.DeleteKey && op.Kind == RegKind.Binary || op.Type == RegOpType.DeleteValue;

        foreach (var t in All)
        {
            foreach (var s in t.Settings)
            {
                if (s.Hive != op.Hive) continue;
                if (op.Type == RegOpType.DeleteKey)
                {
                    // Solo claves creadas por un ajuste: el propio valor o un antecesor de al menos 4 niveles,
                    // y nunca la clave de un servicio.
                    var isAncestor = s.Key.Equals(op.Key, StringComparison.OrdinalIgnoreCase) ||
                                     s.Key.StartsWith(op.Key + "\\", StringComparison.OrdinalIgnoreCase);
                    if (isAncestor && op.Key.Split('\\').Length >= 4 && !s.RequireKey &&
                        !op.Key.StartsWith(@"SYSTEM\", StringComparison.OrdinalIgnoreCase))
                        return true;
                    continue;
                }
                if (s.Key.Equals(op.Key, StringComparison.OrdinalIgnoreCase) && s.Name.Equals(op.Name, StringComparison.OrdinalIgnoreCase))
                    return op.Type == RegOpType.DeleteValue || op.Kind == s.Kind || op.Kind == RegKind.String;
            }
        }
        return false;
    }

    private sealed class TweakDto
    {
        public string Id { get; set; } = "";
        public TweakCategory Category { get; set; }
        public int Risk { get; set; } = 1;
        public TweakKind? Kind { get; set; }
        public RestartNeed Restart { get; set; }
        public int MinBuild { get; set; }
        public int MaxBuild { get; set; }
        public List<int[]>? Builds { get; set; }
        public List<string>? Choices { get; set; }
        public List<SettingDto>? Settings { get; set; }
        public string? OffDeletesKey { get; set; }
        public string? Open { get; set; }
        public List<string>? Tags { get; set; }
        public TextDto? Es { get; set; }
        public TextDto? En { get; set; }
    }

    private sealed class SettingDto
    {
        public string Hive { get; set; } = "HKCU";
        public string Key { get; set; } = "";
        public string? Name { get; set; }
        public RegKind Type { get; set; } = RegKind.DWord;
        public JsonElement? On { get; set; }
        public JsonElement? Off { get; set; }
        public Dictionary<string, JsonElement?>? Values { get; set; }
        public bool RequireKey { get; set; }
    }

    private sealed class TextDto
    {
        public string Title { get; set; } = "";
        public string? Desc { get; set; }
        public string? Warn { get; set; }
        public Dictionary<string, string>? Choices { get; set; }
    }
}
