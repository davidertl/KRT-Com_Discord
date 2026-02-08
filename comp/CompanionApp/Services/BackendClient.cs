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

    public async Task SendTxEventAsync(int freqId, string action, string discordUserId, int radioSlot)
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
