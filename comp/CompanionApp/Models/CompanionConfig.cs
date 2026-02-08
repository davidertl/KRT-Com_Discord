using System.Collections.Generic;

namespace CompanionApp.Models;

public class CompanionConfig
{
    public string ServerBaseUrl { get; set; } = "http://127.0.0.1:3000";
    public string AdminToken { get; set; } = "";
    public string DiscordUserId { get; set; } = "";
    public int RadioSlot { get; set; } = 1;
    public int SampleRate { get; set; } = 48000;

    // Mumble server settings
    public string MumbleHost { get; set; } = "127.0.0.1";
    public int MumblePort { get; set; } = 64738;
    public string MumbleUsername { get; set; } = "";
    public string MumblePassword { get; set; } = "";

    public List<HotkeyBinding> Bindings { get; set; } = new()
    {
        new HotkeyBinding { FreqId = 1050, Hotkey = "LeftCtrl", Label = "Main" }
    };
}
