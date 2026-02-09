# Architektur (Entwurf)

## Komponenten
- Discord Bot
- Client (Windows) – WPF Companion App
- Audio-Relay-Server (Debian) – Custom Voice Relay

## Aufgabenverteilung
### Client
- Audioaufnahme (NAudio WasapiCapture)
- Hotkey-Erkennung
- Opus Encode/Decode (Concentus)
- WebSocket-Verbindung zum Voice Relay (Steuerung: auth, join/leave freq, heartbeat)
- UDP-Verbindung zum Voice Relay (Opus-Audio-Pakete)

### Discord Bot
- Frequenz-Management
- User-Status
- User-Directory (discord_users)
- Signalisierung (WS broadcast)

### Voice Relay Server
- WebSocket Control Plane (/voice Endpoint)
  - Authentifizierung (discordUserId + guildId gegen discord_users)
  - Frequenz-Subscriptions (join/leave)
  - Heartbeat/Keepalive
- UDP Audio Plane
  - Empfang von Opus-Paketen: [4B freqId][4B seq][opus data]
  - Forward an alle Subscriber derselben Frequenz (außer Sender)
  - UDP Handshake (freqId=0 + session token)
- Session-Management (voice_sessions Tabelle)
