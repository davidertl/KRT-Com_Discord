using System.Collections.Generic;

namespace CompanionApp.Models;

public class CompanionConfig
{
    public string DiscordUserId { get; set; } = "";
    public int RadioSlot { get; set; } = 1;
    public int SampleRate { get; set; } = 48000;

    public string GuildId { get; set; } = "";

    // Voice relay server settings (WS port derived from ServerBaseUrl)
    public string VoiceHost { get; set; } = "127.0.0.1";
    public int VoicePort { get; set; } = 3000;

    // Auth token from last successful login
    public string AuthToken { get; set; } = "";

    // Display name from last successful login
    public string LoggedInDisplayName { get; set; } = "";

    // Accepted privacy policy version
    public string AcceptedPolicyVersion { get; set; } = "";

    // App settings
    public bool AutoConnect { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public bool LaunchOnStartup { get; set; } = false;
    public bool SaveRadioActiveState { get; set; } = true;
    public bool TurnOnEmergencyOnStartup { get; set; } = true;
    public bool DebugLoggingEnabled { get; set; } = false;
    public bool EnableEmergencyRadio { get; set; } = true;

    // Granular beep/sound settings
    public bool PlaySoundOnTransmit { get; set; } = true;
    public bool PlaySoundOnReceive { get; set; } = true;
    public bool PlaySoundOnBegin { get; set; } = true;
    public bool PlaySoundOnEnd { get; set; } = true;

    // Overlay settings
    public bool OverlayEnabled { get; set; } = false;
    public bool OverlayShowRank { get; set; } = false;
    public bool OverlayShowRadioKeybind { get; set; } = false;
    public int OverlayPositionX { get; set; } = 20;
    public int OverlayPositionY { get; set; } = 20;
    public int OverlayOpacity { get; set; } = 80;
    public bool OverlayAutoHideEnabled { get; set; } = true;
    public int OverlayAutoHideSeconds { get; set; } = 60;

    // Master volume (0-125, default 100)
    public int InputVolume { get; set; } = 100;
    public int OutputVolume { get; set; } = 100;

    // Voice ducking settings
    /// <summary>
    /// Master toggle: enables/disables the entire ducking feature.
    /// </summary>
    public bool DuckingEnabled { get; set; } = false;
    /// <summary>
    /// Default ducking level 0-100: 100 = no ducking (disabled), 0 = full mute while TX.
    /// Used as the default for radios that haven't set a custom level.
    /// </summary>
    public int DuckingLevel { get; set; } = 50;
    /// <summary>
    /// Apply ducking while the user is transmitting (PTT held).
    /// </summary>
    public bool DuckOnSend { get; set; } = true;
    /// <summary>
    /// Apply ducking while voice is being received on any active radio.
    /// </summary>
    public bool DuckOnReceive { get; set; } = true;
    /// <summary>
    /// Ducking mode: 0 = Radio audio only, 1 = Selected apps, 2 = All audio except KRT-Com.
    /// </summary>
    public int DuckingMode { get; set; } = 2;
    /// <summary>
    /// Process names selected for ducking when DuckingMode == 1.
    /// </summary>
    public List<string> DuckedProcessNames { get; set; } = new();

    // Per-radio persisted state
    public List<RadioState> RadioStates { get; set; } = new();

    // Emergency radio persisted state
    public RadioState? EmergencyRadioState { get; set; }

    public List<HotkeyBinding> Bindings { get; set; } = new()
    {
        new HotkeyBinding { FreqId = 1050, Hotkey = "LeftCtrl", Label = "Main" }
    };
}

/// <summary>
/// Persisted state for a single radio panel.
/// </summary>
public class RadioState
{
    public int Index { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsMuted { get; set; }
    public int Volume { get; set; } = 100;
    public int Balance { get; set; } = 50;
    public bool IncludedInBroadcast { get; set; }
}
