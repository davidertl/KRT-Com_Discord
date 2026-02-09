# KRT-Com / das-krt – Funkkommunikation für Discord

**Status:** Alpha 0.0.4  
**Stand:** Backend + Companion App funktional, Discord OAuth2 Login, Security-Hardening aktiv

---

## Projektziel

KRT-Com (das-krt) ist eine funkähnliche Kommunikationslösung für Discord, angelehnt an klassische TeamSpeak-Funkplugins.

User sollen **parallel** mit mehreren Gruppen kommunizieren können, ohne den Voice-Channel zu wechseln. Der Fokus liegt auf realistischer Funklogik statt klassischem Voice-Chat.

---

## Kernfunktionen (aktuell)

### ✔ Implementiert (Alpha 0.0.4)

**Backend:**
* Discord Bot (read-only, Discord API, `syncGuildMembers` on startup)
* Voice-State-Erkennung (`voiceStateUpdate`)
* Frequenz-Zuordnung via `channels.json` (automatischer Channel-Sync alle 24h)
* Push-to-Talk Events (`start` / `stop`) mit Opus-Audio-Relay
* Persistente Speicherung (SQLite WAL)
* WebSocket-Realtime-Broadcast (Voice + State Hub)
* Token-basierte Authentifizierung (HMAC-SHA256, 24h Expiry)
* Discord OAuth2 Login (Authorization Code Flow, `identify` + `guilds` Scopes)
* Debug-Login (POST /auth/login) nur im Debug-Modus verfügbar
* DSGVO-Compliance-Modul (automatische Datenbereinigung, DiscordUserID-/GuildID-Löschung)
* Ban-Management (REST + CLI)
* Privacy Policy Endpoint (versioniert, konfigurierbar)
* Server-Status Endpoint (öffentlich, inkl. OAuth- & Debug-Status)
* Systemd-Service + idempotentes `install.sh`
* Admin-CLI (`service.sh`) mit 51 Menüpunkten

**Companion App (.NET 10, WPF):**
* Multi-Radio UI (bis zu 4 Frequenzen + Notfall-Funk)
* Push-to-Talk mit konfigurierbaren Hotkeys (systemweit)
* Opus-Audio-Capture & -Playback (NAudio)
* TX/RX Beep-Sounds mit Volume-Kontrolle
* Server-Verifizierung (Verify-Button: Status + Privacy Policy abrufen)
* Datenschutz-Einwilligung (Privacy Policy anzeigen + akzeptieren)
* Discord OAuth2 Login ("Login with Discord" Button, Browser-Flow)
* Auto-Reconnect mit gespeichertem Token
* Listener-Count pro Frequenz (Live-Updates)
* Channel-Namen-Anzeige (Discord-Kanal → Frequenz-Mapping)
* Server-seitiges Mute/Unmute
* Admin-Token nur im Server-Debug-Modus sichtbar (nicht persistiert)
* Konfigurationspersistenz (%APPDATA%/das-KRT_com/config.json)

### ⏳ In Arbeit / Nächster Schritt

* Notfall-Funk Erweiterungen (Caller-Anzeige, signifikante Beep-Sounds)
* Ingame-Overlay

---

## Discord OAuth2 Setup

### Voraussetzungen

1. **Discord Developer Portal** → [discord.com/developers/applications](https://discord.com/developers/applications)
2. Neue Application erstellen (oder vorhandene verwenden)
3. **Application ID** (= Client ID) notieren

### OAuth2 Konfiguration

1. Im Developer Portal → **OAuth2** → **General**
2. **Client Secret** generieren und sicher speichern
3. Unter **Redirects** die Callback-URL eintragen:
   ```
   https://<DOMAIN>/auth/discord/callback
   ```
   Beispiel: `https://das-krt.com/auth/discord/callback`

### URL Generator Settings

| Einstellung | Wert |
|---|---|
| **Authorization Method** | Authorization Code |
| **Scopes** | `identify`, `guilds` |
| **Response Type** | code |
| **Grant Type** | Authorization Code |

> **Hinweis:** Die Authorize-URL wird automatisch vom Server generiert (`GET /auth/discord/redirect`). Es muss keine URL manuell erstellt werden.

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

### Credentials ändern

OAuth2 Zugangsdaten können jederzeit über `service.sh` → Menüpunkt **51** aktualisiert werden.

---

## Architekturübersicht

```
[Companion App (.NET 10 / WPF)]
   ├─ Hotkeys (PTT, systemweit)
   ├─ Audio Capture/Playback (NAudio, Opus)
   ├─ Server-Verify (GET /server-status, GET /privacy-policy)
   ├─ Auth (Discord OAuth2 → HMAC-SHA256 Token)
   └─ Voice WebSocket (Opus Audio Relay + State)
            │ HTTPS / WSS (TLS)
            ▼
[Traefik Reverse Proxy]
   ├─ TLS Termination (Let's Encrypt / ACME)
   ├─ HTTP → HTTPS Redirect (permanent)
   ├─ DSGVO: Kein unverschlüsselter externer Traffic
   └─ Proxy → http://127.0.0.1:3000
            │ HTTP (intern)
            ▼
[das-krt Backend – Node.js 24]
   ├─ REST API (Public + Auth + Admin)
   ├─ Discord OAuth2 (Authorization Code Flow)
   ├─ Voice WebSocket (Opus Relay, Token-Auth)
   ├─ State WebSocket Hub
   ├─ SQLite WAL (voice_state, tx_events, voice_sessions,
   │              freq_listeners, banned_users, auth_tokens,
   │              policy_acceptance, discord_users)
   ├─ DSGVO Module (Auto-Cleanup, Scheduler)
   ├─ Crypto Module (HMAC-SHA256 Token Sign/Verify)
   ├─ Channel Sync (Discord → Frequenz-Mapping)
   └─ Discord Bot (Status, Namen, Guild-Verify)
```

* **Audio-Transport** über Voice-WebSocket (Opus-Pakete, Server als Relay)
* **TLS-Terminierung** durch Traefik Reverse Proxy (Let's Encrypt ACME)
* **DSGVO-HTTPS-Enforcement**: Bei aktivem DSGVO-Modus wird unverschlüsselter Traffic (HTTP/WS) serverseitig abgelehnt
* Backend ist **State- & Event-Autorität**
* Authentifizierung über Discord OAuth2 → HMAC-signierte Tokens (24h Gültigkeit)

---

## Authentifizierung & Login-Flow

### Discord OAuth2 (Standard)

1. Nutzer gibt Server-Adresse + Port ein
2. Klick auf **Verify** → `GET /server-status` + `GET /privacy-policy`
3. Server-Status und Datenschutzerklärung werden angezeigt
4. Nutzer akzeptiert Privacy Policy → `POST /auth/accept-policy`
5. Klick auf **Login with Discord** → Browser öffnet Discord-Autorisierung
6. Nach Autorisierung: Server tauscht Code gegen Access Token, liest User-Identität, verifiziert Guild-Mitgliedschaft
7. Companion App pollt Server → erhält HMAC-SHA256 signierten Token (24h)
8. Voice-WebSocket-Verbindung mit Token-Auth
9. Token wird lokal gespeichert für Auto-Reconnect

### Debug-Login (nur im Debug-Modus)

Wenn der Server-Admin Debug-Modus aktiviert hat (`service.sh` → 50):
- `POST /auth/login` akzeptiert manuelle Discord User ID + Guild ID
- Im normalen Betrieb gibt dieser Endpoint HTTP 410 zurück

---

## Push-to-Talk Logik

* PTT wird **lokal** in der Companion App erkannt (systemweite Hotkeys)
* Bei Tastendruck: Audio-Capture startet, Opus-Pakete werden über Voice-WS gestreamt
* TX-Event wird an Backend gemeldet (REST)
* Backend speichert Event + Realtime-Broadcast an alle Listener
* Half-Duplex wird **clientseitig** enforced

---

## Identität & Anzeigenamen

* Identität wird über Discord OAuth2 verifiziert (kein Self-Reporting)
* Backend prüft Guild-Mitgliedschaft via Discord Bot
* Anzeigename wird serverseitig ermittelt:
  * **Server-Nickname** (nicht global_name)
* Namensänderungen dürfen historisch durchschlagen

➡️ Keine Historisierung von Namen notwendig

---

## Datenbank

### Aktive Tabellen

* `voice_state` – Aktuelle Voice-States
* `tx_events` – TX-Event-History
* `voice_sessions` – Aktive WebSocket-Sessions
* `freq_listeners` – Frequenz-Subscriber pro Session
* `banned_users` – Gesperrte Nutzer (gehashte Discord User ID + raw ID für Admin-Anzeige)
* `auth_tokens` – Ausgestellte Auth-Tokens (Token ID, gehashte User ID, Expiry)
* `policy_acceptance` – Datenschutz-Einwilligungen (gehashte User ID + Version)
* `discord_users` – OAuth2 autorisierte User (gehashte Discord ID, Display Name)

> **Hinweis:** Alle `discord_user_id`-Spalten speichern ausschließlich HMAC-SHA256-Hashes (64 Zeichen Hex).
> Rohe Discord Snowflake-IDs werden nirgends in der Datenbank persistiert (mit Ausnahme von `banned_users.raw_discord_id` für die Admin-Anzeige).
> Bei einem Datenleck sind die User-IDs nicht rekonstruierbar.

---

## Pipeline

### Muss

* ✔ Discord Bot
* ✔ Push-to-Talk je Frequenz
* ✔ Mehrere Frequenzen gleichzeitig empfangbar
* ✔ Half-Duplex
* ✔ Windows-Client
* ✔ Discord Server-intern
* ✔ Externer Debian Server

### zeitnaher
* ⏳ ACLs für Frequenzen
* ⏳ Statusabhängige Erreichbarkeit

### eher später

* ⏳ Subgruppen/zusätzliche Verschlüsselung keyphrase hashing
* ⏳ Externe Statusanzeige

---

## Sicherheit

### Implementiert (Alpha 0.0.4)

* TLS-Verschlüsselung (HTTPS/WSS) via Traefik Reverse Proxy mit Let's Encrypt
* DSGVO-HTTPS-Enforcement (kein Klartext-Traffic bei aktivem Compliance-Modus)
* Discord OAuth2 Login (Authorization Code Flow, keine Self-Reported Identity)
* Token-basierte Auth (HMAC-SHA256, 24h Expiry)
* **User-ID-Hashing** (HMAC-SHA256, alle Discord User IDs werden vor DB-Speicherung gehasht)
* Automatische Datenmigration (bestehende Raw-IDs werden beim Start gehasht)
* Server-seitige User-Verifizierung via Discord Bot
* Debug-Login (POST /auth/login) nur im Debug-Modus verfügbar
* OAuth Poll-Response enthält keine Raw-IDs (nur Token + DisplayName)
* Ban-System (Login + Voice-Auth blockiert)
* Privacy Policy Consent Flow
* DSGVO Auto-Cleanup (2 Tage / 7 Tage Debug)
* Admin-Token für Management-Endpoints
* Admin-Token nicht in Companion-Config persistiert (Runtime-only)
* Admin-Token UI nur im Server-Debug-Modus sichtbar

### Ausstehend

* Rate-Limiting
* CORS-Policy
* Helmet Security Headers
* DPAPI-Verschlüsselung für lokale Konfiguration

---

## Admin-CLI (service.sh)

| # | Funktion |
|---|---|
| 1-4 | Service Start/Stop/Restart/Status |
| 5 | channels.json bearbeiten |
| 6-8 | Healthcheck / Testlog / Live-Logs |
| 9-11 | TX Event / TX Recent / Users Recent |
| 20-25 | DSGVO: Status / Toggle / Debug / Delete User / Delete Guild / Cleanup |
| 30-32 | Kanal-Sync: Status / Trigger / Intervall |
| 40-43 | Ban: Bannen / Entbannen / Liste / Löschen+Bannen |
| 50 | Debug-Login an/aus |
| 51 | Discord OAuth2 Zugangsdaten ändern |
| 60 | Traefik Status & TLS Zertifikat |
| 61 | Traefik neustarten |
| 62 | Traefik Live-Logs |
| 63 | Domain ändern |

---

## Aktueller Stand

✔ Backend stabil (Alpha 0.0.4)  
✔ Companion App funktional (Multi-Radio, PTT, Audio, Auth)  
✔ Discord OAuth2 Login implementiert  
✔ Security-Hardening: Debug-Login gated, OAuth poll stripped, Admin-Token runtime-only  
✔ TLS via Traefik Reverse Proxy (Let's Encrypt, HTTP→HTTPS Redirect)  
✔ DSGVO-HTTPS-Enforcement (Klartext-Traffic wird bei DSGVO-Modus abgelehnt)  
✔ User-ID-Hashing (HMAC-SHA256, DB enthält nur Hashes, automatische Migration)  
➡️ Nächster Fokus: **Rate-Limiting** + verbleibende Security-Härtung