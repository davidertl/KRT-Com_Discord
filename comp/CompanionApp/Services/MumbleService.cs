using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Concentus.Structs;
using Concentus.Enums;
using Google.Protobuf;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

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
    private OpusDecoder? _opusDecoder;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _pingTask;
    private Task? _udpReceiveTask;
    private Task? _udpPingTask;
    
    // Audio playback
    private WasapiOut? _waveOut;
    private BufferedWaveProvider? _waveProvider;
    private string _outputDeviceName = "Default";
    private bool _isMuted;
    private float _volume = 1.0f;
    private float _balance = 0.5f;
    
    // Track users by session ID
    private readonly Dictionary<uint, string> _userSessions = new();
    
    // Track channels by name -> channel ID
    private readonly Dictionary<string, uint> _channels = new();
    private readonly Dictionary<string, TaskCompletionSource<uint>> _pendingChannelCreates = new();
    private bool _canCreateChannels = true;
    private uint _currentChannelId;

    // UDP voice transport
    private UdpClient? _udpClient;
    private IPEndPoint? _udpEndpoint;
    private bool _udpReady;

    // CryptState (OCB2)
    private readonly CryptStateOcb2 _cryptState = new();
    
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
    
    public bool IsMuted
    {
        get => _isMuted;
        set { _isMuted = value; }
    }

    public event Action<string>? StatusChanged;
    public event Action<Exception>? ErrorOccurred;
    public event Action<uint, string>? AudioReceived; // sessionId, username

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
            
            // Initialize Opus decoder for receiving audio
            _opusDecoder = new OpusDecoder(48000, 1);
            
            // Initialize audio playback
            InitializeAudioPlayback();

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
        // Debug: log all message types
        if (type != MumbleMessageType.Ping)
        {
            StatusChanged?.Invoke($"Msg type={type} ({(int)type}), len={payload.Length}");
        }
        
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

            case MumbleMessageType.PermissionDenied:
                // Permission denied (ex: cannot create temporary channel)
                StatusChanged?.Invoke($"Permission denied: {TryReadReason(payload)}");
                _canCreateChannels = false;
                if (_pendingChannelCreates.Count > 0)
                {
                    foreach (var pending in _pendingChannelCreates.Values)
                    {
                        pending.TrySetResult(0);
                    }
                    _pendingChannelCreates.Clear();
                }
                break;

            case MumbleMessageType.Ping:
                // Respond to ping
                break;

            case MumbleMessageType.ChannelState:
                // Channel info received - track channel IDs
                ParseChannelState(payload);
                break;

            case MumbleMessageType.UserState:
                // User info received - track session ID to username mapping
                ParseUserState(payload);
                break;

            case MumbleMessageType.CryptSetup:
                // Encryption setup
                HandleCryptSetup(payload);
                break;

            case MumbleMessageType.CodecVersion:
                // Codec version info
                break;

            case MumbleMessageType.ServerConfig:
                // Server configuration
                break;

            case MumbleMessageType.UDPTunnel:
                // Audio data received from other users
                StatusChanged?.Invoke($"Audio packet received: {payload.Length} bytes");
                HandleAudioPacket(payload);
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

    private void InitializeAudioPlayback()
    {
        try
        {
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
                catch
                {
                    // Use default
                }
            }

            // Create buffered wave provider (48kHz, 16-bit, mono)
            _waveProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true
            };

            _waveOut = device != null
                ? new WasapiOut(device, AudioClientShareMode.Shared, false, 50)
                : new WasapiOut(AudioClientShareMode.Shared, 50);

            _waveOut.Init(_waveProvider);
            _waveOut.Play();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex);
        }
    }

    public void SetOutputDevice(string deviceName)
    {
        _outputDeviceName = deviceName;
        // Reinitialize playback with new device
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _waveProvider = null;
        InitializeAudioPlayback();
    }

    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        if (_waveOut != null)
        {
            _waveOut.Volume = _volume;
        }
    }

    public void SetBalance(float balance)
    {
        _balance = Math.Clamp(balance, 0f, 1f);
        // Balance is applied during audio processing if stereo output is used
    }

    private void ParseUserState(byte[] payload)
    {
        try
        {
            using var ms = new MemoryStream(payload);
            var cis = new CodedInputStream(ms);

            uint sessionId = 0;
            string name = "";

            uint tag;
            while ((tag = cis.ReadTag()) != 0)
            {
                switch (tag)
                {
                    case 8: // field 1: session (uint32)
                        sessionId = cis.ReadUInt32();
                        break;
                    case 18: // field 2: actor (uint32) - skip
                        cis.ReadUInt32();
                        break;
                    case 26: // field 3: name (string)
                        name = cis.ReadString();
                        break;
                    default:
                        cis.SkipLastField();
                        break;
                }
            }

            if (sessionId != 0 && !string.IsNullOrEmpty(name))
            {
                _userSessions[sessionId] = name;
            }
        }
        catch
        {
            // Ignore parse errors
        }
    }

    private void ParseChannelState(byte[] payload)
    {
        try
        {
            using var ms = new MemoryStream(payload);
            var cis = new CodedInputStream(ms);

            uint channelId = 0;
            string name = "";

            uint tag;
            while ((tag = cis.ReadTag()) != 0)
            {
                switch (tag)
                {
                    case 8: // field 1: channel_id (uint32)
                        channelId = cis.ReadUInt32();
                        break;
                    case 18: // field 2: parent (uint32) - skip
                        cis.ReadUInt32();
                        break;
                    case 26: // field 3: name (string)
                        name = cis.ReadString();
                        break;
                    default:
                        cis.SkipLastField();
                        break;
                }
            }

            if (!string.IsNullOrEmpty(name))
            {
                _channels[name] = channelId;
                if (_pendingChannelCreates.TryGetValue(name, out var tcs))
                {
                    _pendingChannelCreates.Remove(name);
                    tcs.TrySetResult(channelId);
                }
                StatusChanged?.Invoke($"Channel: {name} (ID: {channelId})");
            }
        }
        catch
        {
            // Ignore parse errors
        }
    }

    private void HandleCryptSetup(byte[] payload)
    {
        try
        {
            using var ms = new MemoryStream(payload);
            var cis = new CodedInputStream(ms);

            byte[]? key = null;
            byte[]? clientNonce = null;
            byte[]? serverNonce = null;

            uint tag;
            while ((tag = cis.ReadTag()) != 0)
            {
                switch (tag)
                {
                    case 10: // field 1: key (bytes)
                        key = cis.ReadBytes().ToByteArray();
                        break;
                    case 18: // field 2: client_nonce (bytes)
                        clientNonce = cis.ReadBytes().ToByteArray();
                        break;
                    case 26: // field 3: server_nonce (bytes)
                        serverNonce = cis.ReadBytes().ToByteArray();
                        break;
                    default:
                        cis.SkipLastField();
                        break;
                }
            }

            if (key != null && clientNonce != null && serverNonce != null)
            {
                if (_cryptState.SetKey(key, clientNonce, serverNonce))
                {
                    StatusChanged?.Invoke("CryptSetup: keys set, starting UDP");
                    StartUdpTransport();
                }
                else
                {
                    StatusChanged?.Invoke("CryptSetup: invalid key/nonce");
                }
            }
            else
            {
                StatusChanged?.Invoke("CryptSetup: incomplete payload");
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"CryptSetup error: {ex.Message}");
        }
    }

    private void StartUdpTransport()
    {
        if (_udpClient != null || string.IsNullOrWhiteSpace(_host))
        {
            return;
        }

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.ReceiveBufferSize = 1024 * 1024;
            _udpClient.Client.SendBufferSize = 1024 * 1024;

            _udpEndpoint = ResolveEndpoint(_host, _port);
            _udpClient.Connect(_udpEndpoint);

            _udpReceiveTask = Task.Run(() => UdpReceiveLoopAsync(_cts?.Token ?? CancellationToken.None));
            _udpPingTask = Task.Run(() => UdpPingLoopAsync(_cts?.Token ?? CancellationToken.None));

            StatusChanged?.Invoke($"UDP socket opened: {_udpEndpoint}");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"UDP start failed: {ex.Message}");
        }
    }

    private async Task UdpReceiveLoopAsync(CancellationToken ct)
    {
        if (_udpClient == null)
        {
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _udpClient.ReceiveAsync(ct);
                if (result.Buffer.Length < 5)
                {
                    continue;
                }

                if (_cryptState.TryDecrypt(result.Buffer, out var plain))
                {
                    _udpReady = true;

                    // Ping packet (0x20) should be echoed by server
                    if (plain.Length > 0 && plain[0] == 0x20)
                    {
                        continue;
                    }

                    HandleAudioPacket(plain);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"UDP receive error: {ex.Message}");
        }
    }

    private async Task UdpPingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct);
                SendUdpPing();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
    }

    private void SendUdpPing()
    {
        if (_udpClient == null || !_cryptState.IsValid)
        {
            return;
        }

        using var ms = new MemoryStream();
        ms.WriteByte(0x20); // Ping packet header
        WriteVarint(ms, unchecked((int)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var plain = ms.ToArray();

        if (_cryptState.TryEncrypt(plain, out var encrypted))
        {
            _udpClient.Send(encrypted, encrypted.Length);
        }
    }

    private static IPEndPoint ResolveEndpoint(string host, int port)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            return new IPEndPoint(ip, port);
        }

        var addresses = Dns.GetHostAddresses(host);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
        return new IPEndPoint(address, port);
    }

    private void HandleAudioPacket(byte[] payload)
    {
        if (_isMuted || _waveProvider == null || _opusDecoder == null || payload.Length < 3)
        {
            return;
        }

        try
        {
            // Mumble audio packet format:
            // Header byte: type (3 bits high) | target (5 bits low)
            // Varint: session ID (sender)
            // Varint: sequence number
            // Opus frame(s)

            int offset = 0;
            byte header = payload[offset++];
            
            // Check if this is Opus audio (type 4)
            int audioType = (header >> 5) & 0x07;
            int target = header & 0x1F;
            
            StatusChanged?.Invoke($"Audio type={audioType}, target={target}, len={payload.Length}");
            
            if (audioType != 4) // 4 = Opus
            {
                StatusChanged?.Invoke($"Skipping non-Opus audio type: {audioType}");
                return;
            }

            // Read session ID (mumble varint)
            uint senderSession = (uint)ReadMumbleVarint(payload, ref offset);
            
            StatusChanged?.Invoke($"Sender session={senderSession}, our session={_sessionId}");
            
            // Don't play our own audio back
            if (senderSession == _sessionId)
            {
                StatusChanged?.Invoke("Skipping own audio");
                return;
            }

            // Read sequence number (mumble varint)
            ReadMumbleVarint(payload, ref offset);

            // Read opus frame length (mumble varint) and decode
            while (offset < payload.Length)
            {
                int frameLen = (int)ReadMumbleVarint(payload, ref offset);
                bool isTerminator = (frameLen & 0x2000) != 0;
                frameLen &= 0x1FFF;

                if (frameLen <= 0 || offset + frameLen > payload.Length)
                {
                    break;
                }

                var opusFrame = new byte[frameLen];
                Array.Copy(payload, offset, opusFrame, 0, frameLen);
                offset += frameLen;

                // Decode Opus to PCM
                var pcmBuffer = new short[960]; // 20ms at 48kHz
                int decodedSamples = _opusDecoder.Decode(opusFrame, 0, frameLen, pcmBuffer, 0, pcmBuffer.Length, false);

                StatusChanged?.Invoke($"Decoded {decodedSamples} samples from {frameLen} bytes");
                
                if (decodedSamples > 0)
                {
                    // Convert to bytes and add to buffer
                    var byteBuffer = new byte[decodedSamples * 2];
                    Buffer.BlockCopy(pcmBuffer, 0, byteBuffer, 0, byteBuffer.Length);
                    _waveProvider.AddSamples(byteBuffer, 0, byteBuffer.Length);

                    // Notify that audio was received
                    var username = _userSessions.TryGetValue(senderSession, out var name) ? name : $"Session_{senderSession}";
                    AudioReceived?.Invoke(senderSession, username);
                }

                if (isTerminator)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Log but don't crash on audio errors
            ErrorOccurred?.Invoke(ex);
        }
    }

    /// <summary>
    /// Read Mumble's custom varint format for audio packets.
    /// Different from protobuf varints!
    /// </summary>
    private static long ReadMumbleVarint(byte[] data, ref int offset)
    {
        if (offset >= data.Length)
            return 0;

        byte b = data[offset++];

        // Mumble varint format:
        // 0xxxxxxx - 7-bit value (0-127)
        // 10xxxxxx + 1 byte - 14-bit value
        // 110xxxxx + 2 bytes - 21-bit value
        // 1110xxxx + 3 bytes - 28-bit value
        // 11110000 + 4 bytes - 32-bit value
        // 11110100 + 8 bytes - 64-bit value
        // 11111000 + varint - negative varint
        // 11111100 + 1 byte - inverted byte

        if ((b & 0x80) == 0)
        {
            // 0xxxxxxx - 7 bits
            return b & 0x7F;
        }
        else if ((b & 0xC0) == 0x80)
        {
            // 10xxxxxx - 14 bits
            if (offset >= data.Length) return 0;
            return ((b & 0x3F) << 8) | data[offset++];
        }
        else if ((b & 0xE0) == 0xC0)
        {
            // 110xxxxx - 21 bits
            if (offset + 1 >= data.Length) return 0;
            return ((b & 0x1F) << 16) | (data[offset++] << 8) | data[offset++];
        }
        else if ((b & 0xF0) == 0xE0)
        {
            // 1110xxxx - 28 bits
            if (offset + 2 >= data.Length) return 0;
            return ((b & 0x0F) << 24) | (data[offset++] << 16) | (data[offset++] << 8) | data[offset++];
        }
        else if ((b & 0xFC) == 0xF0)
        {
            // 11110000 - 32 bits
            if (offset + 3 >= data.Length) return 0;
            return (data[offset++] << 24) | (data[offset++] << 16) | (data[offset++] << 8) | data[offset++];
        }
        else if ((b & 0xFC) == 0xF4)
        {
            // 11110100 - 64 bits
            if (offset + 7 >= data.Length) return 0;
            long value = 0;
            for (int i = 0; i < 8; i++)
                value = (value << 8) | data[offset++];
            return value;
        }
        else if ((b & 0xFC) == 0xF8)
        {
            // 11111000 - negative varint
            return -ReadMumbleVarint(data, ref offset);
        }
        else if ((b & 0xFC) == 0xFC)
        {
            // 11111100 - inverted byte
            if (offset >= data.Length) return 0;
            return ~data[offset++];
        }

        return 0;
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
    public async Task<bool> JoinChannelAsync(string channelName)
    {
        if (!IsConnected || _sslStream == null)
        {
            StatusChanged?.Invoke("Cannot join channel: not connected");
            return false;
        }

        _currentChannel = channelName;
        
        // Find channel ID, create if it doesn't exist
        if (!_channels.TryGetValue(channelName, out uint channelId))
        {
            if (!_canCreateChannels)
            {
                StatusChanged?.Invoke($"Channel '{channelName}' not found and creation not permitted");
                return false;
            }

            StatusChanged?.Invoke($"Creating channel: {channelName}");
            await CreateChannelAsync(channelName);

            // Wait for server to announce the channel (short timeout)
            channelId = await WaitForChannelIdAsync(channelName, TimeSpan.FromSeconds(2));
            if (channelId == 0)
            {
                StatusChanged?.Invoke($"Failed to create channel '{channelName}'");
                return false;
            }
        }

        _currentChannelId = channelId;
        StatusChanged?.Invoke($"Joining channel: {channelName} (ID: {channelId})");

        // Send UserState message to join channel
        // UserState: session (uint32, field 1), channel_id (uint32, field 5)
        using var ms = new MemoryStream();
        using var cos = new CodedOutputStream(ms);
        
        // Field 1: session
        cos.WriteTag(1, WireFormat.WireType.Varint);
        cos.WriteUInt32(_sessionId);
        
        // Field 5: channel_id
        cos.WriteTag(5, WireFormat.WireType.Varint);
        cos.WriteUInt32(channelId);
        
        cos.Flush();
        var payload = ms.ToArray();

        // Send as UserState (type 9)
        var packet = new byte[6 + payload.Length];
        packet[0] = 0;
        packet[1] = 9; // UserState
        packet[2] = (byte)(payload.Length >> 24);
        packet[3] = (byte)(payload.Length >> 16);
        packet[4] = (byte)(payload.Length >> 8);
        packet[5] = (byte)payload.Length;
        Array.Copy(payload, 0, packet, 6, payload.Length);

        await _sslStream.WriteAsync(packet);
        await _sslStream.FlushAsync();
        
        StatusChanged?.Invoke($"Joined channel: {channelName}");
        return true;
    }

    /// <summary>
    /// Create a temporary channel on the Mumble server.
    /// </summary>
    private async Task CreateChannelAsync(string channelName)
    {
        if (_sslStream == null || !_canCreateChannels)
            return;

        if (!_pendingChannelCreates.ContainsKey(channelName))
        {
            _pendingChannelCreates[channelName] = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // ChannelState message to create a channel:
        // parent (uint32, field 2): parent channel ID (0 = root)
        // name (string, field 3): channel name
        // temporary (bool, field 8): true for temporary channel
        
        using var ms = new MemoryStream();
        using var cos = new CodedOutputStream(ms);
        
        // Field 2: parent (root = 0)
        cos.WriteTag(2, WireFormat.WireType.Varint);
        cos.WriteUInt32(0);
        
        // Field 3: name
        cos.WriteTag(3, WireFormat.WireType.LengthDelimited);
        cos.WriteString(channelName);
        
        // Field 8: temporary = true
        cos.WriteTag(8, WireFormat.WireType.Varint);
        cos.WriteBool(true);
        
        cos.Flush();
        var payload = ms.ToArray();

        // Send as ChannelState (type 7)
        var packet = new byte[6 + payload.Length];
        packet[0] = 0;
        packet[1] = 7; // ChannelState
        packet[2] = (byte)(payload.Length >> 24);
        packet[3] = (byte)(payload.Length >> 16);
        packet[4] = (byte)(payload.Length >> 8);
        packet[5] = (byte)payload.Length;
        Array.Copy(payload, 0, packet, 6, payload.Length);

        await _sslStream.WriteAsync(packet);
        await _sslStream.FlushAsync();
        
        StatusChanged?.Invoke($"Requested channel creation: {channelName}");
    }

    private async Task<uint> WaitForChannelIdAsync(string channelName, TimeSpan timeout)
    {
        if (_channels.TryGetValue(channelName, out var existing))
        {
            return existing;
        }

        if (!_pendingChannelCreates.TryGetValue(channelName, out var tcs))
        {
            tcs = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingChannelCreates[channelName] = tcs;
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        if (completed == tcs.Task)
        {
            return await tcs.Task;
        }

        return 0;
    }

    private static string TryReadReason(byte[] payload)
    {
        try
        {
            using var ms = new MemoryStream(payload);
            var cis = new CodedInputStream(ms);
            string reason = "";

            uint tag;
            while ((tag = cis.ReadTag()) != 0)
            {
                var fieldNumber = tag >> 3;
                if (fieldNumber == 2) // reason
                {
                    reason = cis.ReadString();
                }
                else
                {
                    cis.SkipLastField();
                }
            }

            return string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason;
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Join a channel by frequency ID.
    /// </summary>
    public Task<bool> JoinFrequencyAsync(int freqId)
    {
        // Use raw frequency number as channel name
        return JoinChannelAsync(freqId.ToString());
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

            // Prefer UDP if crypt is ready and UDP is active
            if (_udpClient != null && _cryptState.IsValid)
            {
                if (_cryptState.TryEncrypt(audioPacket, out var encrypted))
                {
                    _udpClient.Send(encrypted, encrypted.Length);
                    return;
                }
            }

            // Fallback: Send as UDPTunnel message (type 1)
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

    private sealed class CryptStateOcb2
    {
        private readonly AesEngine _aesEncrypt = new();
        private readonly AesEngine _aesDecrypt = new();
        private KeyParameter? _keyParam;

        private readonly byte[] _key = new byte[16];
        private readonly byte[] _encryptIv = new byte[16];
        private readonly byte[] _decryptIv = new byte[16];
        private bool _valid;

        public bool IsValid => _valid;

        public bool SetKey(byte[] key, byte[] clientNonce, byte[] serverNonce)
        {
            if (key.Length != 16 || clientNonce.Length != 16 || serverNonce.Length != 16)
            {
                return false;
            }

            Buffer.BlockCopy(key, 0, _key, 0, 16);
            Buffer.BlockCopy(clientNonce, 0, _encryptIv, 0, 16);
            Buffer.BlockCopy(serverNonce, 0, _decryptIv, 0, 16);

            _keyParam = new KeyParameter(_key);
            _aesEncrypt.Init(true, _keyParam);
            _aesDecrypt.Init(false, _keyParam);
            _valid = true;
            return true;
        }

        public bool TryEncrypt(byte[] plain, out byte[] encrypted)
        {
            encrypted = Array.Empty<byte>();
            if (!_valid)
            {
                return false;
            }

            IncrementIv(_encryptIv);

            var cipherText = new byte[plain.Length];
            var tag = new byte[16];
            OcbEncrypt(plain, cipherText, _encryptIv, tag);

            encrypted = new byte[cipherText.Length + 4];
            encrypted[0] = _encryptIv[0];
            encrypted[1] = tag[0];
            encrypted[2] = tag[1];
            encrypted[3] = tag[2];
            Buffer.BlockCopy(cipherText, 0, encrypted, 4, cipherText.Length);
            return true;
        }

        public bool TryDecrypt(byte[] encrypted, out byte[] plain)
        {
            plain = Array.Empty<byte>();
            if (!_valid || encrypted.Length < 5)
            {
                return false;
            }

            var saveIv = new byte[16];
            Buffer.BlockCopy(_decryptIv, 0, saveIv, 0, 16);

            byte ivByte = encrypted[0];
            if (!UpdateDecryptIv(ivByte))
            {
                return false;
            }

            var cipherText = new byte[encrypted.Length - 4];
            Buffer.BlockCopy(encrypted, 4, cipherText, 0, cipherText.Length);

            var tag = new byte[16];
            plain = new byte[cipherText.Length];
            OcbDecrypt(cipherText, plain, _decryptIv, tag);

            if (tag[0] != encrypted[1] || tag[1] != encrypted[2] || tag[2] != encrypted[3])
            {
                Buffer.BlockCopy(saveIv, 0, _decryptIv, 0, 16);
                return false;
            }

            return true;
        }

        private bool UpdateDecryptIv(byte ivByte)
        {
            var current = _decryptIv[0];
            if (ivByte == current)
            {
                return false;
            }

            if (ivByte > current)
            {
                _decryptIv[0] = ivByte;
                return true;
            }

            _decryptIv[0] = ivByte;
            for (int i = 1; i < 16; i++)
            {
                if (++_decryptIv[i] != 0)
                {
                    break;
                }
            }
            return true;
        }

        private void OcbEncrypt(byte[] plain, byte[] encrypted, byte[] nonce, byte[] tag)
        {
            var delta = AesEncryptBlock(nonce);
            var checksum = new byte[16];

            int offset = 0;
            int remaining = plain.Length;

            while (remaining > 16)
            {
                S2(delta);
                var tmp = Xor(delta, Slice(plain, offset, 16));
                tmp = AesEncryptBlock(tmp);
                var outBlock = Xor(delta, tmp);
                Buffer.BlockCopy(outBlock, 0, encrypted, offset, 16);
                XorInPlace(checksum, Slice(plain, offset, 16));

                offset += 16;
                remaining -= 16;
            }

            S2(delta);
            var tmp2 = new byte[16];
            tmp2[15] = (byte)(remaining * 8);
            XorInPlace(tmp2, delta);
            var pad = AesEncryptBlock(tmp2);

            var lastBlock = new byte[16];
            Buffer.BlockCopy(plain, offset, lastBlock, 0, remaining);
            Buffer.BlockCopy(pad, remaining, lastBlock, remaining, 16 - remaining);
            XorInPlace(checksum, lastBlock);

            var lastOut = Xor(lastBlock, pad);
            Buffer.BlockCopy(lastOut, 0, encrypted, offset, remaining);

            S3(delta);
            var tagInput = Xor(delta, checksum);
            var tagBlock = AesEncryptBlock(tagInput);
            Buffer.BlockCopy(tagBlock, 0, tag, 0, 16);
        }

        private void OcbDecrypt(byte[] encrypted, byte[] plain, byte[] nonce, byte[] tag)
        {
            var delta = AesEncryptBlock(nonce);
            var checksum = new byte[16];

            int offset = 0;
            int remaining = encrypted.Length;

            while (remaining > 16)
            {
                S2(delta);
                var tmp = Xor(delta, Slice(encrypted, offset, 16));
                tmp = AesDecryptBlock(tmp);
                var outBlock = Xor(delta, tmp);
                Buffer.BlockCopy(outBlock, 0, plain, offset, 16);
                XorInPlace(checksum, outBlock);

                offset += 16;
                remaining -= 16;
            }

            S2(delta);
            var tmp2 = new byte[16];
            tmp2[15] = (byte)(remaining * 8);
            XorInPlace(tmp2, delta);
            var pad = AesEncryptBlock(tmp2);

            var lastBlock = new byte[16];
            Buffer.BlockCopy(encrypted, offset, lastBlock, 0, remaining);
            Buffer.BlockCopy(pad, remaining, lastBlock, remaining, 16 - remaining);
            var outLast = Xor(lastBlock, pad);
            Buffer.BlockCopy(outLast, 0, plain, offset, remaining);
            XorInPlace(checksum, outLast);

            S3(delta);
            var tagInput = Xor(delta, checksum);
            var tagBlock = AesEncryptBlock(tagInput);
            Buffer.BlockCopy(tagBlock, 0, tag, 0, 16);
        }

        private byte[] AesEncryptBlock(byte[] input)
        {
            var output = new byte[16];
            _aesEncrypt.ProcessBlock(input, 0, output, 0);
            return output;
        }

        private byte[] AesDecryptBlock(byte[] input)
        {
            var output = new byte[16];
            _aesDecrypt.ProcessBlock(input, 0, output, 0);
            return output;
        }

        private static void IncrementIv(byte[] iv)
        {
            for (int i = 0; i < iv.Length; i++)
            {
                iv[i]++;
                if (iv[i] != 0)
                {
                    break;
                }
            }
        }

        private static byte[] Xor(byte[] a, byte[] b)
        {
            var result = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                result[i] = (byte)(a[i] ^ b[i]);
            }
            return result;
        }

        private static void XorInPlace(byte[] dst, byte[] src)
        {
            for (int i = 0; i < 16; i++)
            {
                dst[i] ^= src[i];
            }
        }

        private static void S2(byte[] block)
        {
            byte carry = 0;
            for (int i = 15; i >= 0; i--)
            {
                byte b = block[i];
                block[i] = (byte)((b << 1) | carry);
                carry = (byte)((b & 0x80) != 0 ? 1 : 0);
            }
            if (carry != 0)
            {
                block[15] ^= 0x87;
            }
        }

        private static void S3(byte[] block)
        {
            var tmp = new byte[16];
            Buffer.BlockCopy(block, 0, tmp, 0, 16);
            S2(tmp);
            XorInPlace(block, tmp);
        }

        private static byte[] Slice(byte[] data, int offset, int length)
        {
            var slice = new byte[16];
            Buffer.BlockCopy(data, offset, slice, 0, length);
            return slice;
        }
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

        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;
        _udpReady = false;

        _opusEncoder = null;
        _opusDecoder = null;
        
        // Stop audio playback
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _waveProvider = null;
        
        _userSessions.Clear();
        _channels.Clear();
        _pendingChannelCreates.Clear();
        _canCreateChannels = true;
        _currentChannelId = 0;

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
