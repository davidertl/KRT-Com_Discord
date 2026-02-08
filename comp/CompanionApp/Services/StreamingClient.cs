using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CompanionApp.Services;

public sealed class StreamingClient : IDisposable
{
    private ClientWebSocket? _ws;
    private Channel<byte[]>? _audioQueue;
    private CancellationTokenSource? _cts;
    private Task? _sendLoop;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task StartAsync(Uri wsUri, object helloPayload, string? adminToken, CancellationToken externalToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = _cts.Token;

        _ws = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(adminToken))
        {
            _ws.Options.SetRequestHeader("x-admin-token", adminToken);
        }

        await _ws.ConnectAsync(wsUri, token);

        var helloJson = JsonSerializer.Serialize(helloPayload);
        var helloBytes = Encoding.UTF8.GetBytes(helloJson);
        await _ws.SendAsync(helloBytes, WebSocketMessageType.Text, true, token);

        _audioQueue = Channel.CreateUnbounded<byte[]>();
        _sendLoop = Task.Run(() => SendLoopAsync(token), token);
    }

    public bool EnqueueAudio(byte[] data)
    {
        if (_audioQueue == null)
        {
            return false;
        }

        return _audioQueue.Writer.TryWrite(data);
    }

    public async Task StopAsync()
    {
        if (_cts == null)
        {
            return;
        }

        _cts.Cancel();

        if (_audioQueue != null)
        {
            _audioQueue.Writer.TryComplete();
        }

        if (_sendLoop != null)
        {
            try { await _sendLoop; } catch { }
        }

        if (_ws != null && _ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "ptt_stop", CancellationToken.None); } catch { }
        }

        _ws?.Dispose();
        _ws = null;
        _audioQueue = null;
        _cts.Dispose();
        _cts = null;
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        if (_ws == null || _audioQueue == null)
        {
            return;
        }

        var reader = _audioQueue.Reader;
        while (await reader.WaitToReadAsync(token))
        {
            while (reader.TryRead(out var data))
            {
                if (_ws.State != WebSocketState.Open)
                {
                    return;
                }

                await _ws.SendAsync(data, WebSocketMessageType.Binary, true, token);
            }
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
