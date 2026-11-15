using System.Windows;
using WindowsReborn.App.ViewModels;
using WindowsReborn.Programs;
using Wpf.Ui.Controls;

namespace WindowsReborn.App.Views;

public partial class ProgramEditorWindow : FluentWindow
{
    private readonly ProgramEditorViewModel _vm;

    private ProgramEditorWindow(ProgramEntry? entry)
    {
        InitializeComponent();
        _vm = new ProgramEditorViewModel(entry);
        DataContext = _vm;
        Owner = Application.Current.MainWindow;
    }

    public ProgramEntry? Result { get; private set; }

    /// <summary>Abre el editor. Devuelve la entrada guardada o null si se cancela.</summary>
    public static ProgramEntry? Edit(ProgramEntry? entry)
    {
        var window = new ProgramEditorWindow(entry);
        return window.ShowDialog() == true ? window.Result : null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var result = _vm.TryBuild();
        if (result is null) return;
        Result = result;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
