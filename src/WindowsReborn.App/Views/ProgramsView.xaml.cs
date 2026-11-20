using System.Windows.Controls;
using WindowsReborn.App.ViewModels;

namespace WindowsReborn.App.Views;

public partial class ProgramsView : UserControl
{
    public ProgramsView()
    {
        InitializeComponent();
        DataContext = ViewModelLocator.Programs;
        Loaded += async (_, _) => await ViewModelLocator.Programs.EnsureLoadedAsync();
    }

    private async void OnConnectGitHub(object sender, System.Windows.RoutedEventArgs e)
    {
        var token = TokenBox.Password;
        TokenBox.Password = "";
        await ViewModelLocator.Programs.ConnectGitHubAsync(token);
    }
}
