using System.Collections.Generic;

namespace CompanionApp.Models;

public class CompanionConfig
{
    public string ServerBaseUrl { get; set; } = "http://127.0.0.1:3000";
    public string AdminToken { get; set; } = "";
    public string DiscordUserId { get; set; } = "";
    public int RadioSlot { get; set; } = 1;
    public int SampleRate { get; set; } = 48000;

    public string GuildId { get; set; } = "";

    // Voice relay server settings (WS port derived from ServerBaseUrl)
    public string VoiceHost { get; set; } = "127.0.0.1";
    public int VoicePort { get; set; } = 3000;

    // App settings
    public bool AutoConnect { get; set; } = true;
    public bool DeactivateRadiosOnAutoConnect { get; set; } = false;
    public bool DebugLoggingEnabled { get; set; } = false;

    public List<HotkeyBinding> Bindings { get; set; } = new()
    {
        new HotkeyBinding { FreqId = 1050, Hotkey = "LeftCtrl", Label = "Main" }
    };
}
