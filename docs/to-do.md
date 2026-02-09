High priority:
-add encryption to all out and inbound communication (websocket, audio)
  > Pending: TLS (HTTPS/WSS) via Traefik reverse proxy planned but not yet deployed. install.sh ready for Traefik integration.
-please also don't handle userid and clanid unhashed. 

(?) Token-based authentication & consent flow
  > Fix: Added src/crypto.js (HMAC-SHA256 signToken/verifyToken, 24h expiry). POST /auth/login validates user via Discord bot, returns signed token. Voice WS auth now requires authToken. New DB tables: banned_users, auth_tokens, policy_acceptance. Companion App: Verify button fetches GET /server-status + GET /privacy-policy. Privacy policy displayed with Accept button. Login gated behind policy acceptance. Token persisted for auto-reconnect.
  > Bugfix: IsServerVerified setter was not notifying PolicyNeedsAcceptance/CanLogin — privacy policy accept button and login fields were hidden after verify. Fixed.

(?) Replace temporary Discord User ID + Guild ID login form with proper Discord OAuth (Identify + Guilds scope). Current manual login is a security risk (user self-reports identity). Implement OAuth2 authorization code flow: Companion App opens browser → Discord authorize → redirect with code → backend exchanges code for access token → fetches user identity → issues signed auth token. This eliminates the need for users to manually enter their Discord User ID.
  > Fix: Implemented full Discord OAuth2 authorization code flow. Server: new .env vars DISCORD_CLIENT_ID/SECRET/REDIRECT_URI; GET /auth/discord/redirect → Discord authorize; GET /auth/discord/callback → code exchange, user identity fetch, guild verification, HMAC token issuance; GET /auth/discord/poll → companion app retrieves token via state param. OAuth result includes policyAccepted status. Discord access token revoked after use. Companion App: replaced Discord User ID + Guild ID text fields with "Login with Discord" button. Button disabled until privacy policy is accepted (with hint text explaining why). OAuth flow: generates state → opens browser → polls /auth/discord/poll every 2s up to 3min → on success sets AuthToken, DiscordUserId, GuildId from server response → auto-connects voice. New converters: InverseBoolToVisibilityConverter, StringToVisibilityConverter. ServerStatusInfo now includes OauthEnabled flag.
  > Security Hardening: POST /auth/login gated behind Debug Mode — disabled by default, only available when admin enables debug mode via service.sh (menu item 50) or POST /admin/dsgvo/debug. OAuth poll response no longer returns raw discordUserId/guildId — signed token contains all identity info. /server-status now exposes debugLoginEnabled flag.

(?) Ban management
  > Fix: Added banned_users DB table. POST /admin/ban, DELETE /admin/ban/:userId, GET /admin/bans REST endpoints. Login and voice auth reject banned users. service.sh menu items 40-43 for ban/unban/list/delete-and-ban.
  -please add ban2fail setup to the service.sh panel, if a user gets banned so he can unban them easily, list them, or add a ip to be banned.
  -add a debug tool to log ip adresses of users who are logging in. Please mark this mode clearly as a under-attack-mode and write this info to privacy policy, so that the users are aware of this mode and can make an informed decision about logging in. please also add a warning message to the service.sh panel when this mode is active, so that the admin is aware of the potential privacy implications.
    - please revoke the under attack mode after a custom set time.

(?) please add a tool for "DsGVO-compliance" to the service.sh panel, so that the admin can easily delete all data related to a specific user (discord id) when requested. this should include all entries in the database and also the session tokens. within this should be the options to:
  -delete all data related to a specific user (discord id) - this should include all entries in the database and also the session tokens.
  -delete all data related to a specific guild (guild id) - this should include all entries in the database and also the session tokens.
  -set a server-side variable for dsgvo compliance mode, that when enabled, automatically deletes all user data (database entries and session tokens) after a certain period of time (of 2 days). this variable should be set to true when the compliance mode is enabled. when this mode is enabled, the server should automatically delete all user data that is older than 2 days. this should be done via a scheduled task that runs every 24 hours and deletes all data that is older than 2 days. please also add a tool to the service.sh panel to manually trigger this cleanup task, so that the admin can easily delete all old data when needed. when debug mode is enabled the dsgvo mode will be disabled and the automatic cleanup will run only every 7 days instead of 2 days, to make debugging easier. if any debug tool is running automatically disable the dsgvo compliance mode, so that the data is not deleted while debugging. please also add a warning message to the service.sh panel when the dsgvo compliance mode is disabled and show which tool is active.
  > Fix: Added src/dsgvo.js module with deleteUser/deleteGuild/runCleanup/startScheduler (24h interval). Retention: 2 days normal, 7 days debug. Debug mode auto-disables DSGVO. 6 REST admin endpoints added to http.js. service.sh menu items 20-25 for DSGVO status/toggle/debug/delete-user/delete-guild/cleanup with warnings on menu start. .env: DSGVO_ENABLED, DEBUG_MODE.
        -please add the possibility to set the retention time for the user data when the dsgvo compliance mode is enabled, so that the admin can choose how long the data should be kept before it is automatically deleted. this should be a custom set time in days, that can be configured via the service.sh panel. please also add a warning message to the service.sh panel when the retention time is set to a high value (more than 7 days), so that the admin is aware of the potential implications of setting a high retention time.

Medium priority: 
(?) the count of active listeners does not properly show if users are talking, it raises when i push ptt but also lowers wenn i let go. should be the amount of people listening, not speaking
  > Fix: Removed ListenerCount updates from TX event REST responses. Listener count now only updates via voice WS join/leave/listener_update messages. Also added server-side cleanup of stale freq_listeners DB rows on restart.
    (?) But now the first user who joins a frequency shows 0 listeners until the second user joins, then it shows 2 listener for secondly joined users. The first user still sees 0 listeners. please update the listener count for all users in the frequency when releasing ptt, so that the first user also sees the correct listener count.
      > Fix: Server now broadcasts listener_update to ALL subscribers (including sender) on TX stop via notifyTxEvent. Every user on the frequency gets the correct count after a transmission ends.
      (?) very good, when activating radio or deactivating radio please also update. can you check for all radios the state of the listeners and update accordingly?
        > Fix: Client now calls JoinFrequencyAsync/LeaveFrequencyAsync when radio IsEnabled changes (OnRadioPanelPropertyChanged) and when emergency radio is toggled (HandleEmergencyRadioToggleAsync). Server broadcasts listener_update to all subscribers on join/leave.

(?) toggle the mute radio serverside, so that the client can just send a "mute" command and the server will ignore all audio packets from that user until "unmute" is sent. please always send a confirmation with the status and the radio id back to the client, so that the client can update the ui accordingly.
  > Fix: Server voice.js now handles 'mute'/'unmute' WS messages per-session per-frequency. Audio forwarding skips muted receivers. Server confirms with 'mute_ok' including freqId + muted status. Client sends mute/unmute on IsMuted/IsEnabled change and pushes all mute states on connect.

(?) please make a task that checks regularyly if there are new channel-names on the discord server with a 4didget number in brackets e.g. testkanal (2042). update the channel.json file accordingly, so that the names are always up to date. check this regularly every 24 hours. add a menu to service.sh to manually trigger this update. also add a option to set the frequency of this check. please also add the name of the discord-channel, so that the companion app shows this name after entering an frequency that is linked to a discord channel. 
  > Fix: mapping.js now tracks freqId→channelName, saves discovered mappings+names to channels.json via save(). discord.js runs scheduled re-scan (default 24h, configurable via CHANNEL_SYNC_INTERVAL_HOURS env var). REST: GET /freq/names (public), GET/POST /admin/channel-sync/status|trigger|interval. service.sh menu items 30-32 for sync status/trigger/interval. Client: RadioPanelViewModel.ChannelName property, BackendClient.GetFreqNamesAsync(), channel names displayed below frequency in UI (italic, 70% opacity). Names fetched on voice connect.
    -if the user changes to a channel that has no name, please do not show a name, but also do not show "undefined" or "null" or something like that. just hide the name if there is no name for the channel.
    -also keep the window for the channel the same size, so even if no name is displayed, the space is kept for the name that way the ui does not jump when changing to a channel with a name and a channel without a name.

   

Low priority: 
-make the tx and rx beep of emergency radio more signficant, so that it is clear that the emergency radio is active. maybe also add a visual indicator in the companion app.
-add a recent to the emergency radio - when sending an emergency call (ptt on Emergency Radio) it should show in the companion app who sent the call and which frequency he is active on. max display 911 and max 3 other radio id's (if the user who is sending the emergency call is active on more than 3 frequencies, then just show the 3 most active ones).
On emergendy radio in the ui the calling person shoudl be highlighted in the black window on the right hand side of the green Channel-ID-Display. 

-ingame overlay wer zulezt auf welcher Frequenz gefunkt hat. 
    -essentiell mit on/off toggle im "App Settings" Tab
    -frage bitte bevor du dieses Projekt anfängst nach deutlichen Instruuktionen. 
    -optional mit Rang (kann erst später implementiert werden, wenn ich weiß wie das Kartell die Daten angelegt hat)

Security Audit Notes (as of 2025-02-17, updated 2026-02-09):
Security:

  Critical:
  -All traffic defaults to plaintext HTTP/WS — admin token, Discord user IDs, guild IDs, session tokens, and raw Opus audio travel unencrypted. Need TLS (HTTPS/WSS) via reverse proxy (nginx/caddy) or direct Node.js HTTPS.
    > Still open. Traefik reverse proxy script designed but not yet deployed.
  -Voice WebSocket auth only requires discordUserId + guildId (no password/token) — anyone who knows a user's Discord ID can impersonate them, join frequencies, and transmit audio.
    > FIXED: Voice WS now requires HMAC-SHA256 signed authToken obtained via POST /auth/login. Server validates token signature + expiry + ban status before allowing connection.
  -Most REST endpoints (/freq/join, /freq/leave, /tx/event, /state, /users/recent, etc.) have zero authentication — anyone with network access can read all user data and manipulate frequencies.
    > Partial Fix: Auth endpoints require signed token (Bearer header). Public endpoints limited to /server-status and /privacy-policy. Admin endpoints require x-admin-token. Freq/TX endpoints still use self-reported identity — to be migrated to token-based identity extraction.
  
  High:
  -Admin token sent as cleartext HTTP header (x-admin-token) — sniffable on the network.
    > Mitigated once TLS is deployed.
  -Session token transmitted in plaintext over WS (auth_ok message).
    > Mitigated once TLS is deployed. Token is now HMAC-signed (not guessable).
  -Admin token stored in plaintext in %APPDATA%/das-KRT_com/config.json — use Windows DPAPI (ProtectedData) to encrypt.
    > Still open.
  -/ws WebSocket hub has no authentication — any client immediately receives full snapshot of all voice states (user IDs, guild IDs, frequencies).
    > Still open.
  -Self-reported user identity in REST requests (discordUserId in body) trusted without verification.
    > Partial Fix: Login endpoint verifies user via Discord bot guild member lookup. Voice WS uses signed token. REST freq/TX endpoints still trust self-reported identity.
  -No rate limiting on any HTTP endpoint or WebSocket auth — enables brute force / resource exhaustion.
    > Still open.
  -No concurrent session limit per user — unlimited sessions can be created for resource exhaustion.
    > Still open.
  -No CORS policy on Express server — any web page can make cross-origin requests.
    > Still open.
  -No WebSocket origin checking on voice.js or ws.js — malicious web pages can connect.
    > Still open.
  
  Medium:
  -Discord bot token in plaintext .env file (permissions 600 is OK but still cleartext).
  -Session tokens stored unhashed in SQLite voice_sessions table.
  -/freq/join and /freq/leave missing freqId range validation (1000-9999).
  -limit query parameter not sanitized against NaN.
  -No Express security headers (helmet middleware missing).
  -User enumeration via unauthenticated REST endpoints.
    > Partial Fix: /server-status returns only version/DSGVO status/debug mode — no user data. Login returns generic errors.
  -Server logs expose user PII (Discord IDs, display names).
  -Auth error messages enable user enumeration ("user not found in guild").
    > FIXED: Login returns generic "Login failed" on all auth failures.
  -No session expiration policy — sessions live forever with heartbeats.
    > FIXED: Auth tokens expire after 24 hours (configurable). Token expiry checked on voice WS auth.
  -No admin mechanism to revoke/kill specific sessions.
    > Partial Fix: Ban system allows banning users (blocks login + voice auth). Individual token revocation not yet implemented.
  -curl | bash pattern in install.sh for NodeSource install (supply-chain risk).

  Low:
  -Client VoiceHost/VoicePort not validated before use.
  -Debug log writes sensitive connection parameters in plaintext.
  -Command injection risk in install.sh interactive menu (user input interpolated into curl -d JSON).
