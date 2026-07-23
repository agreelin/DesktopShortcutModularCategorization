using System.Windows;
using FolderSessionLock.App.ViewModels;

namespace FolderSessionLock.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
    }
}
