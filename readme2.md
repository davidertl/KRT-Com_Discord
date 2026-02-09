# das-krt.com – Funkkommunikation für Discord

KRT-Com ist eine funkähnliche Kommunikationslösung für Discord, angelehnt an klassische TeamSpeak-Funkplugins. User kommunizieren parallel auf mehreren Frequenzen, ohne den Voice-Channel zu wechseln – mit realistischer Half-Duplex-Funklogik statt klassischem Voice-Chat.

**Status:** Alpha 0.0.4

---

## Inhaltsverzeichnis

- [Features](#features)
- [Architektur](#architektur)
- [Voraussetzungen](#voraussetzungen)
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
| **Discord-Integration** | Bot (read-only), Voice-State-Erkennung, Guild-Member-Sync, Channel-Sync (24h-Intervall) |
| **Funk-Logik** | Push-to-Talk Events (start/stop), Opus-Audio-Relay via WebSocket, Half-Duplex |
| **Authentifizierung** | Discord OAuth2 Login, HMAC-SHA256 Token-Auth (24h Expiry), Debug-Login (nur im Debug-Modus) |
| **Datenschutz** | DSGVO-Compliance-Modul (Auto-Cleanup), User-ID-Hashing (HMAC-SHA256), Privacy Policy Endpoint |
| **Administration** | Ban-Management (REST + CLI), Admin-CLI mit interaktivem Menü, Server-Status Endpoint |
| **Infrastruktur** | SQLite WAL, Systemd-Service, idempotentes `install.sh`, Traefik Reverse Proxy (Let's Encrypt) |

### Companion App (.NET 10, WPF)

| Bereich | Feature |
|---|---|
| **Funk-UI** | Multi-Radio (bis zu 4 Frequenzen + Notfall-Funk), Listener-Count pro Frequenz, Channel-Namen |
| **Audio** | Push-to-Talk (systemweite Hotkeys), Opus Capture & Playback (NAudio), TX/RX Beep-Sounds |
| **Auth & Verbindung** | Discord OAuth2 Login (Browser-Flow), Auto-Reconnect, Token-Persistenz |
| **Server-Verwaltung** | Verify-Button (Status + Privacy Policy), Datenschutz-Einwilligung, Mute/Unmute |
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

1. PTT-Taste → Companion App erkennt systemweiten Hotkey
2. Audio-Capture startet, Opus-Pakete werden über Voice-WebSocket gestreamt
3. TX-Event wird an Backend gemeldet → Realtime-Broadcast an alle Listener
4. Half-Duplex wird clientseitig enforced

### Identität & Anzeigenamen

- Identität über Discord OAuth2 verifiziert (kein Self-Reporting)
- Guild-Mitgliedschaft wird serverseitig via Discord Bot geprüft
- Anzeigename = Server-Nickname (nicht global_name)
- Namensänderungen von discord werden automatisch synchronisiert (über Bot-Events), jedoch nicht in Echtzeit (max 24h Verzögerung durch Channel-Sync-Intervall) - keine historisierung (kann man das sagen?) von alten Namen, nur der aktuellste Nickname wird gespeichert.

---

## Voraussetzungen

### Server

| Komponente | Anforderung |
|---|---|
| **OS** | Debian/Ubuntu (Systemd) |
| **Runtime** | Node.js 24 |
| **Reverse Proxy** | Traefik v3 (wird von `install.sh` eingerichtet) |
| **Domain** | Öffentliche Domain mit DNS A-Record auf den Server |
| **Ports** | 80 (HTTP→HTTPS Redirect), 443 (HTTPS/WSS) |
| **TLS** | Automatisch via Let's Encrypt (ACME TLS-ALPN-01) |

### Discord Developer Portal

1. Application erstellen unter [discord.com/developers/applications](https://discord.com/developers/applications)
2. **Bot** einrichten (read-only, Guild Members Intent)
3. **OAuth2** konfigurieren:
   - Client ID + Client Secret notieren
   - Redirect URL: `https://<DOMAIN>/auth/discord/callback`
   - Scopes: `identify`, `guilds`

> Für Details zum OAuth2-Flow siehe [Discord OAuth2 Dokumentation](https://discord.com/developers/docs/topics/oauth2).

### Companion App

| Komponente | Anforderung |
|---|---|
| **OS** | Windows 10/11 |
| **Runtime** | .NET 10 |
| **Audio** | WASAPI-kompatible Soundkarte (für NAudio) |

---

## Installation & Setup

### Server-Deployment

Its a 2 script guided install.

```bash
# install.sh herunterladen und ausführen
bash install.sh
```

Das Installationsskript ist idempotent ( ;) geiles Wort! )  und richtet ein:
- Node.js Backend unter `/opt/das-krt/backend`
- Traefik Reverse Proxy mit Let's Encrypt
- Systemd-Service (`das-krt-backend`)
- Discord Bot Token, OAuth2 Credentials, Admin-Token (interaktive Abfrage)

### OAuth2 Credentials ändern

```bash
bash service.sh    # → Menüpunkt 51
```

---

## Admin-CLI (service.sh)

```bash
bash service.sh [start|stop|restart|status|logs|menu]
```

| # | Bereich | Funktionen |
|---|---|---|
| 1–4 | **Service** | Start / Stop / Restart / Status & Healthcheck |
| 5–8 | **Tools** | channels.json bearbeiten / Healthcheck / Testlog / Live-Logs |
| 9–11 | **Monitoring** | TX Event senden / TX Recent / Users Recent |
| 20–25 | **DSGVO** | Status / Toggle / Debug-Modus / User löschen / Guild löschen / Cleanup |
| 30–32 | **Kanal-Sync** | Status / Trigger / Intervall ändern |
| 40–43 | **Ban** | Bannen / Entbannen / Liste / Löschen+Bannen |
| 50–51 | **Security** | Debug-Login an/aus / OAuth2 Credentials ändern |
| 60–64 | **Traefik** | Status / Restart / Logs / Domain ändern / Let's Encrypt Zertifikat prüfen |

---

## Sicherheit

### Transportverschlüsselung

- **TLS (HTTPS/WSS)** via Traefik Reverse Proxy mit Let's Encrypt
- **DSGVO-HTTPS-Enforcement**: Bei aktivem Compliance-Modus wird unverschlüsselter Traffic serverseitig abgelehnt
- Zertifikatsprüfung über `service.sh` → Menüpunkt 64

### Authentifizierung

- **Discord OAuth2** (Authorization Code Flow) – keine Self-Reported Identity
- **HMAC-SHA256 Token-Auth** mit 24h Expiry
- Server-seitige Guild-Mitgliedschaftsprüfung via Discord Bot
- aktuell nur Single Guild-Mitgliedschaft.
- Debug-Login (`POST /auth/login`) nur im Debug-Modus verfügbar (HTTP 410 im Normalbetrieb)

### Datenschutz (DSGVO)

- **User-ID-Hashing**: Alle Discord User IDs werden vor DB-Speicherung mit HMAC-SHA256 gehasht
- Automatische Datenmigration bestehender Raw-IDs beim Serverstart
- Auto-Cleanup (2 Tage Aufbewahrung, 7 Tage im Debug-Modus)
- Privacy Policy Consent Flow (versioniert)
- Vollständige Datenlöschung pro User oder Guild durch service.sh (durch Admin). 

### Zugriffsschutz

- Ban-System blockiert Login + Voice-Auth
- Admin-Token für Management-Endpoints (nicht in Companion-Config persistiert)
- Admin-Token UI nur im Server-Debug-Modus sichtbar
- OAuth Poll-Response enthält keine Raw-IDs

### Ausstehend

- Rate-Limiting
- CORS-Policy
- Helmet Security Headers
- DPAPI-Verschlüsselung für lokale Konfiguration

---

## Datenbank

SQLite WAL mit 8 Tabellen:

| Tabelle | Inhalt |
|---|---|
| `voice_state` | Aktuelle Voice-States |
| `tx_events` | TX-Event-History |
| `voice_sessions` | Aktive WebSocket-Sessions |
| `freq_listeners` | Frequenz-Subscriber pro Session |
| `banned_users` | Gesperrte Nutzer (gehashte + raw ID für Admin-Anzeige) |
| `auth_tokens` | Ausgestellte Auth-Tokens (Token ID, gehashte User ID, Expiry) |
| `policy_acceptance` | Datenschutz-Einwilligungen (gehashte User ID + Version) |
| `discord_users` | OAuth2 autorisierte User (gehashte Discord ID, Display Name) |

> Alle `discord_user_id`-Spalten speichern ausschließlich HMAC-SHA256-Hashes (64 Zeichen Hex). Rohe Discord Snowflake-IDs werden nicht persistiert (Ausnahme: `banned_users.raw_discord_id` für Admin-Anzeige). Bei einem Datenleck sind die User-IDs nicht rekonstruierbar.

---

## Roadmap

### Zeitnah

- [ ] Rate-Limiting & Security-Härtung (CORS, Helmet)
- [ ] ACLs für Frequenzen
- [ ] Statusabhängige Erreichbarkeit
- [ ] Notfall-Funk Erweiterungen (Caller-Anzeige, signifikante Beep-Sounds)
- [ ] Ingame-Overlay

### Später

- [ ] Subgruppen / zusätzliche Verschlüsselung (Keyphrase-Hashing)
- [ ] Externe Statusanzeige
- [ ] ban2fail / Under-Attack-Mode
- [ ] multi Guild Support (aktuell auf eine Guild limitiert)
