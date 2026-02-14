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
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using CompanionApp.Models;
using CompanionApp.Services;

namespace CompanionApp.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    public const string AppVersion = "Alpha 0.0.10";

    private CompanionConfig _config = new();
    private HotkeyHook? _hook;
    private BackendClient? _backend;
    private AudioCaptureService? _audio;
    private VoiceService? _voice;
    private ReconnectManager? _reconnect;
    private BeepService? _beepService;
    private AudioDuckingService? _audioDuckingService;
    private CancellationTokenSource? _streamCts;
    private RadioPanelViewModel? _activeRadio;
    private HashSet<RadioPanelViewModel> _activeBroadcastRadios = new();
    private OverlayWindow? _overlayWindow;

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

    private string _guildId = "";
    public string GuildId
    {
        get => _guildId;
        set { _guildId = value; OnPropertyChanged(); }
    }

    private int _sampleRate = 48000;
    public int SampleRate
    {
        get => _sampleRate;
        set { _sampleRate = value; OnPropertyChanged(); }
    }

    // Voice server settings
    private string _voiceHost = "127.0.0.1";
    public string VoiceHost
    {
        get => _voiceHost;
        set { _voiceHost = value; OnPropertyChanged(); }
    }

    private int _voicePort = 3000;
    public int VoicePort
    {
        get => _voicePort;
        set { _voicePort = value; OnPropertyChanged(); }
    }

    // Auth token from last login
    private string _authToken = "";
    public string AuthToken
    {
        get => _authToken;
        set { _authToken = value; OnPropertyChanged(); }
    }

    // Accepted policy version
    private string _acceptedPolicyVersion = "";

    #endregion

    #region Server Verification

    private bool _isServerVerified;
    public bool IsServerVerified
    {
        get => _isServerVerified;
        set { _isServerVerified = value; OnPropertyChanged(); OnPropertyChanged(nameof(ServerVerifiedVisibility)); OnPropertyChanged(nameof(PolicyNeedsAcceptance)); OnPropertyChanged(nameof(CanLogin)); OnPropertyChanged(nameof(CanLoginWithDiscord)); OnPropertyChanged(nameof(DiscordLoginHint)); }
    }

    public Visibility ServerVerifiedVisibility => IsServerVerified ? Visibility.Visible : Visibility.Collapsed;

    private string _serverVersion = "";
    public string ServerVersion
    {
        get => _serverVersion;
        set { _serverVersion = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsVersionMismatch)); OnPropertyChanged(nameof(VersionMismatchText)); }
    }

    public bool IsVersionMismatch => IsServerVerified
        && !string.IsNullOrEmpty(ServerVersion)
        && !string.Equals(ServerVersion, AppVersion, StringComparison.OrdinalIgnoreCase);

    public string VersionMismatchText =>
        $"Warning: Version mismatch — Server is {ServerVersion}, Companion is {AppVersion}. This may cause compatibility issues.";

    private bool _serverDsgvoEnabled;
    public bool ServerDsgvoEnabled
    {
        get => _serverDsgvoEnabled;
        set { _serverDsgvoEnabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(DsgvoStatusText)); OnPropertyChanged(nameof(DsgvoStatusColor)); }
    }

    private bool _serverDebugMode;
    public bool ServerDebugMode
    {
        get => _serverDebugMode;
        set { _serverDebugMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(DebugModeStatusText)); }
    }

    private int _serverRetentionDays;
    public int ServerRetentionDays
    {
        get => _serverRetentionDays;
        set { _serverRetentionDays = value; OnPropertyChanged(); }
    }

    private string _serverPolicyVersion = "";
    public string ServerPolicyVersion
    {
        get => _serverPolicyVersion;
        set { _serverPolicyVersion = value; OnPropertyChanged(); OnPropertyChanged(nameof(PolicyNeedsAcceptance)); }
    }

    private string _privacyPolicyText = "";
    public string PrivacyPolicyText
    {
        get => _privacyPolicyText;
        set { _privacyPolicyText = value; OnPropertyChanged(); }
    }

    private bool _policyAccepted;
    public bool PolicyAccepted
    {
        get => _policyAccepted;
        set { _policyAccepted = value; OnPropertyChanged(); OnPropertyChanged(nameof(PolicyNeedsAcceptance)); OnPropertyChanged(nameof(CanLogin)); OnPropertyChanged(nameof(CanLoginWithDiscord)); OnPropertyChanged(nameof(DiscordLoginHint)); }
    }

    public bool PolicyNeedsAcceptance => IsServerVerified && !PolicyAccepted;
    public bool CanLogin => IsServerVerified && PolicyAccepted;

    private bool _oauthEnabled;
    public bool OauthEnabled
    {
        get => _oauthEnabled;
        set { _oauthEnabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanLoginWithDiscord)); OnPropertyChanged(nameof(DiscordLoginHint)); }
    }

    /// <summary>True when user can click "Login with Discord".</summary>
    public bool CanLoginWithDiscord => IsServerVerified && PolicyAccepted && OauthEnabled && !_isOAuthInProgress && !IsLoggedIn;

    /// <summary>Hint text shown when button is disabled.</summary>
    public string DiscordLoginHint
    {
        get
        {
            if (!IsServerVerified) return "";
            if (!OauthEnabled) return "Server does not have Discord OAuth configured.";
            if (!PolicyAccepted) return "Accept the privacy policy to enable login.";
            if (_isOAuthInProgress) return "Login in progress…";
            if (IsLoggedIn) return "";
            return "";
        }
    }

    private bool _isOAuthInProgress;
    public bool IsOAuthInProgress
    {
        get => _isOAuthInProgress;
        set { _isOAuthInProgress = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanLoginWithDiscord)); OnPropertyChanged(nameof(DiscordLoginHint)); }
    }

    private string _oauthLoginStatus = "";
    public string OAuthLoginStatus
    {
        get => _oauthLoginStatus;
        set { _oauthLoginStatus = value; OnPropertyChanged(); }
    }

    private string _loggedInDisplayName = "";
    public string LoggedInDisplayName
    {
        get => _loggedInDisplayName;
        set { _loggedInDisplayName = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLoggedIn)); OnPropertyChanged(nameof(CanLoginWithDiscord)); OnPropertyChanged(nameof(DiscordLoginHint)); }
    }

    public bool IsLoggedIn => !string.IsNullOrEmpty(LoggedInDisplayName);

    public string DsgvoStatusText => ServerDsgvoEnabled ? "DSGVO: Enabled" : "DSGVO: Disabled";
    public string DsgvoStatusColor => ServerDsgvoEnabled ? "#4AFF9E" : "#FF4A4A";
    public string DebugModeStatusText => ServerDebugMode ? "Debug: Active" : "Debug: Off";

    private string _verifyStatusText = "";
    public string VerifyStatusText
    {
        get => _verifyStatusText;
        set { _verifyStatusText = value; OnPropertyChanged(); }
    }

    private bool _isVoiceConnected;
    public bool IsVoiceConnected
    {
        get => _isVoiceConnected;
        set 
        { 
            _isVoiceConnected = value; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(VoiceConnectionIndicator)); 
            OnPropertyChanged(nameof(VoiceConnectButtonText));
            OnPropertyChanged(nameof(CanLogin));
        }
    }

    public string VoiceConnectionIndicator => IsVoiceConnected ? "Connected" 
        : (_voiceConnectionState == VoiceConnectionState.Reconnecting ? "Reconnecting…" 
        : (_voiceConnectionState == VoiceConnectionState.Failed ? "Reconnect Failed" 
        : "Disconnected"));
    public string VoiceConnectButtonText => IsVoiceConnected ? "Disconnect" : "Connect";

    private VoiceConnectionState _voiceConnectionState = VoiceConnectionState.Disconnected;
    public VoiceConnectionState VoiceConnectionState
    {
        get => _voiceConnectionState;
        private set
        {
            _voiceConnectionState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VoiceConnectionIndicator));
            OnPropertyChanged(nameof(IsVoiceReconnecting));
            OnPropertyChanged(nameof(ReconnectStatusColor));
        }
    }

    public bool IsVoiceReconnecting => _voiceConnectionState == VoiceConnectionState.Reconnecting;

    /// <summary>Indicator dot color: green=connected, orange=reconnecting, red=disconnected/failed.</summary>
    public string ReconnectStatusColor => _voiceConnectionState switch
    {
        VoiceConnectionState.Connected => "#4AFF9E",
        VoiceConnectionState.Reconnecting => "#FFD84A",
        VoiceConnectionState.Connecting => "#FFD84A",
        _ => "#FF4A4A"
    };

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
        set
        {
            if (_startMinimized == value) return;
            _startMinimized = value;
            OnPropertyChanged();
            MarkGlobalChanged();
        }
    }

    private bool _launchOnStartup;
    public bool LaunchOnStartup
    {
        get => _launchOnStartup;
        set
        {
            if (_launchOnStartup == value) return;
            _launchOnStartup = value;
            OnPropertyChanged();
            MarkGlobalChanged();
            ApplyLaunchOnStartupSetting();
        }
    }

    private bool _playPttBeep = true;
    public bool PlayPttBeep
    {
        get => _playPttBeep;
        set 
        { 
            _playPttBeep = value; 
            OnPropertyChanged(); 
            MarkGlobalChanged();
            if (_beepService != null) _beepService.Enabled = value;
        }
    }

    private bool _playSoundOnTransmit = true;
    public bool PlaySoundOnTransmit
    {
        get => _playSoundOnTransmit;
        set { _playSoundOnTransmit = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    private bool _playSoundOnReceive = true;
    public bool PlaySoundOnReceive
    {
        get => _playSoundOnReceive;
        set { _playSoundOnReceive = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    private bool _playSoundOnBegin = true;
    public bool PlaySoundOnBegin
    {
        get => _playSoundOnBegin;
        set { _playSoundOnBegin = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    private bool _playSoundOnEnd = true;
    public bool PlaySoundOnEnd
    {
        get => _playSoundOnEnd;
        set { _playSoundOnEnd = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    private bool _autoConnect = true;
    public bool AutoConnect
    {
        get => _autoConnect;
        set { _autoConnect = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    private bool _saveRadioActiveState = true;
    public bool SaveRadioActiveState
    {
        get => _saveRadioActiveState;
        set { _saveRadioActiveState = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    private bool _turnOnEmergencyOnStartup = true;
    public bool TurnOnEmergencyOnStartup
    {
        get => _turnOnEmergencyOnStartup;
        set { _turnOnEmergencyOnStartup = value; OnPropertyChanged(); MarkGlobalChanged(); }
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
            PushRadioSettingsToVoice(EmergencyRadio);
            _ = PushServerMuteAsync(EmergencyRadio);
            _ = HandleEmergencyRadioToggleAsync();
        }
    }

    // ---- Overlay settings ----
    private bool _overlayEnabled;
    public bool OverlayEnabled
    {
        get => _overlayEnabled;
        set
        {
            _overlayEnabled = value;
            OnPropertyChanged();
            MarkGlobalChanged();
            if (_overlayEnabled) ShowOverlay(); else HideOverlay();
        }
    }

    private bool _overlayShowRank;
    public bool OverlayShowRank
    {
        get => _overlayShowRank;
        set { _overlayShowRank = value; OnPropertyChanged(); MarkGlobalChanged(); RefreshOverlay(); }
    }

    private bool _overlayShowRadioKeybind;
    public bool OverlayShowRadioKeybind
    {
        get => _overlayShowRadioKeybind;
        set { _overlayShowRadioKeybind = value; OnPropertyChanged(); MarkGlobalChanged(); RefreshOverlay(); }
    }

    private int _overlayPositionX = 20;
    public int OverlayPositionX
    {
        get => _overlayPositionX;
        set { _overlayPositionX = value; OnPropertyChanged(); MarkGlobalChanged(); _overlayWindow?.SetPosition(_overlayPositionX, _overlayPositionY); }
    }

    private int _overlayPositionY = 20;
    public int OverlayPositionY
    {
        get => _overlayPositionY;
        set { _overlayPositionY = value; OnPropertyChanged(); MarkGlobalChanged(); _overlayWindow?.SetPosition(_overlayPositionX, _overlayPositionY); }
    }

    private int _overlayOpacity = 80;
    public int OverlayOpacity
    {
        get => _overlayOpacity;
        set { _overlayOpacity = Math.Clamp(value, 10, 100); OnPropertyChanged(); OnPropertyChanged(nameof(OverlayOpacityText)); MarkGlobalChanged(); _overlayWindow?.SetBackgroundOpacity(_overlayOpacity / 100.0); }
    }

    public string OverlayOpacityText => $"{OverlayOpacity}%";

    private bool _overlayAutoHideEnabled = true;
    public bool OverlayAutoHideEnabled
    {
        get => _overlayAutoHideEnabled;
        set { _overlayAutoHideEnabled = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    private int _overlayAutoHideSeconds = 60;
    public int OverlayAutoHideSeconds
    {
        get => _overlayAutoHideSeconds;
        set { _overlayAutoHideSeconds = Math.Clamp(value, 5, 600); OnPropertyChanged(); OnPropertyChanged(nameof(OverlayAutoHideText)); MarkGlobalChanged(); }
    }

    public string OverlayAutoHideText => $"{OverlayAutoHideSeconds}s";

    private System.Windows.Threading.DispatcherTimer? _overlayCleanupTimer;

    private bool _debugLoggingEnabled;
    public bool DebugLoggingEnabled
    {
        get => _debugLoggingEnabled;
        set
        {
            _debugLoggingEnabled = value;
            OnPropertyChanged();
            MarkGlobalChanged();

            if (_debugLoggingEnabled)
            {
                EnsureDebugLogDirectory();
                LogDebug("Debug logging enabled");
            }
            else
            {
                LogDebug("Debug logging disabled");
            }
        }
    }

    public string DebugLogFilePath => _debugLogFilePath;

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

    private string _toggleMuteAllHotkey = "";
    public string ToggleMuteAllHotkey
    {
        get => _toggleMuteAllHotkey;
        set { _toggleMuteAllHotkey = value; OnPropertyChanged(); MarkGlobalChanged(); }
    }

    // Master input volume (0-125, default 100)
    private int _inputVolume = 100;
    public int InputVolume
    {
        get => _inputVolume;
        set 
        { 
            _inputVolume = Math.Clamp(value, 0, 125); 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(InputVolumeText));
            MarkGlobalChanged();
            _voice?.SetMasterInputVolume(_inputVolume / 100f);
        }
    }
    public string InputVolumeText => $"{_inputVolume}%";

    // Master output volume (0-125, default 100)
    private int _outputVolume = 100;
    public int OutputVolume
    {
        get => _outputVolume;
        set 
        { 
            _outputVolume = Math.Clamp(value, 0, 125); 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(OutputVolumeText));
            MarkGlobalChanged();
            _voice?.SetMasterOutputVolume(_outputVolume / 100f);
            _beepService?.SetMasterVolume(_outputVolume / 100f);
        }
    }
    public string OutputVolumeText => $"{_outputVolume}%";

    // Voice ducking enabled
    private bool _duckingEnabled;
    public bool DuckingEnabled
    {
        get => _duckingEnabled;
        set
        {
            _duckingEnabled = value;
            OnPropertyChanged();
            MarkGlobalChanged();
            _voice?.SetDuckingEnabled(_duckingEnabled);
        }
    }

    // Duck when sending (PTT held)
    private bool _duckOnSend = true;
    public bool DuckOnSend
    {
        get => _duckOnSend;
        set
        {
            _duckOnSend = value;
            OnPropertyChanged();
            MarkGlobalChanged();
            _voice?.SetDuckOnSend(_duckOnSend);
        }
    }

    // Duck when receiving voice from others
    private bool _duckOnReceive = true;
    public bool DuckOnReceive
    {
        get => _duckOnReceive;
        set
        {
            _duckOnReceive = value;
            OnPropertyChanged();
            MarkGlobalChanged();
            _voice?.SetDuckOnReceive(_duckOnReceive);
            // If disabling duck-on-receive while currently ducked, restore immediately
            if (!value) RestoreRxDucking();
        }
    }

    /// <summary>Number of frequencies currently receiving audio from other users.</summary>
    private int _activeRxCount;

    // Voice ducking level (0-100, default 50 = moderate ducking)
    private int _duckingLevel = 50;
    public int DuckingLevel
    {
        get => _duckingLevel;
        set
        {
            _duckingLevel = Math.Clamp(value, 0, 100);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DuckingLevelText));
            MarkGlobalChanged();
            _voice?.SetDuckingLevel(_duckingLevel);
        }
    }
    public string DuckingLevelText => $"{_duckingLevel}%";

    // Voice ducking mode: 0 = Radio audio only, 1 = Selected apps, 2 = All audio except KRT-Com
    private int _duckingMode;
    public int DuckingMode
    {
        get => _duckingMode;
        set
        {
            _duckingMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowDuckedProcessSelector));
            MarkGlobalChanged();
            _audioDuckingService?.SetDuckingTargets((DuckingTargetMode)_duckingMode, DuckedProcessNames?.ToList());
        }
    }

    public bool ShowDuckedProcessSelector => _duckingMode == 1 && _duckingEnabled;

    // Process names selected for ducking
    private ObservableCollection<string> _duckedProcessNames = new();
    public ObservableCollection<string> DuckedProcessNames
    {
        get => _duckedProcessNames;
        set { _duckedProcessNames = value; OnPropertyChanged(); }
    }

    // Available audio sessions for the process selector
    private ObservableCollection<AudioSessionInfo> _availableAudioSessions = new();
    public ObservableCollection<AudioSessionInfo> AvailableAudioSessions
    {
        get => _availableAudioSessions;
        set { _availableAudioSessions = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Refresh the list of audio sessions available for ducking selection.
    /// </summary>
    public void RefreshAudioSessions()
    {
        if (_audioDuckingService == null) return;
        var sessions = _audioDuckingService.GetAudioSessions();

        // Mark sessions that are already selected
        foreach (var s in sessions)
            s.IsSelected = DuckedProcessNames.Contains(s.ProcessName);

        AvailableAudioSessions = new ObservableCollection<AudioSessionInfo>(sessions);
    }

    /// <summary>
    /// Toggle a process name in the ducked process list.
    /// </summary>
    public void ToggleDuckedProcess(string processName)
    {
        if (DuckedProcessNames.Contains(processName))
            DuckedProcessNames.Remove(processName);
        else
            DuckedProcessNames.Add(processName);

        MarkGlobalChanged();
        _audioDuckingService?.SetDuckingTargets((DuckingTargetMode)DuckingMode, DuckedProcessNames.ToList());

        // Update selection state in available sessions
        foreach (var s in AvailableAudioSessions)
            s.IsSelected = DuckedProcessNames.Contains(s.ProcessName);
        OnPropertyChanged(nameof(AvailableAudioSessions));
    }

    private bool _allRadiosMuted;
    public bool AllRadiosMuted
    {
        get => _allRadiosMuted;
        set { _allRadiosMuted = value; OnPropertyChanged(); }
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
        set 
        { 
            _selectedAudioOutputDevice = value; 
            OnPropertyChanged(); 
            MarkGlobalChanged();
            _beepService?.SetOutputDevice(value);
            _voice?.SetOutputDevice(value);
        }
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
        set
        {
            _statusText = value;
            OnPropertyChanged();
            LogDebug($"Status: {value}");
        }
    }

    private bool _isStreaming;
    public bool IsStreaming
    {
        get => _isStreaming;
        set { _isStreaming = value; OnPropertyChanged(); OnPropertyChanged(nameof(StreamingIndicator)); OnPropertyChanged(nameof(StreamingIndicatorColor)); }
    }

    private bool _isBroadcasting;
    public bool IsBroadcasting
    {
        get => _isBroadcasting;
        set { _isBroadcasting = value; OnPropertyChanged(); OnPropertyChanged(nameof(StreamingIndicatorColor)); }
    }

    public string StreamingIndicator => IsStreaming ? "On" : "Off";
    public string StreamingIndicatorColor => IsBroadcasting ? "#4A9EFF" : "#4AFF9E"; // Blue when broadcasting, green otherwise

    #endregion

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel()
    {
        _debugLogFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KRT-Com_Discord",
            "debug.log");

        // Initialize beep service
        _beepService = new BeepService();
        _beepService.SetMasterVolume(_outputVolume / 100f);

        // Initialize audio ducking service
        _audioDuckingService = new AudioDuckingService();
        _audioDuckingService.Log = msg => LogDebug(msg);

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
            panel.PropertyChanged += OnRadioPanelPropertyChanged;
            RadioPanels.Add(panel);
        }

        // Set up emergency radio
        EmergencyRadio.UnsavedChangesOccurred += MarkGlobalChanged;
        EmergencyRadio.PropertyChanged += OnRadioPanelPropertyChanged;

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
            // If NOT saving active state, turn all radios off on startup
            if (!SaveRadioActiveState)
            {
                foreach (var panel in RadioPanels)
                {
                    panel.IsEnabled = false;
                }
            }

            // If "Turn on Emergency on startup" is set, enable it
            if (TurnOnEmergencyOnStartup)
            {
                EmergencyRadio.IsEnabled = true;
            }

            try
            {
                // Auto-verify server first
                await VerifyServerAsync();

                // If we have a saved auth token and policy is accepted, auto-connect
                if (IsServerVerified && PolicyAccepted && !string.IsNullOrWhiteSpace(AuthToken))
                {
                    await ConnectVoiceAsync();
                }
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

    private void ApplyLaunchOnStartupSetting()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null)
            {
                StatusText = "Could not access startup registry key";
                return;
            }

            if (LaunchOnStartup)
            {
                var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue("KRTComDiscord", "\"" + exePath + "\"");
                }
            }
            else
            {
                key.DeleteValue("KRTComDiscord", false);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Startup setting failed: {ex.Message}";
            LogDebug($"[Startup] Failed to set LaunchOnStartup={LaunchOnStartup}: {ex.Message}");
        }
    }

    /// <summary>
    /// Verify server connection: fetch server status and privacy policy.
    /// Called when user clicks the "Verify" button.
    /// </summary>
    public async Task VerifyServerAsync()
    {
        var baseUrl = BuildBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            VerifyStatusText = "Please enter host and port first";
            return;
        }

        VerifyStatusText = "Verifying server...";
        IsServerVerified = false;
        PolicyAccepted = false;

        try
        {
            var status = await BackendClient.GetServerStatusAsync(baseUrl);
            if (status == null)
            {
                VerifyStatusText = "Server not reachable or invalid response";
                return;
            }

            ServerVersion = status.Version;
            ServerDsgvoEnabled = status.DsgvoEnabled;
            ServerDebugMode = status.DebugMode;
            ServerRetentionDays = status.RetentionDays;
            ServerPolicyVersion = status.PolicyVersion;
            OauthEnabled = status.OauthEnabled;

            var policy = await BackendClient.GetPrivacyPolicyAsync(baseUrl);
            PrivacyPolicyText = policy?.Text ?? "Could not fetch privacy policy.";

            // Check if user already accepted this policy version
            if (_acceptedPolicyVersion == ServerPolicyVersion)
            {
                PolicyAccepted = true;
            }

            IsServerVerified = true;
            OnPropertyChanged(nameof(IsVersionMismatch));
            OnPropertyChanged(nameof(VersionMismatchText));
            VerifyStatusText = $"Server verified: {status.Version}";
            LogDebug($"[Verify] Server verified: version={status.Version} dsgvo={status.DsgvoEnabled} debug={status.DebugMode} oauth={status.OauthEnabled}");
            if (IsVersionMismatch)
                LogDebug($"[Verify] VERSION MISMATCH: server={status.Version} companion={AppVersion}");
        }
        catch (Exception ex)
        {
            VerifyStatusText = $"Verification failed: {ex.Message}";
            LogDebug($"[Verify] Failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Accept the privacy policy for the current server policy version.
    /// </summary>
    public async Task AcceptPolicyAsync()
    {
        _acceptedPolicyVersion = ServerPolicyVersion;
        PolicyAccepted = true;
        MarkGlobalChanged();

        // If we already have an auth token, notify the server
        if (!string.IsNullOrWhiteSpace(AuthToken))
        {
            try
            {
                var baseUrl = BuildBaseUrl();
                using var client = new BackendClient(baseUrl, AdminToken);
                client.SetAuthToken(AuthToken);
                await client.AcceptPolicyAsync(ServerPolicyVersion);
                LogDebug($"[Policy] Accepted policy version {ServerPolicyVersion} on server");
            }
            catch (Exception ex)
            {
                LogDebug($"[Policy] Server accept failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Login via Discord OAuth2. Opens browser, polls for token.
    /// </summary>
    public async Task<bool> LoginWithDiscordAsync()
    {
        if (!IsServerVerified)
        {
            OAuthLoginStatus = "Please verify the server first";
            return false;
        }

        if (!PolicyAccepted)
        {
            OAuthLoginStatus = "Please accept the privacy policy first";
            return false;
        }

        if (!OauthEnabled)
        {
            OAuthLoginStatus = "Server does not have Discord OAuth configured";
            return false;
        }

        IsOAuthInProgress = true;
        OAuthLoginStatus = "Opening browser for Discord login…";

        var baseUrl = BuildBaseUrl();
        var state = Guid.NewGuid().ToString("N");

        try
        {
            // Open browser to backend's OAuth redirect endpoint
            var redirectUrl = $"{baseUrl}/auth/discord/redirect?state={Uri.EscapeDataString(state)}";
            // Validate URL scheme to prevent opening arbitrary protocols
            if (!Uri.TryCreate(redirectUrl, UriKind.Absolute, out var validUri) ||
                (validUri.Scheme != "http" && validUri.Scheme != "https"))
            {
                OAuthLoginStatus = "Invalid server URL scheme";
                return false;
            }
            Process.Start(new ProcessStartInfo(validUri.AbsoluteUri) { UseShellExecute = true });

            // Poll for result (every 2 seconds, up to 3 minutes)
            OAuthLoginStatus = "Waiting for Discord authorization…";
            var timeout = DateTime.UtcNow.AddMinutes(3);

            while (DateTime.UtcNow < timeout)
            {
                await Task.Delay(2000);

                var result = await BackendClient.PollOAuthTokenAsync(baseUrl, state);
                if (result == null) continue;

                if (result.Status == "pending") continue;

                if (result.Status == "error")
                {
                    var errorMsg = result.Error switch
                    {
                        "not_in_guild" => "You are not a member of the allowed Discord server.",
                        "no_guild" => "No matching guilds found.",
                        "banned" => "Your account has been banned.",
                        _ => $"Login error: {result.Error}"
                    };
                    OAuthLoginStatus = errorMsg;
                    LogDebug($"[OAuth] Error: {result.Error}");
                    return false;
                }

                if (result.Status == "success" && !string.IsNullOrEmpty(result.Token))
                {
                    AuthToken = result.Token;
                    LoggedInDisplayName = result.DisplayName ?? "";

                    // Accept policy on server if needed
                    if (!result.PolicyAccepted && !string.IsNullOrEmpty(AuthToken))
                    {
                        using var client = new BackendClient(baseUrl, AdminToken);
                        client.SetAuthToken(AuthToken);
                        await client.AcceptPolicyAsync(result.PolicyVersion ?? ServerPolicyVersion);
                    }

                    OAuthLoginStatus = $"Logged in as {LoggedInDisplayName}";
                    StatusText = $"Logged in as {LoggedInDisplayName}";
                    LogDebug($"[OAuth] Login OK: {LoggedInDisplayName}");
                    MarkGlobalChanged();

                    // Auto-connect voice
                    await ConnectVoiceAsync();
                    return true;
                }

                if (result.Status == "unknown")
                {
                    // State expired or was never registered
                    OAuthLoginStatus = "Login session expired. Please try again.";
                    return false;
                }
            }

            OAuthLoginStatus = "Login timed out. Please try again.";
            return false;
        }
        catch (Exception ex)
        {
            OAuthLoginStatus = $"Login error: {ex.Message}";
            LogDebug($"[OAuth] Error: {ex.Message}");
            return false;
        }
        finally
        {
            IsOAuthInProgress = false;
        }
    }

    private string BuildBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(VoiceHost)) return "";
        var cleanHost = VoiceHost
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        // Default to HTTPS; only use HTTP for explicit localhost
        var isLocalhost = cleanHost is "127.0.0.1" or "localhost" or "::1";
        var scheme = (VoiceHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || isLocalhost) ? "http" : "https";
        return $"{scheme}://{cleanHost}:{VoicePort}";
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

        // Only works in advanced mode
        if (!AdvancedMode)
        {
            StatusText = "Talk to All requires Advanced Mode";
            return;
        }

        // NOTE: Broadcasts intentionally do NOT check IsMuted — the to-do spec says
        // "disable the possibility to transmit on a frequency that is muted except for broadcasts."
        // Muted radios that are included in broadcast should still transmit.
        var broadcastRadios = RadioPanels
            .Where(r => r.IsEnabled && r.IncludedInBroadcast)
            .ToList();

        if (!broadcastRadios.Any())
        {
            StatusText = "No radios configured for broadcast";
            return;
        }

        try
        {
            _activeBroadcastRadios = new HashSet<RadioPanelViewModel>(broadcastRadios);
            IsBroadcasting = true;

            foreach (var radio in broadcastRadios)
            {
                radio.SetBroadcasting(true);
            }

            StatusText = $"Broadcasting to {broadcastRadios.Count} radios";

            // Connect to voice server if needed
            if (_voice == null || !_voice.IsConnected)
            {
                await ConnectVoiceAsync();
            }

            if (_voice == null || !_voice.IsConnected)
            {
                StatusText = "Voice not connected - broadcast failed";
                foreach (var radio in broadcastRadios)
                {
                    radio.SetBroadcasting(false);
                }
                _activeBroadcastRadios.Clear();
                IsBroadcasting = false;
                return;
            }

            // Start transmitting to first radio's frequency (broadcasts will go to all)
            var firstRadio = broadcastRadios.First();
            await _voice.JoinFrequencyAsync(firstRadio.FreqId);
            _voice.StartTransmit();

            // Start audio capture
            _audio?.Dispose();
            _audio = new AudioCaptureService(SelectedAudioInputDevice);
            _audio.AudioFrame += AudioOnAudioFrame;
            _audio.Start();

            IsStreaming = true;

            // Apply external app ducking if configured (duck-on-send)
            if (DuckingEnabled && _duckOnSend && _duckingLevel < 100 && _duckingMode != 0)
            {
                _audioDuckingService?.ApplyDucking(_duckingLevel / 100f);
                LogDebug($"[Ducking] External duck-on-send (broadcast) applied: level={_duckingLevel}% mode={_duckingMode}");
            }

            if (PlaySoundOnTransmit && PlaySoundOnBegin)
                _beepService?.PlayTalkToAllBeep(
                broadcastRadios.Max(r => r.Volume) / 100f,
                (float)broadcastRadios.Average(r => r.Balance) / 100f);
        }
        catch (Exception ex)
        {
            StatusText = $"Broadcast error: {ex.Message}";
            foreach (var radio in _activeBroadcastRadios)
            {
                radio.SetBroadcasting(false);
            }
            _activeBroadcastRadios.Clear();
            IsBroadcasting = false;
        }
    }

    public async Task StopTalkToAllAsync()
    {
        if (!IsStreaming || !IsBroadcasting)
        {
            return;
        }

        var userName = string.IsNullOrWhiteSpace(LoggedInDisplayName) ? "You" : LoggedInDisplayName;
        var timestamp = $"{DateTime.Now:HH:mm} - {userName} (Broadcast)";

        // Capture vol/pan before clearing the set
        float bcastVol = _activeBroadcastRadios.Any() ? _activeBroadcastRadios.Max(r => r.Volume) / 100f : 1f;
        float bcastPan = _activeBroadcastRadios.Any() ? (float)_activeBroadcastRadios.Average(r => r.Balance) / 100f : 0.5f;

        foreach (var radio in _activeBroadcastRadios)
        {
            radio.SetBroadcasting(false);
            radio.AddTransmission(timestamp);
        }
        _activeBroadcastRadios.Clear();

        _audio?.Dispose();
        _audio = null;

        _voice?.StopTransmit();

        // Restore external app ducking (unless duck-on-receive is keeping it active)
        if (_activeRxCount == 0)
            _audioDuckingService?.RestoreDucking();

        IsBroadcasting = false;
        IsStreaming = false;
        if (PlaySoundOnTransmit && PlaySoundOnEnd)
            _beepService?.PlayTxEndBeep(bcastVol, bcastPan);
        StatusText = "Broadcast stopped";

        await Task.CompletedTask;
    }

    /// <summary>
    /// Toggle mute all radios
    /// </summary>
    public void ToggleMuteAllRadios()
    {
        AllRadiosMuted = !AllRadiosMuted;
        
        // Apply mute state to all radios
        foreach (var panel in RadioPanels)
        {
            panel.IsMuted = AllRadiosMuted;
        }
        EmergencyRadio.IsMuted = AllRadiosMuted;
        
        StatusText = AllRadiosMuted ? "All radios muted" : "All radios unmuted";
    }

    /// <summary>
    /// Push-to-Mute all radios (while key is held)
    /// </summary>
    public void SetAllRadiosMuted(bool muted)
    {
        AllRadiosMuted = muted;
        
        // Apply mute state to all radios
        foreach (var panel in RadioPanels)
        {
            panel.IsMuted = muted;
        }
        EmergencyRadio.IsMuted = muted;
        
        StatusText = muted ? "All radios muted (PTM)" : "All radios unmuted";
    }

    /// <summary>
    /// Check if a hotkey is already in use by another function.
    /// Returns the name of the conflicting function, or null if no conflict.
    /// </summary>
    public string? GetHotkeyConflict(string hotkey, string excludeSource = "")
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return null;

        // Check radio panel hotkeys
        foreach (var panel in RadioPanels)
        {
            if (panel.Hotkey == hotkey && panel.Label != excludeSource)
                return panel.Label;
        }

        // Check emergency radio
        if (EmergencyRadio.Hotkey == hotkey && "Emergency" != excludeSource)
            return "Emergency";

        // Check global hotkeys
        if (TalkToAllHotkey == hotkey && "Talk To All" != excludeSource)
            return "Talk To All";
        if (PttMuteAllHotkey == hotkey && "PTM All Radio" != excludeSource)
            return "PTM All Radio";
        if (ToggleMuteAllHotkey == hotkey && "TTM All Radio" != excludeSource)
            return "TTM All Radio";

        return null;
    }

    /// <summary>
    /// Clear a hotkey from any function that currently uses it.
    /// </summary>
    public void ClearHotkeyFromAll(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return;

        foreach (var panel in RadioPanels)
        {
            if (panel.Hotkey == hotkey) panel.Hotkey = "";
        }

        if (EmergencyRadio.Hotkey == hotkey) EmergencyRadio.Hotkey = "";
        if (TalkToAllHotkey == hotkey) TalkToAllHotkey = "";
        if (PttMuteAllHotkey == hotkey) PttMuteAllHotkey = "";
        if (ToggleMuteAllHotkey == hotkey) ToggleMuteAllHotkey = "";
    }

    private void LoadFromConfig(CompanionConfig config)
    {
        _config = config;

        DiscordUserId = config.DiscordUserId;
        GuildId = config.GuildId;
        SampleRate = config.SampleRate;

        VoiceHost = config.VoiceHost;
        VoicePort = config.VoicePort;

        AuthToken = config.AuthToken;
        LoggedInDisplayName = config.LoggedInDisplayName;
        _acceptedPolicyVersion = config.AcceptedPolicyVersion;

        AutoConnect = config.AutoConnect;
        StartMinimized = config.StartMinimized;
        _launchOnStartup = config.LaunchOnStartup;
        OnPropertyChanged(nameof(LaunchOnStartup));
        SaveRadioActiveState = config.SaveRadioActiveState;
        TurnOnEmergencyOnStartup = config.TurnOnEmergencyOnStartup;
        EnableEmergencyRadio = config.EnableEmergencyRadio;
        OverlayShowRank = config.OverlayShowRank;
        OverlayShowRadioKeybind = config.OverlayShowRadioKeybind;
        _overlayPositionX = config.OverlayPositionX;
        OnPropertyChanged(nameof(OverlayPositionX));
        _overlayPositionY = config.OverlayPositionY;
        OnPropertyChanged(nameof(OverlayPositionY));
        _overlayOpacity = Math.Clamp(config.OverlayOpacity, 10, 100);
        OnPropertyChanged(nameof(OverlayOpacity));
        OnPropertyChanged(nameof(OverlayOpacityText));
        OverlayAutoHideEnabled = config.OverlayAutoHideEnabled;
        OverlayAutoHideSeconds = config.OverlayAutoHideSeconds;
        // Open overlay last so position/opacity are already set
        OverlayEnabled = config.OverlayEnabled;
        DebugLoggingEnabled = config.DebugLoggingEnabled;
        PlaySoundOnTransmit = config.PlaySoundOnTransmit;
        PlaySoundOnReceive = config.PlaySoundOnReceive;
        PlaySoundOnBegin = config.PlaySoundOnBegin;
        PlaySoundOnEnd = config.PlaySoundOnEnd;
        // Keep PlayPttBeep in sync: enabled if any sound toggle is on
        PlayPttBeep = config.PlaySoundOnTransmit || config.PlaySoundOnReceive || config.PlaySoundOnBegin || config.PlaySoundOnEnd;
        InputVolume = config.InputVolume;
        OutputVolume = config.OutputVolume;
        DuckingEnabled = config.DuckingEnabled;
        DuckOnSend = config.DuckOnSend;
        DuckOnReceive = config.DuckOnReceive;
        DuckingLevel = config.DuckingLevel;
        DuckingMode = config.DuckingMode;
        DuckedProcessNames = new ObservableCollection<string>(config.DuckedProcessNames ?? new List<string>());
        _audioDuckingService?.SetDuckingTargets((DuckingTargetMode)DuckingMode, DuckedProcessNames?.ToList());

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

        // Restore per-radio state (volume, balance, muted, broadcast)
        foreach (var state in config.RadioStates)
        {
            if (state.Index >= 0 && state.Index < RadioPanels.Count)
            {
                var panel = RadioPanels[state.Index];
                panel.Volume = state.Volume;
                panel.Balance = state.Balance;
                panel.IsMuted = state.IsMuted;
                panel.IncludedInBroadcast = state.IncludedInBroadcast;
            }
        }

        // Restore emergency radio state
        if (config.EmergencyRadioState != null)
        {
            EmergencyRadio.Volume = config.EmergencyRadioState.Volume;
            EmergencyRadio.Balance = config.EmergencyRadioState.Balance;
            EmergencyRadio.IsMuted = config.EmergencyRadioState.IsMuted;
            EmergencyRadio.IsEnabled = config.EmergencyRadioState.IsEnabled;
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
        
        // Add radio panel bindings
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
        
        // Add Emergency radio binding
        if (EmergencyRadio != null && !string.IsNullOrEmpty(EmergencyRadio.Hotkey))
        {
            Bindings.Add(new HotkeyBinding
            {
                IsEnabled = true,
                FreqId = EmergencyRadio.FreqId,
                Hotkey = EmergencyRadio.Hotkey,
                Label = "Emergency",
                ChannelName = ""
            });
        }
        
        // Add global hotkeys (use negative FreqId to identify them)
        if (!string.IsNullOrEmpty(TalkToAllHotkey))
        {
            Bindings.Add(new HotkeyBinding
            {
                IsEnabled = true,
                FreqId = -1, // Talk to All
                Hotkey = TalkToAllHotkey,
                Label = "Talk To All",
                ChannelName = ""
            });
        }
        
        if (!string.IsNullOrEmpty(PttMuteAllHotkey))
        {
            Bindings.Add(new HotkeyBinding
            {
                IsEnabled = true,
                FreqId = -2, // PTM All Radio
                Hotkey = PttMuteAllHotkey,
                Label = "PTM All Radio",
                ChannelName = ""
            });
        }
        
        if (!string.IsNullOrEmpty(ToggleMuteAllHotkey))
        {
            Bindings.Add(new HotkeyBinding
            {
                IsEnabled = true,
                FreqId = -3, // Toggle Mute All
                Hotkey = ToggleMuteAllHotkey,
                Label = "TTM All Radio",
                ChannelName = ""
            });
        }
    }

    private void ApplyToConfig()
    {
        _config.DiscordUserId = DiscordUserId;
        _config.GuildId = GuildId;
        _config.SampleRate = SampleRate;

        _config.VoiceHost = VoiceHost;
        _config.VoicePort = VoicePort;

        _config.AuthToken = AuthToken;
        _config.LoggedInDisplayName = LoggedInDisplayName;
        _config.AcceptedPolicyVersion = _acceptedPolicyVersion;

        _config.AutoConnect = AutoConnect;
        _config.StartMinimized = StartMinimized;
        _config.LaunchOnStartup = LaunchOnStartup;
        _config.SaveRadioActiveState = SaveRadioActiveState;
        _config.TurnOnEmergencyOnStartup = TurnOnEmergencyOnStartup;
        _config.EnableEmergencyRadio = EnableEmergencyRadio;
        _config.OverlayEnabled = OverlayEnabled;
        _config.OverlayShowRank = OverlayShowRank;
        _config.OverlayShowRadioKeybind = OverlayShowRadioKeybind;
        _config.OverlayPositionX = OverlayPositionX;
        _config.OverlayPositionY = OverlayPositionY;
        _config.OverlayOpacity = OverlayOpacity;
        _config.OverlayAutoHideEnabled = OverlayAutoHideEnabled;
        _config.OverlayAutoHideSeconds = OverlayAutoHideSeconds;
        _config.DebugLoggingEnabled = DebugLoggingEnabled;
        _config.PlaySoundOnTransmit = PlaySoundOnTransmit;
        _config.PlaySoundOnReceive = PlaySoundOnReceive;
        _config.PlaySoundOnBegin = PlaySoundOnBegin;
        _config.PlaySoundOnEnd = PlaySoundOnEnd;
        _config.InputVolume = InputVolume;
        _config.OutputVolume = OutputVolume;
        _config.DuckingEnabled = DuckingEnabled;
        _config.DuckOnSend = DuckOnSend;
        _config.DuckOnReceive = DuckOnReceive;
        _config.DuckingLevel = DuckingLevel;
        _config.DuckingMode = DuckingMode;
        _config.DuckedProcessNames = DuckedProcessNames?.ToList() ?? new List<string>();

        _config.Bindings = Bindings.ToList();

        // Save per-radio state
        _config.RadioStates = RadioPanels.Select(p => new Models.RadioState
        {
            Index = p.Index,
            IsEnabled = p.IsEnabled,
            IsMuted = p.IsMuted,
            Volume = p.Volume,
            Balance = p.Balance,
            IncludedInBroadcast = p.IncludedInBroadcast
        }).ToList();

        _config.EmergencyRadioState = new Models.RadioState
        {
            Index = -1,
            IsEnabled = EmergencyRadio.IsEnabled,
            IsMuted = EmergencyRadio.IsMuted,
            Volume = EmergencyRadio.Volume,
            Balance = EmergencyRadio.Balance
        };
    }

    private readonly object _debugLogLock = new();
    private readonly string _debugLogFilePath;

    private void EnsureDebugLogDirectory()
    {
        try
        {
            var directory = Path.GetDirectoryName(_debugLogFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch
        {
            // Ignore logging setup failures
        }
    }

    private void LogDebug(string message)
    {
        if (!_debugLoggingEnabled)
        {
            return;
        }

        try
        {
            EnsureDebugLogDirectory();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            lock (_debugLogLock)
            {
                File.AppendAllText(_debugLogFilePath, line);
            }
        }
        catch
        {
            // Ignore logging failures
        }
    }

    private async Task SyncFreqNamesAsync()
    {
        var baseUrl = BuildBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(AdminToken))
        {
            return;
        }

        try
        {
            using var client = new BackendClient(baseUrl, AdminToken);
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
            // Handle global hotkeys (negative FreqId)
            if (binding.FreqId == -1) // Talk to All
            {
                await StartTalkToAllAsync();
                return;
            }
            else if (binding.FreqId == -2) // PTM All Radio
            {
                SetAllRadiosMuted(true);
                return;
            }
            else if (binding.FreqId == -3) // Toggle Mute All
            {
                ToggleMuteAllRadios();
                return;
            }
            
            // Check Emergency radio
            if (EmergencyRadio != null && EmergencyRadio.FreqId == binding.FreqId && EmergencyRadio.Hotkey == binding.Hotkey)
            {
                if (!EnableEmergencyRadio)
                {
                    StatusText = "Emergency radio is disabled in App Settings";
                    return;
                }
                await HandlePttPressedAsync(EmergencyRadio);
                return;
            }
            
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
            // Handle global hotkey releases
            if (binding.FreqId == -1) // Talk to All
            {
                await StopTalkToAllAsync();
                return;
            }
            else if (binding.FreqId == -2) // PTM All Radio - unmute on release
            {
                SetAllRadiosMuted(false);
                return;
            }
            else if (binding.FreqId == -3) // Toggle Mute All - no action on release
            {
                return;
            }
            
            await HandlePttReleasedAsync();
        });
    }

    private async Task HandlePttPressedAsync(RadioPanelViewModel radio)
    {
        if (IsStreaming)
        {
            return;
        }

        // Don't allow transmitting on disabled radios
        if (!radio.IsEnabled)
        {
            StatusText = $"Radio {radio.Label} is disabled";
            return;
        }

        // Don't allow transmitting on muted frequencies (except broadcasts)
        bool effectiveMuted = radio.IsMuted;
        if (radio.IsEmergencyRadio)
            effectiveMuted = effectiveMuted || !EnableEmergencyRadio;
        if (effectiveMuted)
        {
            StatusText = $"Cannot transmit — Radio {radio.Label} is muted";
            return;
        }

        try
        {
            _activeRadio = radio;
            radio.SetTransmitting(true);
            UpdateOverlayTransmitState(radio.FreqId, true);
            StatusText = $"PTT start ({radio.Label} - Freq {radio.FreqId})";

            _backend?.Dispose();
            _backend = new BackendClient(BuildBaseUrl(), AdminToken);

            _audio?.Dispose();
            _audio = new AudioCaptureService(SelectedAudioInputDevice);
            _audio.AudioFrame += AudioOnAudioFrame;

            _streamCts?.Dispose();
            _streamCts = new CancellationTokenSource();

            // Use VoiceService for audio transmission
            if (_voice == null || !_voice.IsConnected)
            {
                await ConnectVoiceAsync();
            }

            if (_voice != null && _voice.IsConnected)
            {
                // Join the frequency channel — abort if join fails
                bool joined = await _voice.JoinFrequencyAsync(radio.FreqId);
                if (!joined)
                {
                    StatusText = $"Cannot transmit: channel {radio.FreqId} unavailable";
                    radio.SetTransmitting(false);
                    return;
                }
                _voice.StartTransmit();
                _audio.Start();
            }
            else
            {
                StatusText = "Voice not connected";
                radio.SetTransmitting(false);
                return;
            }

            // Mark streaming BEFORE notifying backend so the self-echo guard
            // in OnRxStateChanged fires even if the server broadcasts before HTTP responds.
            IsStreaming = true;

            // Apply external app ducking if configured (duck-on-send)
            if (DuckingEnabled && _duckOnSend && _duckingLevel < 100 && _duckingMode != 0)
            {
                _audioDuckingService?.ApplyDucking(_duckingLevel / 100f);
                LogDebug($"[Ducking] External duck-on-send applied: level={_duckingLevel}% mode={_duckingMode}");
            }
            LogDebug($"[Ducking] TX start: enabled={DuckingEnabled} onSend={_duckOnSend} level={_duckingLevel} mode={_duckingMode}");

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
            if (radio.IsEmergencyRadio)
            {
                if (PlaySoundOnTransmit && PlaySoundOnBegin)
                    _beepService?.PlayEmergencyTxBeep(radio.Volume / 100f, radio.Balance / 100f);
            }
            else
            {
                if (PlaySoundOnTransmit && PlaySoundOnBegin)
                    _beepService?.PlayTxStartBeep(radio.Volume / 100f, radio.Balance / 100f);
            }
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
            UpdateOverlayTransmitState(radio.FreqId, false);
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

        _voice?.StopTransmit();
        // Keep voice connection alive for next PTT

        // Restore external app ducking (unless duck-on-receive is keeping it active)
        if (_activeRxCount == 0)
            _audioDuckingService?.RestoreDucking();

        _streamCts?.Dispose();
        _streamCts = null;

        // Log the transmission to recent activity
        if (radio != null)
        {
            var userName = string.IsNullOrWhiteSpace(LoggedInDisplayName) ? "You" : LoggedInDisplayName;
            var txTimestamp = $"{DateTime.Now:HH:mm} - {userName}";
            radio.AddTransmission(txTimestamp);
            UpdateOverlayForFreq(radio.FreqId, false, txTimestamp);
        }

        IsStreaming = false;
        if (radio?.IsEmergencyRadio == true)
        {
            if (PlaySoundOnTransmit && PlaySoundOnEnd)
                _beepService?.PlayEmergencyTxEndBeep(radio.Volume / 100f, radio.Balance / 100f);
        }
        else
        {
            if (PlaySoundOnTransmit && PlaySoundOnEnd)
                _beepService?.PlayTxEndBeep(radio?.Volume / 100f ?? 1f, radio?.Balance / 100f ?? 0.5f);
        }
        StatusText = "PTT stop";
    }

    private void AudioOnAudioFrame(byte[] data)
    {
        try
        {
            if (_voice != null)
            {
                var format = _audio?.WaveFormat;
                _voice.SendAudio(data, format?.SampleRate ?? SampleRate, format?.Channels ?? 1);
            }
        }
        catch
        {
            // Ignore audio frame errors to prevent crashes
        }
    }

    public async Task ConnectVoiceAsync()
    {
        LogDebug($"[Voice] ConnectVoiceAsync start: host={VoiceHost} port={VoicePort}");

        // Tear down previous instances
        if (_reconnect != null)
        {
            _reconnect.StateChanged -= OnReconnectStateChanged;
            _reconnect.Log -= OnReconnectLog;
            _reconnect.Reconnected -= OnReconnectedAsync;
            _reconnect.Dispose();
            _reconnect = null;
        }

        if (_voice != null)
        {
            await _voice.DisconnectAsync();
            _voice.Dispose();
        }

        _voice = new VoiceService();
        _voice.StatusChanged += status =>
        {
            LogDebug($"[Voice] Status: {status}");
            Application.Current.Dispatcher.Invoke(() => StatusText = status);
        };
        _voice.ErrorOccurred += ex =>
        {
            LogDebug($"[Voice] Error: {ex}");
            Application.Current.Dispatcher.Invoke(() => StatusText = $"Voice error: {ex.Message}");
        };
        _voice.RxStateChanged += OnRxStateChanged;
        _voice.FreqJoined += OnFreqJoined;
        _voice.MuteConfirmed += OnMuteConfirmed;
        
        // Set output device
        _voice.SetOutputDevice(SelectedAudioOutputDevice);

        // Set master volumes
        _voice.SetMasterInputVolume(InputVolume / 100f);
        _voice.SetMasterOutputVolume(OutputVolume / 100f);

        // Set ducking level
        _voice.SetDuckingLevel(DuckingLevel);
        _voice.SetDuckingEnabled(DuckingEnabled);
        _voice.SetDuckOnSend(DuckOnSend);
        _voice.SetDuckOnReceive(DuckOnReceive);

        // Create and wire ReconnectManager
        _reconnect = new ReconnectManager(_voice);
        _reconnect.SetConnectionParams(VoiceHost, VoicePort, DiscordUserId, GuildId, AuthToken);
        _reconnect.StateChanged += OnReconnectStateChanged;
        _reconnect.Log += OnReconnectLog;
        _reconnect.Reconnected += OnReconnectedAsync;

        // Connect via ReconnectManager (which will auto-reconnect on drop)
        bool connected = await _reconnect.ConnectAsync();
        IsVoiceConnected = _voice.IsConnected;

        // Auto-join all enabled radio frequencies so we receive RX notifications and audio
        if (IsVoiceConnected)
        {
            foreach (var panel in RadioPanels.Where(r => r.IsEnabled))
            {
                await _voice.JoinFrequencyAsync(panel.FreqId);
                LogDebug($"[Voice] Auto-joined freq {panel.FreqId} ({panel.Label})");
            }
            if (EnableEmergencyRadio)
            {
                await _voice.JoinFrequencyAsync(EmergencyRadio.FreqId);
                LogDebug($"[Voice] Auto-joined emergency freq {EmergencyRadio.FreqId}");
            }
        }

        // Push per-radio audio settings to VoiceService (volume, pan, mute)
        if (IsVoiceConnected)
        {
            PushAllRadioSettingsToVoice();
            await PushAllServerMutesAsync();
            await FetchAndApplyFreqNamesAsync();
        }

        LogDebug($"[Voice] ConnectVoiceAsync done: IsConnected={IsVoiceConnected}");
    }

    /// <summary>
    /// Called by ReconnectManager after a successful automatic reconnect.
    /// Re-joins frequencies and pushes audio settings.
    /// </summary>
    private async Task OnReconnectedAsync()
    {
        LogDebug("[Voice] Reconnected — re-joining frequencies and pushing settings");

        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            IsVoiceConnected = _voice?.IsConnected ?? false;

            if (_voice == null || !_voice.IsConnected) return;

            // Re-set output device & volumes (playback device may have been cleaned up)
            _voice.SetOutputDevice(SelectedAudioOutputDevice);
            _voice.SetMasterInputVolume(InputVolume / 100f);
            _voice.SetMasterOutputVolume(OutputVolume / 100f);

            // Re-set ducking settings
            _voice.SetDuckingLevel(DuckingLevel);
            _voice.SetDuckingEnabled(DuckingEnabled);
            _voice.SetDuckOnSend(DuckOnSend);
            _voice.SetDuckOnReceive(DuckOnReceive);

            // Re-join all enabled frequencies
            foreach (var panel in RadioPanels.Where(r => r.IsEnabled))
            {
                await _voice.JoinFrequencyAsync(panel.FreqId);
                LogDebug($"[Voice] Reconnect re-joined freq {panel.FreqId} ({panel.Label})");
            }
            if (EnableEmergencyRadio)
            {
                await _voice.JoinFrequencyAsync(EmergencyRadio.FreqId);
                LogDebug($"[Voice] Reconnect re-joined emergency freq {EmergencyRadio.FreqId}");
            }

            PushAllRadioSettingsToVoice();
            await PushAllServerMutesAsync();

            LogDebug("[Voice] Reconnect state restoration complete: frequencies re-joined, mutes pushed, audio devices set");
            StatusText = "Reconnected successfully";
        });
    }

    private void OnReconnectStateChanged(VoiceConnectionState newState)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            VoiceConnectionState = newState;
            IsVoiceConnected = newState == VoiceConnectionState.Connected;

            switch (newState)
            {
                case VoiceConnectionState.Reconnecting:
                    StatusText = "Connection lost — reconnecting…";
                    break;
                case VoiceConnectionState.Failed:
                    StatusText = "Reconnect failed — click Connect to retry";
                    break;
                case VoiceConnectionState.Connected:
                    // StatusText set by OnReconnectedAsync or VoiceService
                    break;
            }
        });
    }

    private void OnReconnectLog(string message)
    {
        LogDebug($"[Reconnect] {message}");
    }

    /// <summary>
    /// Fetch frequency → channel name mappings from the server and apply to radio panels.
    /// </summary>
    private async Task FetchAndApplyFreqNamesAsync()
    {
        var baseUrl = BuildBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl)) return;
        try
        {
            using var client = new BackendClient(baseUrl, AdminToken ?? "");
            var freqNames = await client.GetFreqNamesAsync();
            if (freqNames.Count == 0) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var panel in RadioPanels)
                {
                    panel.ChannelName = freqNames.TryGetValue(panel.FreqId, out var name) ? name : "";
                }
                EmergencyRadio.ChannelName = freqNames.TryGetValue(EmergencyRadio.FreqId, out var eName) ? eName : "";
            });
            LogDebug($"[Voice] Applied {freqNames.Count} freq name mappings");
        }
        catch (Exception ex)
        {
            LogDebug($"[Voice] Failed to fetch freq names: {ex.Message}");
        }
    }
    
    private void OnRxStateChanged(string discordUserId, string username, int freqId, string action)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // Ignore RX events from ourselves (we are currently transmitting)
            if (IsStreaming && _activeRadio != null && _activeRadio.FreqId == freqId)
                return;

            // Find radio panel matching the frequency
            var matchingRadio = RadioPanels.FirstOrDefault(r => r.IsEnabled && !r.IsMuted && r.FreqId == freqId);
            if (matchingRadio == null && EnableEmergencyRadio && EmergencyRadio.IsEnabled && !EmergencyRadio.IsMuted && EmergencyRadio.FreqId == freqId)
            {
                matchingRadio = EmergencyRadio;
            }

            if (matchingRadio == null) return;

            if (action == "start")
            {
                var timestamp = $"{DateTime.Now:HH:mm} - {username}";
                matchingRadio.AddTransmission(timestamp);
                matchingRadio.SetReceiving(true);

                // Update overlay
                UpdateOverlayForFreq(freqId, true, timestamp);

                // Notify VoiceService so internal duck-on-receive works
                _voice?.NotifyRxStart();

                // Apply duck-on-receive external ducking (first RX starts ducking)
                if (DuckingEnabled && _duckOnReceive && _duckingMode != 0 && _duckingLevel < 100)
                {
                    _activeRxCount++;
                    if (_activeRxCount == 1)
                        _audioDuckingService?.ApplyDucking(_duckingLevel / 100f);
                }
                LogDebug($"[Ducking] RX start on freq {freqId}: rxCount={_activeRxCount} enabled={DuckingEnabled} onRecv={_duckOnReceive} mode={_duckingMode}");

                // Play appropriate RX beep
                if (PlaySoundOnReceive && PlaySoundOnBegin)
                {
                    if (matchingRadio.IsEmergencyRadio)
                        _beepService?.PlayEmergencyRxBeep(matchingRadio.Volume / 100f, matchingRadio.Balance / 100f);
                    else
                        _beepService?.PlayRxStartBeep(matchingRadio.Volume / 100f, matchingRadio.Balance / 100f);
                }
            }
            else if (action == "stop")
            {
                matchingRadio.SetReceiving(false);

                // Update overlay
                UpdateOverlayForFreq(freqId, false);

                // Notify VoiceService so internal duck-on-receive stops
                _voice?.NotifyRxStop();

                // Restore duck-on-receive when last RX ends (but not if duck-on-send is active)
                if (_activeRxCount > 0)
                {
                    _activeRxCount--;
                    if (_activeRxCount == 0 && !IsStreaming)
                        _audioDuckingService?.RestoreDucking();
                }
                LogDebug($"[Ducking] RX stop on freq {freqId}: rxCount={_activeRxCount}");
            }
        });
    }

    /// <summary>
    /// Force-restore external ducking caused by RX activity and reset the counter.
    /// Used when disabling duck-on-receive at runtime.
    /// </summary>
    private void RestoreRxDucking()
    {
        if (_activeRxCount > 0)
        {
            _activeRxCount = 0;
            if (!IsStreaming) // don't disturb duck-on-send
                _audioDuckingService?.RestoreDucking();
        }
    }

    private void OnFreqJoined(int freqId, int listenerCount)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var panel = RadioPanels.FirstOrDefault(r => r.FreqId == freqId);
            if (panel != null)
                panel.ListenerCount = listenerCount;
            if (EmergencyRadio.FreqId == freqId)
                EmergencyRadio.ListenerCount = listenerCount;
        });
    }

    private void OnRadioPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not RadioPanelViewModel panel) return;
        if (e.PropertyName is "Volume" or "Balance" or "IsMuted" or "IsEnabled")
        {
            PushRadioSettingsToVoice(panel);

            // Send server-side mute/unmute when mute state changes
            if (e.PropertyName is "IsMuted" or "IsEnabled")
            {
                _ = PushServerMuteAsync(panel);
            }

            // Join/leave freq on server when radio is enabled/disabled (updates listener count for all users)
            if (e.PropertyName is "IsEnabled")
            {
                _ = HandleRadioEnabledChangedAsync(panel);
            }
        }

        // Auto-connect on frequency change when radio is active
        if (e.PropertyName is "FreqId")
        {
            _ = HandleFreqIdChangedAsync(panel);
        }
    }

    private async Task HandleFreqIdChangedAsync(RadioPanelViewModel panel)
    {
        if (_voice == null || !_voice.IsConnected) return;
        if (!panel.IsEnabled) return;
        if (panel.IsEmergencyRadio && !EnableEmergencyRadio) return;

        int oldFreq = panel.PreviousFreqId;
        int newFreq = panel.FreqId;

        if (oldFreq == newFreq) return;

        // Leave old frequency
        await _voice.LeaveFrequencyAsync(oldFreq);
        LogDebug($"[Voice] Freq changed: left {oldFreq}");

        // Join new frequency
        await _voice.JoinFrequencyAsync(newFreq);
        LogDebug($"[Voice] Freq changed: joined {newFreq}");

        // Update audio settings for new frequency
        PushRadioSettingsToVoice(panel);
        await PushServerMuteAsync(panel);
    }

    private async Task HandleRadioEnabledChangedAsync(RadioPanelViewModel panel)
    {
        if (_voice == null || !_voice.IsConnected) return;

        // Emergency radio gating: respect EnableEmergencyRadio setting
        if (panel.IsEmergencyRadio && !EnableEmergencyRadio) return;

        if (panel.IsEnabled)
        {
            await _voice.JoinFrequencyAsync(panel.FreqId);
            LogDebug($"[Voice] Radio enabled → joined freq {panel.FreqId}");
        }
        else
        {
            await _voice.LeaveFrequencyAsync(panel.FreqId);
            LogDebug($"[Voice] Radio disabled → left freq {panel.FreqId}");
        }
    }

    private async Task HandleEmergencyRadioToggleAsync()
    {
        if (_voice == null || !_voice.IsConnected) return;

        if (EnableEmergencyRadio)
        {
            await _voice.JoinFrequencyAsync(EmergencyRadio.FreqId);
            LogDebug($"[Voice] Emergency radio enabled → joined freq {EmergencyRadio.FreqId}");
        }
        else
        {
            await _voice.LeaveFrequencyAsync(EmergencyRadio.FreqId);
            LogDebug($"[Voice] Emergency radio disabled → left freq {EmergencyRadio.FreqId}");
        }
    }

    private async Task PushServerMuteAsync(RadioPanelViewModel panel)
    {
        if (_voice == null || !_voice.IsConnected) return;

        bool effectiveMuted = panel.IsMuted || !panel.IsEnabled;
        if (panel.IsEmergencyRadio)
            effectiveMuted = effectiveMuted || !EnableEmergencyRadio;

        if (effectiveMuted)
            await _voice.MuteFrequencyAsync(panel.FreqId);
        else
            await _voice.UnmuteFrequencyAsync(panel.FreqId);
    }

    private async Task PushAllServerMutesAsync()
    {
        if (_voice == null || !_voice.IsConnected) return;

        foreach (var panel in RadioPanels)
        {
            await PushServerMuteAsync(panel);
        }
        await PushServerMuteAsync(EmergencyRadio);
    }

    private void OnMuteConfirmed(int freqId, bool isMuted)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogDebug($"[Voice] Server mute confirmed: freq={freqId} muted={isMuted}");
        });
    }

    private void PushRadioSettingsToVoice(RadioPanelViewModel panel)
    {
        if (_voice == null || !_voice.IsConnected) return;

        bool effectiveMuted = panel.IsMuted || !panel.IsEnabled;
        if (panel.IsEmergencyRadio)
            effectiveMuted = effectiveMuted || !EnableEmergencyRadio;

        _voice.SetFreqSettings(panel.FreqId, panel.Volume / 100f, panel.Balance / 100f, effectiveMuted);
    }

    private void PushAllRadioSettingsToVoice()
    {
        foreach (var panel in RadioPanels)
        {
            PushRadioSettingsToVoice(panel);
        }
        PushRadioSettingsToVoice(EmergencyRadio);
    }

    public async Task DisconnectVoiceAsync()
    {
        if (_reconnect != null)
        {
            await _reconnect.DisconnectAsync();
            _reconnect.StateChanged -= OnReconnectStateChanged;
            _reconnect.Log -= OnReconnectLog;
            _reconnect.Reconnected -= OnReconnectedAsync;
            _reconnect.Dispose();
            _reconnect = null;
        }
        else if (_voice != null)
        {
            await _voice.DisconnectAsync();
            _voice.Dispose();
            _voice = null;
        }
        IsVoiceConnected = false;
        VoiceConnectionState = VoiceConnectionState.Disconnected;
    }

    // ---- Overlay window management ----

    private void ShowOverlay()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_overlayWindow != null) return;
            _overlayWindow = new OverlayWindow();
            _overlayWindow.SetPosition(_overlayPositionX, _overlayPositionY);
            _overlayWindow.SetBackgroundOpacity(_overlayOpacity / 100.0);
            RefreshOverlay();
            _overlayWindow.Show();
            StartOverlayCleanupTimer();
        });
    }

    private void HideOverlay()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StopOverlayCleanupTimer();
            _overlayWindow?.Close();
            _overlayWindow = null;
        });
    }

    private void StartOverlayCleanupTimer()
    {
        if (_overlayCleanupTimer != null) return;
        _overlayCleanupTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _overlayCleanupTimer.Tick += OverlayCleanupTick;
        _overlayCleanupTimer.Start();
    }

    private void StopOverlayCleanupTimer()
    {
        if (_overlayCleanupTimer == null) return;
        _overlayCleanupTimer.Stop();
        _overlayCleanupTimer.Tick -= OverlayCleanupTick;
        _overlayCleanupTimer = null;
    }

    private void OverlayCleanupTick(object? sender, EventArgs e)
    {
        if (_overlayWindow == null || !_overlayAutoHideEnabled) return;

        var cutoff = DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(_overlayAutoHideSeconds).Ticks;
        var stale = _overlayWindow.Entries
            .Where(entry => !entry.IsReceiving && !entry.IsTransmitting && entry.LastActivityTicks < cutoff)
            .ToList();

        foreach (var entry in stale)
            _overlayWindow.Entries.Remove(entry);
    }

    /// <summary>
    /// Rebuild overlay entries from current radio state.
    /// Only shows radios that are currently active (TX/RX or have recent transmissions),
    /// sorted by most-recently-active first.
    /// </summary>
    private void RefreshOverlay()
    {
        if (_overlayWindow == null) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            _overlayWindow.Entries.Clear();

            var radios = RadioPanels.Where(r => r.IsEnabled).ToList();
            if (EnableEmergencyRadio && EmergencyRadio.IsEnabled)
                radios.Add(EmergencyRadio);

            foreach (var radio in radios)
            {
                bool isRx = radio.Status == RadioStatus.Receiving;
                bool isTx = radio.Status == RadioStatus.Transmitting || radio.Status == RadioStatus.Broadcasting;
                string lastTx = radio.RecentTransmissions.Count > 0 ? radio.RecentTransmissions[0].Text : "";

                // Only include active radios
                if (!isRx && !isTx && string.IsNullOrWhiteSpace(lastTx)) continue;

                var entry = new OverlayRadioEntry
                {
                    Label = radio.Label,
                    FreqDisplay = radio.FreqId.ToString(),
                    Hotkey = radio.Hotkey,
                    ShowHotkey = OverlayShowRadioKeybind,
                    IsReceiving = isRx,
                    IsTransmitting = isTx,
                    LastTransmission = lastTx,
                    LastActivityTicks = (isRx || isTx) ? DateTime.UtcNow.Ticks : DateTime.UtcNow.Ticks - 1
                };
                _overlayWindow.Entries.Add(entry);
            }

            SortOverlayEntries();
        });
    }

    /// <summary>
    /// Sort overlay entries so that the most recently active radio is on top.
    /// </summary>
    private void SortOverlayEntries()
    {
        if (_overlayWindow == null) return;

        var sorted = _overlayWindow.Entries.OrderByDescending(e => e.LastActivityTicks).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int oldIndex = _overlayWindow.Entries.IndexOf(sorted[i]);
            if (oldIndex != i)
                _overlayWindow.Entries.Move(oldIndex, i);
        }
    }

    /// <summary>
    /// Incrementally update (or insert) an overlay entry for the given frequency on RX events.
    /// Entries are added when they become active and sorted by most-recently-active.
    /// </summary>
    private void UpdateOverlayForFreq(int freqId, bool isReceiving, string? lastTx = null)
    {
        if (_overlayWindow == null) return;

        var freqStr = freqId.ToString();
        var entry = _overlayWindow.Entries.FirstOrDefault(e => e.FreqDisplay == freqStr);

        if (entry == null)
        {
            // Radio just became active — find the source radio and create an entry
            var radio = RadioPanels.FirstOrDefault(r => r.IsEnabled && r.FreqId == freqId);
            if (radio == null && EnableEmergencyRadio && EmergencyRadio.FreqId == freqId)
                radio = EmergencyRadio;
            if (radio == null) return;

            entry = new OverlayRadioEntry
            {
                Label = radio.Label,
                FreqDisplay = freqStr,
                Hotkey = radio.Hotkey,
                ShowHotkey = OverlayShowRadioKeybind,
                IsReceiving = isReceiving,
                LastTransmission = lastTx ?? "",
                LastActivityTicks = DateTime.UtcNow.Ticks
            };
            _overlayWindow.Entries.Insert(0, entry);
        }
        else
        {
            entry.IsReceiving = isReceiving;
            if (lastTx != null)
                entry.LastTransmission = lastTx;
            entry.LastActivityTicks = DateTime.UtcNow.Ticks;
        }

        SortOverlayEntries();
    }

    /// <summary>
    /// Update transmit status in the overlay for the active radio.
    /// </summary>
    private void UpdateOverlayTransmitState(int freqId, bool isTransmitting)
    {
        if (_overlayWindow == null) return;

        var freqStr = freqId.ToString();
        var entry = _overlayWindow.Entries.FirstOrDefault(e => e.FreqDisplay == freqStr);

        if (entry == null && isTransmitting)
        {
            // Radio just became active via TX — create an entry
            var radio = RadioPanels.FirstOrDefault(r => r.IsEnabled && r.FreqId == freqId);
            if (radio == null && EnableEmergencyRadio && EmergencyRadio.FreqId == freqId)
                radio = EmergencyRadio;
            if (radio == null) return;

            entry = new OverlayRadioEntry
            {
                Label = radio.Label,
                FreqDisplay = freqStr,
                Hotkey = radio.Hotkey,
                ShowHotkey = OverlayShowRadioKeybind,
                IsTransmitting = true,
                LastActivityTicks = DateTime.UtcNow.Ticks
            };
            _overlayWindow.Entries.Insert(0, entry);
        }
        else if (entry != null)
        {
            entry.IsTransmitting = isTransmitting;
            if (isTransmitting)
                entry.LastActivityTicks = DateTime.UtcNow.Ticks;
        }

        SortOverlayEntries();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Dispose()
    {
        StopOverlayCleanupTimer();
        HideOverlay();
        _hook?.Dispose();
        _backend?.Dispose();
        _audio?.Dispose();
        _audioDuckingService?.Dispose();
        _reconnect?.Dispose();
        _voice?.Dispose();
        _streamCts?.Dispose();
    }
}
