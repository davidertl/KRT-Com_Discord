# High priority:

# Medium priority: 

# Low priority:

(✔) make the tx and rx beep of emergency radio more signficant, so that it is clear that the emergency radio is active. maybe also add a visual indicator in the companion app.
  > FIXED: Emergency beeps redesigned — TX start: ascending 3-tone siren (1200→1500→1800 Hz) with boosted volume (0.55–0.6). TX end: descending 3-tone (1500→1200→900 Hz). RX: rapid triple-pulse (1600→1600→1800 Hz). All emergency tones ~2x louder than regular beeps. PlayBeep now accepts optional volumeMultiplier param.
(✔) add a recent to the emergency radio - when sending an emergency call (ptt on Emergency Radio) it should show in the companion app who sent the call and which frequency he is active on. max display 911 and max 3 other radio id's (if the user who is sending the emergency call is active on more than 3 frequencies, then just show the 3 most active ones).
On emergendy radio in the ui the calling person should be highlighted in the black window on the right hand side of the green Channel-ID-Display.
  > FIXED: Emergency radio panel now has a black recent-callers display (#111111 background) right of the frequency display. Shows "Recent:" header in red + last 3 callers in orange (#FF6644, bold). Uses existing RecentTransmissions/AddTransmission infrastructure from RadioPanelViewModel. MinWidth 140px. 

-ingame overlay wer zuletzt auf welcher Frequenz gefunkt hat. 
    -essentiell mit on/off toggle im "App Settings" Tab
    -frage bitte bevor du dieses Projekt anfängst nach deutlichen Instruktionen. 
    -optional mit Rang (kann erst später implementiert werden, wenn ich weiß wie das Kartell die Daten angelegt hat)


# Security Audit and debugging:
check security autit notes and check which ones are already fixed, for the one thar are fixed mark them as fixed.

Security Audit Notes (as of 2025-02-17, updated 2026-02-09):
Security:

  Critical:

  -Most REST endpoints have zero authentication.
    > Partial Fix: Auth endpoints require signed token (Bearer header). Public endpoints limited to /server-status and /privacy-policy. Admin endpoints require x-admin-token. Freq/TX endpoints still use self-reported identity.
  
  High:
  - ws WebSocket hub has no authentication.
    > Still open.
  -No rate limiting on any HTTP endpoint or WebSocket auth.
    > Still open.
  -No concurrent session limit per user.
    > Still open.
  -No CORS policy on Express server.
    > Still open.
  -No WebSocket origin checking on voice.js or ws.js.
    > Still open.
  
  Medium:
  -Discord bot token in plaintext .env file (permissions 600 is OK but still cleartext).
   -> can we store this somehow encrypted?
    > Still open.

  -Session tokens stored unhashed in SQLite voice_sessions table.
  -/freq/join and /freq/leave missing freqId range validation (1000-9999).
  -limit query parameter not sanitized against NaN.
  -No Express security headers (helmet middleware missing).
  -Server logs expose user PII (Discord IDs, display names).




  Low:
  (✔) Client VoiceHost/VoicePort not validated before use.
    > FIXED: VoiceService.ConnectAsync now validates host (non-empty) and port (1–65535) with ArgumentException/ArgumentOutOfRangeException before connecting.
  (✔) Debug log writes sensitive connection parameters in plaintext.
    > FIXED: ConnectVoiceAsync LogDebug no longer logs userId or guildId. Only host and port are logged.
  (✔) Command injection risk in install.sh interactive menu (user input interpolated into curl -d JSON).
    > FIXED: Added json_escape() helper to service.sh (escapes backslashes, quotes, control chars). Applied to all user-input curl -d payloads: delete-user, delete-guild, ban, unban, delete-and-ban, TX event action. Numeric inputs (freqId, hours) validated with regex before use.


