namespace WindowsReborn.Tweaks;

/// <summary>
/// Configuraciones predefinidas que se aplican con un solo botón. Nunca incluyen nada
/// que desactive Defender, SmartScreen, UAC, Windows Update o el firewall, ni los
/// permisos de cámara/micrófono (romperían videollamadas).
/// </summary>
public static class TweakPresets
{
    /// <summary>Privacidad recomendada: telemetría, anuncios, sugerencias, IA y búsqueda web.</summary>
    public static IReadOnlyList<string> Privacy { get; } =
    [
        "privacy.advertising-id", "privacy.tailored-experiences", "privacy.language-list", "privacy.app-launch-tracking",
        "privacy.settings-suggestions", "privacy.tips", "privacy.welcome-experience", "privacy.finish-setup",
        "privacy.lockscreen-tips", "privacy.silent-app-installs", "privacy.feedback-requests", "privacy.typing-data",
        "privacy.inking-personalization", "privacy.online-speech", "privacy.error-reporting", "privacy.no-copilot",
        "privacy.no-recall", "privacy.activity-history", "privacy.diagnostic-required", "privacy.diagtrack-service",
        "privacy.consumer-features", "privacy.ceip", "privacy.appcompat-telemetry", "privacy.handwriting-reports",
        "privacy.cloud-clipboard", "privacy.cortana", "privacy.explorer-ads", "privacy.office-telemetry",
        "privacy.nvidia-telemetry", "privacy.edge-telemetry", "permissions.app-diagnostics",
        "search.no-web", "search.no-highlights", "search.no-cloud", "search.no-history", "search.no-bing-win10",
        "start.no-recommendations", "start.no-account-notifications", "start.no-suggestions-win10",
    ];

    /// <summary>Optimización recomendada: Explorador cómodo, menú clásico y algo de rendimiento.</summary>
    public static IReadOnlyList<string> Optimize { get; } =
    [
        "explorer.show-extensions", "explorer.classic-context-menu", "explorer.open-this-pc", "explorer.no-autoplay",
        "taskbar.end-task", "taskbar.hide-chat", "taskbar.hide-copilot",
        "performance.game-mode", "performance.startup-delay", "performance.edge-background",
        "appearance.menu-delay", "system.sticky-keys-prompt", "system.long-paths",
    ];

    /// <summary>Opción «activado» para los conmutadores del preset (los de elección se ignoran).</summary>
    public static IEnumerable<(TweakDefinition Tweak, string Option)> Resolve(IEnumerable<string> ids) =>
        ids.Select(TweakCatalog.Find)
           .Where(t => t is not null && t.Kind == TweakKind.Toggle)
           .Select(t => (t!, TweakDefinition.On));
}
