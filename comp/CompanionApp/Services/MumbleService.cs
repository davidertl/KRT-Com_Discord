using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Concentus.Structs;
using Concentus.Enums;

namespace CompanionApp.Services;

/// <summary>
/// Service that manages connection to a Mumble server for audio transmission.
/// Note: This is a simplified implementation that stubs Mumble connectivity.
/// Full Mumble protocol support can be added later.
/// For now, the app falls back to WebSocket streaming when Mumble is not available.
/// </summary>
public sealed class MumbleService : IDisposable
{
    private TcpClient? _tcpClient;
    private OpusEncoder? _opusEncoder;
    private CancellationTokenSource? _cts;
    private volatile bool _isConnected;
    private volatile bool _isTransmitting;
    private string _currentChannel = "";
    private string _host = "";
    private int _port;

    public bool IsConnected => _isConnected;
    public bool IsTransmitting => _isTransmitting;

    public event Action<string>? StatusChanged;
    public event Action<Exception>? ErrorOccurred;

    /// <summary>
    /// Connect to a Mumble server.
    /// Note: Simplified implementation - tests TCP connectivity only.
    /// Full Mumble protocol handshake requires more implementation.
    /// </summary>
    public async Task ConnectAsync(string host, int port, string username, string password = "")
    {
        if (_isConnected)
        {
            await DisconnectAsync();
        }

        _host = host;
        _port = port;
        _cts = new CancellationTokenSource();

        try
        {
            // Test TCP connectivity to Mumble server
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port);

            // Initialize Opus encoder (48kHz, mono, VOIP optimized)
            _opusEncoder = new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _opusEncoder.Bitrate = 64000;

            _isConnected = true;
            StatusChanged?.Invoke($"Connected to {host}:{port}");
        }
        catch (Exception ex)
        {
            _isConnected = false;
            StatusChanged?.Invoke($"Connection failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Join a specific channel (mapped from frequency ID).
    /// </summary>
    public Task JoinChannelAsync(string channelName)
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException("Not connected to server");
        }

        _currentChannel = channelName;
        StatusChanged?.Invoke($"Channel set: {channelName}");
        
        // Note: Full channel join requires Mumble protocol implementation
        // For now, this just tracks the intended channel
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Join a channel by frequency ID.
    /// </summary>
    public Task JoinFrequencyAsync(int freqId)
    {
        return JoinChannelAsync($"Freq-{freqId}");
    }

    /// <summary>
    /// Start transmitting audio (push-to-talk pressed).
    /// </summary>
    public void StartTransmit()
    {
        if (!_isConnected)
        {
            StatusChanged?.Invoke("Cannot transmit: not connected");
            return;
        }
        
        _isTransmitting = true;
        StatusChanged?.Invoke("Transmitting...");
    }

    /// <summary>
    /// Stop transmitting audio (push-to-talk released).
    /// </summary>
    public void StopTransmit()
    {
        _isTransmitting = false;
        StatusChanged?.Invoke("Transmission ended");
    }

    /// <summary>
    /// Send PCM audio data. Will be encoded to Opus.
    /// Expected format: 48kHz, 16-bit, mono.
    /// Note: Full audio transmission requires Mumble protocol implementation.
    /// </summary>
    public void SendAudio(byte[] pcmData, int sampleRate = 48000, int channels = 1)
    {
        if (!_isTransmitting || !_isConnected || _opusEncoder == null)
        {
            return;
        }

        try
        {
            // Convert byte array to short array (16-bit samples)
            var sampleCount = pcmData.Length / 2;
            var samples = new short[sampleCount];
            Buffer.BlockCopy(pcmData, 0, samples, 0, pcmData.Length);

            // Resample if needed (simple linear interpolation)
            if (sampleRate != 48000 || channels != 1)
            {
                samples = ResampleToMono48k(samples, sampleRate, channels);
                sampleCount = samples.Length;
            }

            // Opus encodes in frames (typically 20ms = 960 samples at 48kHz)
            const int frameSize = 960;
            var encodedBuffer = new byte[4000];

            for (int offset = 0; offset < sampleCount - frameSize; offset += frameSize)
            {
                var frame = new short[frameSize];
                Array.Copy(samples, offset, frame, 0, frameSize);

                var encodedLength = _opusEncoder.Encode(frame, 0, frameSize, encodedBuffer, 0, encodedBuffer.Length);
                if (encodedLength > 0)
                {
                    // Note: Would send via Mumble protocol here
                    // For now, audio is encoded but not transmitted
                }
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex);
        }
    }

    /// <summary>
    /// Disconnect from the Mumble server.
    /// </summary>
    public Task DisconnectAsync()
    {
        _isTransmitting = false;
        _isConnected = false;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _tcpClient?.Close();
        _tcpClient?.Dispose();
        _tcpClient = null;

        _opusEncoder = null;

        StatusChanged?.Invoke("Disconnected");
        return Task.CompletedTask;
    }

    private short[] ResampleToMono48k(short[] samples, int sourceSampleRate, int sourceChannels)
    {
        // First convert to mono if stereo
        short[] mono;
        if (sourceChannels == 2)
        {
            mono = new short[samples.Length / 2];
            for (int i = 0; i < mono.Length; i++)
            {
                mono[i] = (short)((samples[i * 2] + samples[i * 2 + 1]) / 2);
            }
        }
        else
        {
            mono = samples;
        }

        // Resample if needed
        if (sourceSampleRate == 48000)
        {
            return mono;
        }

        double ratio = 48000.0 / sourceSampleRate;
        int newLength = (int)(mono.Length * ratio);
        var resampled = new short[newLength];

        for (int i = 0; i < newLength; i++)
        {
            double srcIndex = i / ratio;
            int srcIndexInt = (int)srcIndex;
            double frac = srcIndex - srcIndexInt;

            if (srcIndexInt + 1 < mono.Length)
            {
                resampled[i] = (short)(mono[srcIndexInt] * (1 - frac) + mono[srcIndexInt + 1] * frac);
            }
            else if (srcIndexInt < mono.Length)
            {
                resampled[i] = mono[srcIndexInt];
            }
        }

        return resampled;
    }

    public void Dispose()
    {
        DisconnectAsync().Wait(2000);
    }
}
