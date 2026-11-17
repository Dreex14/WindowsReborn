using System.Windows.Controls;
using WindowsReborn.App.ViewModels;

namespace WindowsReborn.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        DataContext = ViewModelLocator.Home;
    }
}
