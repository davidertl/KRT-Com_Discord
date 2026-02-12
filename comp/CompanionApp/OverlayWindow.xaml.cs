using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CompanionApp;

/// <summary>
/// A single radio entry displayed in the overlay.
/// </summary>
public class OverlayRadioEntry : INotifyPropertyChanged
{
    private string _label = "";
    private string _freqDisplay = "";
    private string _hotkey = "";
    private string _lastTransmission = "";
    private bool _isReceiving;
    private bool _isTransmitting;
    private bool _showHotkey;
    private long _lastActivityTicks;

    public string Label
    {
        get => _label;
        set { _label = value; OnPropertyChanged(); }
    }

    public string FreqDisplay
    {
        get => _freqDisplay;
        set { _freqDisplay = value; OnPropertyChanged(); }
    }

    public string Hotkey
    {
        get => _hotkey;
        set { _hotkey = value; OnPropertyChanged(); OnPropertyChanged(nameof(HotkeyVisible)); }
    }

    public string LastTransmission
    {
        get => _lastTransmission;
        set { _lastTransmission = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLastTransmission)); }
    }

    public bool IsReceiving
    {
        get => _isReceiving;
        set { _isReceiving = value; OnPropertyChanged(); }
    }

    public bool IsTransmitting
    {
        get => _isTransmitting;
        set { _isTransmitting = value; OnPropertyChanged(); }
    }

    public bool ShowHotkey
    {
        get => _showHotkey;
        set { _showHotkey = value; OnPropertyChanged(); OnPropertyChanged(nameof(HotkeyVisible)); }
    }

    /// <summary>
    /// Ticks of the last TX/RX activity on this entry. Used for sorting entries by most recent.
    /// </summary>
    public long LastActivityTicks
    {
        get => _lastActivityTicks;
        set { _lastActivityTicks = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// True when the radio is currently active (receiving, transmitting, or has a recent transmission).
    /// Only active radios are displayed in the overlay.
    /// </summary>
    public bool IsActive => IsReceiving || IsTransmitting || !string.IsNullOrWhiteSpace(LastTransmission);

    public void NotifyActiveChanged() => OnPropertyChanged(nameof(IsActive));

    public Visibility HotkeyVisible =>
        ShowHotkey && !string.IsNullOrWhiteSpace(Hotkey) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HasLastTransmission =>
        string.IsNullOrWhiteSpace(LastTransmission) ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Topmost, transparent, click-through overlay window that displays
/// active radio status and recent transmissions.
/// </summary>
public partial class OverlayWindow : Window
{
    // Win32 extended window styles for click-through
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public ObservableCollection<OverlayRadioEntry> Entries { get; } = new();

    public OverlayWindow()
    {
        InitializeComponent();
        RadioItems.ItemsSource = Entries;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MakeClickThrough();
    }

    /// <summary>
    /// Makes the window click-through and hides it from the taskbar/alt-tab.
    /// </summary>
    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Position the overlay at the specified screen coordinates.
    /// </summary>
    public void SetPosition(int x, int y)
    {
        Left = x;
        Top = y;
    }

    /// <summary>
    /// Set the overlay background opacity (0.0 – 1.0).
    /// Text and controls stay fully opaque; only the background darkens/lightens.
    /// </summary>
    public void SetBackgroundOpacity(double opacity)
    {
        opacity = Math.Clamp(opacity, 0.1, 1.0);
        byte alpha = (byte)(opacity * 255);
        // Base color is #1E1E2E
        OverlayBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x1E, 0x1E, 0x2E));
    }
}
