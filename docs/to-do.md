# High priority:
- reconnect logic: auto-reconnect voice WebSocket on disconnect without requiring PTT press
- change the beep at the end of a push-to-talk session to a hissing noice like a walkie talkie.
- voice ducking with a slider. 
    - when the slider is at 100% the voice ducking is disabled and the received audio is played at full volume. 
    - when the slider is at 0% the voice ducking is fully enabled and the received audio is completely muted while transmitting. 
    - intermediate values would reduce the volume of the received audio proportionally while transmitting, allowing users to find a comfortable balance between hearing others and being heard themselves.
    - please let the user choose which audio should be ducked (e.g. only discord, or all audio). This could be implemented by allowing users to select specific audio sources or applications in the companion app settings, and then applying the ducking effect only to those selected sources when transmitting on the radio.

-disable the possibility to transmit on a frequency that is muted except for broadcasts. 
  
# Medium priority: 
- Derzeit werden Sprachdaten zwar über TLS zum Server gesendet, aber auf der Strecke zwischen Server und Clients über UDP ausgetauscht. Um ein wirklich verschlüsseltes Funksystem zu erreichen, sollte die Audio-Übertragung selbst Ende-zu-Ende oder zumindest Ende-zu-Server-zu-Ende verschlüsselt werden. Eine Erweiterung wäre, pro Funk-Frequenz einen verschlüsselten Sprachkanal einzurichten. Praktisch könnte das so aussehen: Beim Frequenzbeitritt erzeugt der Server oder die Clients einen zufälligen Session-Schlüssel (z.B. via sicheren Diffie-Hellman-Austausch oder vom Server generiert und über den TLS-gesicherten WebSocket verteilt). Alle Teilnehmer dieser Frequenz verwenden diesen Schlüssel, um die Opus-Audiopakete vor dem Versand zu verschlüsseln (und entsprechend nach Empfang zu entschlüsseln). Der Server würde die verschlüsselten Pakete lediglich weiterleiten, ohne sie selbst zu decodieren. So wären die Audioinhalte auch dann vertraulich, wenn jemand den UDP-Datenverkehr abhört. Diese zusätzliche Verschlüsselung könnte optional oder für bestimmte als sensibel markierte Frequenzen aktiviert werden. Im Projekt-Backlog ist bereits ein ähnliches Konzept vorgesehen (“Zusätzliche Verschlüsselung per Keyphrase”). Die Implementierung lässt sich mit begrenztem Aufwand ergänzen, da auf Client-Seite dank der bestehenden Concentus-Bibliothek bereits Byte-Buffer der Audiopakete vorliegen – diese könnten vor dem Senden mit z.B. AES-GCM symmetrisch verschlüsselt werden. Wichtig ist, einen Schlüsselaustausch-Mechanismus zu integrieren, der in die bestehende WebSocket-Kontrollverbindung passt (z.B. als Teil der Authentifizierung beim Frequenzbeitritt). Insgesamt würde diese Maßnahme die Vertraulichkeit der Sprachkommunikation erheblich stärken, ohne die grundlegende Architektur (WebSocket-Steuerkanal + UDP-Datenkanal) zu verändern.
# Low priority:
-ingame overlay wer zuletzt auf welcher Frequenz gefunkt hat. 
    -essentiell mit on/off toggle im "App Settings" Tab
    -zuerst noch planen was alles nötig ist für Overlay
    -optional mit Rang (kann erst später implementiert werden, wenn ich weiß wie das Kartell die Daten angelegt hat)

-companion app: Tab "App Settings:
    please change the order of the settings to this:
    In "General"
    -Play sound on PTT start/end
      -enable sound on receiving
      -enable sound on transmitting
      -enable sound on beginning
      -enable sound on end
    - Enable Emergency Radio 
      -Turn on Emergency radio on startup
    In "Autostart"
    - Launch on Windows start
      - launch minimized on tray
    - Autoconnect on launch
    - recover active state from radios (otherwise radios will always start with the active state set to false)
    In "Debugg"
    - enable debug logging and the location of the log file
  -rearange the two cards horizontally.

-companion app: when restarting i am do properly reconnect, but authentication shows me "login with discord" instead of log out button and the username that i am logged in with. this should only apply when the session token is actually valid and the user is authenticated.

-companion app:when receiving broadcasts, you hear the audio only on the pan and vol settings of the first radio that is receiving the broadcast, even if multiple radios are receiving the same broadcast. This should be fixed so that the pan is avaraged between the radios and vol settings are taken the highest of the receiving radios, so that if one radio is set to 100% and another to 50% the resulting volume is 100% and not 75%.

-companion app: remove the testPTT button from the companion app. 

-companion app on Tab Server: add a disconnect button to disconnect the voice connection next to the connected status.

-companion app on Tab Radio: when changing frequency the radio should automatically connect if the radio is active, so that the user doesnt have to send on the frequency to register. 

# Wunschliste:
-speech priority by user rank
  - sync user rank from discord roles (custom table which lists discord groupid and corresponding rank in the app)
    -discord roles that have no corresponding rank in the app should be ignored, so that they don't get assigned a default rank.
  - alternative option in service.sh to manually assign ranks to users (can we safeley search for a discordusername [not discordservernickname]) this setting should be higher and not rewritten by discord role sync.
  - when adding the priority feature please check discord channel names for the number in "[]" and use that as the minimum rank required to send or to recieve voice on a channel. (e.g. [3] means that only users with rank 3 or higher can send or receive voice on this channel)
  -with this we need to implement key load per frequency, so that users only ativea the radio on frequencies they have access to. This would reduce bandwidth and improve performance for users with lower ranks who may have access to fewer channels.
- Optioale Störgeräusche (as a option in the companion app settings) to simulate walkie-talkie communication, such as static, interference, or a squelch effect when no one is transmitting. This would enhance the immersion and authenticity of the radio communication experience. The noise could be generated locally on the client side and mixed with the received audio stream, allowing users to adjust the intensity or type of noise according to their preferences.
-overlay injection: an in-game overlay that shows who is currently transmitting on which frequency, with an on/off toggle in the "App Settings" tab. This would provide users with real-time information about active communications and help them identify who is speaking without needing to check the companion app. The overlay could display the username and rank of the speaker, as well as the frequency they are using. This feature would enhance situational awareness and improve coordination among users, especially in larger groups or during complex operations. It would be important to ensure that the overlay is unobtrusive and can be easily toggled on or off based on user preference.
add a checkmark for showing the rank.
optional add the checkmark to this setting to add the keybind for the radio.
-ElgatoStreamDeck integration: Implementing integration with Elgato Stream Deck to allow users to control their radio communication (e.g., push-to-talk, frequency switching, profiles) directly from their Stream Deck device. This would provide a convenient and customizable way for users to manage their radio interactions without needing to switch between applications or use keyboard shortcuts. The integration could include features such as assigning specific buttons for different frequencies, toggling the radio on/off, or even displaying real-time information about active communications. This would enhance the user experience and make it easier for streamers and gamers to incorporate the radio system into their workflow.
-add a webSocket API for controlling the radio from external applications (e.g., Stream Deck, custom scripts). This API would allow users to send commands to the radio system, such as changing frequencies, toggling push-to-talk, adjusting volume levels, or applying specific profiles. By providing a standardized interface for external control, users could integrate the radio system with various tools and platforms, enhancing its versatility and usability. The API could be secured with authentication tokens to ensure that only authorized applications can control the radio, and it could support both RESTful endpoints for simple commands and WebSocket connections for real-time updates and interactions.
-profiles for channel settings: allowing users to create and save profiles for different channel configurations (e.g., specific frequencies, volume levels, ducking settings) that can be quickly applied based on their current activity or preferences. This would enable users to easily switch between different setups for various scenarios, such as gaming sessions, streaming, or casual communication. The profiles could be managed through the companion app, with options to create, edit, and delete profiles as needed. This feature would enhance the flexibility and usability of the radio system, allowing users to tailor their experience to their specific needs and preferences.

# Security Audit and debugging:
check security autit notes and check which ones are already fixed, for the one thar are fixed mark them as fixed.

Security Audit Notes (as of 2025-02-17, updated 2026-02-09):
Security:

  -Session tokens stored unhashed in SQLite voice_sessions table.
  

# To-Do / Changelog

## Fixed (Alpha 0.0.4 patch)

- [x] **Voice WebSocket 403 error when DSGVO enabled**: The server-side DSGVO HTTPS enforcement for WebSocket upgrades now correctly reads `X-Forwarded-Proto` from Traefik (loopback connections). Previously all WS upgrades were rejected because the header value wasn't being parsed properly (comma-separated values, missing `.split(',')[0].trim()`).
- [x] **Voice WebSocket URI with redundant port 443**: The companion app no longer appends `:443` when connecting via `wss://` (default port), preventing potential proxy issues.
- [x] **Testlauf logic bug**: Fixed broken if/else structure and duplicate `log_ok` call in install.sh test-run logic.
- [x] **limit NaN bugs (4x)**: All `limit` query params in admin endpoints now use `parseInt` + `Number.isFinite` with safe defaults.
- [x] **admin/bans syntax error**: Removed errant `{}` that caused handler body to run outside its closure.
- [x] **admin/ban undefined `kicked`**: Added `_voiceRelay.kickUser()` call before referencing result.
- [x] **admin/dsgvo/delete-and-ban undefined `kicked`**: Same fix applied.
- [x] **admin/log-level double response**: Removed premature `res.json()`, added proper success response.
- [x] **freqId range validation**: REST routes `/freq/join` and `/freq/leave` now validate 1000–9999.
- [x] **Security middleware**: Added `helmet`, `cors`, `express-rate-limit` npm deps; helmet headers, CORS rejection policy, global + auth rate limiters.
- [x] **WebSocket origin check**: Upgrade handler rejects connections with browser Origin header.
- [x] **/ws hub authentication**: State WebSocket now requires valid token in query string.
- [x] **Concurrent session limit**: Voice relay caps 3 sessions per user.
- [x] **ConfigService race condition**: Replaced TOCTOU check-then-move with try-move-catch for config folder migration.
- [x] **VoiceService pong validation**: Heartbeat loop now tracks last pong timestamp and reconnects after 30s timeout.
- [x] **.gitignore**: Added `**/bin/` and `**/obj/` patterns for .NET build artifacts.

## Open

- [ ] Reconnect logic: auto-reconnect voice WebSocket on disconnect without requiring PTT press
- [ ] Connection pooling: keep a single voice connection alive across PTT presses instead of connecting per-press
- [ ] service.sh: fix `show_dsgvo_warnings` — dangling `fi` without matching `if`


