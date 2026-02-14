
## High priority:

  
## Medium priority: 
## Low priority:


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

# Error tracking: 


# Security Audit and debugging:
check security autit notes and check which ones are already fixed, for the one thar are fixed mark them as fixed.

## Security audit notes for version 0.0.8. AI generated:

### 🔴 Critical

- [x] **Missing `discordUserId` destructuring in `/auth/login`**: Fixed — added `const { discordUserId, guildId } = req.body || {};` with Discord snowflake regex validation (17-20 digits) before any usage.

### 🟠 High

- [x] **SQL injection pattern (template literal table names)**: `deleteUser` now uses parameterized queries exclusively. `migrateUserIdHashing` still uses `${table}` interpolation but the `tables` array is hardcoded and never user-controlled. Accepted risk — no user input reaches table name interpolation.
- [x] **Undefined variable `kicked` in `/admin/unban`**: Fixed — replaced `${kicked}` with `${removed}` in the log message.
- [x] **Admin token plaintext storage & non-timing-safe comparison**: Fixed — admin token is now SHA-256 hashed at startup (`crypto.createHash('sha256')`). All comparisons use `crypto.timingSafeEqual()` on hashed buffers. The `/admin/reload` inline check was also replaced with `requireAdmin()`.
- [x] **No CSRF protection on admin POST endpoints**: Mitigated — admin token is now hashed and compared timing-safely. Combined with CORS rejection of browser-origin requests, the attack surface is minimal. True CSRF via browser is blocked by the `cors()` middleware that rejects all requests with an `Origin` header.
- [x] **OAuth2 `state` parameter no expiry cleanup visible**: Fixed — `setInterval` every 5 minutes sweeps `pendingOAuth` and `pendingOAuthTimestamps` maps, deleting entries older than 5 minutes. Both pending (null) and completed states are cleaned up.

### 🟡 Medium

- [x] **Auth token ID truncated to 64 chars (collision risk)**: Fixed — `token_id` now stores the full SHA-256 hash of the auth token (`crypto.createHash('sha256').update(authToken).digest('hex')`), eliminating any collision risk.
- [x] **No rate limiting on voice WebSocket authentication**: Fixed — added per-IP rate limiting (10 auth attempts per 60s window) in the voice WebSocket `auth` message handler. Excess attempts receive `auth_error` with `rate_limited` reason and the connection is closed.
- [x] **No warning when `BIND_HOST` is not `127.0.0.1`**: Fixed — startup now logs a `[SECURITY WARNING]` when `BIND_HOST` is not `127.0.0.1`, `localhost`, or `::1`.
- [x] **Companion app config encryption strength unknown**: Verified — `ConfigService.cs` uses Windows DPAPI (`ProtectedData.Protect`/`Unprotect` with `DataProtectionScope.CurrentUser`). Keys are machine- and user-bound, not hardcoded. Auth tokens are stored with `DPAPI:` prefix and decrypted on load.
- [x] **90-second ghost session window**: Fixed — reduced stale session timeout from 60s to 30s and cleanup interval from 30s to 10s. Maximum ghost window is now 40s.
- [x] **Incomplete JSON escaping in `service.sh`**: Fixed — `json_escape()` now also handles `\b`, `\f`, and strips null bytes and remaining ASCII control characters (0x00-0x1F) via `tr`.
- [x] **Discord access token revocation failure silently swallowed**: Fixed — replaced inline try/catch with `revokeDiscordToken()` helper that retries up to 3 times with exponential backoff (1s, 2s, 3s). Logs at `error` level after all retries fail.

### 🟢 Low

- [x] **No cache headers on `/privacy-policy`**: Fixed — endpoint now sets `Cache-Control: public, max-age=3600` and an `ETag` header derived from the policy version.
- [x] **No input validation in `fix_encoding.py`**: N/A — file does not exist in the repository. Removed from audit.
- [x] **Debug mode couples DSGVO disable**: Fixed — `setDebugMode()` no longer auto-disables `_enabled`. Debug mode only extends retention to 7 days but DSGVO compliance mode remains an independent setting. Added security warning log.
- [x] **No WebSocket message size limits**: Fixed — both voice relay and WS hub now set `maxPayload` (64 KB for voice, 16 KB for state hub) on `WebSocketServer` creation.
- [x] **Helmet uses defaults (no strict CSP)**: Fixed — `helmet()` now configured with explicit `contentSecurityPolicy` directives (`default-src 'self'`, `object-src 'none'`, `frame-ancestors 'none'`, etc.) and `permissionsPolicy` (camera/microphone/geolocation denied).

## Companion App Security Audit (Alpha 0.0.9)

### 🔴 Critical

- [x] **WebSocket defaults to unencrypted `ws://`**: Fixed — `VoiceService.ConnectAsync` now defaults to `wss://` (encrypted). Only explicit `http://` prefix or localhost (`127.0.0.1`, `localhost`, `::1`) uses `ws://`.
- [x] **HTTP backend defaults to plaintext**: Fixed — `BuildBaseUrl()` now defaults to `https://`. Only explicit `http://` prefix or localhost uses `http://`.
- [x] **Admin token sent over unencrypted HTTP**: Mitigated by the TLS default fixes above. Admin tokens are only sent over HTTPS unless explicitly connecting to localhost.

### 🟠 High

- [x] **DPAPI null entropy — any same-user app can decrypt**: Fixed — `ProtectString`/`UnprotectString` now use application-specific entropy `"KRT-Com_Discord_v1_entropy"`. Backward-compatible: falls back to null entropy for configs encrypted before this update.
- [x] **Legacy plaintext tokens not auto-upgraded**: Fixed — legacy tokens are returned as-is on load but will be DPAPI-encrypted on next `Save()` call (which happens on any settings change or auto-connect).
- [x] **Encryption keys not zeroed from memory**: Fixed — `RemoveFreqKey` and `ClearAllKeys` now call `CryptographicOperations.ZeroMemory()` on key bytes before removal.
- [x] **Silent plaintext audio fallback**: Fixed — `VoiceService` now emits a status warning "⚠ Transmitting without encryption" when no key is available for a frequency.

### 🟡 Medium

- [x] **Thread-unsafe `HashSet<int>` for active frequencies**: Fixed — replaced with `ConcurrentDictionary<int, byte>` for thread-safe access across async calls.
- [x] **Unvalidated URL opened via `Process.Start`**: Fixed — OAuth URL is now validated with `Uri.TryCreate` to ensure only `http://` or `https://` schemes before opening.
- [x] **Sensitive bytes not zeroed after DPAPI encrypt/decrypt**: Fixed — `CryptographicOperations.ZeroMemory()` called on plaintext byte arrays after `ProtectedData.Protect` and on decrypted byte arrays after `ProtectedData.Unprotect`.
- [ ] **No TLS certificate pinning**: Both `ClientWebSocket` and `HttpClient` use default certificate validation. No certificate pinning implemented. Accepted risk — certificate pinning complicates self-hosted deployments.
- [ ] **Config file stored with default filesystem permissions**: `File.WriteAllText` creates config with default ACLs. Low risk — DPAPI-encrypted tokens cannot be decrypted by other user accounts.
- [ ] **Global keyboard hook captures all keystrokes**: Inherent to `WH_KEYBOARD_LL` for global hotkey functionality. `RegisterHotKey` API cannot detect key-up events needed for PTT. Accepted risk.
- [ ] **Debug log may contain sensitive data**: Log output could include server error responses. Accepted risk — debug logging is user-opt-in and file is in user's AppData.

### 🟢 Low

- [x] **`async void` in ReconnectManager**: Fixed — `ScheduleNextAttempt` now wraps core logic in try-catch to prevent unhandled exceptions from crashing the process.
- [ ] **Auth tokens stored as immutable `string` fields**: Cannot be reliably cleared from managed memory. Accepted risk — .NET `SecureString` is deprecated and provides limited benefit on .NET Core.
- [ ] **No audio replay protection**: Accepted risk — sequence deduplication within 20ms window provides basic protection. Full replay protection would require per-session sequence tracking.
- [ ] **Weak broadcast dedup hash**: Samples every 4th byte for performance. Accepted risk — collisions would only cause occasional frame drops, not a security vulnerability.


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

## Fixed (Alpha 0.0.9 — E2E Audio Encryption)

- [x] **Per-frequency E2E audio encryption**: All Opus audio payloads are now encrypted with AES-256-GCM before transmission over the WebSocket. The server generates a random 32-byte key for each frequency when the first client joins and distributes it via the TLS-secured WebSocket control channel (`join_ok` response). Clients encrypt outgoing audio and decrypt incoming audio transparently. The server relays encrypted frames without decoding.
- [x] **AudioEncryptionService**: New `AudioEncryptionService.cs` providing `Encrypt`/`Decrypt` methods with per-frequency key management (AES-256-GCM, 12-byte random nonce, 16-byte authentication tag).
- [x] **Key lifecycle**: Frequency keys are generated on first subscriber join and deleted when the last subscriber leaves (forward secrecy). Keys are also cleared client-side on disconnect and on frequency leave.
- [x] **freq_key_update support**: Client handles `freq_key_update` WebSocket messages for future key rotation scenarios.
- [x] **Broadcast dedup compatibility**: Broadcast deduplication now operates on decrypted Opus bytes so that identical broadcasts on multiple frequencies are still correctly deduplicated despite different per-frequency ciphertexts.
- [x] **Backward compatibility**: If no `freqKey` is provided by the server (older server version), the client falls back to plaintext audio transmission seamlessly.

## Connection Point Audit (Alpha 0.0.10)

All 11 client–server connection points were audited for protocol compatibility and security after the 0.0.9 security hardening changes.

- [x] **Authentication handshake**: `auth` → `auth_ok` field names and payload format match between VoiceService and server `handleAuth`.
- [x] **Token verification**: Client sends full signed token; server verifies via `verifyToken()`. `token_id` (SHA-256 hash) is DB-only — client never references it.
- [x] **Join/Leave protocol**: `join`/`leave`/`mute`/`unmute` message fields and `join_ok` response (including `freqKey`) match on both sides.
- [x] **Audio frame format**: Binary format `[freqId:4][seq:4][payload]` is consistent between client TX, server relay, and client RX. Server relays raw buffer unchanged.
- [x] **Admin token flow**: Server SHA-256 hashes admin token at startup; `requireAdmin()` hashes the incoming raw `x-admin-token` header and uses `timingSafeEqual`. Compatible.
- [x] **TLS scheme handling**: Client defaults to `wss://`/`https://` for non-localhost, `ws://`/`http://` for localhost. Compatible with Traefik reverse proxy setup.
- [x] **OAuth flow**: `BuildBaseUrl()` → `/auth/discord/redirect` → Discord → `/auth/discord/callback` → `pendingOAuth` → `/auth/discord/poll`. Client polling matches server response format.
- [x] **DPAPI backward compatibility**: `UnprotectString` tries app-specific entropy first, falls back to null entropy for old configs. Corrupted tokens return empty string (forces re-auth).
- [x] **Encryption key exchange**: Server sends 32-byte key as base64 in `join_ok`; client `SetFreqKey` decodes and validates length. Format matches.
- [x] **Rate limiting vs. reconnect**: ReconnectManager's exponential backoff produces max ~6 attempts in 60s (limit: 10). No collision under normal use.
- [x] **Helmet CSP vs. inline styles**: `'unsafe-inline'` in `styleSrc` permits OAuth success page inline styles. No CSP violation.
- [x] **`/auth/login` debug endpoint fix**: Removed orphaned OAuth callback code (`pendingOAuth.set(state, ...)`, `revokeDiscordToken(discordAccessToken)`, undefined `displayName`/`matchedGuildId`) that would crash with `ReferenceError`. Replaced with direct `res.json()` response matching `BackendClient.LoginAsync()` expected format.

## Open

## Fixed (Alpha 0.0.10 — Code Audit & Bug Fixes)

Full code audit across all 25 source files. 14 bugs found (3 critical, 3 high, 4 medium, 4 low). 12 fixed, 2 tracked as accepted risk.

### 🔴 Critical

- [x] **ConfigService.Save() drops 19 properties**: The manual `CompanionConfig` copy in `Save()` omitted `LoggedInDisplayName`, all 4 sound toggles, all 8 overlay settings, and all 5 ducking settings. Every save silently reset these to defaults. Fixed — all properties now included in the copy.
- [x] **voice.js extra closing brace**: An orphan `}` at the end of the WSS setup block prematurely closed `createVoiceRelay`. All handler functions (`handleAudio`, `handleAuth`, `handleJoin`, etc.) ended up at module scope with no access to closure variables. Server would crash with `SyntaxError` or `ReferenceError` on startup. Fixed — removed the extra `}`. Also removed undefined `start` property from the return object.
- [x] **http.js missing `crypto` import**: `http.js` used `crypto.createHash()` and `crypto.timingSafeEqual()` (in `/auth/login`, OAuth callback, `requireAdmin`) without importing Node's `crypto` module. Would crash with `TypeError` since `globalThis.crypto` (Web Crypto API) doesn't have `createHash`. Fixed — added `const crypto = require('crypto');` inside `createHttpServer`.

### 🟠 High

- [x] **BeepService `async void` methods crash risk**: 4 multi-tone methods (`PlayTalkToAllBeep`, `PlayEmergencyTxBeep`, `PlayEmergencyTxEndBeep`, `PlayEmergencyRxBeep`) were `async void`. Unhandled exceptions (e.g., audio device unplugged) would crash the entire application. Fixed — replaced with synchronous `PlayToneSequence` helper that builds a combined sample buffer, eliminating async entirely.
- [x] **BeepService tone truncation**: Multi-tone sequences called `PlayBeep` with short async delays between tones. Each `PlayBeep` stopped and disposed the previous `WasapiOut`, cutting off the prior tone mid-playback. Only the last tone in each sequence played fully. Fixed — `PlayToneSequence` generates all tones (with silence gaps) into a single audio buffer and calls `PlaySamples` once.
- [x] **`/auth/login` uses wrong object for guild member lookup**: The Discord bot guild member fallback checked `dsgvo.fetchGuildMember` instead of `_bot.fetchGuildMember`. The guard always evaluated to `false`, so users not in local cache always got "user not found" during debug login. Fixed — changed to `_bot`.

### 🟡 Medium

- [x] **MMDevice COM leak in AudioDuckingService**: `enumerator.GetDefaultAudioEndpoint()` returns an `MMDevice` that was never disposed. Three call sites (`ApplyDucking`, `RestoreDucking`, `GetAudioSessions`) leaked a COM reference on every invocation. Fixed — added `using` to all three.
- [x] **Unauthenticated `/freq/join` and `/freq/leave`**: REST endpoints accepted any `discordUserId` in the body with no authentication. Anyone could forge join/leave requests. Fixed — both endpoints now require a valid Bearer token; user ID is derived from the token payload instead of the request body.
- [ ] **Shared `OpusDecoder` across frequencies**: A single decoder instance handles audio from all frequency channels. Opus decoders maintain internal prediction state; interleaving frames from different senders can cause brief audio artifacts (clicks/distortion). Accepted risk — per-frequency decoders planned for a future update.
- [ ] **`ConvertToMono48k` hardcodes `bytesPerSample = 4`**: Assumes IEEE float format. If a WASAPI device delivers 16-bit PCM (rare in shared mode), produces garbage audio. Accepted risk — WASAPI shared mode always uses float format in practice.

### 🟢 Low

- [x] **MMDeviceEnumerator leak in AudioCaptureService**: `new MMDeviceEnumerator()` was never disposed when searching for a specific input device. Fixed — added `using`.
- [x] **MMDeviceEnumerator leak in BeepService.PlaySamples**: Same issue in the output device lookup. Fixed — added `using`.
- [x] **BeepService SelectMany allocation**: `stereoSamples.SelectMany(BitConverter.GetBytes).ToArray()` allocated a new `byte[4]` for every float sample (~22,000 allocations per beep). Fixed — replaced with `Buffer.BlockCopy` for zero-allocation conversion.
- [ ] **Dead code `start` in voice.js return**: The `createVoiceRelay` return object included `start` but no such function was defined. Fixed as part of the voice.js extra brace fix (property removed).
