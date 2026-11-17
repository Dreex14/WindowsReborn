using System.Windows;
using System.Windows.Threading;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Core.Logging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WindowsReborn.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Copia elevada: aplica un lote de cambios del catálogo y termina, sin interfaz.
        var elevated = Array.IndexOf(e.Args, Tweaks.ElevatedHost.Argument);
        if (elevated >= 0 && elevated + 1 < e.Args.Length)
        {
            Shutdown(Tweaks.ElevatedHost.Run(e.Args[elevated + 1]));
            return;
        }

        DispatcherUnhandledException += OnUnhandled;

        await AppServices.InitializeAsync();
        ApplyTheme(AppServices.Settings.Theme);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // «--page backup» abre directamente una sección (accesos directos y capturas de documentación).
        var pageIndex = Array.IndexOf(e.Args, "--page");
        if (pageIndex >= 0 && pageIndex + 1 < e.Args.Length)
            window.Navigate(e.Args[pageIndex + 1]);

        // Abrir un backup o una configuración pasados como argumento («Abrir con» o arrastrar sobre el .exe).
        var fileArg = e.Args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a) &&
                                                 a.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (fileArg is not null)
        {
            window.Navigate("restore");
            await ViewModels.ViewModelLocator.Restore.ImportPathsAsync([Path.GetFullPath(fileArg)]);
        }
    }

    public static void ApplyTheme(string theme)
    {
        var target = theme switch
        {
            "light" => ApplicationTheme.Light,
            "system" => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
            _ => ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(target, WindowBackdropType.Mica, true);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        System.Windows.MessageBox.Show(e.Exception.Message, "Windows Reborn", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        e.Handled = true;
    }
}
