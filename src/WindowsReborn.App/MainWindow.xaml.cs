using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using WindowsReborn.App.Views;
using WindowsReborn.App.ViewModels;
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
        NavHome.IsChecked = true;
    }

    /// <summary>Navega a una sección por su clave (home, backup, restore, programs…).</summary>
    public void Navigate(string key)
    {
        var target = key switch
        {
            "backup" => NavBackup,
            "restore" => NavRestore,
            "programs" => NavPrograms,
            "privacy" => NavPrivacy,
            "personalization" => NavPersonalization,
            "prepare" => NavPrepare,
            "settings" => NavSettings,
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
            // Las vistas se crean una vez y se conservan: una copia en curso no se pierde al cambiar de sección.
            page = key switch
            {
                "backup" => new BackupView(),
                "restore" => new RestoreView(),
                "programs" => new ProgramsView(),
                "privacy" => new TweaksView(ViewModels.ViewModelLocator.Privacy),
                "personalization" => new TweaksView(ViewModels.ViewModelLocator.Personalization),
                "prepare" => new PrepareView(),
                "settings" => new SettingsView(),
                _ => new HomeView(),
            };
            _pages[key] = page;
        }

        PageHost.Content = page;
        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }
}
