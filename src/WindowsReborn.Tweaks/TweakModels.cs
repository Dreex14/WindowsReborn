namespace WindowsReborn.Tweaks;

/// <summary>Nivel de riesgo que se muestra junto a cada ajuste.</summary>
public enum RiskLevel
{
    /// <summary>Configuración habitual, documentada y reversible.</summary>
    Safe = 1,
    /// <summary>Puede cambiar el comportamiento de Windows o dejar de funcionar tras una actualización.</summary>
    Moderate = 2,
    /// <summary>Afecta a funciones del sistema, servicios o compatibilidad. Pide confirmación explícita.</summary>
    Advanced = 3,
}

public enum TweakCategory
{
    Explorer,
    Taskbar,
    Start,
    Appearance,
    Privacy,
    Permissions,
    Search,
    Apps,
    System,
    Performance,
}

public enum TweakKind
{
    Toggle,
    Choice,
    /// <summary>Solo informativo: abre la herramienta oficial (cambios sin reversión fiable).</summary>
    Info,
}

/// <summary>Qué hace falta para que se note el cambio.</summary>
public enum RestartNeed
{
    None,
    Explorer,
    SignOut,
    Reboot,
}

public enum RegHive
{
    CurrentUser,
    LocalMachine,
}

public enum RegKind
{
    DWord,
    String,
    Binary,
}

/// <summary>Valor leído del registro. <see cref="Data"/> va normalizado como texto (DWORD en decimal sin signo, binario en hexadecimal).</summary>
public sealed record RegValue(RegKind Kind, string Data);

/// <summary>Un valor del registro que el ajuste controla.</summary>
public sealed class RegSetting
{
    public RegHive Hive { get; init; }
    public required string Key { get; init; }
    public string Name { get; init; } = "";
    public RegKind Kind { get; init; } = RegKind.DWord;
    /// <summary>Valor por opción; <c>null</c> significa «sin valor» (lo que Windows trae por defecto).</summary>
    public required IReadOnlyDictionary<string, string?> Values { get; init; }
    /// <summary>La clave debe existir de antemano (p. ej. un servicio); si no, el ajuste no es compatible.</summary>
    public bool RequireKey { get; init; }
}

public sealed record BuildRange(int Min, int Max)
{
    public bool Contains(int build) => build >= Min && (Max == 0 || build <= Max);
}

public sealed record TweakText(string Title, string Description, string? Warning, IReadOnlyDictionary<string, string> Choices);

public sealed class TweakDefinition
{
    public const string On = "on";
    public const string Off = "off";

    public required string Id { get; init; }
    public TweakCategory Category { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Safe;
    public TweakKind Kind { get; init; } = TweakKind.Toggle;
    public RestartNeed Restart { get; init; }
    public IReadOnlyList<BuildRange> Builds { get; init; } = [];
    public IReadOnlyList<RegSetting> Settings { get; init; } = [];
    /// <summary>Opciones en el orden en que se muestran (para conmutadores: <c>on</c>, <c>off</c>).</summary>
    public IReadOnlyList<string> Options { get; init; } = [On, Off];
    /// <summary>Clave que se borra entera al aplicar <c>off</c> (si la crea el ajuste, como el menú contextual clásico).</summary>
    public string? OffDeletesKey { get; init; }
    /// <summary>Página oficial que abre un ajuste informativo.</summary>
    public string? OpenUri { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required IReadOnlyDictionary<string, TweakText> Texts { get; init; }

    public bool RequiresAdmin => Settings.Any(s => s.Hive == RegHive.LocalMachine || s.Key.StartsWith(@"Software\Policies\", StringComparison.OrdinalIgnoreCase));

    public TweakText Text(string language) =>
        Texts.TryGetValue(language, out var t) ? t : Texts.TryGetValue("en", out var en) ? en : Texts.Values.First();

    public bool SupportsBuild(int build) => Builds.Count == 0 || Builds.Any(b => b.Contains(build));
}

public enum Support
{
    Supported,
    /// <summary>Esta versión de Windows no tiene la función.</summary>
    WrongBuild,
    /// <summary>Falta el componente (servicio, clave…) en este equipo.</summary>
    Missing,
    /// <summary>Solo informativo.</summary>
    InfoOnly,
}

/// <summary>Estado real de un ajuste en este equipo.</summary>
public sealed record TweakStatus(Support Support, string? CurrentOption, bool HasOriginal, bool AllAbsent = false)
{
    public bool IsSupported => Support == Support.Supported;
    /// <summary>El valor actual no coincide con ninguna opción (lo cambió otra herramienta o el usuario).</summary>
    public bool IsCustom => IsSupported && CurrentOption is null && !AllAbsent;
}

public enum TweakOutcome
{
    Applied,
    Reverted,
    Unchanged,
    Unsupported,
    Failed,
}

public sealed record TweakResult(string TweakId, TweakOutcome Outcome, string? Error = null);

public enum RegOpType
{
    SetValue,
    DeleteValue,
    DeleteKey,
}

/// <summary>Operación de escritura en el registro (serializable para el proceso elevado).</summary>
public sealed record RegOp(RegOpType Type, RegHive Hive, string Key, string Name = "", RegKind Kind = RegKind.DWord, string? Data = null);
