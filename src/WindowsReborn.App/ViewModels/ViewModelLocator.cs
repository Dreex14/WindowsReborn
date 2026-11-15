namespace WindowsReborn.App.ViewModels;

/// <summary>Instancias únicas de los modelos de vista (se crean al usarse por primera vez).</summary>
public static class ViewModelLocator
{
    private static BackupViewModel? _backup;
    private static RestoreViewModel? _restore;
    private static ProgramsViewModel? _programs;
    private static SettingsViewModel? _settings;
    private static HomeViewModel? _home;
    private static TweaksViewModel? _personalization;
    private static TweaksViewModel? _privacy;
    private static PrepareViewModel? _prepare;

    public static BackupViewModel Backup => _backup ??= new BackupViewModel();
    public static RestoreViewModel Restore => _restore ??= new RestoreViewModel();
    public static ProgramsViewModel Programs => _programs ??= new ProgramsViewModel();
    public static SettingsViewModel Settings => _settings ??= new SettingsViewModel();
    public static HomeViewModel Home => _home ??= new HomeViewModel();
    public static TweaksViewModel Personalization => _personalization ??= new TweaksViewModel("personalization");
    public static TweaksViewModel Privacy => _privacy ??= new TweaksViewModel("privacy");
    public static PrepareViewModel Prepare => _prepare ??= new PrepareViewModel();
}
