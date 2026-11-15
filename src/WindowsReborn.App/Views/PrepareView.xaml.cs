using System.Windows.Controls;
using WindowsReborn.App.ViewModels;

namespace WindowsReborn.App.Views;

public partial class PrepareView : UserControl
{
    public PrepareView()
    {
        InitializeComponent();
        DataContext = ViewModelLocator.Prepare;
    }
}
