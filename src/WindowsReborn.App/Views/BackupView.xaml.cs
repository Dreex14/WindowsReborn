using System.Windows.Controls;
using WindowsReborn.App.ViewModels;

namespace WindowsReborn.App.Views;

public partial class BackupView : UserControl
{
    public BackupView()
    {
        InitializeComponent();
        DataContext = ViewModelLocator.Backup;
    }
}
