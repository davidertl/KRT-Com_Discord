# KRT-Com / das-krt – Funkkommunikation für Discord

**Status:** Alpha 0.0.2
**Stand:** Backend stabil, Companion App Designphase

---

## Projektziel

KRT-Com (das-krt) ist eine funkähnliche Kommunikationslösung für Discord, angelehnt an klassische TeamSpeak-Funkplugins.

User sollen **parallel** mit mehreren Gruppen kommunizieren können, ohne den Voice-Channel zu wechseln. Der Fokus liegt auf realistischer Funklogik statt klassischem Voice-Chat.

---

## Kernfunktionen (aktuell)

### ✔ Implementiert (Alpha 0.0.2)

* Discord Bot (read-only, Discord API)
* Voice-State-Erkennung (`voiceStateUpdate`)
* Frequenz-Zuordnung via `channels.json`
* Push-to-Talk Events (`start` / `stop`)
* Persistente Speicherung von TX-Events (SQLite)
* WebSocket-Realtime-Broadcast
* Systemd-Service + idempotentes `install.sh`

### ⏳ In Arbeit / Nächster Schritt

* Companion App (.NET, Windows)
* Discord OAuth (Identify + Guilds)
* Benutzerfreundliche PTT-Hotkeys

---

## Architekturübersicht

```
[Companion App (.NET)]
   ├─ Hotkeys (PTT)
   ├─ Discord OAuth (Identify)
   └─ HTTP / WebSocket
            │
            ▼
[das-krt Backend – Node.js]
   ├─ REST API
   ├─ WebSocket Hub
   ├─ SQLite (voice_state, tx_events)
   └─ Discord Bot (Status & Namen)
```

* **Kein Audio-Transport** im Backend
* Backend ist **State- & Event-Autorität**
* Audio bleibt vollständig clientseitig

---

## Push-to-Talk Logik

* PTT wird **lokal** in der Companion App erkannt
* Bei Tastendruck:

```json
{
  "freqId": 1060,
  "action": "start",
  "discordUserId": "239048901951225857"
}
```

* Backend speichert Event
* Realtime-Broadcast an alle verbundenen Clients
* Half-Duplex wird **clientseitig** enforced

---

## Identität & Anzeigenamen

* Companion App authentifiziert User via Discord OAuth
* Backend erhält nur die **DiscordUserID**
* Anzeigename wird serverseitig ermittelt:

  * **Server-Nickname** (nicht global_name)
* Namensänderungen dürfen historisch durchschlagen

➡️ Keine Historisierung von Namen notwendig

---

## Datenbank

### Aktive Tabellen

* `voice_state`
* `tx_events`

### Optional (später)

* `discord_users` als Cache / Performance-Optimierung

---

## Anforderungen

### Muss

* Discord Bot
* Push-to-Talk je Frequenz
* Mehrere Frequenzen gleichzeitig empfangbar
* Half-Duplex
* Windows-Client
* Discord Server-intern

### Soll

* Externer Debian Server
* ACLs für Frequenzen
* Statusabhängige Erreichbarkeit

### Kann

* Subgruppen via Hash
* Externe Statusanzeige (z. B. Mumble)

---

## Sicherheit

* Dev-Phase: HTTP, offene IPs erlaubt
* Live-Betrieb (später):

  * Reverse Proxy (HTTPS)
  * API-Tokens / OAuth
  * Rate-Limits

---

## Aktueller Stand

✔ Backend stabil (Alpha 0.0.2)
➡️ Fokus wechselt jetzt auf **Companion App (.NET)**