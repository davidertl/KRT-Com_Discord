using System.Threading.Tasks;
using System.Windows;
using CompanionApp.ViewModels;

namespace CompanionApp;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _vm.InitializeAsync();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _vm.Dispose();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await _vm.SaveAsync();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        await _vm.ReloadAsync();
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        _vm.OpenConfigFolder();
    }

    private async void StartTest_Click(object sender, RoutedEventArgs e)
    {
        await _vm.StartTestAsync();
    }

    private async void StopTest_Click(object sender, RoutedEventArgs e)
    {
        await _vm.StopTestAsync();
    }
}
