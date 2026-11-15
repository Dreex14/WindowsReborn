using System.Windows;
using System.Windows.Controls;
using WindowsReborn.App.ViewModels;

namespace WindowsReborn.App.Views;

public partial class RestoreView : UserControl
{
    public RestoreView()
    {
        InitializeComponent();
        DataContext = ViewModelLocator.Restore;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) =>
        ((RestoreViewModel)DataContext).Password = PasswordInput.Password;
}
