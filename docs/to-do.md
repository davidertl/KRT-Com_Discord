High priority:
-add encryption to all out and inbound communication (websocket, audio)
  > (✔) Implemented: Traefik reverse proxy with Let's Encrypt TLS. HTTP→HTTPS redirect. DSGVO-HTTPS-Enforcement middleware rejects plaintext traffic when DSGVO compliance mode is active. install.sh step [7/12] installs Traefik, [11/12] configures TLS + domain. service.sh menu items 60-63 for Traefik management.
-please also don't handle userid and clanid unhashed. 
  > (✔) FIXED: All discord_user_id values are hashed via HMAC-SHA256 (using TOKEN_SECRET) before database storage. Raw Discord snowflake IDs are never persisted. Hash function: hashUserId() in src/crypto.js. All modules updated: discord.js (bot events), http.js (auth + data endpoints), voice.js (WebSocket auth), dsgvo.js (ban/policy functions). Admin endpoints accept raw IDs and hash before lookup. banned_users table stores raw_discord_id for admin display. Automatic migration on startup converts existing raw IDs to hashes.

(✔) Token-based authentication & consent flow
  > Fix: Added src/crypto.js (HMAC-SHA256 signToken/verifyToken, 24h expiry). POST /auth/login validates user via Discord bot, returns signed token. Voice WS auth now requires authToken. New DB tables: banned_users, auth_tokens, policy_acceptance. Companion App: Verify button fetches GET /server-status + GET /privacy-policy. Privacy policy displayed with Accept button. Login gated behind policy acceptance. Token persisted for auto-reconnect.
  > Bugfix: IsServerVerified setter was not notifying PolicyNeedsAcceptance/CanLogin — privacy policy accept button and login fields were hidden after verify. Fixed.

(✔) Discord OAuth2 Login (Alpha 0.0.4)
  > Implemented full Discord OAuth2 authorization code flow. Server: .env vars DISCORD_CLIENT_ID/SECRET/REDIRECT_URI; GET /auth/discord/redirect → Discord authorize; GET /auth/discord/callback → code exchange, user identity fetch, guild verification, HMAC token issuance; GET /auth/discord/poll → companion app retrieves token via state param. OAuth result includes policyAccepted status. Discord access token revoked after use. Companion App: "Login with Discord" button (disabled until privacy policy accepted). OAuth flow: generates state → opens browser → polls /auth/discord/poll every 2s up to 3min → on success sets AuthToken, auto-connects voice.
  > Security Hardening (Alpha 0.0.4): POST /auth/login gated behind Debug Mode — disabled by default, returns HTTP 410 in production. Only available when admin enables debug mode via service.sh (menu item 50). OAuth poll response stripped of raw discordUserId/guildId — only returns token + displayName. /server-status exposes debugLoginEnabled flag.
  > Admin-Token Hardening (Alpha 0.0.4): Admin token removed from Companion App config persistence (runtime-only, never saved to disk). Admin token UI field only visible when server debug mode is active. ServerBaseUrl (dead code) removed entirely.
  > service.sh: Menu item 51 added for updating Discord OAuth2 credentials (Client ID, Client Secret, Redirect URI) without re-running install.sh.

(✔) Ban management
  > Fix: Added banned_users DB table. POST /admin/ban, DELETE /admin/ban/:userId, GET /admin/bans REST endpoints. Login and voice auth reject banned users. service.sh menu items 40-43 for ban/unban/list/delete-and-ban.
  -please add ban2fail setup to the service.sh panel, if a user gets banned so he can unban them easily, list them, or add a ip to be banned.
  -add a debug tool to log ip adresses of users who are logging in. Please mark this mode clearly as a under-attack-mode and write this info to privacy policy, so that the users are aware of this mode and can make an informed decision about logging in. please also add a warning message to the service.sh panel when this mode is active, so that the admin is aware of the potential privacy implications.
    - please revoke the under attack mode after a custom set time.

(✔) DSGVO-compliance tool
  > Fix: Added src/dsgvo.js module with deleteUser/deleteGuild/runCleanup/startScheduler (24h interval). Retention: 2 days normal, 7 days debug. Debug mode auto-disables DSGVO. 6 REST admin endpoints added to http.js. service.sh menu items 20-25 for DSGVO status/toggle/debug/delete-user/delete-guild/cleanup with warnings on menu start. .env: DSGVO_ENABLED, DEBUG_MODE.
        -please add the possibility to set the retention time for the user data when the dsgvo compliance mode is enabled, so that the admin can choose how long the data should be kept before it is automatically deleted. this should be a custom set time in days, that can be configured via the service.sh panel. please also add a warning message to the service.sh panel when the retention time is set to a high value (more than 7 days), so that the admin is aware of the potential implications of setting a high retention time.

Medium priority: 
(✔) the count of active listeners does not properly show if users are talking
  > Fix: Listener count now only updates via voice WS join/leave/listener_update messages. Server broadcasts listener_update to ALL subscribers on join/leave/TX stop. Client calls JoinFrequencyAsync/LeaveFrequencyAsync when radio IsEnabled changes and when emergency radio is toggled.

(✔) toggle the mute radio serverside
  > Fix: Server voice.js handles 'mute'/'unmute' WS messages per-session per-frequency. Audio forwarding skips muted receivers. Server confirms with 'mute_ok'. Client sends mute/unmute on IsMuted/IsEnabled change.

(✔) channel-name sync from Discord
  > Fix: mapping.js tracks freqId→channelName, discord.js runs scheduled re-scan (default 24h, configurable). REST: GET /freq/names, admin channel-sync endpoints. service.sh menu items 30-32. Client shows channel names below frequency.
    -if the user changes to a channel that has no name, please do not show a name, but also do not show "undefined" or "null" or something like that. just hide the name if there is no name for the channel.
    -also keep the window for the channel the same size, so even if no name is displayed, the space is kept for the name.

Low priority: 
-make the tx and rx beep of emergency radio more signficant, so that it is clear that the emergency radio is active. maybe also add a visual indicator in the companion app.
-add a recent to the emergency radio - when sending an emergency call (ptt on Emergency Radio) it should show in the companion app who sent the call and which frequency he is active on. max display 911 and max 3 other radio id's (if the user who is sending the emergency call is active on more than 3 frequencies, then just show the 3 most active ones).
On emergendy radio in the ui the calling person should be highlighted in the black window on the right hand side of the green Channel-ID-Display. 

-ingame overlay wer zuletzt auf welcher Frequenz gefunkt hat. 
    -essentiell mit on/off toggle im "App Settings" Tab
    -frage bitte bevor du dieses Projekt anfängst nach deutlichen Instruktionen. 
    -optional mit Rang (kann erst später implementiert werden, wenn ich weiß wie das Kartell die Daten angelegt hat)

Security Audit Notes (as of 2025-02-17, updated 2026-02-09):
Security:

  Critical:
  -All traffic defaults to plaintext HTTP/WS — admin token, Discord user IDs, guild IDs, session tokens, and raw Opus audio travel unencrypted. Need TLS (HTTPS/WSS) via reverse proxy.
    > FIXED: Traefik reverse proxy deployed via install.sh. Let's Encrypt TLS auto-provisioned. HTTP→HTTPS permanent redirect. DSGVO-HTTPS-Enforcement middleware in http.js rejects non-HTTPS requests when DSGVO enabled. WebSocket upgrade handler also enforces X-Forwarded-Proto check.
  -Voice WebSocket auth only requires discordUserId + guildId (no password/token).
    > FIXED: Voice WS now requires HMAC-SHA256 signed authToken. Server validates token signature + expiry + ban status.
  -Most REST endpoints have zero authentication.
    > Partial Fix: Auth endpoints require signed token (Bearer header). Public endpoints limited to /server-status and /privacy-policy. Admin endpoints require x-admin-token. Freq/TX endpoints still use self-reported identity.
  -Self-reported user identity (discordUserId in login body) trusted without verification.
    > FIXED (Alpha 0.0.4): Discord OAuth2 replaces self-reported identity. POST /auth/login gated behind debug mode (HTTP 410 in production). Identity now verified via Discord OAuth2 authorization code flow.
      -when state is logged in as username please remove the login with discord button. you need to logout first to see the button again, or the loggin state changes, by any other reason.

  High:
  -Admin token sent as cleartext HTTP header (x-admin-token).
    > FIXED: TLS deployed via Traefik. All external traffic encrypted.
  -Session token transmitted in plaintext over WS.
    > FIXED: TLS deployed via Traefik. WSS used for all external connections. Token is HMAC-signed (not guessable).
  -Admin token stored in plaintext in %APPDATA%/das-KRT_com/config.json.
    > FIXED (Alpha 0.0.4): Admin token no longer persisted to config.json. Runtime-only in ViewModel, UI only visible in debug mode.
  -/ws WebSocket hub has no authentication.
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
  -Session tokens stored unhashed in SQLite voice_sessions table.
  -/freq/join and /freq/leave missing freqId range validation (1000-9999).
  -limit query parameter not sanitized against NaN.
  -No Express security headers (helmet middleware missing).
  -User enumeration via unauthenticated REST endpoints.
    > Partial Fix: /server-status returns only version/DSGVO status/debug mode — no user data.
  -Server logs expose user PII (Discord IDs, display names).
  -Auth error messages enable user enumeration.
    > FIXED: Login returns generic "Login failed" on all auth failures.
  -No session expiration policy.
    > FIXED: Auth tokens expire after 24 hours. Token expiry checked on voice WS auth.
  -No admin mechanism to revoke/kill specific sessions.
    > Partial Fix: Ban system blocks login + voice auth. Individual token revocation not yet implemented.
  -OAuth poll response leaked raw discordUserId/guildId.
    > FIXED (Alpha 0.0.4): Poll response stripped to token + displayName only.

  Low:
  -Client VoiceHost/VoicePort not validated before use.
  -Debug log writes sensitive connection parameters in plaintext.
  -Command injection risk in install.sh interactive menu (user input interpolated into curl -d JSON).

  Low:
  -Client VoiceHost/VoicePort not validated before use.
  -Debug log writes sensitive connection parameters in plaintext.
  -Command injection risk in install.sh interactive menu (user input interpolated into curl -d JSON).
