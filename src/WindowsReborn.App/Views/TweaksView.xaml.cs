using System.Windows.Controls;
using WindowsReborn.App.ViewModels;
using Wpf.Ui.Controls;

namespace WindowsReborn.App.Views;

public partial class TweaksView : UserControl
{
    private bool _loaded;

    public TweaksView(TweaksViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        if (!vm.IsPrivacy)
        {
            PresetHero.Background = (System.Windows.Media.Brush)FindResource("HeroGradient");
            PresetIcon.Symbol = SymbolRegular.Rocket24;
        }
        else
        {
            PresetHero.Background = (System.Windows.Media.Brush)FindResource("PrivacyGradient");
        }
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            vm.Refresh();
        };
    }
}
