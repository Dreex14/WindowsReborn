using System.Windows.Controls;
using WindowsReborn.App.ViewModels;

namespace WindowsReborn.App.Views;

public partial class TweaksView : UserControl
{
    public TweaksView(TweaksViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
