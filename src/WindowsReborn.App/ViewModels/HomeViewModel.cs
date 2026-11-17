using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Core.Localization;

namespace WindowsReborn.App.ViewModels;

public sealed record HomeStep(int Number, string Title, string Text, string Icon, string Target);

public partial class HomeViewModel : ObservableObject
{
    public const string RufusUrl = "https://rufus.ie/";
    public const string Windows11Url = "https://www.microsoft.com/software-download/windows11";
    public const string Windows10Url = "https://www.microsoft.com/software-download/windows10";

    public IReadOnlyList<HomeStep> Before { get; } =
    [
        new(1, Loc.T("home.before.1"), Loc.T("home.before.1.text"), "Archive24", "backup"),
        new(2, Loc.T("home.before.2"), Loc.T("home.before.2.text"), "BoxMultiple24", "config"),
        new(3, Loc.T("home.before.3"), Loc.T("home.before.3.text"), "UsbStick24", "usb"),
    ];

    public IReadOnlyList<HomeStep> After { get; } =
    [
        new(1, Loc.T("home.after.1"), Loc.T("home.after.1.text"), "ArrowImport24", "restore"),
        new(2, Loc.T("home.after.2"), Loc.T("home.after.2.text"), "AppsAddIn24", "programs"),
        new(3, Loc.T("home.after.3"), Loc.T("home.after.3.text"), "ShieldLock24", "privacy"),
        new(4, Loc.T("home.after.4"), Loc.T("home.after.4.text"), "Rocket24", "optimize"),
    ];

    public string? LastBackup => AppServices.Settings.LastBackupPath is { } p && Directory.Exists(p)
        ? Loc.T("home.last-backup", Path.GetFileName(p))
        : null;

    [RelayCommand]
    private void Go(string target)
    {
        if (target == "usb")
        {
            Dialogs.OpenUri(RufusUrl);
            return;
        }
        MainWindow.Current?.Navigate(target);
    }

    [RelayCommand]
    private static void OpenLink(string url) => Dialogs.OpenUri(url);

    [RelayCommand]
    private void OpenLastBackup()
    {
        if (AppServices.Settings.LastBackupPath is { } p) Dialogs.OpenFolder(p);
    }
}
