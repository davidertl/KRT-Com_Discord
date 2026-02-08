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
    private MumbleService? _mumble;
    private CancellationTokenSource? _streamCts;
    private HotkeyBinding? _activeBinding;

    public ObservableCollection<HotkeyBinding> Bindings { get; } = new();

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
        set { _isMumbleConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(MumbleConnectionIndicator)); }
    }

    public string MumbleConnectionIndicator => IsMumbleConnected ? "Connected" : "Disconnected";

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
        AdminToken = config.AdminToken;
        DiscordUserId = config.DiscordUserId;
        RadioSlot = config.RadioSlot;
        SampleRate = config.SampleRate;

        MumbleHost = config.MumbleHost;
        MumblePort = config.MumblePort;
        MumbleUsername = config.MumbleUsername;
        MumblePassword = config.MumblePassword;

        Bindings.Clear();
        foreach (var b in config.Bindings)
        {
            Bindings.Add(b);
        }
    }

    private void ApplyToConfig()
    {
        _config.ServerBaseUrl = ServerBaseUrl;
        _config.AdminToken = AdminToken;
        _config.DiscordUserId = DiscordUserId;
        _config.RadioSlot = RadioSlot;
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

        try
        {
            _activeBinding = binding;
            StatusText = $"PTT start (Freq {binding.FreqId})";

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
                await _mumble.JoinFrequencyAsync(binding.FreqId);
                _mumble.StartTransmit();
                _audio.Start();
            }
            else
            {
                StatusText = "Mumble not connected";
                return;
            }

            // Notify backend of TX start (non-fatal if fails)
            try
            {
                await _backend.SendTxEventAsync(binding.FreqId, "start", DiscordUserId, RadioSlot);
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
            // Cleanup on error
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
