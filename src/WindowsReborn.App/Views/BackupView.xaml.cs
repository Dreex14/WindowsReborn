using System.Windows;
using System.Windows.Controls;
using WindowsReborn.App.ViewModels;

namespace WindowsReborn.App.Views;

public partial class BackupView : UserControl
{
    public BackupView()
    {
        InitializeComponent();
        DataContext = ViewModelLocator.Backup;
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true) await ViewModelLocator.Backup.RefreshInstallersAsync();
        };
    }

    // PasswordBox no admite enlace de datos por seguridad; se copia al modelo de vista.
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var vm = (BackupViewModel)DataContext;
        vm.Password = PasswordInput.Password;
        vm.PasswordConfirm = PasswordConfirmInput.Password;
    }
}
