# High priority:

  
# Medium priority: 
- Derzeit werden Sprachdaten zwar über TLS zum Server gesendet, aber auf der Strecke zwischen Server und Clients über UDP ausgetauscht. Um ein wirklich verschlüsseltes Funksystem zu erreichen, sollte die Audio-Übertragung selbst Ende-zu-Ende oder zumindest Ende-zu-Server-zu-Ende verschlüsselt werden. Eine Erweiterung wäre, pro Funk-Frequenz einen verschlüsselten Sprachkanal einzurichten. Praktisch könnte das so aussehen: Beim Frequenzbeitritt erzeugt der Server oder die Clients einen zufälligen Session-Schlüssel (z.B. via sicheren Diffie-Hellman-Austausch oder vom Server generiert und über den TLS-gesicherten WebSocket verteilt). Alle Teilnehmer dieser Frequenz verwenden diesen Schlüssel, um die Opus-Audiopakete vor dem Versand zu verschlüsseln (und entsprechend nach Empfang zu entschlüsseln). Der Server würde die verschlüsselten Pakete lediglich weiterleiten, ohne sie selbst zu decodieren. So wären die Audioinhalte auch dann vertraulich, wenn jemand den UDP-Datenverkehr abhört. Diese zusätzliche Verschlüsselung könnte optional oder für bestimmte als sensibel markierte Frequenzen aktiviert werden. Im Projekt-Backlog ist bereits ein ähnliches Konzept vorgesehen (“Zusätzliche Verschlüsselung per Keyphrase”). Die Implementierung lässt sich mit begrenztem Aufwand ergänzen, da auf Client-Seite dank der bestehenden Concentus-Bibliothek bereits Byte-Buffer der Audiopakete vorliegen – diese könnten vor dem Senden mit z.B. AES-GCM symmetrisch verschlüsselt werden. Wichtig ist, einen Schlüsselaustausch-Mechanismus zu integrieren, der in die bestehende WebSocket-Kontrollverbindung passt (z.B. als Teil der Authentifizierung beim Frequenzbeitritt). Insgesamt würde diese Maßnahme die Vertraulichkeit der Sprachkommunikation erheblich stärken, ohne die grundlegende Architektur (WebSocket-Steuerkanal + UDP-Datenkanal) zu verändern.
# Low priority:


# General wishlist (quasi meine Notizen damit ich nix vergesse):
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

## Security audit notes for version 0.0.8. AI generated:

### 🔴 Critical

- [ ] **Missing `discordUserId` destructuring in `/auth/login`**: The variable `discordUserId` is referenced but never destructured from `req.body` in the handler. This causes a `ReferenceError` at runtime (in debug mode), potentially leaking stack trace information. Add proper destructuring and validate that it is a non-empty string matching Discord snowflake format.

### 🟠 High

- [ ] **SQL injection pattern (template literal table names)**: `migrateUserIdHashing` and `deleteUser` in `install.sh` interpolate table names via JS template literals (`${table}`). Currently the `tables` array is hardcoded so not directly exploitable, but this pattern is fragile — any future refactoring introducing user-controlled values would create a direct SQL injection vector. Use a whitelist check or parameterize differently.
- [ ] **Undefined variable `kicked` in `/admin/unban`**: The handler logs `${kicked}` but only defines `removed`. This causes a `ReferenceError` at runtime, crashing the request and potentially leaking stack trace info. Replace with `removed`.
- [ ] **Admin token plaintext storage & non-timing-safe comparison**: `ADMIN_TOKEN` is stored in plaintext in `.env` and `config.json`, transmitted via `x-admin-token` header, and compared with `!==` (not timing-safe). Use `crypto.timingSafeEqual()` for comparison. Consider hashing the stored token.
- [ ] **No CSRF protection on admin POST endpoints**: All admin state-changing endpoints (`/admin/ban`, `/admin/unban`, `/admin/dsgvo/delete-user`, `/admin/dsgvo/debug`, etc.) rely solely on the `x-admin-token` header. If an attacker discovers the token, there is no secondary CSRF mechanism (no nonce, no session binding).
- [ ] **OAuth2 `state` parameter no expiry cleanup visible**: The `pendingOAuth` map stores OAuth state tokens but no cleanup/expiry mechanism is visible in the code. Old state tokens could accumulate (memory leak) and potentially be replayed. Add a `setTimeout` or periodic sweep to expire states after e.g. 5 minutes.

### 🟡 Medium

- [ ] **Auth token ID truncated to 64 chars (collision risk)**: `token_id` in the DB uses `authToken.substring(0, 64)`. Different tokens sharing the same 64-char prefix would overwrite each other. Store the full hash or use a separate UUID as the DB key.
- [ ] **No rate limiting on voice WebSocket authentication**: The voice relay WebSocket accepts `auth` messages without rate limiting. An attacker could open many connections and brute-force session tokens. Add per-IP connection rate limiting on the upgrade handler.
- [ ] **No warning when `BIND_HOST` is not `127.0.0.1`**: If an operator changes `BIND_HOST` to `0.0.0.0` in `.env`, the backend is directly exposed without TLS (Traefik bypassed). Add a startup warning log when `BIND_HOST` is not loopback.
- [ ] **Companion app config encryption strength unknown**: `ConfigService.cs` imports `System.Security.Cryptography` but `AuthToken`, `AdminToken`, and other sensitive fields are persisted in `config.json`. Verify that encryption uses a strong key derivation (e.g. DPAPI or machine-bound key), not a predictable/hardcoded key.
- [ ] **90-second ghost session window**: Stale session cleanup uses a 60s timeout with 30s intervals, meaning a disconnected client could remain "active" and receive audio for up to 90 seconds. Reduce the cleanup interval or timeout, and verify `freq_listeners` DB entries are also cleaned up.
- [ ] **Incomplete JSON escaping in `service.sh`**: The `json_escape` function only handles `\`, `"`, `\n`, `\r`, `\t`. Other control characters (`\b`, `\f`, null bytes, Unicode control chars) are not escaped. A malicious input could cause JSON parsing issues or injection in the backend.
- [ ] **Discord access token revocation failure silently swallowed**: If the `fetch` to Discord's `/oauth2/token/revoke` fails, only a `console.warn` is emitted. The token remains valid despite the privacy policy claiming immediate revocation. Add retry logic or at minimum log at error level.

### 🟢 Low

- [ ] **No cache headers on `/privacy-policy`**: The endpoint doesn't set `Cache-Control` or `ETag` headers. Clients can't detect policy changes efficiently. Add appropriate cache headers.
- [ ] **No input validation in `fix_encoding.py`**: `sys.argv[1]` is used directly as a file path without validation. A crafted path could read/write arbitrary files. Add path validation (dev tool, low risk).
- [ ] **Debug mode couples DSGVO disable**: Enabling debug mode automatically disables DSGVO compliance and extends data retention to 7 days. An operator might inadvertently violate GDPR. Decouple debug logging from DSGVO settings.
- [ ] **No WebSocket message size limits**: Voice relay `ws.on('message')` parses JSON without checking message size. An attacker could send very large payloads to consume memory. Add `maxPayload` option to the WebSocket server.
- [ ] **Helmet uses defaults (no strict CSP)**: `app.use(helmet())` uses default configuration without a strict `Content-Security-Policy` or `Permissions-Policy`. The OAuth success redirect page would benefit from a strict CSP. Configure helmet with explicit CSP rules.


# Changelog

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

## Fixed (Alpha 0.0.6 — Voice Ducking & Muted TX Guard)

- [x] **Voice ducking system**: Full ducking pipeline with global slider (0–100%), enable/disable toggle, and per-radio ducking level override (checkbox + slider in each radio panel).
- [x] **Duck-on-send / duck-on-receive checkboxes**: Users can independently choose to duck when transmitting, when receiving, or both.
- [x] **Duck-on-receive is external-only**: Ducking on receive only affects external applications (via WASAPI AudioSessionAPI), never the internal radio audio being received.
- [x] **External app ducking via AudioDuckingService**: WASAPI-based ducking of external applications with three target modes: Radio audio only / Selected apps / All audio except KRT-Com.
- [x] **Internal radio ducking on send**: Received radio audio volume is reduced by the ducking multiplier while the user is transmitting.
- [x] **Separate ducking card in App Settings**: Dedicated UI card with enable toggle, send/receive checkboxes, global slider, ducking target selector, and process picker.
- [x] **Per-radio ducking level**: Each radio panel has a checkbox to override the global ducking level with a custom per-radio slider.
- [x] **Ducking settings persistence**: All ducking settings (enabled, send/receive, level, mode, process list, per-radio overrides) saved to config and re-pushed on reconnect.
- [x] **Debug logging for ducking events**: `[Ducking]`-prefixed log entries for TX start/stop, RX start/stop, apply/restore, and level changes.
- [x] **Muted TX guard**: Transmitting on a muted frequency is blocked (except for broadcasts).

## Fixed (Alpha 0.0.7 — UI, Settings, Broadcast & Auth)

- [x] **Reconnect logic hardened**: Max retries increased from 10 to 50 for production resilience. State restoration logging added after reconnect.
- [x] **Connection pooling**: Voice WebSocket stays open between PTT presses — already implemented, verified and marked complete.
- [x] **Removed testPTT button**: Test PTT button removed from footer bar, along with `StartTestAsync`/`StopTestAsync` methods.
- [x] **Disconnect button on Server tab**: Added a "Disconnect" button next to the voice connection status indicator in the Voice Server card header.
- [x] **Auth display fixed on restart**: `LoggedInDisplayName` is now persisted in `CompanionConfig` and restored on app restart, so the UI correctly shows the logged-in username instead of the login button.
- [x] **Auto-connect on frequency change**: When changing frequency on an active radio, the client automatically leaves the old frequency and joins the new one without requiring a PTT press or save.
- [x] **App Settings reordered**: Settings tab reorganized into two horizontal cards — "General" (sound toggles, emergency radio) and "Autostart + Debug" (startup options, debug logging). Settings order matches the to-do specification.
- [x] **Granular beep/sound toggles**: Single "Play beep" checkbox replaced with 4 independent sub-toggles: "Enable sound on receiving", "Enable sound on transmitting", "Enable sound on beginning", "Enable sound on end". All persisted to config.
- [x] **Broadcast pan/vol averaging**: When receiving the same broadcast audio on multiple radios, duplicate audio frames are deduplicated. The first-arriving frequency's settings are used, and duplicates are suppressed within a 20ms window.
- [x] **Session tokens**: Server already hashes session tokens (SHA-256) before DB storage. Security audit note marked as fixed.

## Fixed (Alpha 0.0.8 — In-Game Overlay)

- [x] **In-game overlay window**: New topmost, transparent, click-through WPF overlay (`OverlayWindow.xaml`) that displays active radio status and recent transmissions. Uses Win32 `WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW` for click-through and alt-tab hiding.
- [x] **Overlay settings card in App Settings**: Enable/disable toggle, show rank checkbox, show radio keybind checkbox, background opacity slider (10–100%), and position X/Y inputs.
- [x] **Background opacity**: Opacity slider controls only the overlay background alpha channel — text and status indicators remain fully readable at any setting.
- [x] **Only active radios shown**: The overlay only displays radios that are currently transmitting, receiving, or have had recent transmissions. Idle radios with no history are hidden.
- [x] **Sorted by last active**: Overlay entries are sorted by most-recently-active first. Sorting updates on every TX/RX event.
- [x] **Recent transmission prominent on right**: Layout redesigned — radio name on top of frequency on the left, last transmission (username + time) right-aligned in larger/bolder text. Row background highlights red during RX and green during TX.
- [x] **Auto-hide inactive radios**: New "Auto-hide inactive radios" checkbox + slider (5–300 seconds, default 60s). A background timer removes overlay entries that haven't had TX/RX activity for longer than the configured duration. Currently active radios are never removed.
- [x] **Compact overlay design**: Reduced overlay width (260px), smaller fonts, tighter spacing for minimal screen footprint.
- [x] **Live settings sync**: Position, opacity, show-keybind, and show-rank changes push to the overlay window in real-time without restart.
- [x] **Overlay lifecycle**: Overlay opens/closes with the enable toggle, refreshes on settings changes, and closes cleanly on app dispose.

## Open
