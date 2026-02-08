using System;
using System.IO;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace CompanionApp.Services;

/// <summary>
/// Mumble protocol message types
/// </summary>
public enum MumbleMessageType : ushort
{
    Version = 0,
    UDPTunnel = 1,
    Authenticate = 2,
    Ping = 3,
    Reject = 4,
    ServerSync = 5,
    ChannelRemove = 6,
    ChannelState = 7,
    UserRemove = 8,
    UserState = 9,
    BanList = 10,
    TextMessage = 11,
    PermissionDenied = 12,
    ACL = 13,
    QueryUsers = 14,
    CryptSetup = 15,
    ContextActionModify = 16,
    ContextAction = 17,
    UserList = 18,
    VoiceTarget = 19,
    PermissionQuery = 20,
    CodecVersion = 21,
    UserStats = 22,
    RequestBlob = 23,
    ServerConfig = 24,
    SuggestConfig = 25,
    PluginDataTransmission = 26
}

/// <summary>
/// Mumble Version message
/// </summary>
public class MumbleVersion : IMessage<MumbleVersion>
{
    public uint Version_ { get; set; }
    public string Release { get; set; } = "";
    public string Os { get; set; } = "";
    public string OsVersion { get; set; } = "";

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize()
    {
        int size = 0;
        if (Version_ != 0) size += 1 + 4;
        if (!string.IsNullOrEmpty(Release)) size += 1 + Encoding.UTF8.GetByteCount(Release) + 1;
        if (!string.IsNullOrEmpty(Os)) size += 1 + Encoding.UTF8.GetByteCount(Os) + 1;
        if (!string.IsNullOrEmpty(OsVersion)) size += 1 + Encoding.UTF8.GetByteCount(OsVersion) + 1;
        return size;
    }

    public MumbleVersion Clone() => new MumbleVersion { Version_ = Version_, Release = Release, Os = Os, OsVersion = OsVersion };

    public bool Equals(MumbleVersion? other) => other != null && Version_ == other.Version_;

    public void MergeFrom(MumbleVersion message)
    {
        Version_ = message.Version_;
        Release = message.Release;
        Os = message.Os;
        OsVersion = message.OsVersion;
    }

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 8: Version_ = input.ReadUInt32(); break;
                case 18: Release = input.ReadString(); break;
                case 26: Os = input.ReadString(); break;
                case 34: OsVersion = input.ReadString(); break;
                default: input.SkipLastField(); break;
            }
        }
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (Version_ != 0)
        {
            output.WriteRawTag(8);
            output.WriteUInt32(Version_);
        }
        if (!string.IsNullOrEmpty(Release))
        {
            output.WriteRawTag(18);
            output.WriteString(Release);
        }
        if (!string.IsNullOrEmpty(Os))
        {
            output.WriteRawTag(26);
            output.WriteString(Os);
        }
        if (!string.IsNullOrEmpty(OsVersion))
        {
            output.WriteRawTag(34);
            output.WriteString(OsVersion);
        }
    }
}

/// <summary>
/// Mumble Authenticate message
/// </summary>
public class MumbleAuthenticate : IMessage<MumbleAuthenticate>
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public List<string> Tokens { get; } = new();
    public List<int> CeltVersions { get; } = new();
    public bool Opus { get; set; } = true;

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize()
    {
        int size = 0;
        if (!string.IsNullOrEmpty(Username)) size += 1 + CodedOutputStream.ComputeStringSize(Username);
        if (!string.IsNullOrEmpty(Password)) size += 1 + CodedOutputStream.ComputeStringSize(Password);
        size += 1 + 1; // opus bool
        return size;
    }

    public MumbleAuthenticate Clone() => new MumbleAuthenticate { Username = Username, Password = Password, Opus = Opus };

    public bool Equals(MumbleAuthenticate? other) => other != null && Username == other.Username;

    public void MergeFrom(MumbleAuthenticate message)
    {
        Username = message.Username;
        Password = message.Password;
        Opus = message.Opus;
    }

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 10: Username = input.ReadString(); break;
                case 18: Password = input.ReadString(); break;
                case 40: Opus = input.ReadBool(); break;
                default: input.SkipLastField(); break;
            }
        }
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (!string.IsNullOrEmpty(Username))
        {
            output.WriteRawTag(10);
            output.WriteString(Username);
        }
        if (!string.IsNullOrEmpty(Password))
        {
            output.WriteRawTag(18);
            output.WriteString(Password);
        }
        output.WriteRawTag(40);
        output.WriteBool(Opus);
    }
}

/// <summary>
/// Mumble Ping message
/// </summary>
public class MumblePing : IMessage<MumblePing>
{
    public ulong Timestamp { get; set; }

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize() => Timestamp != 0 ? 1 + 8 : 0;

    public MumblePing Clone() => new MumblePing { Timestamp = Timestamp };

    public bool Equals(MumblePing? other) => other != null && Timestamp == other.Timestamp;

    public void MergeFrom(MumblePing message) => Timestamp = message.Timestamp;

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == 8) Timestamp = input.ReadUInt64();
            else input.SkipLastField();
        }
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (Timestamp != 0)
        {
            output.WriteRawTag(8);
            output.WriteUInt64(Timestamp);
        }
    }
}

/// <summary>
/// Helper class for encoding/decoding Mumble protocol messages
/// </summary>
public static class MumbleProtocolHelper
{
    /// <summary>
    /// Encode a protobuf message with Mumble header (type + length)
    /// </summary>
    public static byte[] EncodeMessage<T>(MumbleMessageType type, T message) where T : IMessage
    {
        using var ms = new MemoryStream();
        message.WriteTo(ms);
        var payload = ms.ToArray();

        var result = new byte[6 + payload.Length];
        // Type (2 bytes, big endian)
        result[0] = (byte)((ushort)type >> 8);
        result[1] = (byte)type;
        // Length (4 bytes, big endian)
        result[2] = (byte)(payload.Length >> 24);
        result[3] = (byte)(payload.Length >> 16);
        result[4] = (byte)(payload.Length >> 8);
        result[5] = (byte)payload.Length;
        // Payload
        Array.Copy(payload, 0, result, 6, payload.Length);

        return result;
    }

    /// <summary>
    /// Decode Mumble header from stream
    /// </summary>
    public static (MumbleMessageType type, int length) DecodeHeader(byte[] header)
    {
        var type = (MumbleMessageType)((header[0] << 8) | header[1]);
        var length = (header[2] << 24) | (header[3] << 16) | (header[4] << 8) | header[5];
        return (type, length);
    }

    /// <summary>
    /// Create Mumble version number (major.minor.patch encoded as uint32)
    /// </summary>
    public static uint MakeVersion(int major, int minor, int patch)
    {
        return (uint)((major << 16) | (minor << 8) | patch);
    }
}
