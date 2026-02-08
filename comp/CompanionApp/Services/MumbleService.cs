using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Concentus.Structs;
using Concentus.Enums;
using Google.Protobuf;

namespace CompanionApp.Services;

/// <summary>
/// Service that manages connection to a Mumble server for audio transmission.
/// Implements the Mumble protocol with TLS and protobuf messages.
/// </summary>
public sealed class MumbleService : IDisposable
{
    private TcpClient? _tcpClient;
    private SslStream? _sslStream;
    private OpusEncoder? _opusEncoder;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _pingTask;
    
    private volatile bool _isConnected;
    private volatile bool _isAuthenticated;
    private volatile bool _isTransmitting;
    private string _currentChannel = "";
    private string _host = "";
    private int _port;
    private uint _sessionId;
    private uint _maxBandwidth;
    private int _audioSequence;

    public bool IsConnected => _isConnected && _isAuthenticated;
    public bool IsTransmitting => _isTransmitting;
    public uint SessionId => _sessionId;

    public event Action<string>? StatusChanged;
    public event Action<Exception>? ErrorOccurred;

    /// <summary>
    /// Connect to a Mumble server with TLS.
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
        _audioSequence = 0;

        try
        {
            StatusChanged?.Invoke($"Connecting to {host}:{port}...");

            // Create TCP connection
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port);

            // Wrap with SSL/TLS (Mumble requires TLS)
            _sslStream = new SslStream(
                _tcpClient.GetStream(),
                false,
                ValidateServerCertificate,
                null);

            // Authenticate SSL (accept any certificate for now - Mumble uses self-signed certs)
            await _sslStream.AuthenticateAsClientAsync(host);

            _isConnected = true;
            StatusChanged?.Invoke("TLS connected, authenticating...");

            // Initialize Opus encoder (48kHz, mono, VOIP optimized)
            _opusEncoder = new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _opusEncoder.Bitrate = 64000;

            // Send Version message
            var version = new MumbleVersion
            {
                Version_ = MumbleProtocolHelper.MakeVersion(1, 5, 0),
                Release = "CompanionApp 1.0",
                Os = "Windows",
                OsVersion = Environment.OSVersion.VersionString
            };
            await SendMessageAsync(MumbleMessageType.Version, version);

            // Send Authenticate message
            var auth = new MumbleAuthenticate
            {
                Username = string.IsNullOrWhiteSpace(username) ? $"Companion_{Environment.MachineName}" : username,
                Password = password,
                Opus = true
            };
            await SendMessageAsync(MumbleMessageType.Authenticate, auth);

            // Start receive loop
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));

            // Start ping loop (Mumble requires periodic pings)
            _pingTask = Task.Run(() => PingLoopAsync(_cts.Token));

            // Wait a bit for authentication to complete
            await Task.Delay(2000);

            if (_isAuthenticated)
            {
                StatusChanged?.Invoke($"Connected as session {_sessionId}");
            }
            else
            {
                StatusChanged?.Invoke("Connected (authentication pending)");
            }
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _isAuthenticated = false;
            StatusChanged?.Invoke($"Connection failed: {ex.Message}");
            throw;
        }
    }

    private async Task SendMessageAsync<T>(MumbleMessageType type, T message) where T : IMessage
    {
        if (_sslStream == null || !_isConnected)
            return;

        var data = MumbleProtocolHelper.EncodeMessage(type, message);
        await _sslStream.WriteAsync(data);
        await _sslStream.FlushAsync();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var headerBuffer = new byte[6];

        try
        {
            while (!ct.IsCancellationRequested && _sslStream != null)
            {
                // Read header (6 bytes: 2 type + 4 length)
                var bytesRead = 0;
                while (bytesRead < 6)
                {
                    var read = await _sslStream.ReadAsync(headerBuffer.AsMemory(bytesRead, 6 - bytesRead), ct);
                    if (read == 0)
                    {
                        StatusChanged?.Invoke("Server closed connection");
                        _isConnected = false;
                        return;
                    }
                    bytesRead += read;
                }

                var (type, length) = MumbleProtocolHelper.DecodeHeader(headerBuffer);

                // Read payload
                var payload = new byte[length];
                bytesRead = 0;
                while (bytesRead < length)
                {
                    var read = await _sslStream.ReadAsync(payload.AsMemory(bytesRead, length - bytesRead), ct);
                    if (read == 0)
                    {
                        _isConnected = false;
                        return;
                    }
                    bytesRead += read;
                }

                // Handle message
                HandleMessage(type, payload);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            if (_isConnected)
            {
                ErrorOccurred?.Invoke(ex);
                StatusChanged?.Invoke($"Receive error: {ex.Message}");
            }
        }
    }

    private void HandleMessage(MumbleMessageType type, byte[] payload)
    {
        switch (type)
        {
            case MumbleMessageType.Version:
                // Server version received
                StatusChanged?.Invoke("Server version received");
                break;

            case MumbleMessageType.ServerSync:
                // Authentication successful, extract session ID
                // ServerSync format: session (varint), max_bandwidth (varint), welcome_text, permissions
                using (var ms = new MemoryStream(payload))
                using (var cis = new CodedInputStream(ms))
                {
                    while (!cis.IsAtEnd)
                    {
                        var tag = cis.ReadTag();
                        var fieldNumber = tag >> 3;
                        switch (fieldNumber)
                        {
                            case 1: _sessionId = cis.ReadUInt32(); break;
                            case 2: _maxBandwidth = cis.ReadUInt32(); break;
                            default: cis.SkipLastField(); break;
                        }
                    }
                }
                _isAuthenticated = true;
                StatusChanged?.Invoke($"Authenticated (session {_sessionId})");
                break;

            case MumbleMessageType.Reject:
                // Authentication rejected
                _isAuthenticated = false;
                string reason = "Unknown";
                using (var ms = new MemoryStream(payload))
                using (var cis = new CodedInputStream(ms))
                {
                    while (!cis.IsAtEnd)
                    {
                        var tag = cis.ReadTag();
                        var fieldNumber = tag >> 3;
                        if (fieldNumber == 2) // reason string
                            reason = cis.ReadString();
                        else
                            cis.SkipLastField();
                    }
                }
                StatusChanged?.Invoke($"Rejected: {reason}");
                break;

            case MumbleMessageType.Ping:
                // Respond to ping
                break;

            case MumbleMessageType.ChannelState:
                // Channel info received
                break;

            case MumbleMessageType.UserState:
                // User info received
                break;

            case MumbleMessageType.CryptSetup:
                // Encryption setup
                break;

            case MumbleMessageType.CodecVersion:
                // Codec version info
                break;

            case MumbleMessageType.ServerConfig:
                // Server configuration
                break;

            case MumbleMessageType.UDPTunnel:
                // Audio data (we're not receiving, just sending)
                break;
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _isConnected)
            {
                await Task.Delay(15000, ct); // Ping every 15 seconds

                if (_isConnected && _sslStream != null)
                {
                    var ping = new MumblePing
                    {
                        Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    await SendMessageAsync(MumbleMessageType.Ping, ping);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch
        {
            // Ignore ping errors
        }
    }

    private static bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // Accept any certificate (Mumble typically uses self-signed certs)
        return true;
    }

    /// <summary>
    /// Join a specific channel (mapped from frequency ID).
    /// </summary>
    public Task JoinChannelAsync(string channelName)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Not connected to server");
        }

        _currentChannel = channelName;
        StatusChanged?.Invoke($"Channel set: {channelName}");
        
        // Note: Full channel join would require sending UserState message
        // For now, just transmitting to root channel
        
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
        if (!IsConnected)
        {
            StatusChanged?.Invoke("Cannot transmit: not connected");
            return;
        }
        
        _isTransmitting = true;
        _audioSequence = 0;
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
    /// Send PCM audio data. Will be encoded to Opus and sent via UDPTunnel.
    /// Expected format: 48kHz, 16-bit, mono.
    /// </summary>
    public void SendAudio(byte[] pcmData, int sampleRate = 48000, int channels = 1)
    {
        if (!_isTransmitting || !IsConnected || _opusEncoder == null || _sslStream == null)
        {
            return;
        }

        try
        {
            // Convert byte array to short array (16-bit samples)
            var sampleCount = pcmData.Length / 2;
            var samples = new short[sampleCount];
            Buffer.BlockCopy(pcmData, 0, samples, 0, pcmData.Length);

            // Resample if needed
            if (sampleRate != 48000 || channels != 1)
            {
                samples = ResampleToMono48k(samples, sampleRate, channels);
                sampleCount = samples.Length;
            }

            // Opus encodes in frames (20ms = 960 samples at 48kHz)
            const int frameSize = 960;
            var encodedBuffer = new byte[4000];

            for (int offset = 0; offset + frameSize <= sampleCount; offset += frameSize)
            {
                var frame = new short[frameSize];
                Array.Copy(samples, offset, frame, 0, frameSize);

                var encodedLength = _opusEncoder.Encode(frame, 0, frameSize, encodedBuffer, 0, encodedBuffer.Length);
                if (encodedLength > 0)
                {
                    SendAudioPacket(encodedBuffer, encodedLength);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex);
        }
    }

    private void SendAudioPacket(byte[] opusData, int length)
    {
        if (_sslStream == null)
            return;

        try
        {
            // Mumble audio packet format (via UDPTunnel):
            // Header byte: type (3 bits) | target (5 bits)
            // Varint: sequence number
            // Varint: opus length
            // Opus data

            using var ms = new MemoryStream();
            
            // Header: Opus type (4) | Normal talking (0)
            ms.WriteByte(0x80); // 100 00000 = Opus, normal

            // Sequence number (varint)
            WriteVarint(ms, _audioSequence++);

            // Opus frame with length prefix
            WriteVarint(ms, length);
            ms.Write(opusData, 0, length);

            var audioPacket = ms.ToArray();

            // Send as UDPTunnel message (type 1)
            var packet = new byte[6 + audioPacket.Length];
            // Type (2 bytes, big endian) = 1
            packet[0] = 0;
            packet[1] = 1;
            // Length (4 bytes, big endian)
            packet[2] = (byte)(audioPacket.Length >> 24);
            packet[3] = (byte)(audioPacket.Length >> 16);
            packet[4] = (byte)(audioPacket.Length >> 8);
            packet[5] = (byte)audioPacket.Length;
            // Payload
            Array.Copy(audioPacket, 0, packet, 6, audioPacket.Length);

            lock (_sslStream)
            {
                _sslStream.Write(packet);
            }
        }
        catch
        {
            // Ignore individual packet errors
        }
    }

    private static void WriteVarint(Stream stream, int value)
    {
        while (value > 127)
        {
            stream.WriteByte((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    /// <summary>
    /// Disconnect from the Mumble server.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _isTransmitting = false;
        _isConnected = false;
        _isAuthenticated = false;

        _cts?.Cancel();

        if (_receiveTask != null)
        {
            try { await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { }
        }

        if (_pingTask != null)
        {
            try { await _pingTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }
        }

        _cts?.Dispose();
        _cts = null;

        _sslStream?.Close();
        _sslStream?.Dispose();
        _sslStream = null;

        _tcpClient?.Close();
        _tcpClient?.Dispose();
        _tcpClient = null;

        _opusEncoder = null;

        StatusChanged?.Invoke("Disconnected");
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
