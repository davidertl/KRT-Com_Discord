using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NAudio.CoreAudioApi;
using CompanionApp.Models;
using CompanionApp.Services;

namespace CompanionApp.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private CompanionConfig _config = new();
    private HotkeyHook? _hook;
    private BackendClient? _backend;
    private AudioCaptureService? _audio;
    private MumbleService? _mumble;
    private CancellationTokenSource? _streamCts;
    private RadioPanelViewModel? _activeRadio;
    private HashSet<RadioPanelViewModel> _activeBroadcastRadios = new();

    // Radio Panels (8 total, 4 visible in basic mode)
    public ObservableCollection<RadioPanelViewModel> RadioPanels { get; } = new();

    // Emergency Radio (special, always visible when enabled)
    public RadioPanelViewModel EmergencyRadio { get; } = new()
    {
        Index = -1,
        Label = "Emergency",
        FreqId = 9110,
        IsEmergencyRadio = true,
        IsEnabled = true
    };

    // Legacy bindings for hotkey hook compatibility
    public ObservableCollection<HotkeyBinding> Bindings { get; } = new();

    #region Server Settings

    private string _serverBaseUrl = "";
    public string ServerBaseUrl
    {
        get => _serverBaseUrl;
        set { _serverBaseUrl = value; OnPropertyChanged(); }
    }

    private string _adminToken = "";
    public string AdminToken
    {
        get => _adminToken;
        set { _adminToken = value; OnPropertyChanged(); }
    }

    private string _discordUserId = "";
    public string DiscordUserId
    {
        get => _discordUserId;
        set { _discordUserId = value; OnPropertyChanged(); }
    }

    private int _sampleRate = 48000;
    public int SampleRate
    {
        get => _sampleRate;
        set { _sampleRate = value; OnPropertyChanged(); }
    }

    // Mumble settings
    private string _mumbleHost = "127.0.0.1";
    public string MumbleHost
    {
        get => _mumbleHost;
        set { _mumbleHost = value; OnPropertyChanged(); }
    }

    private int _mumblePort = 64738;
    public int MumblePort
    {
        get => _mumblePort;
        set { _mumblePort = value; OnPropertyChanged(); }
    }

    private string _mumbleUsername = "";
    public string MumbleUsername
    {
        get => _mumbleUsername;
        set { _mumbleUsername = value; OnPropertyChanged(); }
    }

    private string _mumblePassword = "";
    public string MumblePassword
    {
        get => _mumblePassword;
        set { _mumblePassword = value; OnPropertyChanged(); }
    }

    private bool _isMumbleConnected;
    public bool IsMumbleConnected
    {
        get => _isMumbleConnected;
        set 
        { 
            _isMumbleConnected = value; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(MumbleConnectionIndicator)); 
            OnPropertyChanged(nameof(MumbleConnectButtonText));
        }
    }

    public string MumbleConnectionIndicator => IsMumbleConnected ? "Connected" : "Disconnected";
    public string MumbleConnectButtonText => IsMumbleConnected ? "Disconnect" : "Connect";

    #endregion

    #region App Settings

    private bool _advancedMode;
    public bool AdvancedMode
    {
        get => _advancedMode;
        set 
        { 
            _advancedMode = value; 
            OnPropertyChanged();
            // Update visibility on all radio panels
            foreach (var panel in RadioPanels)
            {
                panel.AdvancedMode = value;
            }
        }
    }

    private bool _startMinimized;
    public bool StartMinimized
    {
        get => _startMinimized;
        set { _startMinimized = value; OnPropertyChanged(); }
    }

    private bool _launchOnStartup;
    public bool LaunchOnStartup
    {
        get => _launchOnStartup;
        set { _launchOnStartup = value; OnPropertyChanged(); }
    }

    private bool _playPttBeep = true;
    public bool PlayPttBeep
    {
        get => _playPttBeep;
        set { _playPttBeep = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    private bool _autoConnect;
    public bool AutoConnect
    {
        get => _autoConnect;
        set { _autoConnect = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    private bool _deactivateRadiosOnAutoConnect;
    public bool DeactivateRadiosOnAutoConnect
    {
        get => _deactivateRadiosOnAutoConnect;
        set { _deactivateRadiosOnAutoConnect = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    private bool _enableEmergencyRadio = true;
    public bool EnableEmergencyRadio
    {
        get => _enableEmergencyRadio;
        set 
        { 
            _enableEmergencyRadio = value; 
            OnPropertyChanged();
            OnPropertyChanged(nameof(EmergencyRadioVisibility));
            MarkGlobalChanged();
        }
    }

    public Visibility EmergencyRadioVisibility => EnableEmergencyRadio ? Visibility.Visible : Visibility.Collapsed;

    private string _talkToAllHotkey = "";
    public string TalkToAllHotkey
    {
        get => _talkToAllHotkey;
        set { _talkToAllHotkey = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    private string _pttMuteAllHotkey = "";
    public string PttMuteAllHotkey
    {
        get => _pttMuteAllHotkey;
        set { _pttMuteAllHotkey = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    // Audio devices
    public ObservableCollection<string> AudioInputDevices { get; } = new();
    public ObservableCollection<string> AudioOutputDevices { get; } = new();
    
    private string _selectedAudioInputDevice = "Default";
    public string SelectedAudioInputDevice
    {
        get => _selectedAudioInputDevice;
        set { _selectedAudioInputDevice = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    private string _selectedAudioOutputDevice = "Default";
    public string SelectedAudioOutputDevice
    {
        get => _selectedAudioOutputDevice;
        set { _selectedAudioOutputDevice = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    // Has unsaved changes
    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set { _hasUnsavedChanges = value; OnPropertyChanged(); OnPropertyChanged(nameof(SaveButtonBackground)); }
    }

    public string SaveButtonBackground => HasUnsavedChanges ? "#00AA55" : "#3A3A4A";

    #endregion

    #region Status

    private string _statusText = "Idle";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private bool _isStreaming;
    public bool IsStreaming
    {
        get => _isStreaming;
        set { _isStreaming = value; OnPropertyChanged(); OnPropertyChanged(nameof(StreamingIndicator)); }
    }

    public string StreamingIndicator => IsStreaming ? "On" : "Off";

    #endregion

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel()
    {
        // Initialize 8 radio panels with default names
        for (int i = 0; i < 8; i++)
        {
            var panel = new RadioPanelViewModel
            {
                Index = i,
                Label = $"Radio{i + 1}",
                FreqId = 1000 + i,
                IsEnabled = false, // Default unchecked
                AdvancedMode = false
            };
            panel.UnsavedChangesOccurred += MarkGlobalChanged;
            RadioPanels.Add(panel);
        }

        // Set up emergency radio
        EmergencyRadio.UnsavedChangesOccurred += MarkGlobalChanged;

        // Load audio devices
        LoadAudioDevices();
    }

    private void LoadAudioDevices()
    {
        AudioInputDevices.Clear();
        AudioOutputDevices.Clear();

        AudioInputDevices.Add("Default");
        AudioOutputDevices.Add("Default");

        try
        {
            var enumerator = new MMDeviceEnumerator();

            // Input devices
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                AudioInputDevices.Add(device.FriendlyName);
            }

            // Output devices
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                AudioOutputDevices.Add(device.FriendlyName);
            }
        }
        catch
        {
            // Ignore audio enumeration errors
        }
    }

    private void MarkGlobalChanged()
    {
        HasUnsavedChanges = true;
    }

    public async Task InitializeAsync()
    {
        LoadFromConfig(ConfigService.Load());
        SyncRadioPanelsToBindings();
        StartHotkeyHook();
        StatusText = "Ready";
        HasUnsavedChanges = false;

        if (AutoConnect)
        {
            // Deactivate all radios except Emergency if option is set
            if (DeactivateRadiosOnAutoConnect)
            {
                foreach (var panel in RadioPanels)
                {
                    panel.IsEnabled = false;
                }
            }

            try
            {
                await ConnectMumbleAsync();
            }
            catch
            {
                // Ignore auto-connect failures
            }
        }
    }

    public async Task SaveAsync()
    {
        SyncRadioPanelsToBindings();
        ApplyToConfig();
        ConfigService.Save(_config);
        RestartHook();
        await SyncFreqNamesAsync();
        
        // Clear unsaved changes flag
        HasUnsavedChanges = false;
        foreach (var panel in RadioPanels)
        {
            panel.ClearChangedFlag();
        }
        EmergencyRadio.ClearChangedFlag();
        
        StatusText = "Saved";
    }

    public async Task ReloadAsync()
    {
        LoadFromConfig(ConfigService.Load());
        RestartHook();
        StatusText = "Reloaded";
        await Task.CompletedTask;
    }

    public void OpenConfigFolder()
    {
        var folder = ConfigService.GetConfigFolder();
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = folder,
            UseShellExecute = true
        });
    }

    public async Task StartTestAsync()
    {
        var radio = RadioPanels.FirstOrDefault(r => r.IsEnabled);
        if (radio == null)
        {
            StatusText = "No enabled radio";
            return;
        }

        await HandlePttPressedAsync(radio);
    }

    public async Task StopTestAsync()
    {
        await HandlePttReleasedAsync();
    }

    /// <summary>
    /// Start broadcasting to all radios that have IncludedInBroadcast set.
    /// Used for TalkToAll hotkey.
    /// </summary>
    public async Task StartTalkToAllAsync()
    {
        if (IsStreaming)
        {
            return;
        }

        var broadcastRadios = RadioPanels
            .Where(r => r.IsEnabled && r.IncludedInBroadcast)
            .ToList();

        if (!broadcastRadios.Any())
        {
            StatusText = "No radios configured for broadcast";
            return;
        }

        _activeBroadcastRadios = new HashSet<RadioPanelViewModel>(broadcastRadios);

        foreach (var radio in broadcastRadios)
        {
            radio.SetTransmitting(true);
        }

        StatusText = $"Broadcasting to {broadcastRadios.Count} radios";
        IsStreaming = true;

        // Start audio capture once
        _audio?.Dispose();
        _audio = new AudioCaptureService();
        _audio.AudioFrame += AudioOnAudioFrame;
        _audio.Start();
    }

    public async Task StopTalkToAllAsync()
    {
        if (!IsStreaming)
        {
            return;
        }

        foreach (var radio in _activeBroadcastRadios)
        {
            radio.SetTransmitting(false);
        }
        _activeBroadcastRadios.Clear();

        _audio?.Dispose();
        _audio = null;

        IsStreaming = false;
        StatusText = "Broadcast stopped";

        await Task.CompletedTask;
    }

    private void LoadFromConfig(CompanionConfig config)
    {
        _config = config;

        ServerBaseUrl = config.ServerBaseUrl;
        AdminToken = config.AdminToken;
        DiscordUserId = config.DiscordUserId;
        SampleRate = config.SampleRate;

        MumbleHost = config.MumbleHost;
        MumblePort = config.MumblePort;
        MumbleUsername = config.MumbleUsername;
        MumblePassword = config.MumblePassword;

        // Load bindings into radio panels
        for (int i = 0; i < RadioPanels.Count && i < config.Bindings.Count; i++)
        {
            var binding = config.Bindings[i];
            var panel = RadioPanels[i];
            panel.IsEnabled = binding.IsEnabled;
            panel.FreqId = binding.FreqId;
            panel.Hotkey = binding.Hotkey;
            panel.Label = string.IsNullOrWhiteSpace(binding.Label) ? panel.Label : binding.Label;
        }

        Bindings.Clear();
        foreach (var b in config.Bindings)
        {
            Bindings.Add(b);
        }
    }

    private void SyncRadioPanelsToBindings()
    {
        Bindings.Clear();
        foreach (var panel in RadioPanels)
        {
            Bindings.Add(new HotkeyBinding
            {
                IsEnabled = panel.IsEnabled,
                FreqId = panel.FreqId,
                Hotkey = panel.Hotkey,
                Label = panel.Label,
                ChannelName = ""
            });
        }
    }

    private void ApplyToConfig()
    {
        _config.ServerBaseUrl = ServerBaseUrl;
        _config.AdminToken = AdminToken;
        _config.DiscordUserId = DiscordUserId;
        _config.SampleRate = SampleRate;

        _config.MumbleHost = MumbleHost;
        _config.MumblePort = MumblePort;
        _config.MumbleUsername = MumbleUsername;
        _config.MumblePassword = MumblePassword;

        _config.Bindings = Bindings.ToList();
    }

    private async Task SyncFreqNamesAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerBaseUrl) || string.IsNullOrWhiteSpace(AdminToken))
        {
            return;
        }

        try
        {
            using var client = new BackendClient(ServerBaseUrl, AdminToken);
            var entries = RadioPanels
                .Where(r => r.FreqId >= 1000 && r.FreqId <= 9999)
                .Select(r => new { r.FreqId, Name = r.Label.Trim() })
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .GroupBy(r => r.FreqId)
                .Select(g => g.Last());

            foreach (var entry in entries)
            {
                await client.SetFreqNameAsync(entry.FreqId, entry.Name);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Name sync failed: {ex.Message}";
        }
    }

    private void StartHotkeyHook()
    {
        _hook?.Dispose();
        _hook = new HotkeyHook(Bindings, OnHotkeyPressed, OnHotkeyReleased);
        _hook.Start();
    }

    private void RestartHook()
    {
        SyncRadioPanelsToBindings();
        StartHotkeyHook();
    }

    private void OnHotkeyPressed(HotkeyBinding binding)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            // Find matching radio panel
            var radio = RadioPanels.FirstOrDefault(r => r.FreqId == binding.FreqId && r.Hotkey == binding.Hotkey);
            if (radio != null)
            {
                await HandlePttPressedAsync(radio);
            }
        });
    }

    private void OnHotkeyReleased(HotkeyBinding binding)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await HandlePttReleasedAsync();
        });
    }

    private async Task HandlePttPressedAsync(RadioPanelViewModel radio)
    {
        if (IsStreaming)
        {
            return;
        }

        try
        {
            _activeRadio = radio;
            radio.SetTransmitting(true);
            StatusText = $"PTT start ({radio.Label} - Freq {radio.FreqId})";

            _backend?.Dispose();
            _backend = new BackendClient(ServerBaseUrl, AdminToken);

            _audio?.Dispose();
            _audio = new AudioCaptureService();
            _audio.AudioFrame += AudioOnAudioFrame;

            _streamCts?.Dispose();
            _streamCts = new CancellationTokenSource();

            // Use Mumble for audio transmission
            if (_mumble == null || !_mumble.IsConnected)
            {
                await ConnectMumbleAsync();
            }

            if (_mumble != null && _mumble.IsConnected)
            {
                // Join the frequency channel
                await _mumble.JoinFrequencyAsync(radio.FreqId);
                _mumble.StartTransmit();
                _audio.Start();
            }
            else
            {
                StatusText = "Mumble not connected";
                radio.SetTransmitting(false);
                return;
            }

            // Notify backend of TX start (non-fatal if fails)
            try
            {
                await _backend.SendTxEventAsync(radio.FreqId, "start", DiscordUserId, radio.Index + 1);
            }
            catch (Exception backendEx)
            {
                StatusText = $"Backend notify failed: {backendEx.Message}";
                // Continue - audio capture still works
            }

            IsStreaming = true;
        }
        catch (Exception ex)
        {
            StatusText = $"PTT error: {ex.Message}";
            radio.SetTransmitting(false);
            _audio?.Dispose();
            _audio = null;
        }
    }

    private async Task HandlePttReleasedAsync()
    {
        if (!IsStreaming)
        {
            return;
        }

        var radio = _activeRadio;
        _activeRadio = null;

        if (radio != null)
        {
            radio.SetTransmitting(false);
        }

        try
        {
            if (radio != null && _backend != null)
            {
                await _backend.SendTxEventAsync(radio.FreqId, "stop", DiscordUserId, radio.Index + 1);
            }
        }
        catch
        {
        }

        _audio?.Dispose();
        _audio = null;

        _mumble?.StopTransmit();
        // Keep Mumble connection alive for next PTT

        _streamCts?.Dispose();
        _streamCts = null;

        IsStreaming = false;
        StatusText = "PTT stop";
    }

    private void AudioOnAudioFrame(byte[] data)
    {
        try
        {
            if (_mumble != null)
            {
                var format = _audio?.WaveFormat;
                _mumble.SendAudio(data, format?.SampleRate ?? SampleRate, format?.Channels ?? 1);
            }
        }
        catch
        {
            // Ignore audio frame errors to prevent crashes
        }
    }

    public async Task ConnectMumbleAsync()
    {
        if (_mumble != null)
        {
            await _mumble.DisconnectAsync();
            _mumble.Dispose();
        }

        _mumble = new MumbleService();
        _mumble.StatusChanged += status => Application.Current.Dispatcher.Invoke(() => StatusText = status);
        _mumble.ErrorOccurred += ex => Application.Current.Dispatcher.Invoke(() => StatusText = $"Mumble error: {ex.Message}");

        var username = string.IsNullOrWhiteSpace(MumbleUsername) ? $"Companion_{DiscordUserId}" : MumbleUsername;
        await _mumble.ConnectAsync(MumbleHost, MumblePort, username, MumblePassword);
        IsMumbleConnected = _mumble.IsConnected;
    }

    public async Task DisconnectMumbleAsync()
    {
        if (_mumble != null)
        {
            await _mumble.DisconnectAsync();
            _mumble.Dispose();
            _mumble = null;
        }
        IsMumbleConnected = false;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _backend?.Dispose();
        _audio?.Dispose();
        _mumble?.Dispose();
        _streamCts?.Dispose();
    }
}
