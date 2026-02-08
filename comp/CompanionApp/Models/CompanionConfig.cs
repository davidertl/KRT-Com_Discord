using System.Collections.Generic;

namespace CompanionApp.Models;

public class CompanionConfig
{
    public string ServerBaseUrl { get; set; } = "http://127.0.0.1:3000";
    public string WsAudioUrl { get; set; } = "ws://127.0.0.1:3000/audio";
    public string AdminToken { get; set; } = "";
    public string DiscordUserId { get; set; } = "";
    public int RadioSlot { get; set; } = 1;
    public int SampleRate { get; set; } = 48000;
    public List<HotkeyBinding> Bindings { get; set; } = new()
    {
        new HotkeyBinding { FreqId = 1050, Hotkey = "LeftCtrl", Label = "Main" }
    };
}
