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

    // Master volume (0-125, default 100)
    public int InputVolume { get; set; } = 100;
    public int OutputVolume { get; set; } = 100;

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
