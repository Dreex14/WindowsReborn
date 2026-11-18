namespace WindowsReborn.App.ViewModels;

/// <summary>Modelos de vista compartidos (el estado se conserva al cambiar de sección).</summary>
public static class ViewModelLocator
{
    public static HomeViewModel Home { get; } = new();
    public static BackupViewModel Backup { get; } = new();
    public static RestoreViewModel Restore { get; } = new();
    public static ProgramsViewModel Programs { get; } = new();
    public static TweaksViewModel Privacy { get; } = new(TweaksPage.Privacy);
    public static TweaksViewModel Optimize { get; } = new(TweaksPage.Optimize);
    public static ConfigViewModel Config { get; } = new();
}
