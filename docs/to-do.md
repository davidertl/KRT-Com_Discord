High priority:
-add encryption to all out and inbound communication (websocket, audio)
-please also don't handle userid and clanid unhashed. 
-Channel-ID's can be unencrypted.


(?) please add a tool for "DsGVO-compliance" to the service.sh panel, so that the admin can easily delete all data related to a specific user (discord id) when requested. this should include all entries in the database and also the session tokens. within this should be the options to:
  -delete all data related to a specific user (discord id) - this should include all entries in the database and also the session tokens.
  -delete all data related to a specific guild (guild id) - this should include all entries in the database and also the session tokens.
  -set a server-side variable for dsgvo compliance mode, that when enabled, automatically deletes all user data (database entries and session tokens) after a certain period of time (of 2 days). this variable should be set to true when the compliance mode is enabled. when this mode is enabled, the server should automatically delete all user data that is older than 2 days. this should be done via a scheduled task that runs every 24 hours and deletes all data that is older than 2 days. please also add a tool to the service.sh panel to manually trigger this cleanup task, so that the admin can easily delete all old data when needed. when debug mode is enabled the dsgvo mode will be disabled and the automatic cleanup will run only every 7 days instead of 2 days, to make debugging easier. if any debug tool is running automatically disable the dsgvo compliance mode, so that the data is not deleted while debugging. please also add a warning message to the service.sh panel when the dsgvo compliance mode is disabled and show which tool is active.
  > Fix: Added src/dsgvo.js module with deleteUser/deleteGuild/runCleanup/startScheduler (24h interval). Retention: 2 days normal, 7 days debug. Debug mode auto-disables DSGVO. 6 REST admin endpoints added to http.js. service.sh menu items 20-25 for DSGVO status/toggle/debug/delete-user/delete-guild/cleanup with warnings on menu start. .env: DSGVO_ENABLED, DEBUG_MODE.

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

   

Low priority: 
-make the tx and rx beep of emergency radio more signficant, so that it is clear that the emergency radio is active. maybe also add a visual indicator in the companion app.
-add a recent to the emergency radio - when sending an emergency call (ptt on Emergency Radio) it should show in the companion app who sent the call and which frequency he is active on. max display 911 and max 3 other radio id's (if the user who is sending the emergency call is active on more than 3 frequencies, then just show the 3 most active ones).
On emergendy radio in the ui the calling person shoudl be highlighted in the black window on the right hand side of the green Channel-ID-Display. 

-ingame overlay wer zulezt auf welcher Frequenz gefunkt hat. 
    -essentiell mit on/off toggle im "App Settings" Tab
    -frage bitte bevor du dieses Projekt anfängst nach deutlichen Instruuktionen. 
    -optional mit Rang (kann erst später implementiert werden, wenn ich weiß wie das Kartell die Daten angelegt hat)

Security Audit Notes (as of 2025-02-17):
Security:

  Critical:
  -All traffic defaults to plaintext HTTP/WS — admin token, Discord user IDs, guild IDs, session tokens, and raw Opus audio travel unencrypted. Need TLS (HTTPS/WSS) via reverse proxy (nginx/caddy) or direct Node.js HTTPS.
  -Voice WebSocket auth only requires discordUserId + guildId (no password/token) — anyone who knows a user's Discord ID can impersonate them, join frequencies, and transmit audio.
  -Most REST endpoints (/freq/join, /freq/leave, /tx/event, /state, /users/recent, etc.) have zero authentication — anyone with network access can read all user data and manipulate frequencies.
  
  High:
  -Admin token sent as cleartext HTTP header (x-admin-token) — sniffable on the network.
  -Session token transmitted in plaintext over WS (auth_ok message).
  -Admin token stored in plaintext in %APPDATA%/das-KRT_com/config.json — use Windows DPAPI (ProtectedData) to encrypt.
  -/ws WebSocket hub has no authentication — any client immediately receives full snapshot of all voice states (user IDs, guild IDs, frequencies).
  -Self-reported user identity in REST requests (discordUserId in body) trusted without verification.
  -No rate limiting on any HTTP endpoint or WebSocket auth — enables brute force / resource exhaustion.
  -No concurrent session limit per user — unlimited sessions can be created for resource exhaustion.
  -No CORS policy on Express server — any web page can make cross-origin requests.
  -No WebSocket origin checking on voice.js or ws.js — malicious web pages can connect.
  
  Medium:
  -Discord bot token in plaintext .env file (permissions 600 is OK but still cleartext).
  -Session tokens stored unhashed in SQLite voice_sessions table.
  -/freq/join and /freq/leave missing freqId range validation (1000-9999).
  -limit query parameter not sanitized against NaN.
  -No Express security headers (helmet middleware missing).
  -User enumeration via unauthenticated REST endpoints.
  -Server logs expose user PII (Discord IDs, display names).
  -Auth error messages enable user enumeration ("user not found in guild").
  -No session expiration policy — sessions live forever with heartbeats.
  -No admin mechanism to revoke/kill specific sessions.
  -curl | bash pattern in install.sh for NodeSource install (supply-chain risk).

  Low:
  -Client VoiceHost/VoicePort not validated before use.
  -Debug log writes sensitive connection parameters in plaintext.
  -Command injection risk in install.sh interactive menu (user input interpolated into curl -d JSON).
