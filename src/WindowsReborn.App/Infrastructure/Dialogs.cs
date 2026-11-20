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

    /// <summary>
    /// Pide una contraseña (dos veces si <paramref name="confirm"/>). Devuelve null si se cancela.
    /// La contraseña nunca se guarda ni se registra.
    /// </summary>
    public static async Task<string?> PasswordAsync(string title, string message, string confirmText, bool confirm, string? error = null)
    {
        var first = new System.Windows.Controls.PasswordBox { Margin = new Thickness(0, 12, 0, 0), MinWidth = 320 };
        var second = new System.Windows.Controls.PasswordBox { Margin = new Thickness(0, 8, 0, 0), MinWidth = 320 };
        var panel = new System.Windows.Controls.StackPanel { MaxWidth = 520 };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (error is not null)
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = error, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0x5A, 0x5A)),
            });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = Loc.T("password.field"), Margin = new Thickness(0, 12, 0, -6), Opacity = 0.8 });
        panel.Children.Add(first);
        if (confirm)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = Loc.T("password.repeat"), Margin = new Thickness(0, 10, 0, -6), Opacity = 0.8 });
            panel.Children.Add(second);
        }
        var box = new MessageBox
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = confirmText,
            CloseButtonText = Loc.T("common.cancel"),
            Owner = Application.Current.MainWindow,
        };
        box.Loaded += (_, _) => first.Focus();
        if (await box.ShowDialogAsync() != MessageBoxResult.Primary) return null;
        var password = first.Password;
        first.Clear();
        var repeated = second.Password;
        second.Clear();
        if (password.Length < Core.Configuration.PasswordVault.MinPasswordLength)
            return await PasswordAsync(title, message, confirmText, confirm, Loc.T("password.too-short", Core.Configuration.PasswordVault.MinPasswordLength));
        if (confirm && password != repeated)
            return await PasswordAsync(title, message, confirmText, confirm, Loc.T("password.mismatch"));
        return password;
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
