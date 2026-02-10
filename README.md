# das-krt.com – Funkkommunikation für Discord

KRT-Com ist eine funkähnliche Kommunikationslösung für Discord, angelehnt an klassische TeamSpeak-Funkplugins. User kommunizieren parallel auf mehreren Frequenzen, ohne den Voice-Channel zu wechseln – mit realistischer Half-Duplex-Funklogik statt klassischem Voice-Chat.

**Status:** Alpha 0.0.5  

Die Idee basiert auf einem TS3 Plugin, leider ist dieses auf Github nicht mehr verfügbar. Da das Plugin mit Teamspeak wirklich fantastisch funktioniert hat, wollte ich eine ähnliche Lösung für Discord schaffen. Das Projekt ist komplett Open Source und wird von mir in meiner Freizeit entwickelt. Es ist kostenlos, frei verfügbar und soll es auch bleiben.

---

## Inhaltsverzeichnis

- [Features](#features)
- [Architektur](#architektur)
- [Voraussetzungen](#voraussetzungen)
- [Discord OAuth2 Setup](#discord-oauth2-setup)
- [Installation & Setup](#installation--setup)
- [Admin-CLI (service.sh)](#admin-cli-servicesh)
- [Sicherheit](#sicherheit)
- [Datenbank](#datenbank)
- [Roadmap](#roadmap)

---

## Features

### Backend (Node.js 24)

| Bereich | Feature |
|---|---|
| **Discord-Integration** | Bot (read-only), Voice-State-Erkennung (`voiceStateUpdate`), Guild-Member-Sync, Channel-Sync (24h-Intervall) |
| **Funk-Logik** | Push-to-Talk Events (start/stop), Opus-Audio-Relay via WebSocket, Half-Duplex, Persistente TX-History |
| **Authentifizierung** | Discord OAuth2 Login (Authorization Code Flow), HMAC-SHA256 Token-Auth (24h Expiry), Debug-Login gated |
| **Datenschutz** | DSGVO-Compliance-Modul (Auto-Cleanup 2/7 Tage), User-ID-Hashing (HMAC-SHA256), Privacy Policy Endpoint |
| **Administration** | Ban-Management (REST + CLI), Admin-CLI mit interaktivem Menü (64+ Funktionen), Server-Status Endpoint |
| **Infrastruktur** | SQLite WAL, Systemd-Service, idempotentes `install.sh`, Traefik Reverse Proxy (Let's Encrypt) |

### Companion App (.NET 10, WPF)

| Bereich | Feature |
|---|---|
| **Funk-UI** | Multi-Radio (bis zu 4 Frequenzen + Notfall-Funk), Listener-Count pro Frequenz, Channel-Namen-Display |
| **Audio** | Push-to-Talk (systemweite Hotkeys), Opus Capture & Playback (NAudio), TX/RX Beep-Sounds mit Volume-Kontrolle |
| **Auth & Verbindung** | "Login with Discord" (Browser-Flow), Auto-Reconnect mit gespeichertem Token, Server-Verifizierung (Verify-Button) |
| **Compliance** | Datenschutz-Einwilligung (Privacy Policy Anzeige + Akzeptanz), Server-seitiges Mute/Unmute |
| **Konfiguration** | Einstellungen in `%APPDATA%/das-KRT_com/config.json`, Admin-Token nur im Debug-Modus sichtbar |

---

## Architektur

```
[Companion App (.NET 10 / WPF)]
   ├─ Hotkeys, Audio (NAudio/Opus), Auth, UI
   └─ HTTPS / WSS (TLS)
            │
            ▼
[Traefik Reverse Proxy]
   ├─ TLS Termination (Let's Encrypt / ACME)
   ├─ HTTP → HTTPS Redirect
   └─ Proxy → http://127.0.0.1:3000
            │
            ▼
[das-krt Backend – Node.js 24]
   ├─ REST API (Public + Auth + Admin)
   ├─ Voice WebSocket (Opus Relay)
   ├─ State WebSocket Hub
   ├─ Discord Bot (read-only)
   ├─ SQLite WAL (8 Tabellen)
   ├─ DSGVO Module (Auto-Cleanup)
   └─ Crypto Module (HMAC-SHA256)
```

### Push-to-Talk Ablauf

1. **PTT-Taste** → Companion App erkennt systemweiten Hotkey.
2. **Audio-Capture** startet, Opus-Pakete werden über Voice-WebSocket gestreamt.
3. **TX-Event** wird an Backend gemeldet → Realtime-Broadcast an alle Listener auf der Frequenz.
4. **Half-Duplex** wird clientseitig erzwungen (kein gleichzeitiges Senden und Empfangen).

### Identität & Anzeigenamen

- **Verifizierte Identität**: Über Discord OAuth2 (kein Self-Reporting möglich).
- **Mitgliedschaft**: Guild-Mitgliedschaft wird serverseitig via Discord Bot geprüft.
- **Anzeigename**: Verwendet den **Server-Nickname** (nicht global_name).
- **Synchronisation**: Namensänderungen werden bei Voice-Events oder alle 24h synchronisiert. Es findet keine Historisierung statt; nur der aktuellste Name wird gespeichert.

---

## Voraussetzungen

### Server
- **OS**: Debian/Ubuntu (mit Systemd)
- **Runtime**: Node.js 24
- **Domain**: Öffentliche Domain mit DNS A-Record auf den Server
- **Ports**: 80 (Redirect), 443 (HTTPS/WSS)
- **Infrastruktur**: Traefik v3 (wird automatisch via `install.sh` eingerichtet)

### Companion App
- **OS**: Windows 10/11
- **Runtime**: .NET 10
- **Hardware**: WASAPI-kompatible Soundkarte

---

## Discord OAuth2 Setup

1. **Application erstellen**: Unter [discord.com/developers/applications](https://discord.com/developers/applications).
2. **Bot einrichten**: "Guild Members Intent" aktivieren (notwendig für Namens-Sync).
3. **OAuth2 Konfiguration**:
   - **Client ID + Client Secret** notieren.
   - **Redirect URL** hinzufügen: `https://<DOMAIN>/auth/discord/callback`.
   - **Scopes**: `identify`, `guilds`.

### OAuth2 Flow

```
Companion App                    Server                      Discord
     │                             │                            │
     ├─ GET /auth/discord/redirect ─►                           │
     │  (mit state param)          │                            │
     │                             ├─ 302 Redirect ────────────►│
     │                             │  (authorize URL)           │
     │  ◄── Browser öffnet ────────┤                            │
     │                             │                            │
     │  User autorisiert ──────────┼───────────────────────────►│
     │                             │                            │
     │                             │◄── GET /callback?code= ────┤
     │                             │                            │
     │                             ├─ POST /oauth2/token ──────►│
     │                             │◄── access_token ───────────┤
     │                             │                            │
     │                             ├─ GET /users/@me ──────────►│
     │                             │◄── user identity ──────────┤
     │                             │                            │
     │                             ├─ POST /token/revoke ──────►│
     │                             │  (access token widerrufen) │
     │                             │                            │
     │  ◄── GET /auth/discord/poll ┤                            │
     │  (token + displayName)      │                            │
     └─────────────────────────────┘                            │
```

---

## Installation & Setup

### Server-Deployment

Die Installation erfolgt menügeführt über ein idempotentes (geiles Wort, oder? ;) ) Skript:

```bash
# install.sh herunterladen und ausführen
bash install.sh
```

**Was das Skript erledigt:**
- Node.js Backend unter `/opt/das-krt/backend` einrichten.
- Traefik Reverse Proxy mit Let's Encrypt (TLS) konfigurieren.
- Systemd-Service (`das-krt-backend`) erstellen und starten.
- Interaktive Abfrage von Discord Bot Token, OAuth2 Credentials und Admin-Token.

### Admin-CLI (service.sh)

Das zentrale Werkzeug zur Verwaltung des Servers:

```bash
bash service.sh [start|stop|restart|status|logs|menu]
```

| Bereich | Funktionen |
|---|---|
| **Service** | Start / Stop / Restart / Status & Healthcheck |
| **Tools** | `channels.json` bearbeiten, Healthcheck, Testlogs, Live-Logs |
| **Monitoring** | TX Events simulieren, TX Recent History, User Directory |
| **DSGVO** | Status, Toggle, Debug-Modus, User/Guild löschen, Manueller Cleanup |
| **Kanal-Sync** | Bot-Status, Manueller Trigger, Sync-Intervall ändern |
| **Security** | Ban/Unban (ID-basiert), Debug-Login Toggle, OAuth2 Credentials Update |
| **Traefik** | Status, Logs, Domain-Wechsel, Zertifikats-Check |

---

## Sicherheit

- **Transport**: TLS (HTTPS/WSS) via Traefik. DSGVO-HTTPS-Enforcement lehnt unverschlüsselten Traffic ab.
- **Authentifizierung**: Discord OAuth2 (Authorization Code Flow). HMAC-SHA256 signierte Tokens mit 24h Gültigkeit.
- **Datenschutz**: **User-ID-Hashing** (HMAC-SHA256). In der Datenbank werden keine rohen Discord-IDs gespeichert (außer für Bans im Admin-View). Bei einem Leak sind IDs nicht rekonstruierbar.
- **Zugriffsschutz**: Ban-System, Admin-Tokens (nicht persistiert in der App), Debug-Endpoints sind standardmäßig deaktiviert.

---

## Datenbank

SQLite (WAL Mode) mit folgendem Schema:

| Tabelle | Beschreibung |
|---|---|
| `voice_state` | Aktuelle Voice-Channel Belegung der User |
| `tx_events` | Historie der Sendevorgänge (PTT) |
| `voice_sessions` | Aktive WebSocket-Verbindungen |
| `freq_listeners` | Zuordnung User -> Frequenzen |
| `banned_users` | Blacklist (gehasht + raw für Admin) |
| `auth_tokens` | Gültige Sitzungstoken |
| `policy_acceptance`| Versionierte Bestätigung der Datenschutzregeln |
| `discord_users` | Cache für Anzeigenamen (gehashte IDs) |

---

## Roadmap

### Zeitnah
- [ ] **Rate-Limiting** & Security-Härtung (CORS, Helmet)
- [ ] ACLs für Frequenzen (Berechtigungen)
- [ ] Statusabhängige Erreichbarkeit
- [ ] Notfall-Funk Erweiterungen (Caller-Anzeige, Alarm-Beeps)
- [ ] Ingame-Overlay (Statusanzeige im Spiel)

### Später
- [ ] Subgruppen / Zusätzliche Verschlüsselung (Keyphrase-Hashing)
- [ ] Externe Statusanzeige (Web-Interface)
- [ ] Under-Attack-Mode (Automatisierte Abwehr)
- [ ] Multi-Guild Support (aktuell auf eine Guild limitiert)

---

## Lizenz

Dieses Projekt steht unter der [Apache License 2.0](LICENSE).  
**Jede kommerzielle Nutzung (Geld verdienen mit dieser Software) erfordert eine separate Genehmigung.**  
Siehe [COMMERCIAL_HOSTING_POLICY.md](COMMERCIAL_HOSTING_POLICY.md) für Details.
