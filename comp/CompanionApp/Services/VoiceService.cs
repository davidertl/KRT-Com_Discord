using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Concentus.Enums;
using Concentus.Structs;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CompanionApp.Services;

/// <summary>
/// Per-frequency audio settings for volume, panning, and mute.
/// </summary>
public class FreqAudioSettings
{
    public float Volume { get; set; } = 1.0f;
    public float Pan { get; set; } = 0.5f; // 0=full left, 0.5=center, 1=full right
    public bool Muted { get; set; }
}

/// <summary>
/// Custom voice transport service.
/// Uses WebSocket for control signalling (auth, join/leave freq)
/// and binary WebSocket frames for Opus audio.
/// </summary>
public sealed class VoiceService : IDisposable
{
    // ---- events ----
    public event Action<string>? StatusChanged;
    public event Action<Exception>? ErrorOccurred;
    /// <summary>Raised on RX start/stop. Args: (discordUserId, username, freqId, action)</summary>
    public event Action<string, string, int, string>? RxStateChanged;
    /// <summary>Raised when a frequency is joined. Args: (freqId, listenerCount)</summary>
    public event Action<int, int>? FreqJoined;
    /// <summary>Raised when server confirms mute/unmute. Args: (freqId, isMuted)</summary>
    public event Action<int, bool>? MuteConfirmed;

    // ---- connection state ----
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _wsReceiveTask;
    private Task? _heartbeatTask;
    private string _sessionToken = "";

    // ---- audio send queue (binary WS frames) ----
    private Channel<byte[]>? _audioSendChannel;
    private Task? _audioSendTask;

    // ---- opus codec ----
    private OpusEncoder? _opusEncoder;
    private OpusDecoder? _opusDecoder;
    private const int OpusSampleRate = 48000;
    private const int OpusChannels = 1;
    private const int OpusFrameMs = 20; // 20ms frames
    private const int OpusFrameSamples = OpusSampleRate * OpusFrameMs / 1000; // 960
    private const int OpusBitrate = 32000;

    // ---- audio playback (stereo, per-frequency mixing) ----
    private WasapiOut? _waveOut;
    private BufferedWaveProvider? _waveProvider;
    private string _outputDeviceName = "Default";
    private readonly ConcurrentDictionary<int, FreqAudioSettings> _freqSettings = new();
    private readonly object _playbackLock = new();

    // ---- audio capture buffering ----
    private readonly List<byte> _pcmBuffer = new();
    private readonly object _pcmLock = new();
    private int _lastCaptureRate = OpusSampleRate;
    private int _lastCaptureChannels = 1;

    // ---- master volume (0.0 – 1.25) ----
    private float _masterInputVolume = 1.0f;
    private float _masterOutputVolume = 1.0f;

    // ---- TX state ----
    private bool _isTransmitting;
    private int _currentFreqId;
    private uint _txSequence;

    // ---- heartbeat / pong tracking ----
    private long _lastPongTicks = 0;
    private const int PongTimeoutSeconds = 30;

    // ---- connection info ----
    private string _host = "";
    private int _wsPort = 3000;
    private string _discordUserId = "";
    private string _guildId = "";
    private string _authToken = "";

    public bool IsConnected { get; private set; }

    // ----------------------------------------------------------------
    // Public API
    // ----------------------------------------------------------------

    public void SetOutputDevice(string deviceName)
    {
        _outputDeviceName = deviceName;
        lock (_playbackLock)
        {
            if (_waveOut != null)
            {
                InitPlayback();
            }
        }
    }

    /// <summary>
    /// Set master input (microphone) volume. 0-125 maps to 0.0-1.25.
    /// </summary>
    public void SetMasterInputVolume(float volume) => _masterInputVolume = Math.Clamp(volume, 0f, 1.25f);

    /// <summary>
    /// Set master output (speakers) volume. 0-125 maps to 0.0-1.25.
    /// </summary>
    public void SetMasterOutputVolume(float volume) => _masterOutputVolume = Math.Clamp(volume, 0f, 1.25f);

    /// <summary>
    /// Set per-frequency audio settings (volume, pan, mute).
    /// </summary>
    public void SetFreqSettings(int freqId, float volume, float pan, bool muted)
    {
        _freqSettings.AddOrUpdate(freqId,
            _ => new FreqAudioSettings { Volume = volume, Pan = pan, Muted = muted },
            (_, s) => { s.Volume = volume; s.Pan = pan; s.Muted = muted; return s; });
    }

    /// <summary>
    /// Connect to the voice relay server.
    /// </summary>
    public async Task ConnectAsync(string host, int wsPort, string discordUserId, string guildId, string authToken = "")
    {
        // Validate host
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Voice host cannot be empty.", nameof(host));

        // Validate port range (1-65535)
        if (wsPort < 1 || wsPort > 65535)
            throw new ArgumentOutOfRangeException(nameof(wsPort), $"Port must be between 1 and 65535, got {wsPort}.");

        _host = host;
        _wsPort = wsPort;
        _discordUserId = discordUserId;
        _guildId = guildId;
        _authToken = authToken ?? "";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Status("Connecting...");

        // ---- WebSocket ----
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        // Determine scheme – if host explicitly starts with https, use wss
        var scheme = host.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var cleanHost = host
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        // Don't append port if it's the default for the scheme (443 for wss, 80 for ws)
        bool isDefaultPort = (scheme == "wss" && wsPort == 443) || (scheme == "ws" && wsPort == 80);
        var wsUri = isDefaultPort
            ? new Uri($"{scheme}://{cleanHost}/voice")
            : new Uri($"{scheme}://{cleanHost}:{wsPort}/voice");
        Status($"Connecting to {wsUri} ...");

        try
        {
            await _ws.ConnectAsync(wsUri, ct);
        }
        catch (Exception ex)
        {
            Error(ex);
            Status($"WebSocket connect failed: {ex.Message}");
            return;
        }

        Status("WebSocket connected, sending auth...");

        // Send auth message (prefer token-based auth if available)
        var auth = string.IsNullOrEmpty(_authToken)
            ? new { type = "auth", discordUserId, guildId, authToken = (string?)null }
            : new { type = "auth", discordUserId, guildId, authToken = (string?)_authToken };
        await WsSendAsync(auth, ct);

        // Start WS receive loop (will process auth_ok with UDP port)
        _wsReceiveTask = Task.Run(() => WsReceiveLoopAsync(ct), ct);

        // Wait for auth response (up to 5s)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!IsConnected && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);
        }

        if (!IsConnected)
        {
            Status("Auth timeout – server did not respond");
        }
    }

    /// <summary>
    /// Join a frequency channel for receiving audio.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> JoinFrequencyAsync(int freqId)
    {
        if (!IsConnected || _ws == null) return false;

        var msg = new { type = "join", freqId };
        await WsSendAsync(msg, _cts!.Token);

        // The server will confirm via WS message; for now we assume success
        _currentFreqId = freqId;
        return true;
    }

    /// <summary>
    /// Leave a frequency channel.
    /// </summary>
    public async Task LeaveFrequencyAsync(int freqId)
    {
        if (!IsConnected || _ws == null) return;

        var msg = new { type = "leave", freqId };
        await WsSendAsync(msg, _cts!.Token);
    }

    /// <summary>
    /// Tell the server to mute a frequency (stop forwarding audio to us).
    /// </summary>
    public async Task MuteFrequencyAsync(int freqId)
    {
        if (!IsConnected || _ws == null) return;

        var msg = new { type = "mute", freqId };
        await WsSendAsync(msg, _cts!.Token);
    }

    /// <summary>
    /// Tell the server to unmute a frequency (resume forwarding audio to us).
    /// </summary>
    public async Task UnmuteFrequencyAsync(int freqId)
    {
        if (!IsConnected || _ws == null) return;

        var msg = new { type = "unmute", freqId };
        await WsSendAsync(msg, _cts!.Token);
    }

    /// <summary>
    /// Start transmitting on the current frequency.
    /// </summary>
    public void StartTransmit()
    {
        _isTransmitting = true;
        _txSequence = 0;

        // Ensure encoder exists
        _opusEncoder ??= new OpusEncoder(OpusSampleRate, OpusChannels, OpusApplication.OPUS_APPLICATION_VOIP)
        {
            Bitrate = OpusBitrate,
            Complexity = 5,
            UseVBR = true,
        };

        lock (_pcmLock)
        {
            _pcmBuffer.Clear();
        }
    }

    /// <summary>
    /// Stop transmitting.
    /// </summary>
    public void StopTransmit()
    {
        _isTransmitting = false;

        lock (_pcmLock)
        {
            _pcmBuffer.Clear();
        }
    }

    /// <summary>
    /// Feed raw PCM audio data captured from the microphone.
    /// The data will be resampled if needed, Opus-encoded, and sent over UDP.
    /// </summary>
    public void SendAudio(byte[] pcmData, int sampleRate, int channels)
    {
        if (!_isTransmitting || _ws == null || _ws.State != WebSocketState.Open) return;

        _lastCaptureRate = sampleRate;
        _lastCaptureChannels = channels;

        // Convert to 16-bit mono 48kHz if needed, then buffer
        var mono48k = ConvertToMono48k(pcmData, sampleRate, channels);

        lock (_pcmLock)
        {
            _pcmBuffer.AddRange(mono48k);

            // Process complete frames (960 samples * 2 bytes = 1920 bytes per frame)
            var frameSizeBytes = OpusFrameSamples * 2;
            while (_pcmBuffer.Count >= frameSizeBytes)
            {
                var frameBytes = _pcmBuffer.GetRange(0, frameSizeBytes).ToArray();
                _pcmBuffer.RemoveRange(0, frameSizeBytes);

                // Convert bytes to short[]
                var frameSamples = new short[OpusFrameSamples];
                Buffer.BlockCopy(frameBytes, 0, frameSamples, 0, frameSizeBytes);

                // Apply master input volume
                if (Math.Abs(_masterInputVolume - 1.0f) > 0.001f)
                {
                    for (int i = 0; i < frameSamples.Length; i++)
                    {
                        frameSamples[i] = (short)Math.Clamp(frameSamples[i] * _masterInputVolume, short.MinValue, short.MaxValue);
                    }
                }

                // Encode with Opus
                var encoded = new byte[4000];
                int encodedLen;
                try
                {
                    encodedLen = _opusEncoder!.Encode(frameSamples, 0, OpusFrameSamples, encoded, 0, encoded.Length);
                }
                catch
                {
                    continue;
                }

                if (encodedLen <= 0) continue;

                // Build audio packet: [4 bytes freqId BE][4 bytes sequence BE][encoded opus data]
                var packet = new byte[8 + encodedLen];
                BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0), _currentFreqId);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), _txSequence++);
                Array.Copy(encoded, 0, packet, 8, encodedLen);

                _audioSendChannel?.Writer.TryWrite(packet);
            }
        }
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        _isTransmitting = false;
        IsConnected = false;

        if (_ws != null && _ws.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
        }

        CleanupConnection();
        Status("Disconnected");
    }

    // ----------------------------------------------------------------
    // WebSocket send / receive
    // ----------------------------------------------------------------

    private async Task WsSendAsync(object msg, CancellationToken ct)
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(msg);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task WsReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var msgBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    IsConnected = false;
                    Status("Server closed connection");
                    break;
                }

                msgBuffer.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var data = msgBuffer.ToArray();
                var len = (int)msgBuffer.Length;
                msgBuffer.SetLength(0);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    HandleAudioFrame(data, len);
                }
                else
                {
                    HandleWsMessage(Encoding.UTF8.GetString(data, 0, len));
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Error(ex);
            }
        }
        finally
        {
            IsConnected = false;
        }
    }

    private void HandleWsMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            switch (type)
            {
                case "auth_ok":
                    _lastPongTicks = DateTime.UtcNow.Ticks;
                    _sessionToken = root.TryGetProperty("sessionToken", out var st) ? st.GetString() ?? "" : "";

                    // Set up audio send channel for binary WS frames
                    _audioSendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest
                    });
                    _audioSendTask = Task.Run(() => AudioSendLoopAsync(_cts!.Token));

                    InitPlayback();

                    IsConnected = true;
                    Status("Connected");

                    // Start heartbeat
                    _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts!.Token));
                    break;

                case "auth_error":
                case "auth_fail":
                    var reason = root.TryGetProperty("reason", out var rp) ? rp.GetString() : "unknown";
                    Status($"Auth failed: {reason}");
                    IsConnected = false;
                    break;

                case "rx":
                    // Someone is transmitting on a freq we're listening to
                    // { type: "rx", freqId, discordUserId, username, action: "start"|"stop" }
                    var rxFreqId = root.TryGetProperty("freqId", out var rf) ? rf.GetInt32() : 0;
                    var rxUser = root.TryGetProperty("discordUserId", out var du) ? du.GetString() ?? "" : "";
                    var rxName = root.TryGetProperty("username", out var un) ? un.GetString() ?? rxUser : rxUser;
                    var rxAction = root.TryGetProperty("action", out var ra) ? ra.GetString() : "";

                    RxStateChanged?.Invoke(rxUser, rxName, rxFreqId, rxAction ?? "");
                    break;

                case "join_ok":
                    var joinFreqId = root.TryGetProperty("freqId", out var jf) ? jf.GetInt32() : 0;
                    var joinListeners = root.TryGetProperty("listenerCount", out var jl) ? jl.GetInt32() : 0;
                    FreqJoined?.Invoke(joinFreqId, joinListeners);
                    break;

                case "listener_update":
                    var luFreq = root.TryGetProperty("freqId", out var luf) ? luf.GetInt32() : 0;
                    var luCount = root.TryGetProperty("listenerCount", out var luc) ? luc.GetInt32() : 0;
                    FreqJoined?.Invoke(luFreq, luCount);
                    break;

                case "mute_ok":
                    var muteFreq = root.TryGetProperty("freqId", out var mf) ? mf.GetInt32() : 0;
                    var isMuted = root.TryGetProperty("muted", out var mm) && mm.GetBoolean();
                    MuteConfirmed?.Invoke(muteFreq, isMuted);
                    break;

                case "pong":
                    _lastPongTicks = DateTime.UtcNow.Ticks;
                    break;

                case "error":
                    var errMsg = root.TryGetProperty("message", out var em) ? em.GetString() : "unknown error";
                    Status($"Server error: {errMsg}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Status($"WS parse error: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------
    // Audio send/receive (binary WebSocket frames)
    // ----------------------------------------------------------------

    /// <summary>
    /// Background loop that drains the audio send channel and sends binary WS frames.
    /// </summary>
    private async Task AudioSendLoopAsync(CancellationToken ct)
    {
        if (_audioSendChannel == null || _ws == null) return;

        try
        {
            await foreach (var packet in _audioSendChannel.Reader.ReadAllAsync(ct))
            {
                if (_ws.State != WebSocketState.Open) break;
                await _ws.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    /// <summary>
    /// Handle an incoming binary WS frame containing an audio packet.
    /// Format: [4 bytes freqId BE][4 bytes sequence BE][opus data]
    /// </summary>
    private void HandleAudioFrame(byte[] data, int length)
    {
        if (length < 9) return;

        // Parse big-endian header
        int freqId = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0));

        // Look up per-frequency audio settings
        var settings = _freqSettings.GetValueOrDefault(freqId);
        if (settings is { Muted: true }) return;

        float vol = (settings?.Volume ?? 1.0f) * _masterOutputVolume;
        float pan = settings?.Pan ?? 0.5f;

        int opusLen = length - 8;
        var opusData = new byte[opusLen];
        Array.Copy(data, 8, opusData, 0, opusLen);

        // Decode Opus to PCM
        try
        {
            _opusDecoder ??= new OpusDecoder(OpusSampleRate, OpusChannels);
            var pcm = new short[OpusFrameSamples];
            var decoded = _opusDecoder.Decode(opusData, 0, opusLen, pcm, 0, OpusFrameSamples, false);
            if (decoded > 0)
            {
                PlayPcmStereo(pcm, decoded, vol, pan);
            }
        }
        catch
        {
            // Ignore decode errors
        }
    }

    // ----------------------------------------------------------------
    // Audio playback
    // ----------------------------------------------------------------

    private void InitPlayback()
    {
        lock (_playbackLock)
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveProvider?.ClearBuffer();

            _waveProvider = new BufferedWaveProvider(new WaveFormat(OpusSampleRate, 16, 2)) // stereo output
            {
                BufferLength = OpusSampleRate * 2 * 2 * 2, // 2 seconds stereo buffer
                DiscardOnBufferOverflow = true
            };

            // Find output device
            MMDevice? device = null;
            if (_outputDeviceName != "Default")
            {
                try
                {
                    var enumerator = new MMDeviceEnumerator();
                    device = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                        .FirstOrDefault(d => d.FriendlyName == _outputDeviceName);
                }
                catch { }
            }

            _waveOut = device != null
                ? new WasapiOut(device, AudioClientShareMode.Shared, false, 50)
                : new WasapiOut(AudioClientShareMode.Shared, 50);

            _waveOut.Init(_waveProvider);
            _waveOut.Play();
        }
    }

    private void PlayPcmStereo(short[] monoSamples, int count, float volume, float pan)
    {
        if (_waveProvider == null) return;

        // Calculate stereo gains from pan (0=left, 0.5=center, 1=right)
        float leftGain = Math.Min(1.0f, 2.0f * (1.0f - pan)) * volume;
        float rightGain = Math.Min(1.0f, 2.0f * pan) * volume;

        // Interleave left/right channels
        var stereoBytes = new byte[count * 4]; // 2 channels x 2 bytes per sample
        for (int i = 0; i < count; i++)
        {
            var sample = monoSamples[i];
            var left = (short)Math.Clamp(sample * leftGain, short.MinValue, short.MaxValue);
            var right = (short)Math.Clamp(sample * rightGain, short.MinValue, short.MaxValue);
            BitConverter.GetBytes(left).CopyTo(stereoBytes, i * 4);
            BitConverter.GetBytes(right).CopyTo(stereoBytes, i * 4 + 2);
        }

        lock (_playbackLock)
        {
            _waveProvider?.AddSamples(stereoBytes, 0, stereoBytes.Length);
        }
    }

    // ----------------------------------------------------------------
    // Audio format conversion
    // ----------------------------------------------------------------

    /// <summary>
    /// Converts arbitrary PCM input (float or int16, any channel count, any sample rate)
    /// to 16-bit mono 48kHz PCM.
    /// </summary>
    private static byte[] ConvertToMono48k(byte[] pcmData, int sampleRate, int channels)
    {
        // NAudio's WasapiCapture gives IEEE float by default
        // Detect format by typical sizes: 4 bytes/sample for float, 2 bytes/sample for int16
        var bytesPerSample = 4; // assume float
        var totalSamples = pcmData.Length / bytesPerSample / channels;

        if (totalSamples == 0) return Array.Empty<byte>();

        // Convert to mono float first
        var monoFloat = new float[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                var offset = (i * channels + ch) * bytesPerSample;
                if (offset + bytesPerSample > pcmData.Length) break;

                if (bytesPerSample == 4)
                {
                    sum += BitConverter.ToSingle(pcmData, offset);
                }
                else
                {
                    sum += BitConverter.ToInt16(pcmData, offset) / 32768f;
                }
            }
            monoFloat[i] = sum / channels;
        }

        // Resample to 48kHz if needed
        float[] resampled;
        if (sampleRate != OpusSampleRate)
        {
            var ratio = (double)OpusSampleRate / sampleRate;
            var newLength = (int)(totalSamples * ratio);
            resampled = new float[newLength];
            for (int i = 0; i < newLength; i++)
            {
                var srcIdx = i / ratio;
                var idx = (int)srcIdx;
                var frac = (float)(srcIdx - idx);
                if (idx + 1 < totalSamples)
                    resampled[i] = monoFloat[idx] * (1 - frac) + monoFloat[idx + 1] * frac;
                else if (idx < totalSamples)
                    resampled[i] = monoFloat[idx];
            }
        }
        else
        {
            resampled = monoFloat;
        }

        // Convert to 16-bit PCM
        var result = new byte[resampled.Length * 2];
        for (int i = 0; i < resampled.Length; i++)
        {
            var clamped = Math.Clamp(resampled[i], -1f, 1f);
            var s16 = (short)(clamped * 32767f);
            BitConverter.GetBytes(s16).CopyTo(result, i * 2);
        }

        return result;
    }

    // ----------------------------------------------------------------
    // Heartbeat
    // ----------------------------------------------------------------

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);

                // Check if last pong is stale
                var elapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastPongTicks));
                if (elapsed.TotalSeconds > PongTimeoutSeconds)
                {
                    Status("Heartbeat timeout — no pong received, reconnecting...");
                    _ = Task.Run(async () =>
                    {
                        await DisconnectAsync();
                        try { await ConnectAsync(_host, _wsPort, _discordUserId, _guildId, _authToken); }
                        catch { }
                    });
                    return;
                }

                await WsSendAsync(new { type = "ping" }, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private void Status(string msg) => StatusChanged?.Invoke(msg);
    private void Error(Exception ex) => ErrorOccurred?.Invoke(ex);

    private void CleanupConnection()
    {
        lock (_playbackLock)
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            _waveProvider = null;
        }

        _audioSendChannel?.Writer.TryComplete();
        _audioSendChannel = null;

        _ws?.Dispose();
        _ws = null;

        _opusEncoder = null;
        _opusDecoder = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _isTransmitting = false;
        IsConnected = false;
        CleanupConnection();
        _cts?.Dispose();
    }
}
