using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
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

public sealed class BackendFetchResult<T>
{
    public T? Data { get; set; }
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
    /// Fetch server status from a base URL.
    /// </summary>
    public static async Task<BackendFetchResult<ServerStatusInfo>> GetServerStatusAsync(string baseUrl)
    {
        var result = new BackendFetchResult<ServerStatusInfo>();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var url = baseUrl.TrimEnd('/') + "/server-status";
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                result.Error = $"Server returned HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}).";
                return result;
            }

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                result.Error = "Server response did not include the expected data payload.";
                return result;
            }

            result.Data = new ServerStatusInfo
            {
                Version = data.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                DsgvoEnabled = data.TryGetProperty("dsgvoEnabled", out var d) && d.GetBoolean(),
                DebugMode = data.TryGetProperty("debugMode", out var dm) && dm.GetBoolean(),
                RetentionDays = data.TryGetProperty("retentionDays", out var r) ? r.GetInt32() : 0,
                PolicyVersion = data.TryGetProperty("policyVersion", out var pv) ? pv.GetString() ?? "1.0" : "1.0",
                OauthEnabled = data.TryGetProperty("oauthEnabled", out var oa) && oa.GetBoolean(),
            };
            return result;
        }
        catch (TaskCanceledException)
        {
            result.Error = "Connection timed out. Check host, port, and firewall.";
            return result;
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            result.Error = "TLS certificate validation failed. Use your domain with a valid certificate (usually port 443).";
            return result;
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.HostNotFound)
        {
            result.Error = "DNS lookup failed. Check the domain name.";
            return result;
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused)
        {
            result.Error = "Connection refused. The service is not reachable on this port.";
            return result;
        }
        catch (HttpRequestException ex)
        {
            result.Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "Network request failed."
                : $"Network request failed: {ex.Message}";
            return result;
        }
        catch
        {
            result.Error = "Unexpected error while contacting server.";
            return result;
        }
    }

    /// <summary>
    /// Fetch privacy policy from a base URL.
    /// </summary>
    public static async Task<BackendFetchResult<PrivacyPolicyInfo>> GetPrivacyPolicyAsync(string baseUrl)
    {
        var result = new BackendFetchResult<PrivacyPolicyInfo>();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var url = baseUrl.TrimEnd('/') + "/privacy-policy";
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                result.Error = $"Privacy policy request returned HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}).";
                return result;
            }

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                result.Error = "Server response did not include privacy policy data.";
                return result;
            }

            result.Data = new PrivacyPolicyInfo
            {
                Version = data.TryGetProperty("version", out var v) ? v.GetString() ?? "1.0" : "1.0",
                Text = data.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
            };
            return result;
        }
        catch (TaskCanceledException)
        {
            result.Error = "Privacy policy request timed out.";
            return result;
        }
        catch (HttpRequestException ex)
        {
            result.Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "Privacy policy request failed."
                : $"Privacy policy request failed: {ex.Message}";
            return result;
        }
        catch
        {
            result.Error = "Unexpected error while fetching privacy policy.";
            return result;
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
    public async Task<int> SendTxEventAsync(int freqId, string action, int radioSlot)
    {
        var payload = new
        {
            freqId,
            action,
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

        // TX event uses Bearer token only — admin token not needed on this endpoint
        if (!string.IsNullOrWhiteSpace(_authToken))
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
    public async Task<int> JoinFrequencyAsync(int freqId, int radioSlot)
    {
        var payload = new { freqId, radioSlot };
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
    public async Task<int> LeaveFrequencyAsync(int freqId)
    {
        var payload = new { freqId };
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
