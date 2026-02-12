namespace CompanionApp.Services;

/// <summary>
/// Represents the current state of the voice WebSocket connection.
/// </summary>
public enum VoiceConnectionState
{
    /// <summary>User is not connected, no reconnect scheduled.</summary>
    Disconnected,

    /// <summary>Initial connection attempt in progress.</summary>
    Connecting,

    /// <summary>WebSocket is open, heartbeat healthy.</summary>
    Connected,

    /// <summary>Unexpected drop detected, back-off timer running.</summary>
    Reconnecting,

    /// <summary>Max retries exhausted, manual action required.</summary>
    Failed
}
