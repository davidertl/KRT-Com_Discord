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
    private VoiceService? _voice;
    private BeepService? _beepService;
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
        set { _serverVersion = value; OnPropertyChanged(); }
    }

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
    public bool CanLoginWithDiscord => IsServerVerified && PolicyAccepted && OauthEnabled && !_isOAuthInProgress;

    /// <summary>Hint text shown when button is disabled.</summary>
    public string DiscordLoginHint
    {
        get
        {
            if (!IsServerVerified) return "";
            if (!OauthEnabled) return "Server does not have Discord OAuth configured.";
            if (!PolicyAccepted) return "Accept the privacy policy to enable login.";
            if (_isOAuthInProgress) return "Login in progress…";
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
        set { _loggedInDisplayName = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLoggedIn)); }
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

    public string VoiceConnectionIndicator => IsVoiceConnected ? "Connected" : "Disconnected";
    public string VoiceConnectButtonText => IsVoiceConnected ? "Disconnect" : "Connect";

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
        set 
        { 
            _playPttBeep = value; 
            OnPropertyChanged(); 
            MarkGlobalChanged();
            if (_beepService != null) _beepService.Enabled = value;
        }
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
            VerifyStatusText = $"Server verified: {status.Version}";
            LogDebug($"[Verify] Server verified: version={status.Version} dsgvo={status.DsgvoEnabled} debug={status.DebugMode} oauth={status.OauthEnabled}");
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
            Process.Start(new ProcessStartInfo(redirectUrl) { UseShellExecute = true });

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
        var scheme = VoiceHost.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        var cleanHost = VoiceHost
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        return $"{scheme}://{cleanHost}:{VoicePort}";
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

        // Only works in advanced mode
        if (!AdvancedMode)
        {
            StatusText = "Talk to All requires Advanced Mode";
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
            _beepService?.PlayTalkToAllBeep();
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

        var userName = string.IsNullOrWhiteSpace(DiscordUserId) ? "You" : $"You ({DiscordUserId})";
        var timestamp = $"{DateTime.Now:HH:mm} - {userName} (Broadcast)";

        foreach (var radio in _activeBroadcastRadios)
        {
            radio.SetBroadcasting(false);
            radio.AddTransmission(timestamp);
        }
        _activeBroadcastRadios.Clear();

        _audio?.Dispose();
        _audio = null;

        _voice?.StopTransmit();

        IsBroadcasting = false;
        IsStreaming = false;
        _beepService?.PlayTxEndBeep();
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
        _acceptedPolicyVersion = config.AcceptedPolicyVersion;

        AutoConnect = config.AutoConnect;
        SaveRadioActiveState = config.SaveRadioActiveState;
        TurnOnEmergencyOnStartup = config.TurnOnEmergencyOnStartup;
        EnableEmergencyRadio = config.EnableEmergencyRadio;
        DebugLoggingEnabled = config.DebugLoggingEnabled;
        InputVolume = config.InputVolume;
        OutputVolume = config.OutputVolume;

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
        _config.AcceptedPolicyVersion = _acceptedPolicyVersion;

        _config.AutoConnect = AutoConnect;
        _config.SaveRadioActiveState = SaveRadioActiveState;
        _config.TurnOnEmergencyOnStartup = TurnOnEmergencyOnStartup;
        _config.EnableEmergencyRadio = EnableEmergencyRadio;
        _config.DebugLoggingEnabled = DebugLoggingEnabled;
        _config.InputVolume = InputVolume;
        _config.OutputVolume = OutputVolume;

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

        try
        {
            _activeRadio = radio;
            radio.SetTransmitting(true);
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
            if (radio.IsEmergencyRadio)
                _beepService?.PlayEmergencyTxBeep();
            else
                _beepService?.PlayTxStartBeep();
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

        _voice?.StopTransmit();
        // Keep voice connection alive for next PTT

        _streamCts?.Dispose();
        _streamCts = null;

        // Log the transmission to recent activity
        if (radio != null)
        {
            var userName = string.IsNullOrWhiteSpace(DiscordUserId) ? "You" : $"You ({DiscordUserId})";
            radio.AddTransmission($"{DateTime.Now:HH:mm} - {userName}");
        }

        IsStreaming = false;
        if (radio?.IsEmergencyRadio == true)
            _beepService?.PlayEmergencyTxEndBeep();
        else
            _beepService?.PlayTxEndBeep();
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
        LogDebug($"[Voice] ConnectVoiceAsync start: host={VoiceHost} port={VoicePort} userId={DiscordUserId} guildId={GuildId}");

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

        await _voice.ConnectAsync(VoiceHost, VoicePort, DiscordUserId, GuildId, AuthToken);
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

                // Play appropriate RX beep
                if (matchingRadio.IsEmergencyRadio)
                    _beepService?.PlayEmergencyRxBeep();
                else
                    _beepService?.PlayRxStartBeep();
            }
            else if (action == "stop")
            {
                matchingRadio.SetReceiving(false);
            }
        });
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
        if (_voice != null)
        {
            await _voice.DisconnectAsync();
            _voice.Dispose();
            _voice = null;
        }
        IsVoiceConnected = false;
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
        _voice?.Dispose();
        _streamCts?.Dispose();
    }
}
