# KRT-Com / das-krt – Funkkommunikation für Discord

**Status:** Alpha 0.0.4  
**Stand:** Backend + Companion App funktional, Security-Rework aktiv

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
* DSGVO-Compliance-Modul (automatische Datenbereinigung, DiscordUserID-/GuildID-Löschung)
* Ban-Management (REST + CLI)
* Privacy Policy Endpoint (versioniert, konfigurierbar)
* Server-Status Endpoint (öffentlich)
* Systemd-Service + idempotentes `install.sh`
* Admin-CLI (`service.sh`) mit 43 Menüpunkten

**Companion App (.NET 10, WPF):**
* Multi-Radio UI (bis zu 4 Frequenzen + Notfall-Funk)
* Push-to-Talk mit konfigurierbaren Hotkeys (systemweit)
* Opus-Audio-Capture & -Playback (NAudio)
* TX/RX Beep-Sounds mit Volume-Kontrolle
* Server-Verifizierung (Verify-Button: Status + Privacy Policy abrufen)
* Datenschutz-Einwilligung (Privacy Policy anzeigen + akzeptieren)
* Token-basierter Login (POST /auth/login → signierter Token)
* Auto-Reconnect mit gespeichertem Token
* Listener-Count pro Frequenz (Live-Updates)
* Channel-Namen-Anzeige (Discord-Kanal → Frequenz-Mapping)
* Server-seitiges Mute/Unmute
* Konfigurationspersistenz (%APPDATA%/das-KRT_com/config.json)

### ⏳ In Arbeit / Nächster Schritt

* TLS-Verschlüsselung via Traefik Reverse Proxy
* User-ID-Hashing in Datenbank
* Notfall-Funk Erweiterungen (Caller-Anzeige, signifikante Beep-Sounds)
* Ingame-Overlay

---

## Architekturübersicht

```
[Companion App (.NET 10 / WPF)]
   ├─ Hotkeys (PTT, systemweit)
   ├─ Audio Capture/Playback (NAudio, Opus)
   ├─ Server-Verify (GET /server-status, GET /privacy-policy)
   ├─ Auth (POST /auth/login → HMAC-SHA256 Token)
   └─ Voice WebSocket (Opus Audio Relay + State)
            │
            ▼
[das-krt Backend – Node.js 24]
   ├─ REST API (Public + Auth + Admin)
   ├─ Voice WebSocket (Opus Relay, Token-Auth)
   ├─ State WebSocket Hub
   ├─ SQLite WAL (voice_state, tx_events, voice_sessions,
   │              freq_listeners, banned_users, auth_tokens,
   │              policy_acceptance)
   ├─ DSGVO Module (Auto-Cleanup, Scheduler)
   ├─ Crypto Module (HMAC-SHA256 Token Sign/Verify)
   ├─ Channel Sync (Discord → Frequenz-Mapping)
   └─ Discord Bot (Status, Namen, Guild-Verify)
```

* **Audio-Transport** über Voice-WebSocket (Opus-Pakete, Server als Relay)
* Backend ist **State- & Event-Autorität**
* Authentifizierung über HMAC-signierte Tokens (24h Gültigkeit)

---

## Authentifizierung & Login-Flow

1. Nutzer gibt Server-Adresse + Port ein
2. Klick auf **Verify** → `GET /server-status` + `GET /privacy-policy`
3. Server-Status und Datenschutzerklärung werden angezeigt
4. Nutzer akzeptiert Privacy Policy → `POST /auth/accept-policy`
5. Discord User ID + Guild ID eingeben → **Connect**
6. `POST /auth/login` → Server verifiziert Nutzer via Discord Bot Guild-Member-Lookup
7. Bei Erfolg: HMAC-SHA256 signierter Token (24h) zurück
8. Voice-WebSocket-Verbindung mit Token-Auth
9. Token wird lokal gespeichert für Auto-Reconnect

---

## Push-to-Talk Logik

* PTT wird **lokal** in der Companion App erkannt (systemweite Hotkeys)
* Bei Tastendruck: Audio-Capture startet, Opus-Pakete werden über Voice-WS gestreamt
* TX-Event wird an Backend gemeldet (REST)
* Backend speichert Event + Realtime-Broadcast an alle Listener
* Half-Duplex wird **clientseitig** enforced

---

## Identität & Anzeigenamen

* Companion App sendet Discord User ID + Guild ID
* Backend verifiziert Mitgliedschaft via Discord Bot
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
* `banned_users` – Gesperrte Nutzer (Discord User ID)
* `auth_tokens` – Ausgestellte Auth-Tokens (Token ID, User, Expiry)
* `policy_acceptance` – Datenschutz-Einwilligungen (User + Version)

---

## Anforderungen

### Muss

* ✔ Discord Bot
* ✔ Push-to-Talk je Frequenz
* ✔ Mehrere Frequenzen gleichzeitig empfangbar
* ✔ Half-Duplex
* ✔ Windows-Client
* ✔ Discord Server-intern
* ✔ Externer Debian Server

### Soll
* ⏳ ACLs für Frequenzen
? ⏳ Statusabhängige Erreichbarkeit

### Kann

* ⏳ Subgruppen/zusätzliche Verschlüsselung keyphrase hashing
* ⏳ Externe Statusanzeige (z. B. Mumble)

---

## Sicherheit

### Implementiert (Alpha 0.0.4)

* Token-basierte Auth (HMAC-SHA256, 24h Expiry)
* Server-seitige User-Verifizierung via Discord Bot
* Ban-System (Login + Voice-Auth blockiert)
* Privacy Policy Consent Flow
* DSGVO Auto-Cleanup (2 Tage / 7 Tage Debug)
* Admin-Token für Management-Endpoints

### Ausstehend

* TLS-Verschlüsselung (HTTPS/WSS) via Reverse Proxy
* User-ID-Hashing in Datenbank
* Rate-Limiting
* CORS-Policy
* Helmet Security Headers
* DPAPI-Verschlüsselung für lokale Konfiguration

---

## Aktueller Stand

✔ Backend stabil (Alpha 0.0.4)  
✔ Companion App funktional (Multi-Radio, PTT, Audio, Auth)  
✔ Security-Rework Phase 1 abgeschlossen (Token-Auth, Ban, DSGVO, Consent)  
➡️ Nächster Fokus: **TLS-Verschlüsselung** + verbleibende Security-Härtung