# Architektur (Entwurf)

## Komponenten
- Discord Bot
- Client (Windows)
- Optional: Audio-Relay-Server (Debian)

## Aufgabenverteilung
### Client
- Audioaufnahme
- Hotkey-Erkennung
- Audio-Streaming zur Ziel-Frequenz

### Discord Bot
- Frequenz-Management
- User-Status
- Berechtigungen / ACL
- Signalisierung

### Relay-Server (optional)
- Audio-Mixing
- Half-Duplex Enforcement
- Skalierung
