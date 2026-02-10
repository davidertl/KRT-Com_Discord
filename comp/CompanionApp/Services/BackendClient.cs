using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CompanionApp.Services;

/// <summary>
/// Server status information returned by GET /server-status.
/// </summary>
public sealed class ServerStatusInfo
{
    public string Version { get; set; } = "";
    public bool DsgvoEnabled { get; set; }
    public bool DebugMode { get; set; }
    public int RetentionDays { get; set; }
    public string PolicyVersion { get; set; } = "1.0";
    public bool OauthEnabled { get; set; }
}

/// <summary>
/// Privacy policy data returned by GET /privacy-policy.
/// </summary>
public sealed class PrivacyPolicyInfo
{
    public string Version { get; set; } = "1.0";
    public string Text { get; set; } = "";
}

/// <summary>
/// Login response from POST /auth/login.
/// </summary>
public sealed class LoginResult
{
    public string Token { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PolicyVersion { get; set; } = "1.0";
    public bool PolicyAccepted { get; set; }
}

/// <summary>
/// OAuth2 poll result from GET /auth/discord/poll.
/// </summary>
public sealed class OAuthPollResult
{
    public string Status { get; set; } = ""; // "pending", "success", "error", "unknown"
    public string? Token { get; set; }
    public string? DisplayName { get; set; }
    public string? PolicyVersion { get; set; }
    public bool PolicyAccepted { get; set; }
    public string? Error { get; set; }
}

public sealed class BackendClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _adminToken;
    private string _authToken = "";

    public BackendClient(string baseUrl, string adminToken)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _adminToken = adminToken;
    }

    /// <summary>
    /// Set the Bearer auth token (from login).
    /// </summary>
    public void SetAuthToken(string token)
    {
        _authToken = token ?? "";
    }

    // --- Static server verification methods (no instance needed) ---

    /// <summary>
    /// Fetch server status from a base URL. Returns null on failure.
    /// </summary>
    public static async Task<ServerStatusInfo?> GetServerStatusAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var url = baseUrl.TrimEnd('/') + "/server-status";
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            return new ServerStatusInfo
            {
                Version = data.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                DsgvoEnabled = data.TryGetProperty("dsgvoEnabled", out var d) && d.GetBoolean(),
                DebugMode = data.TryGetProperty("debugMode", out var dm) && dm.GetBoolean(),
                RetentionDays = data.TryGetProperty("retentionDays", out var r) ? r.GetInt32() : 0,
                PolicyVersion = data.TryGetProperty("policyVersion", out var pv) ? pv.GetString() ?? "1.0" : "1.0",
                OauthEnabled = data.TryGetProperty("oauthEnabled", out var oa) && oa.GetBoolean(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetch privacy policy from a base URL. Returns null on failure.
    /// </summary>
    public static async Task<PrivacyPolicyInfo?> GetPrivacyPolicyAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var url = baseUrl.TrimEnd('/') + "/privacy-policy";
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            return new PrivacyPolicyInfo
            {
                Version = data.TryGetProperty("version", out var v) ? v.GetString() ?? "1.0" : "1.0",
                Text = data.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
            };
        }
        catch
        {
            return null;
        }
    }

    // --- Instance methods ---

    /// <summary>
    /// Poll for OAuth2 login result. Returns OAuthPollResult or null on failure.
    /// </summary>
    public static async Task<OAuthPollResult?> PollOAuthTokenAsync(string baseUrl, string state)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var url = baseUrl.TrimEnd('/') + "/auth/discord/poll?state=" + Uri.EscapeDataString(state);
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            return new OAuthPollResult
            {
                Status = data.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
                Token = data.TryGetProperty("token", out var tk) ? tk.GetString() : null,
                DisplayName = data.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                PolicyVersion = data.TryGetProperty("policyVersion", out var pv) ? pv.GetString() : null,
                PolicyAccepted = data.TryGetProperty("policyAccepted", out var pa) && pa.GetBoolean(),
                Error = data.TryGetProperty("error", out var err) ? err.GetString() : null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Login with discordUserId and guildId. Returns LoginResult or null on failure.
    /// </summary>
    public async Task<LoginResult?> LoginAsync(string discordUserId, string guildId)
    {
        try
        {
            var payload = new { discordUserId, guildId };
            var json = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, "auth/login")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            var result = new LoginResult
            {
                Token = data.TryGetProperty("token", out var tk) ? tk.GetString() ?? "" : "",
                DisplayName = data.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                PolicyVersion = data.TryGetProperty("policyVersion", out var pv) ? pv.GetString() ?? "1.0" : "1.0",
                PolicyAccepted = data.TryGetProperty("policyAccepted", out var pa) && pa.GetBoolean(),
            };

            if (!string.IsNullOrEmpty(result.Token))
            {
                _authToken = result.Token;
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Accept the privacy policy. Requires a valid auth token.
    /// </summary>
    public async Task<bool> AcceptPolicyAsync(string policyVersion)
    {
        try
        {
            var payload = new { version = policyVersion };
            var json = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, "auth/accept-policy")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(_authToken))
            {
                req.Headers.Add("Authorization", $"Bearer {_authToken}");
            }
            using var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Notify backend of TX start/stop. Returns listener count for the frequency, or -1 on failure.
    /// </summary>
    public async Task<int> SendTxEventAsync(int freqId, string action, string discordUserId, int radioSlot)
    {
        var payload = new
        {
            freqId,
            action,
            discordUserId,
            radioSlot,
            meta = new
            {
                source = "companion",
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, "tx/event")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_adminToken))
        {
            req.Headers.Add("x-admin-token", _adminToken);
        }        if (!string.IsNullOrWhiteSpace(_authToken))
        {
            req.Headers.Add("Authorization", $"Bearer {_authToken}");
        }
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        return await ParseListenerCount(resp);
    }

    /// <summary>
    /// Register as listener on a frequency. Returns listener count, or -1 on failure.
    /// </summary>
    public async Task<int> JoinFrequencyAsync(string discordUserId, int freqId, int radioSlot)
    {
        var payload = new { discordUserId, freqId, radioSlot };
        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, "freq/join")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };        if (!string.IsNullOrWhiteSpace(_authToken))
        {
            req.Headers.Add("Authorization", $"Bearer {_authToken}");
        }        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return -1;
        return await ParseListenerCount(resp);
    }

    /// <summary>
    /// Unregister as listener on a frequency. Returns listener count, or -1 on failure.
    /// </summary>
    public async Task<int> LeaveFrequencyAsync(string discordUserId, int freqId)
    {
        var payload = new { discordUserId, freqId };
        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, "freq/leave")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };        if (!string.IsNullOrWhiteSpace(_authToken))
        {
            req.Headers.Add("Authorization", $"Bearer {_authToken}");
        }        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return -1;
        return await ParseListenerCount(resp);
    }

    private static async Task<int> ParseListenerCount(HttpResponseMessage resp)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("listener_count", out var lc))
                return lc.GetInt32();
        }
        catch { }
        return -1;
    }

    public async Task SetFreqNameAsync(int freqId, string name)
    {
        var payload = new
        {
            freqId,
            name
        };

        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, "freq/name")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_adminToken))
        {
            req.Headers.Add("x-admin-token", _adminToken);
        }

        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Get frequency → Discord channel name mappings from the server.
    /// Returns a dictionary of freqId → channelName.
    /// </summary>
    public async Task<Dictionary<int, string>> GetFreqNamesAsync()
    {
        try
        {
            using var resp = await _http.GetAsync("freq/names");
            if (!resp.IsSuccessStatusCode) return new Dictionary<int, string>();

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var result = new Dictionary<int, string>();

            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in data.EnumerateObject())
                {
                    if (int.TryParse(prop.Name, out int freqId))
                    {
                        result[freqId] = prop.Value.GetString() ?? "";
                    }
                }
            }

            return result;
        }
        catch
        {
            return new Dictionary<int, string>();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
