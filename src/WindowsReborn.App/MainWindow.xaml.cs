using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.App.ViewModels;
using WindowsReborn.App.Views;
using Wpf.Ui.Controls;

namespace WindowsReborn.App;

public partial class MainWindow : FluentWindow
{
    private readonly Dictionary<string, FrameworkElement> _pages = new();

    public static MainWindow? Current { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        RestoreWindowPlacement();
        Closing += (_, _) => SaveWindowPlacement();
        // Al volver de Configuración de Windows, las secciones de ajustes muestran el estado real.
        Activated += (_, _) =>
        {
            ViewModelLocator.Privacy.RefreshFromWindows();
            ViewModelLocator.Optimize.RefreshFromWindows();
        };
        VersionText.Text = "v" + AppServices.Version + " · Dreex";
        NavHome.IsChecked = true;
    }

    private void RestoreWindowPlacement() => WindowPlacement.Restore(this, AppServices.Settings);

    private void SaveWindowPlacement()
    {
        WindowPlacement.Save(this, AppServices.Settings);
        try { Task.Run(AppServices.SaveSettingsAsync).Wait(TimeSpan.FromSeconds(2)); }
        catch { /* No impedir el cierre. */ }
    }

    /// <summary>Navega a una sección por su clave.</summary>
    public void Navigate(string key)
    {
        var target = key switch
        {
            "backup" => NavBackup,
            "restore" => NavRestore,
            "programs" => NavPrograms,
            "privacy" => NavPrivacy,
            "optimize" => NavOptimize,
            "config" => NavConfig,
            _ => NavHome,
        };
        if (target.IsChecked == true)
            Show(key);
        else
            target.IsChecked = true;
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key })
            Show(key);
    }

    private void Show(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            // Las vistas se conservan: una copia o instalación en curso no se pierde al cambiar de sección.
            page = key switch
            {
                "backup" => new BackupView(),
                "restore" => new RestoreView(),
                "programs" => new ProgramsView(),
                "privacy" => new TweaksView(ViewModelLocator.Privacy),
                "optimize" => new TweaksView(ViewModelLocator.Optimize),
                "config" => new ConfigView(),
                _ => new HomeView(),
            };
            _pages[key] = page;
        }

        PageHost.Content = page;
        page.Opacity = 0;
        var shift = new TranslateTransform(0, 10);
        page.RenderTransform = shift;
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
    }
}
