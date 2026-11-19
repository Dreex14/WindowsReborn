using System.Windows.Controls;
using WindowsReborn.App.ViewModels;

namespace WindowsReborn.App.Views;

public partial class DebloatView : UserControl
{
    public DebloatView()
    {
        InitializeComponent();
        DataContext = ViewModelLocator.Debloat;
        Loaded += async (_, _) => await ViewModelLocator.Debloat.EnsureLoadedAsync();
    }
}
