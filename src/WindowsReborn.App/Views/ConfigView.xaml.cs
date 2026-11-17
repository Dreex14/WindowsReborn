using System.Windows.Controls;
using WindowsReborn.App.ViewModels;

namespace WindowsReborn.App.Views;

public partial class ConfigView : UserControl
{
    public ConfigView()
    {
        InitializeComponent();
        DataContext = ViewModelLocator.Config;
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) ViewModelLocator.Config.RefreshSummary(); };
    }
}
