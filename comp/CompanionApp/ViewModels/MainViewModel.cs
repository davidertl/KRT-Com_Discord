using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CompanionApp.Models;
using CompanionApp.Services;

namespace CompanionApp.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private CompanionConfig _config = new();
    private HotkeyHook? _hook;
    private BackendClient? _backend;
    private AudioCaptureService? _audio;
    private StreamingClient? _streaming;
    private CancellationTokenSource? _streamCts;
    private HotkeyBinding? _activeBinding;

    public ObservableCollection<HotkeyBinding> Bindings { get; } = new();

    private string _serverBaseUrl = "";
    public string ServerBaseUrl
    {
        get => _serverBaseUrl;
        set { _serverBaseUrl = value; OnPropertyChanged(); }
    }

    private string _wsAudioUrl = "";
    public string WsAudioUrl
    {
        get => _wsAudioUrl;
        set { _wsAudioUrl = value; OnPropertyChanged(); }
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

    private int _radioSlot = 1;
    public int RadioSlot
    {
        get => _radioSlot;
        set { _radioSlot = value; OnPropertyChanged(); }
    }

    private int _sampleRate = 48000;
    public int SampleRate
    {
        get => _sampleRate;
        set { _sampleRate = value; OnPropertyChanged(); }
    }

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

    public event PropertyChangedEventHandler? PropertyChanged;

    public Task InitializeAsync()
    {
        LoadFromConfig(ConfigService.Load());
        StartHotkeyHook();
        StatusText = "Ready";
        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        ApplyToConfig();
        ConfigService.Save(_config);
        RestartHook();
        await SyncFreqNamesAsync();
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
        var binding = Bindings.FirstOrDefault(b => b.IsEnabled);
        if (binding == null)
        {
            StatusText = "No enabled binding";
            return;
        }

        await HandlePttPressedAsync(binding);
    }

    public async Task StopTestAsync()
    {
        await HandlePttReleasedAsync();
    }

    private void LoadFromConfig(CompanionConfig config)
    {
        _config = config;

        ServerBaseUrl = config.ServerBaseUrl;
        WsAudioUrl = config.WsAudioUrl;
        AdminToken = config.AdminToken;
        DiscordUserId = config.DiscordUserId;
        RadioSlot = config.RadioSlot;
        SampleRate = config.SampleRate;

        Bindings.Clear();
        foreach (var b in config.Bindings)
        {
            Bindings.Add(b);
        }
    }

    private void ApplyToConfig()
    {
        _config.ServerBaseUrl = ServerBaseUrl;
        _config.WsAudioUrl = WsAudioUrl;
        _config.AdminToken = AdminToken;
        _config.DiscordUserId = DiscordUserId;
        _config.RadioSlot = RadioSlot;
        _config.SampleRate = SampleRate;
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
            var entries = Bindings
                .Where(b => b.FreqId >= 1000 && b.FreqId <= 9999)
                .Select(b => new { b.FreqId, Name = (b.ChannelName ?? string.Empty).Trim() })
                .Where(b => !string.IsNullOrWhiteSpace(b.Name))
                .GroupBy(b => b.FreqId)
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
        StartHotkeyHook();
    }

    private void OnHotkeyPressed(HotkeyBinding binding)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await HandlePttPressedAsync(binding);
        });
    }

    private void OnHotkeyReleased(HotkeyBinding binding)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await HandlePttReleasedAsync();
        });
    }

    private async Task HandlePttPressedAsync(HotkeyBinding binding)
    {
        if (IsStreaming)
        {
            return;
        }

        _activeBinding = binding;
        StatusText = $"PTT start (Freq {binding.FreqId})";

        _backend?.Dispose();
        _backend = new BackendClient(ServerBaseUrl, AdminToken);

        _audio?.Dispose();
        _audio = new AudioCaptureService();
        _audio.AudioFrame += AudioOnAudioFrame;

        _streaming?.Dispose();
        _streaming = new StreamingClient();

        _streamCts?.Dispose();
        _streamCts = new CancellationTokenSource();

        var wsUri = new Uri(WsAudioUrl);
        _audio.Start();

        var format = _audio.WaveFormat;
        var hello = new
        {
            type = "hello",
            freqId = binding.FreqId,
            discordUserId = DiscordUserId,
            radioSlot = RadioSlot,
            format = new
            {
                sampleRate = format?.SampleRate ?? SampleRate,
                channels = format?.Channels ?? 1,
                bitsPerSample = format?.BitsPerSample ?? 16
            }
        };

        await _streaming.StartAsync(wsUri, hello, AdminToken, _streamCts.Token);

        await _backend.SendTxEventAsync(binding.FreqId, "start", DiscordUserId, RadioSlot);
        IsStreaming = true;
    }

    private async Task HandlePttReleasedAsync()
    {
        if (!IsStreaming)
        {
            return;
        }

        var binding = _activeBinding;
        _activeBinding = null;

        try
        {
            if (binding != null && _backend != null)
            {
                await _backend.SendTxEventAsync(binding.FreqId, "stop", DiscordUserId, RadioSlot);
            }
        }
        catch
        {
        }

        _audio?.Dispose();
        _audio = null;

        if (_streaming != null)
        {
            await _streaming.StopAsync();
            _streaming.Dispose();
            _streaming = null;
        }

        _streamCts?.Dispose();
        _streamCts = null;

        IsStreaming = false;
        StatusText = "PTT stop";
    }

    private void AudioOnAudioFrame(byte[] data)
    {
        _streaming?.EnqueueAudio(data);
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
        _streaming?.Dispose();
        _streamCts?.Dispose();
    }
}
