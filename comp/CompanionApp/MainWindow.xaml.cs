using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CompanionApp.ViewModels;

namespace CompanionApp;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private TextBox? _activeHotkeyBox;

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

    private async void MumbleConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsMumbleConnected)
        {
            await _vm.DisconnectMumbleAsync();
        }
        else
        {
            await _vm.ConnectMumbleAsync();
        }
    }

    private void MumblePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
        {
            _vm.MumblePassword = pb.Password;
        }
    }

    // Hotkey capture handling
    private void HotkeyTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            _activeHotkeyBox = tb;
            tb.Text = "Press a key...";
        }
    }

    private void HotkeyTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Text == "Press a key...")
        {
            // Restore original value if nothing was pressed
            if (tb.Tag is RadioPanelViewModel radio)
            {
                tb.Text = radio.Hotkey;
            }
            else
            {
                tb.Text = "";
            }
        }
        _activeHotkeyBox = null;
    }

    private void HotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;

        e.Handled = true;

        // Get the actual key (handle system keys)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore modifier-only keys
        if (key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LWin || key == Key.RWin)
        {
            return;
        }

        // Build hotkey string
        var modifiers = Keyboard.Modifiers;
        var hotkeyParts = new System.Collections.Generic.List<string>();

        if (modifiers.HasFlag(ModifierKeys.Control))
            hotkeyParts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt))
            hotkeyParts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            hotkeyParts.Add("Shift");

        hotkeyParts.Add(key.ToString());

        var hotkey = string.Join("+", hotkeyParts);

        // Update the bound radio panel
        if (tb.Tag is RadioPanelViewModel radio)
        {
            radio.Hotkey = hotkey;
        }
        
        tb.Text = hotkey;
        
        // Move focus away
        Keyboard.ClearFocus();
    }

    // Test PTT button handling
    private async void TestPtt_MouseDown(object sender, MouseButtonEventArgs e)
    {
        await _vm.StartTestAsync();
    }

    private async void TestPtt_MouseUp(object sender, MouseButtonEventArgs e)
    {
        await _vm.StopTestAsync();
    }

    // Clear global hotkey handlers
    private void ClearTalkToAllHotkey_Click(object sender, RoutedEventArgs e)
    {
        _vm.TalkToAllHotkey = "";
    }

    private void ClearPttMuteAllHotkey_Click(object sender, RoutedEventArgs e)
    {
        _vm.PttMuteAllHotkey = "";
    }
}
