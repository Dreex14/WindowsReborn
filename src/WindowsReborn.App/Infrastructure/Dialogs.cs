using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using WindowsReborn.Core.Localization;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace WindowsReborn.App.Infrastructure;

/// <summary>Diálogos comunes. Ninguno bloquea operaciones en segundo plano.</summary>
public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            PrimaryButtonText = confirmText,
            CloseButtonText = Loc.T("common.cancel"),
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            Owner = Application.Current.MainWindow,
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    public static async Task InfoAsync(string title, string message)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            CloseButtonText = Loc.T("common.close"),
            Owner = Application.Current.MainWindow,
        };
        await box.ShowDialogAsync();
    }

    public static string? PickFolder(string title, string? initial = null)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initial) && System.IO.Directory.Exists(initial))
            dlg.InitialDirectory = initial;
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FolderName : null;
    }

    public static string? OpenFile(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    public static string? SaveFile(string title, string filter, string fileName)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, OverwritePrompt = true };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    /// <summary>Abre una URL o página de Configuración (<c>ms-settings:</c>) con el programa predeterminado.</summary>
    public static void OpenUri(string uri)
    {
        string[] allowed = ["https://", "ms-settings:", "ms-windows-store:", "windowsdefender:"];
        if (!allowed.Any(a => uri.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
        {
            // Herramientas del sistema conocidas (services.msc…).
            if (uri is "services.msc" or "rstrui.exe" or "optionalfeatures.exe" or "SystemPropertiesProtection.exe")
                RunSystemTool(uri);
            return;
        }
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* No hay programa asociado. */ }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });
            else if (System.IO.Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch { }
    }

    public static void RunSystemTool(string exe)
    {
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch { }
    }
}
