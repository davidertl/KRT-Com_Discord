namespace CompanionApp.Models;

public class HotkeyBinding
{
    public bool IsEnabled { get; set; } = true;
    public int FreqId { get; set; } = 1050;
    public string Hotkey { get; set; } = "LeftCtrl";
    public string ChannelName { get; set; } = "";
    public string Label { get; set; } = "";
}
