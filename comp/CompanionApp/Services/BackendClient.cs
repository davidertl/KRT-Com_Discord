using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CompanionApp.Services;

public sealed class BackendClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _adminToken;

    public BackendClient(string baseUrl, string adminToken)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/")
        };
        _adminToken = adminToken;
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
        };
        using var resp = await _http.SendAsync(req);
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
        };
        using var resp = await _http.SendAsync(req);
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

    public void Dispose()
    {
        _http.Dispose();
    }
}
