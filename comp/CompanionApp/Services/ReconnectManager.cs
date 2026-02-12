using System;
using System.Threading;
using System.Threading.Tasks;

namespace CompanionApp.Services;

/// <summary>
/// Orchestrates automatic reconnection of the voice WebSocket with
/// exponential back-off and jitter. Separated from VoiceService so
/// the transport layer stays simple.
/// </summary>
public sealed class ReconnectManager : IDisposable
{
    // ---- Configuration ----
    private const int InitialDelayMs = 1_000;    // 1 s
    private const int MaxDelayMs     = 30_000;   // 30 s cap
    private const double BackoffFactor = 2.0;
    private const double JitterFactor  = 0.3;    // ±30 % randomness
    private const int MaxRetries = 10;

    // ---- State ----
    private int _attempt;
    private CancellationTokenSource? _cts;
    private readonly VoiceService _voiceService;

    // ---- Connection parameters (cached from last connect call) ----
    private string _host = "";
    private int _wsPort;
    private string _discordUserId = "";
    private string _guildId = "";
    private string _authToken = "";

    // ---- Public surface ----
    public VoiceConnectionState State { get; private set; } = VoiceConnectionState.Disconnected;

    /// <summary>Raised whenever the state machine transitions.</summary>
    public event Action<VoiceConnectionState>? StateChanged;

    /// <summary>Raised with human-readable log messages (for debug log).</summary>
    public event Action<string>? Log;

    /// <summary>
    /// Raised after a successful reconnect so the host can re-join
    /// frequencies, push mute states, etc.
    /// </summary>
    public event Func<Task>? Reconnected;

    public ReconnectManager(VoiceService voiceService)
    {
        _voiceService = voiceService;
        _voiceService.UnexpectedDisconnect += HandleUnexpectedDisconnect;
        _voiceService.Connected += HandleConnected;
    }

    /// <summary>
    /// Cache the connection parameters so we can reconnect without
    /// the caller having to pass them again.
    /// </summary>
    public void SetConnectionParams(string host, int wsPort, string discordUserId, string guildId, string authToken)
    {
        _host = host;
        _wsPort = wsPort;
        _discordUserId = discordUserId;
        _guildId = guildId;
        _authToken = authToken;
    }

    /// <summary>
    /// User-initiated connect — wraps VoiceService.ConnectAsync and
    /// transitions through Connecting → Connected / Reconnecting.
    /// </summary>
    public async Task<bool> ConnectAsync()
    {
        CancelPendingAttempt();
        TransitionTo(VoiceConnectionState.Connecting);

        bool ok = await _voiceService.ConnectAsync(_host, _wsPort, _discordUserId, _guildId, _authToken);
        if (!ok)
        {
            TransitionTo(VoiceConnectionState.Disconnected);
        }
        // If ok, HandleConnected fires via the Connected event
        return ok;
    }

    /// <summary>
    /// User-initiated disconnect — suppresses auto-reconnect.
    /// </summary>
    public async Task DisconnectAsync()
    {
        CancelPendingAttempt();
        TransitionTo(VoiceConnectionState.Disconnected);
        await _voiceService.DisconnectAsync();
    }

    // ---- Event handlers from VoiceService ----

    private void HandleUnexpectedDisconnect()
    {
        // Don't reconnect if user deliberately disconnected
        if (State == VoiceConnectionState.Disconnected) return;

        TransitionTo(VoiceConnectionState.Reconnecting);
        _attempt = 0;
        ScheduleNextAttempt();
    }

    private void HandleConnected()
    {
        bool wasReconnecting = State == VoiceConnectionState.Reconnecting;
        _attempt = 0;
        CancelPendingAttempt();
        TransitionTo(VoiceConnectionState.Connected);

        // If this was a reconnect (not the initial connect),
        // notify the host to re-join frequencies etc.
        if (wasReconnecting)
        {
            _ = RaiseReconnectedAsync();
        }
    }

    // ---- Reconnect scheduling ----

    private async void ScheduleNextAttempt()
    {
        if (_attempt >= MaxRetries)
        {
            Log?.Invoke($"Reconnect failed after {MaxRetries} attempts.");
            TransitionTo(VoiceConnectionState.Failed);
            return;
        }

        int baseDelay = (int)Math.Min(InitialDelayMs * Math.Pow(BackoffFactor, _attempt), MaxDelayMs);
        int jitter    = (int)(baseDelay * JitterFactor * (Random.Shared.NextDouble() * 2 - 1));
        int delay     = Math.Max(500, baseDelay + jitter);

        Log?.Invoke($"Reconnect attempt {_attempt + 1}/{MaxRetries} in {delay} ms …");

        _cts = new CancellationTokenSource();
        try
        {
            await Task.Delay(delay, _cts.Token);
            _attempt++;

            // Clean up old connection resources before reconnecting
            _voiceService.CleanupForReconnect();

            bool ok = await _voiceService.ConnectAsync(_host, _wsPort, _discordUserId, _guildId, _authToken);
            if (!ok)
            {
                ScheduleNextAttempt();
            }
            // If ok → HandleConnected fires via event
        }
        catch (TaskCanceledException)
        {
            // Reconnect was cancelled (intentional disconnect or app shutdown)
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Reconnect attempt error: {ex.Message}");
            ScheduleNextAttempt();
        }
    }

    private void CancelPendingAttempt()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void TransitionTo(VoiceConnectionState newState)
    {
        if (State == newState) return;
        Log?.Invoke($"Voice connection: {State} → {newState}");
        State = newState;
        StateChanged?.Invoke(newState);
    }

    private async Task RaiseReconnectedAsync()
    {
        try
        {
            if (Reconnected != null)
                await Reconnected.Invoke();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Post-reconnect handler error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        CancelPendingAttempt();
        _voiceService.UnexpectedDisconnect -= HandleUnexpectedDisconnect;
        _voiceService.Connected -= HandleConnected;
    }
}
