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

        if (_vm.StartMinimized)
        {
            WindowState = WindowState.Minimized;
        }
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

    private async void VoiceConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsVoiceConnected)
        {
            await _vm.DisconnectVoiceAsync();
        }
        else
        {
            await _vm.ConnectVoiceAsync();
        }
    }

    private async void LoginWithDiscord_Click(object sender, RoutedEventArgs e)
    {
        await _vm.LoginWithDiscordAsync();
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        await _vm.VerifyServerAsync();
    }

    private async void AcceptPolicy_Click(object sender, RoutedEventArgs e)
    {
        await _vm.AcceptPolicyAsync();
    }

    // Hotkey capture handling
    private string _originalHotkeyValue = "";
    private DateTime _lastTabChange = DateTime.MinValue;
    
    private void TabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Track when tabs change to prevent auto-focus on hotkey fields
        _lastTabChange = DateTime.Now;
    }

    private void HotkeyTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            // Don't auto-capture if tab just changed (within 100ms)
            if ((DateTime.Now - _lastTabChange).TotalMilliseconds < 100)
            {
                // Tab navigation, not user click - clear focus
                Keyboard.ClearFocus();
                return;
            }
            
            // Also check if user clicked on the textbox
            if (!tb.IsMouseOver)
            {
                // Likely keyboard navigation, don't capture
                Keyboard.ClearFocus();
                return;
            }

            _activeHotkeyBox = tb;
            
            // Store original value for cancel
            if (tb.Tag is RadioPanelViewModel radio)
            {
                _originalHotkeyValue = radio.Hotkey;
            }
            else if (tb.Name == "TalkToAllHotkeyBox")
            {
                _originalHotkeyValue = _vm.TalkToAllHotkey;
            }
            else if (tb.Name == "PttMuteAllHotkeyBox")
            {
                _originalHotkeyValue = _vm.PttMuteAllHotkey;
            }
            else if (tb.Name == "ToggleMuteAllHotkeyBox")
            {
                _originalHotkeyValue = _vm.ToggleMuteAllHotkey;
            }
            else
            {
                _originalHotkeyValue = "";
            }
            
            tb.Text = "Press a key... (ESC to cancel)";
        }
    }

    private void HotkeyTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Text.StartsWith("Press a key"))
        {
            // Restore original value if nothing was pressed
            tb.Text = _originalHotkeyValue;
        }
        _activeHotkeyBox = null;
        _originalHotkeyValue = "";
    }

    private void HotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;

        e.Handled = true;

        // Get the actual key (handle system keys)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // ESC cancels the assignment
        if (key == Key.Escape)
        {
            tb.Text = _originalHotkeyValue;
            Keyboard.ClearFocus();
            return;
        }

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

        // Determine the source name for conflict checking
        string sourceName = "";
        if (tb.Tag is RadioPanelViewModel radio)
        {
            sourceName = radio.Label;
        }
        else if (tb.Name == "TalkToAllHotkeyBox")
        {
            sourceName = "Talk To All";
        }
        else if (tb.Name == "PttMuteAllHotkeyBox")
        {
            sourceName = "PTM All Radio";
        }
        else if (tb.Name == "ToggleMuteAllHotkeyBox")
        {
            sourceName = "TTM All Radio";
        }

        // Check for conflicts
        var conflict = _vm.GetHotkeyConflict(hotkey, sourceName);
        if (conflict != null)
        {
            var result = MessageBox.Show(
                $"The hotkey '{hotkey}' is already used by '{conflict}'.\n\nDo you want to reassign it to this function?",
                "Hotkey Conflict",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.No)
            {
                // Restore original value
                if (tb.Tag is RadioPanelViewModel radioRestore)
                {
                    tb.Text = radioRestore.Hotkey;
                }
                else
                {
                    tb.Text = "";
                }
                Keyboard.ClearFocus();
                return;
            }

            // Clear the hotkey from the conflicting function
            _vm.ClearHotkeyFromAll(hotkey);
        }

        // Update the bound radio panel or global hotkey
        if (tb.Tag is RadioPanelViewModel radioPanel)
        {
            radioPanel.Hotkey = hotkey;
        }
        else if (tb.Name == "TalkToAllHotkeyBox")
        {
            _vm.TalkToAllHotkey = hotkey;
        }
        else if (tb.Name == "PttMuteAllHotkeyBox")
        {
            _vm.PttMuteAllHotkey = hotkey;
        }
        else if (tb.Name == "ToggleMuteAllHotkeyBox")
        {
            _vm.ToggleMuteAllHotkey = hotkey;
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

    private void ClearToggleMuteAllHotkey_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleMuteAllHotkey = "";
    }
}
