using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompanionApp.Models;

namespace CompanionApp.Services;

public static class ConfigService
{
    public static string GetConfigFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var newFolder = Path.Combine(appData, "KRT-Com_Discord");
        var oldFolder = Path.Combine(appData, "das-KRT_com");

// Migrate from old config folder if it exists
        // Use try-move-catch to avoid TOCTOU race condition
        if (Directory.Exists(oldFolder))
        {
            try
            {
                Directory.Move(oldFolder, newFolder);
            }
            catch (IOException)
            {
                // Target already exists or move failed — safe to ignore
            }
        }

        return newFolder;
    }

    public static string GetConfigPath()
    {
        return Path.Combine(GetConfigFolder(), "config.json");
    }

    public static CompanionConfig Load()
    {
        var path = GetConfigPath();
        if (!File.Exists(path))
        {
            var cfg = new CompanionConfig();
            Save(cfg);
            return cfg;
        }

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var config = JsonSerializer.Deserialize<CompanionConfig>(json, options) ?? new CompanionConfig();

        // Decrypt AuthToken if it was stored encrypted (DPAPI)
        if (!string.IsNullOrEmpty(config.AuthToken))
        {
            config.AuthToken = UnprotectString(config.AuthToken);
        }

        // Decrypt GuildId if it was stored encrypted (DPAPI)
        if (!string.IsNullOrEmpty(config.GuildId))
        {
            config.GuildId = UnprotectString(config.GuildId);
        }

        return config;
    }

    public static void Save(CompanionConfig config)
    {
        var folder = GetConfigFolder();
        Directory.CreateDirectory(folder);

        // Encrypt AuthToken before saving (DPAPI — current-user scope)
        var configCopy = new CompanionConfig
        {
            RadioSlot = config.RadioSlot,
            SampleRate = config.SampleRate,
            GuildId = string.IsNullOrEmpty(config.GuildId) ? "" : ProtectString(config.GuildId),
            VoiceHost = config.VoiceHost,
            VoicePort = config.VoicePort,
            AuthToken = string.IsNullOrEmpty(config.AuthToken) ? "" : ProtectString(config.AuthToken),
            AcceptedPolicyVersion = config.AcceptedPolicyVersion,
            AutoConnect = config.AutoConnect,
            StartMinimized = config.StartMinimized,
            LaunchOnStartup = config.LaunchOnStartup,
            SaveRadioActiveState = config.SaveRadioActiveState,
            TurnOnEmergencyOnStartup = config.TurnOnEmergencyOnStartup,
            DebugLoggingEnabled = config.DebugLoggingEnabled,
            EnableEmergencyRadio = config.EnableEmergencyRadio,
            InputVolume = config.InputVolume,
            OutputVolume = config.OutputVolume,
            RadioStates = config.RadioStates,
            EmergencyRadioState = config.EmergencyRadioState,
            Bindings = config.Bindings,
            LoggedInDisplayName = config.LoggedInDisplayName,
            PlaySoundOnTransmit = config.PlaySoundOnTransmit,
            PlaySoundOnReceive = config.PlaySoundOnReceive,
            PlaySoundOnBegin = config.PlaySoundOnBegin,
            PlaySoundOnEnd = config.PlaySoundOnEnd,
            OverlayEnabled = config.OverlayEnabled,
            OverlayShowRank = config.OverlayShowRank,
            OverlayShowRadioKeybind = config.OverlayShowRadioKeybind,
            OverlayPositionX = config.OverlayPositionX,
            OverlayPositionY = config.OverlayPositionY,
            OverlayOpacity = config.OverlayOpacity,
            OverlayAutoHideEnabled = config.OverlayAutoHideEnabled,
            OverlayAutoHideSeconds = config.OverlayAutoHideSeconds,
            DuckingEnabled = config.DuckingEnabled,
            DuckingLevel = config.DuckingLevel,
            DuckOnSend = config.DuckOnSend,
            DuckOnReceive = config.DuckOnReceive,
            DuckingMode = config.DuckingMode,
            DuckedProcessNames = config.DuckedProcessNames,
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(configCopy, options);
        File.WriteAllText(GetConfigPath(), json);
    }

    // Application-specific entropy for DPAPI — prevents other same-user processes from decrypting
    private static readonly byte[] DpapiEntropy = "KRT-Com_Discord_v1_entropy"u8.ToArray();

    /// <summary>
    /// Encrypt a string using Windows DPAPI (current-user scope).
    /// Returns a base64 string of the encrypted bytes.
    /// </summary>
    private static string ProtectString(string plaintext)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var encrypted = ProtectedData.Protect(bytes, DpapiEntropy, DataProtectionScope.CurrentUser);
            CryptographicOperations.ZeroMemory(bytes);
            return "DPAPI:" + Convert.ToBase64String(encrypted);
        }
        catch
        {
            return ""; // Encryption failed (e.g. user profile issue) — return empty to avoid saving plaintext
        }
    }

    /// <summary>
    /// Decrypt a DPAPI-protected string. If the string is not DPAPI-encrypted,
    /// returns it as-is (for backward compatibility with existing configs).
    /// </summary>
    private static string UnprotectString(string protectedText)
    {
        if (!protectedText.StartsWith("DPAPI:"))
            return protectedText; // Not encrypted (legacy config) — will be auto-upgraded on next save

        try
        {
            var encrypted = Convert.FromBase64String(protectedText.Substring(6));
            byte[] decrypted;
            try
            {
                decrypted = ProtectedData.Unprotect(encrypted, DpapiEntropy, DataProtectionScope.CurrentUser);
            }
            catch
            {
                // Fallback: try without entropy for configs encrypted before this update
                decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            }
            var result = Encoding.UTF8.GetString(decrypted);
            CryptographicOperations.ZeroMemory(decrypted);
            return result;
        }
        catch
        {
            return ""; // Corrupted or wrong user — return empty (will re-auth)
        }
    }
}
