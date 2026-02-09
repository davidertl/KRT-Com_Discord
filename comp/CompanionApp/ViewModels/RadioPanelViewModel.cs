using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CompanionApp.ViewModels;

/// <summary>
/// Radio transmission status
/// </summary>
public enum RadioStatus
{
    Idle,           // No activity
    Transmitting,   // User is transmitting (green up arrow)  
    TransmitError,  // Error while transmitting (yellow arrow)
    Receiving,      // Incoming transmission (red down arrow)
    Broadcasting    // Talk to all mode (blue up arrow)
}

/// <summary>
/// ViewModel for a single radio panel in the UI
/// </summary>
public class RadioPanelViewModel : INotifyPropertyChanged
{
    private int _index;
    private bool _isEnabled;
    private bool _isEmergencyRadio;
    private bool _isMuted;
    private string _label = "Radio";
    private string _freqInput = "";
    private int _freqId = 1000;
    private string _hotkey = "";
    private int _volume = 100;
    private int _balance = 50; // 0 = left, 50 = center, 100 = right
    private RadioStatus _status = RadioStatus.Idle;
    private bool _advancedMode;
    private bool _includedInBroadcast;
    private int _listenerCount;
    private bool _hasUnsavedChanges;

    // Last 3 transmissions
    public ObservableCollection<string> RecentTransmissions { get; } = new();

    public int Index
    {
        get => _index;
        set { _index = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsVisible)); OnPropertyChanged(nameof(DisplayNumber)); }
    }

    public int DisplayNumber => Index + 1;

    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(); MarkChanged(); }
    }

    public bool IsEmergencyRadio
    {
        get => _isEmergencyRadio;
        set 
        { 
            _isEmergencyRadio = value; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(CanChangeFrequency));
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    public bool CanChangeFrequency => !IsEmergencyRadio;

    public bool IsMuted
    {
        get => _isMuted;
        set { _isMuted = value; OnPropertyChanged(); }
    }

    public string Label
    {
        get => _label;
        set { _label = value; OnPropertyChanged(); MarkChanged(); }
    }

    public string FreqInput
    {
        get => _freqInput;
        set 
        { 
            _freqInput = value;
            OnPropertyChanged();
            // Only update FreqId when we have 4 digits
            if (int.TryParse(value, out int freq) && value.Length == 4)
            {
                FreqId = freq;
            }
        }
    }

    public int FreqId
    {
        get => _freqId;
        set 
        { 
            _freqId = Math.Clamp(value, 1000, 9999);
            _freqInput = _freqId.ToString();
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(FreqInput));
            OnPropertyChanged(nameof(FrequencyDisplay)); 
            MarkChanged();
        }
    }

    public string Hotkey
    {
        get => _hotkey;
        set { _hotkey = value; OnPropertyChanged(); MarkChanged(); }
    }

    public int Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0, 100); OnPropertyChanged(); MarkChanged(); }
    }

    public int Balance
    {
        get => _balance;
        set { _balance = Math.Clamp(value, 0, 100); OnPropertyChanged(); OnPropertyChanged(nameof(BalanceText)); MarkChanged(); }
    }

    public string BalanceText
    {
        get
        {
            if (_balance < 45) return $"L{50 - _balance}";
            if (_balance > 55) return $"R{_balance - 50}";
            return "C";
        }
    }

    public RadioStatus Status
    {
        get => _status;
        set 
        { 
            _status = value; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(StatusArrowUp));
            OnPropertyChanged(nameof(StatusArrowDown));
            OnPropertyChanged(nameof(StatusUpColor));
            OnPropertyChanged(nameof(StatusDownColor));
        }
    }

    // Arrow visibility and colors based on status
    public Visibility StatusArrowUp => Status == RadioStatus.Transmitting || Status == RadioStatus.TransmitError || Status == RadioStatus.Broadcasting
        ? Visibility.Visible : Visibility.Hidden;
    
    public Visibility StatusArrowDown => Status == RadioStatus.Receiving 
        ? Visibility.Visible : Visibility.Hidden;

    public SolidColorBrush StatusUpColor => Status switch
    {
        RadioStatus.Transmitting => new SolidColorBrush(Color.FromRgb(74, 255, 158)),   // Green
        RadioStatus.Broadcasting => new SolidColorBrush(Color.FromRgb(74, 158, 255)),   // Blue
        _ => new SolidColorBrush(Color.FromRgb(255, 200, 50))   // Yellow for error
    };

    public SolidColorBrush StatusDownColor => new SolidColorBrush(Color.FromRgb(255, 74, 74)); // Red

    public bool AdvancedMode
    {
        get => _advancedMode;
        set { _advancedMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsVisible)); OnPropertyChanged(nameof(ShowBroadcastCheckbox)); }
    }

    public bool IncludedInBroadcast
    {
        get => _includedInBroadcast;
        set { _includedInBroadcast = value; OnPropertyChanged(); MarkChanged(); }
    }

    public bool ShowBroadcastCheckbox => AdvancedMode && !IsEmergencyRadio;

    public int ListenerCount
    {
        get => _listenerCount;
        set { _listenerCount = value; OnPropertyChanged(); }
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set { _hasUnsavedChanges = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Formatted frequency display (e.g., "105.30")
    /// </summary>
    public string FrequencyDisplay
    {
        get
        {
            var freq = FreqId.ToString("0000");
            return $"{freq[0]}{freq[1]}{freq[2]}.{freq[3]}0";
        }
    }

    /// <summary>
    /// Visibility based on advanced mode and emergency status
    /// Emergency radio always visible, others based on index and advanced mode
    /// </summary>
    public Visibility IsVisible
    {
        get
        {
            if (IsEmergencyRadio) return Visibility.Visible;
            return (Index < 4 || AdvancedMode) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // Commands
    public ICommand ClearHotkeyCommand { get; }

    public RadioPanelViewModel()
    {
        ClearHotkeyCommand = new RelayCommand(() => Hotkey = "");
        _freqInput = _freqId.ToString();
    }

    public void AddTransmission(string userName)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RecentTransmissions.Insert(0, userName);
            while (RecentTransmissions.Count > 3)
            {
                RecentTransmissions.RemoveAt(3);
            }
        });
    }

    public void SetTransmitting(bool isTransmitting, bool hasError = false)
    {
        if (isTransmitting)
        {
            Status = hasError ? RadioStatus.TransmitError : RadioStatus.Transmitting;
        }
        else if (Status == RadioStatus.Transmitting || Status == RadioStatus.TransmitError)
        {
            Status = RadioStatus.Idle;
        }
    }

    public void SetBroadcasting(bool isBroadcasting)
    {
        if (isBroadcasting)
        {
            Status = RadioStatus.Broadcasting;
        }
        else if (Status == RadioStatus.Broadcasting)
        {
            Status = RadioStatus.Idle;
        }
    }

    public void SetReceiving(bool isReceiving)
    {
        if (isReceiving)
        {
            Status = RadioStatus.Receiving;
        }
        else if (Status == RadioStatus.Receiving)
        {
            Status = RadioStatus.Idle;
        }
    }

    private void MarkChanged()
    {
        HasUnsavedChanges = true;
        UnsavedChangesOccurred?.Invoke();
    }

    public void ClearChangedFlag()
    {
        HasUnsavedChanges = false;
    }

    public event Action? UnsavedChangesOccurred;
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Simple relay command implementation
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();
}
