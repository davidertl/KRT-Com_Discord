#!/usr/bin/env bash
##version alpha-0.0.3
set -e

# Wenn versehentlich mit sh/dash gestartet wurde, in bash neu starten
if [ -z "${BASH_VERSION:-}" ]; then
  exec /usr/bin/env bash "$0" "$@"
fi

# --------------------------------------------------
# Farben & Logging Helper
# --------------------------------------------------
RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

log_ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
log_info()  { echo -e "${GREEN}[INFO]${NC} $*"; }
log_warn()  { echo -e "${RED}[WARN]${NC} $*"; }
log_error() { echo -e "${RED}[ERROR]${NC} $*"; }
log_input() { echo -e "${CYAN}$*${NC}"; }

VERSION="Alpha 0.0.2"
echo -e "${GREEN}=== das-krt Bootstrap | ${VERSION} ===${NC}"

# --------------------------------------------------
# Variablen
# --------------------------------------------------
ADMIN_USER="ops"
APP_ROOT="/opt/das-krt"
NODE_VERSION="24"
SERVICE_FILE="/etc/systemd/system/das-krt-backend.service"

BACKEND_DIR="$APP_ROOT/backend"
SRC_DIR="$BACKEND_DIR/src"
ENV_FILE="$BACKEND_DIR/.env"
CHANNEL_MAP="$APP_ROOT/config/channels.json"
TEST_LOG="$APP_ROOT/logs/backend-test.log"

# --------------------------------------------------
# Helper: Port check
# --------------------------------------------------
port_in_use() {
  local host="$1"
  local port="$2"
  ss -lnt "( sport = :$port )" 2>/dev/null | awk 'NR>1{print $4}' | grep -q "${host}:${port}" 2>/dev/null
}

# --------------------------------------------------
# Helper: Safe overwrite (backup if exists + differs)
# --------------------------------------------------
write_file_backup() {
  local path="$1"
  local content="$2"
  local ts
  ts="$(date +%Y%m%d-%H%M%S)"

  mkdir -p "$(dirname "$path")"

  if [ -f "$path" ]; then
    # write temp and compare
    local tmp
    tmp="$(mktemp)"
    printf "%s" "$content" > "$tmp"
    if ! cmp -s "$tmp" "$path"; then
      cp -a "$path" "$path.bak.$ts"
      printf "%s" "$content" > "$path"
      log_ok "Updated: $(basename "$path") (Backup: $(basename "$path").bak.$ts)"
    else
      log_ok "Unchanged: $(basename "$path")"
    fi
    rm -f "$tmp"
  else
    printf "%s" "$content" > "$path"
    log_ok "Created: $(basename "$path")"
  fi

  chown "$ADMIN_USER:$ADMIN_USER" "$path" 2>/dev/null || true
}

# --------------------------------------------------
# Basis-Pakete
# --------------------------------------------------
log_info "[1/10] Installiere Basis-Pakete"
apt update
apt -y install sudo curl wget git fail2ban ca-certificates gnupg lsb-release
log_ok "Basis-Pakete installiert"
apt upgrade -y

# --------------------------------------------------
# Zeitzone
# --------------------------------------------------
log_info "[2/10] Setze Zeitzone"
timedatectl set-timezone Europe/Berlin
log_ok "Zeitzone gesetzt: Europe/Berlin"

# --------------------------------------------------
# Admin-User (idempotent)
# --------------------------------------------------
log_info "[3/10] Erstelle Admin-User"
if getent passwd "$ADMIN_USER" > /dev/null; then
  log_ok "User $ADMIN_USER existiert bereits"
else
  adduser --disabled-password --gecos "" "$ADMIN_USER"
  log_ok "User $ADMIN_USER erstellt"
fi
usermod -aG sudo "$ADMIN_USER"
log_ok "User $ADMIN_USER in sudo Gruppe (sichergestellt)"

# --------------------------------------------------
# SSH-Härtung (optional)
# Hinweis: du hast das bei dir absichtlich auskommentiert – bleibt so.
# --------------------------------------------------
log_info "[4/10] SSH-Härtung übersprungen (Script bewusst neutral halten)"

# --------------------------------------------------
# Fail2ban
# --------------------------------------------------
log_info "[5/10] Aktiviere Fail2ban"
systemctl enable fail2ban >/dev/null 2>&1 || true
systemctl start fail2ban >/dev/null 2>&1 || true
log_ok "Fail2ban läuft (oder war bereits aktiv)"

# --------------------------------------------------
# Node.js 24
# --------------------------------------------------
log_info "[6/10] Installiere Node.js ${NODE_VERSION}"
curl -fsSL "https://deb.nodesource.com/setup_${NODE_VERSION}.x" | bash -
apt -y install nodejs
log_ok "Node.js installiert: $(node -v) | npm: $(npm -v)"

# --------------------------------------------------
# Projektstruktur
# --------------------------------------------------
log_info "[8/10] Lege Projektverzeichnisse an"
mkdir -p "$BACKEND_DIR" "$APP_ROOT/config" "$APP_ROOT/logs" "$SRC_DIR"
chown -R "$ADMIN_USER:$ADMIN_USER" "$APP_ROOT" || true
log_ok "Projektstruktur bereit: $APP_ROOT"

# --------------------------------------------------
# Backend Initialisierung (Dependencies)
# --------------------------------------------------
log_info "[9/10] Initialisiere Backend (npm)"
cd "$BACKEND_DIR"

if [ ! -f package.json ]; then
  sudo -u "$ADMIN_USER" npm init -y >/dev/null
  log_ok "package.json erstellt"
else
  log_ok "package.json existiert bereits"
fi

# Hinweis: Debian npm-Paket kann mit NodeSource nodejs kollidieren – wir nutzen npm aus NodeSource.
sudo -u "$ADMIN_USER" npm install discord.js express ws dotenv better-sqlite3 >/dev/null
log_ok "npm Dependencies installiert/aktualisiert"

# --------------------------------------------------
# systemd Service
# --------------------------------------------------
log_info "[10/10] Erstelle/Update systemd Service"
cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=das-krt Backend (${VERSION})
After=network.target

[Service]
User=$ADMIN_USER
WorkingDirectory=$BACKEND_DIR
ExecStart=/usr/bin/node index.js
Restart=always
EnvironmentFile=$ENV_FILE

[Install]
WantedBy=multi-user.target
EOF
log_ok "systemd Service geschrieben: $SERVICE_FILE"

systemctl daemon-reload
systemctl enable das-krt-backend >/dev/null 2>&1 || true
log_ok "systemd Service enabled"

# --------------------------------------------------
# Config defaults
# --------------------------------------------------
if [ ! -f "$CHANNEL_MAP" ]; then
  cat > "$CHANNEL_MAP" <<'EOF'
{
  "discordChannelToFreqId": {
    "123456789012345678": 1050,
    "234567890123456789": 1060
  }
}
EOF
  chown -R "$ADMIN_USER:$ADMIN_USER" "$APP_ROOT/config" || true
  log_ok "Beispiel channels.json erstellt: $CHANNEL_MAP"
else
  log_ok "channels.json existiert bereits: $CHANNEL_MAP (nicht überschrieben)"
fi

# --------------------------------------------------
# Backend Skeleton (Alpha 0.0.2)
# - TX Events (REST + WS broadcast)
# - User directory (discord_users)
# --------------------------------------------------

# index.js
write_file_backup "$BACKEND_DIR/index.js" "$(cat <<'EOF'
'use strict';

const path = require('path');
require('dotenv').config({ path: path.join(__dirname, '.env') });

const { createHttpServer } = require('./src/http');
const { createWsHub } = require('./src/ws');
const { initDb } = require('./src/db');
const { createTxStore } = require('./src/tx');
const { createUsersStore } = require('./src/users');

const { createDiscordBot } = require('./src/discord');
const { createMappingStore } = require('./src/mapping');
const { createStateStore } = require('./src/state');
const { createVoiceRelay } = require('./src/voice');

function mustEnv(name) {
  const v = process.env[name];
  if (!v) throw new Error(`Missing env var: ${name}`);
  return v;
}

(async () => {
  const bindHost = process.env.BIND_HOST || '127.0.0.1';
  const bindPort = Number(process.env.BIND_PORT || '3000');
  const voiceUdpPort = Number(process.env.VOICE_UDP_PORT || '5060');

  const dbPath = mustEnv('DB_PATH');
  const mapPath = mustEnv('CHANNEL_MAP_PATH');

  const db = initDb(dbPath);
  const mapping = createMappingStore(mapPath);
  const stateStore = createStateStore(db);
  const txStore = createTxStore(db);
  const usersStore = createUsersStore(db);

  const httpServer = createHttpServer({
    db,
    mapping,
    stateStore,
    txStore,
    usersStore,
    adminToken: process.env.ADMIN_TOKEN || '',
    allowedGuildIds: process.env.DISCORD_GUILD_ID
      ? process.env.DISCORD_GUILD_ID.split(',')
      : [],
  });

  const wsHub = createWsHub(httpServer, { stateStore });

  // Voice relay (WebSocket control + UDP audio)
  const voiceRelay = createVoiceRelay({
    httpServer,
    db,
    usersStore,
    udpPort: voiceUdpPort,
    allowedGuildIds: process.env.DISCORD_GUILD_ID
      ? process.env.DISCORD_GUILD_ID.split(',')
      : [],
  });
  voiceRelay.start();

  // Wire TX broadcast (keine circular deps)
  if (typeof httpServer._setOnTxEvent === 'function') {
    httpServer._setOnTxEvent((payload) => wsHub.broadcast({ type: 'tx_event', payload }));
  }

  // Discord voice_state broadcast bleibt wie gehabt
  const bot = createDiscordBot({
    token: mustEnv('DISCORD_TOKEN'),
    guildId: process.env.DISCORD_GUILD_ID || null,
    mapping,
    stateStore,
    usersStore,
    onStateChange: (payload) => wsHub.broadcast({ type: 'voice_state', payload }),
  });

  httpServer.listen(bindPort, bindHost, async () => {
    console.log(`[http] listening on http://${bindHost}:${bindPort}`);
    console.log(`[voice] UDP relay on port ${voiceUdpPort}`);
    console.log(`[map] loaded ${mapping.size()} channel mappings from ${mapPath}`);
    await bot.start();
  });
})();
EOF
)"

# src/db.js
write_file_backup "$SRC_DIR/db.js" "$(cat <<'EOF'
'use strict';

const Database = require('better-sqlite3');

function initDb(dbPath) {
  const db = new Database(dbPath);

  db.pragma('journal_mode = WAL');
  db.pragma('synchronous = NORMAL');

  db.exec(`
    CREATE TABLE IF NOT EXISTS voice_state (
      discord_user_id TEXT NOT NULL,
      guild_id        TEXT NOT NULL,
      channel_id      TEXT,
      freq_id         INTEGER,
      updated_at_ms   INTEGER NOT NULL,
      PRIMARY KEY (discord_user_id)
    );

    CREATE INDEX IF NOT EXISTS idx_voice_state_updated_at
      ON voice_state(updated_at_ms);

    CREATE TABLE IF NOT EXISTS tx_events (
      id              INTEGER PRIMARY KEY AUTOINCREMENT,
      freq_id         INTEGER NOT NULL,
      discord_user_id TEXT,
      radio_slot      INTEGER,
      action          TEXT NOT NULL CHECK(action IN ('start','stop')),
      ts_ms           INTEGER NOT NULL,
      meta_json       TEXT
    );

    CREATE INDEX IF NOT EXISTS idx_tx_events_ts
      ON tx_events(ts_ms);

    CREATE INDEX IF NOT EXISTS idx_tx_events_freq_ts
      ON tx_events(freq_id, ts_ms);

    -- User Directory (für "Server-Namen", nicht global_name)
    CREATE TABLE IF NOT EXISTS discord_users (
      discord_user_id TEXT NOT NULL,
      guild_id        TEXT NOT NULL,
      display_name    TEXT,
      updated_at_ms   INTEGER NOT NULL,
      PRIMARY KEY (discord_user_id, guild_id)
    );

    CREATE INDEX IF NOT EXISTS idx_discord_users_updated_at
      ON discord_users(updated_at_ms);

    CREATE INDEX IF NOT EXISTS idx_discord_users_guild_updated_at
      ON discord_users(guild_id, updated_at_ms);

    -- Frequency listener tracking (active radio users per freq)
    CREATE TABLE IF NOT EXISTS freq_listeners (
      discord_user_id TEXT NOT NULL,
      freq_id         INTEGER NOT NULL,
      radio_slot      INTEGER DEFAULT 0,
      connected_at_ms INTEGER NOT NULL,
      PRIMARY KEY (discord_user_id, freq_id)
    );

    CREATE INDEX IF NOT EXISTS idx_freq_listeners_freq
      ON freq_listeners(freq_id);

    -- Voice relay sessions
    CREATE TABLE IF NOT EXISTS voice_sessions (
      session_token   TEXT PRIMARY KEY,
      discord_user_id TEXT NOT NULL,
      guild_id        TEXT NOT NULL,
      display_name    TEXT,
      created_at_ms   INTEGER NOT NULL,
      last_seen_ms    INTEGER NOT NULL
    );

    CREATE INDEX IF NOT EXISTS idx_voice_sessions_user
      ON voice_sessions(discord_user_id);
  `);

  return db;
}

module.exports = { initDb };
EOF
)"

# src/tx.js
write_file_backup "$SRC_DIR/tx.js" "$(cat <<'EOF'
'use strict';

function createTxStore(db) {
  const insertStmt = db.prepare(`
    INSERT INTO tx_events (freq_id, discord_user_id, radio_slot, action, ts_ms, meta_json)
    VALUES (@freq_id, @discord_user_id, @radio_slot, @action, @ts_ms, @meta_json)
  `);

  const listRecentStmt = db.prepare(`
    SELECT id, freq_id, discord_user_id, radio_slot, action, ts_ms, meta_json
    FROM tx_events
    ORDER BY ts_ms DESC
    LIMIT ?
  `);

  const listRecentByFreqStmt = db.prepare(`
    SELECT id, freq_id, discord_user_id, radio_slot, action, ts_ms, meta_json
    FROM tx_events
    WHERE freq_id = ?
    ORDER BY ts_ms DESC
    LIMIT ?
  `);

  return {
    addEvent: (row) => insertStmt.run(row),
    listRecent: (limit = 200) => listRecentStmt.all(limit),
    listRecentByFreq: (freqId, limit = 200) => listRecentByFreqStmt.all(freqId, limit),
  };
}

module.exports = { createTxStore };
EOF
)"

# src/users.js
write_file_backup "$SRC_DIR/users.js" "$(cat <<'EOF'
'use strict';

function createUsersStore(db) {
  const upsertStmt = db.prepare(`
    INSERT INTO discord_users (discord_user_id, guild_id, display_name, updated_at_ms)
    VALUES (@discord_user_id, @guild_id, @display_name, @updated_at_ms)
    ON CONFLICT(discord_user_id, guild_id) DO UPDATE SET
      display_name = excluded.display_name,
      updated_at_ms = excluded.updated_at_ms
  `);

  const getStmt = db.prepare(`
    SELECT discord_user_id, guild_id, display_name, updated_at_ms
    FROM discord_users
    WHERE discord_user_id = ? AND guild_id = ?
  `);

  const listRecentStmt = db.prepare(`
    SELECT discord_user_id, guild_id, display_name, updated_at_ms
    FROM discord_users
    ORDER BY updated_at_ms DESC
    LIMIT ?
  `);

  return {
    upsert: (row) => upsertStmt.run(row),
    get: (discordUserId, guildId) => getStmt.get(String(discordUserId), String(guildId)) || null,
    listRecent: (limit = 200) => listRecentStmt.all(limit),
  };
}

module.exports = { createUsersStore };
EOF
)"

# src/voice.js - Voice relay (WebSocket control + UDP audio)
write_file_backup "$SRC_DIR/voice.js" "$(cat <<'EOF'
'use strict';

const dgram = require('dgram');
const { WebSocketServer } = require('ws');
const crypto = require('crypto');
const url = require('url');

/**
 * Voice Relay
 * - Companion clients connect via WebSocket to /voice for control signaling
 *   (auth, join/leave frequency, heartbeat)
 * - Opus audio is exchanged via UDP
 * - Packet format: [4 bytes freqId BE][4 bytes sequence BE][opus data]
 * - UDP handshake: freqId=0 + session token as payload
 */
function createVoiceRelay({ httpServer, db, usersStore, udpPort = 5060, allowedGuildIds = [] }) {
  // Session management
  const sessions = new Map();       // sessionToken -> { discordUserId, guildId, displayName, ws, udpAddr, udpPort, frequencies: Set, lastSeen }
  const udpClients = new Map();     // 'ip:port' -> sessionToken

  // Frequency subscriptions: freqId -> Set<sessionToken>
  const freqSubscribers = new Map();

  const udpSocket = dgram.createSocket('udp4');

  // --- WebSocket control plane ---
  let wss = null;

  function start() {
    // Create WS server on /voice path, sharing the HTTP server
    wss = new WebSocketServer({ server: httpServer, path: '/voice' });

    wss.on('connection', (ws, req) => {
      let sessionToken = null;

      ws.on('message', (raw) => {
        let msg;
        try { msg = JSON.parse(raw); } catch { return; }

        switch (msg.type) {
          case 'auth':
            handleAuth(ws, msg, (token) => { sessionToken = token; });
            break;
          case 'join':
            if (sessionToken) handleJoin(sessionToken, msg);
            break;
          case 'leave':
            if (sessionToken) handleLeave(sessionToken, msg);
            break;
          case 'ping':
            if (sessionToken) {
              const s = sessions.get(sessionToken);
              if (s) s.lastSeen = Date.now();
              ws.send(JSON.stringify({ type: 'pong' }));
            }
            break;
        }
      });

      ws.on('close', () => {
        if (sessionToken) cleanupSession(sessionToken);
      });

      ws.on('error', () => {
        if (sessionToken) cleanupSession(sessionToken);
      });
    });

    // --- UDP audio plane ---
    udpSocket.on('message', (buf, rinfo) => {
      if (buf.length < 8) return;

      const freqId = buf.readUInt32BE(0);
      const key = rinfo.address + ':' + rinfo.port;

      // Handshake: freqId=0, payload = session token
      if (freqId === 0) {
        const token = buf.slice(8).toString('utf-8').trim();
        const session = sessions.get(token);
        if (session) {
          session.udpAddr = rinfo.address;
          session.udpPort = rinfo.port;
          udpClients.set(key, token);
          // Send ack
          const ack = Buffer.alloc(8);
          ack.writeUInt32BE(0, 0);
          ack.writeUInt32BE(1, 4);
          udpSocket.send(ack, rinfo.port, rinfo.address);
          console.log('[voice] UDP handshake OK for', session.discordUserId);
        }
        return;
      }

      // Audio packet: forward to all subscribers of this frequency except sender
      const senderToken = udpClients.get(key);
      if (!senderToken) return;

      const senderSession = sessions.get(senderToken);
      if (!senderSession) return;

      // Verify sender is subscribed to this frequency
      if (!senderSession.frequencies.has(freqId)) return;

      const subscribers = freqSubscribers.get(freqId);
      if (!subscribers) return;

      // Notify WS listeners about RX start (first packet detection could be added)
      // Forward audio to all other subscribers
      for (const subToken of subscribers) {
        if (subToken === senderToken) continue;
        const sub = sessions.get(subToken);
        if (sub && sub.udpAddr && sub.udpPort) {
          udpSocket.send(buf, sub.udpPort, sub.udpAddr);
        }
      }
    });

    udpSocket.bind(udpPort, '0.0.0.0', () => {
      console.log('[voice] UDP relay listening on port', udpPort);
    });

    // Periodic cleanup of stale sessions (no heartbeat for > 60s)
    setInterval(() => {
      const cutoff = Date.now() - 60000;
      for (const [token, session] of sessions) {
        if (session.lastSeen < cutoff) {
          console.log('[voice] Cleaning up stale session:', session.discordUserId);
          if (session.ws && session.ws.readyState <= 1) {
            session.ws.close(4000, 'timeout');
          }
          cleanupSession(token);
        }
      }
    }, 30000);
  }

  function handleAuth(ws, msg, setToken) {
    const { discordUserId, guildId } = msg;
    if (!discordUserId || !guildId) {
      ws.send(JSON.stringify({ type: 'auth_error', reason: 'missing credentials' }));
      return;
    }

    // Check allowed guilds
    if (allowedGuildIds.length > 0 && !allowedGuildIds.includes(String(guildId))) {
      ws.send(JSON.stringify({ type: 'auth_error', reason: 'guild not allowed' }));
      return;
    }

    // Look up user
    const user = usersStore ? usersStore.get(String(discordUserId), String(guildId)) : null;
    if (!user) {
      ws.send(JSON.stringify({ type: 'auth_error', reason: 'user not found in guild' }));
      return;
    }

    // Generate session token
    const sessionToken = crypto.randomBytes(24).toString('hex');
    const now = Date.now();

    const session = {
      discordUserId: String(discordUserId),
      guildId: String(guildId),
      displayName: user.display_name || String(discordUserId),
      ws,
      udpAddr: null,
      udpPort: null,
      frequencies: new Set(),
      lastSeen: now,
    };
    sessions.set(sessionToken, session);
    setToken(sessionToken);

    // Persist session
    db.prepare(
      'INSERT OR REPLACE INTO voice_sessions (session_token, discord_user_id, guild_id, display_name, created_at_ms, last_seen_ms) VALUES (?,?,?,?,?,?)'
    ).run(sessionToken, session.discordUserId, session.guildId, session.displayName, now, now);

    console.log('[voice] Auth OK:', session.discordUserId, session.displayName);

    ws.send(JSON.stringify({
      type: 'auth_ok',
      sessionToken,
      udpPort,
      displayName: session.displayName,
    }));
  }

  function handleJoin(sessionToken, msg) {
    const session = sessions.get(sessionToken);
    if (!session) return;

    const freqId = Number(msg.freqId);
    if (!Number.isInteger(freqId) || freqId < 1000 || freqId > 9999) {
      session.ws.send(JSON.stringify({ type: 'join_error', reason: 'bad freqId' }));
      return;
    }

    session.frequencies.add(freqId);
    if (!freqSubscribers.has(freqId)) freqSubscribers.set(freqId, new Set());
    freqSubscribers.get(freqId).add(sessionToken);

    console.log('[voice] Join freq', freqId, 'by', session.discordUserId);

    session.ws.send(JSON.stringify({
      type: 'join_ok',
      freqId,
      listenerCount: freqSubscribers.get(freqId).size,
    }));
  }

  function handleLeave(sessionToken, msg) {
    const session = sessions.get(sessionToken);
    if (!session) return;

    const freqId = Number(msg.freqId);
    session.frequencies.delete(freqId);

    const subs = freqSubscribers.get(freqId);
    if (subs) {
      subs.delete(sessionToken);
      if (subs.size === 0) freqSubscribers.delete(freqId);
    }

    console.log('[voice] Leave freq', freqId, 'by', session.discordUserId);

    session.ws.send(JSON.stringify({
      type: 'leave_ok',
      freqId,
    }));
  }

  function cleanupSession(token) {
    const session = sessions.get(token);
    if (!session) return;

    // Remove from all frequency subscriptions
    for (const freqId of session.frequencies) {
      const subs = freqSubscribers.get(freqId);
      if (subs) {
        subs.delete(token);
        if (subs.size === 0) freqSubscribers.delete(freqId);
      }
    }

    // Remove UDP mapping
    if (session.udpAddr && session.udpPort) {
      udpClients.delete(session.udpAddr + ':' + session.udpPort);
    }

    // Remove DB session
    db.prepare('DELETE FROM voice_sessions WHERE session_token = ?').run(token);

    sessions.delete(token);
    console.log('[voice] Session cleaned up:', session.discordUserId);
  }

  return { start };
}

module.exports = { createVoiceRelay };
EOF
)"

# src/mapping.js
write_file_backup "$SRC_DIR/mapping.js" "$(cat <<'EOF'
'use strict';

const fs = require('fs');

// Regex to extract frequency ID from channel name, e.g., "neuer-kanal (1050)" -> 1050
const FREQ_NAME_REGEX = /\((\d{4})\)$/;

function createMappingStore(mapPath) {
  let mapping = new Map();           // channelId -> freqId (static from config)
  let channelNames = new Map();       // channelId -> channelName
  let dynamicMapping = new Map();     // channelId -> freqId (parsed from name)
  let defaultFrequencies = new Set(); // freqIds parsed from Discord channel names (default channels)

  function load() {
    const raw = fs.readFileSync(mapPath, 'utf-8');
    const json = JSON.parse(raw);

    const obj = json.discordChannelToFreqId || {};
    const m = new Map();

    for (const [channelId, freqId] of Object.entries(obj)) {
      const n = Number(freqId);
      if (!Number.isInteger(n) || n < 1000 || n > 9999) {
        throw new Error(`Invalid freqId for channel ${channelId}: ${freqId}`);
      }
      m.set(String(channelId), n);
    }

    mapping = m;
  }

  load();

  // Parse frequency ID from channel name (e.g., "kanal (1050)" -> 1050)
  function parseFreqIdFromName(channelName) {
    if (!channelName) return null;
    const match = channelName.match(FREQ_NAME_REGEX);
    if (match) {
      const freqId = Number(match[1]);
      if (freqId >= 1000 && freqId <= 9999) {
        return freqId;
      }
    }
    return null;
  }

  // Register a Discord channel (called when bot sees channels)
  function registerChannel(channelId, channelName) {
    channelNames.set(String(channelId), channelName);
    const freqId = parseFreqIdFromName(channelName);
    if (freqId) {
      dynamicMapping.set(String(channelId), freqId);
      defaultFrequencies.add(freqId);
      console.log(`[mapping] Registered default freq ${freqId} from channel "${channelName}"`);
    }
  }

  return {
    // Get freqId: prefer static config, fall back to dynamic (from name)
    getFreqIdForChannelId: (channelId) => {
      const id = String(channelId);
      return mapping.get(id) ?? dynamicMapping.get(id) ?? null;
    },
    // Get channel name by ID
    getChannelName: (channelId) => channelNames.get(String(channelId)) ?? null,
    // Register channel with name
    registerChannel,
    // Parse freq from name utility
    parseFreqIdFromName,
    // Get all default frequencies (from Discord channel names)
    getDefaultFrequencies: () => [...defaultFrequencies],
    reload: () => load(),
    size: () => mapping.size + dynamicMapping.size,
  };
}

module.exports = { createMappingStore };
EOF
)"

# src/state.js
write_file_backup "$SRC_DIR/state.js" "$(cat <<'EOF'
'use strict';

function createStateStore(db) {
  const upsertStmt = db.prepare(`
    INSERT INTO voice_state (discord_user_id, guild_id, channel_id, freq_id, updated_at_ms)
    VALUES (@discord_user_id, @guild_id, @channel_id, @freq_id, @updated_at_ms)
    ON CONFLICT(discord_user_id) DO UPDATE SET
      guild_id = excluded.guild_id,
      channel_id = excluded.channel_id,
      freq_id = excluded.freq_id,
      updated_at_ms = excluded.updated_at_ms
  `);

  const getStmt = db.prepare(`
    SELECT discord_user_id, guild_id, channel_id, freq_id, updated_at_ms
    FROM voice_state
    WHERE discord_user_id = ?
  `);

  const listRecentStmt = db.prepare(`
    SELECT discord_user_id, guild_id, channel_id, freq_id, updated_at_ms
    FROM voice_state
    ORDER BY updated_at_ms DESC
    LIMIT ?
  `);

  return {
    upsert: (row) => upsertStmt.run(row),
    get: (discordUserId) => getStmt.get(String(discordUserId)) || null,
    listRecent: (limit = 200) => listRecentStmt.all(limit),
  };
}

module.exports = { createStateStore };
EOF
)"

# src/discord.js
write_file_backup "$SRC_DIR/discord.js" "$(cat <<'EOF'
'use strict';

const { Client, GatewayIntentBits, ChannelType } = require('discord.js');

function createDiscordBot({ token, guildId, mapping, stateStore, usersStore, onStateChange }) {
  // Wichtig: "Server Members Intent" muss im Discord Developer Portal aktiviert sein,
  // sonst liefert member teilweise keine Daten.
  const client = new Client({
    intents: [
      GatewayIntentBits.Guilds,
      GatewayIntentBits.GuildVoiceStates,
      GatewayIntentBits.GuildMembers,
    ],
  });

  // Scan all voice channels on startup to register frequency IDs from names
  async function scanVoiceChannels() {
    const guilds = guildId
      ? [client.guilds.cache.get(guildId)].filter(Boolean)
      : [...client.guilds.cache.values()];

    const defaultFreqs = [];

    for (const guild of guilds) {
      const channels = guild.channels.cache.filter(ch => ch.type === ChannelType.GuildVoice);
      for (const [chId, channel] of channels) {
        mapping.registerChannel(chId, channel.name);
        const freqId = mapping.parseFreqIdFromName(channel.name);
        if (freqId) {
          defaultFreqs.push(freqId);
        }
      }
    }

    console.log(`[discord] Scanned voice channels, found ${defaultFreqs.length} default frequencies`);
  }

  client.once('clientReady', async () => {
    console.log(`[discord] logged in as ${client.user?.tag}`);
    await scanVoiceChannels();
  });

  // Listen for channel updates (rename, create, delete)
  client.on('channelUpdate', (oldChannel, newChannel) => {
    if (newChannel.type === ChannelType.GuildVoice) {
      mapping.registerChannel(newChannel.id, newChannel.name);
    }
  });

  client.on('channelCreate', (channel) => {
    if (channel.type === ChannelType.GuildVoice) {
      mapping.registerChannel(channel.id, channel.name);
    }
  });

  client.on('voiceStateUpdate', (oldState, newState) => {
    try {
      const gId = newState.guild?.id || oldState.guild?.id || null;
      if (!gId) return;
      if (guildId && gId !== guildId) return;

      const discordUserId = newState.id;
      const channelId = newState.channelId;
      const freqId = channelId ? mapping.getFreqIdForChannelId(channelId) : null;

      const payload = {
        discordUserId,
        guildId: gId,
        channelId: channelId || null,
        freqId: freqId || null,
        ts: Date.now(),
      };

      // Voice state persist
      stateStore.upsert({
        discord_user_id: payload.discordUserId,
        guild_id: payload.guildId,
        channel_id: payload.channelId,
        freq_id: payload.freqId,
        updated_at_ms: payload.ts,
      });

      // User directory (Server-Displayname)
      if (usersStore) {
        const m = newState.member || oldState.member || null;
        // prefer server nickname; fall back to global/username when unavailable
        const displayName = (m && (m.nickname || m.displayName)) ? String(m.nickname || m.displayName) : null;
        if (displayName) {
          usersStore.upsert({
            discord_user_id: payload.discordUserId,
            guild_id: payload.guildId,
            display_name: displayName,
            updated_at_ms: payload.ts,
          });
        }
      }

      onStateChange(payload);
    } catch (e) {
      console.error('[discord] voiceStateUpdate error:', e);
    }
  });

  return {
    start: async () => { await client.login(token); },
    stop: async () => { await client.destroy(); },
  };
}

module.exports = { createDiscordBot };
EOF
)"

# src/ws.js
write_file_backup "$SRC_DIR/ws.js" "$(cat <<'EOF'
'use strict';

const WebSocket = require('ws');

function createWsHub(httpServer, { stateStore }) {
  const wss = new WebSocket.Server({ server: httpServer });

  function send(ws, obj) {
    if (ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify(obj));
  }

  wss.on('connection', (ws) => {
    // Snapshot: voice_state
    const recent = stateStore.listRecent(200);
    send(ws, { type: 'snapshot', payload: recent });

    ws.on('message', (buf) => {
      try {
        const msg = JSON.parse(buf.toString('utf-8'));
        if (msg?.type === 'ping') send(ws, { type: 'pong', ts: Date.now() });
      } catch (_) {}
    });
  });

  return {
    broadcast: (obj) => {
      const msg = JSON.stringify(obj);
      for (const ws of wss.clients) {
        if (ws.readyState === WebSocket.OPEN) ws.send(msg);
      }
    },
  };
}

module.exports = { createWsHub };
EOF
)"

# src/http.js
write_file_backup "$SRC_DIR/http.js" "$(cat <<'EOF'
'use strict';

const express = require('express');
const http = require('http');

function createHttpServer({ db, mapping, stateStore, txStore, usersStore, adminToken, allowedGuildIds }) {
  const app = express();
  app.use(express.json());

  let onTxEventFn = null;

  app.get('/health', (req, res) => res.json({ ok: true }));

  app.get('/state/:discordUserId', (req, res) => {
    const row = stateStore.get(req.params.discordUserId);
    res.json({ ok: true, data: row });
  });

  app.get('/state', (req, res) => {
    const limit = Math.min(Number(req.query.limit || 200), 1000);
    const rows = stateStore.listRecent(limit);
    res.json({ ok: true, data: rows });
  });

  // TX: create event
  app.post('/tx/event', (req, res) => {
    if (!txStore) return res.status(500).json({ ok: false, error: 'txStore_not_configured' });

    const { freqId, action, discordUserId, radioSlot, meta } = req.body || {};
    const f = Number(freqId);

    if (!Number.isInteger(f) || f < 1000 || f > 9999) {
      return res.status(400).json({ ok: false, error: 'bad freqId' });
    }
    if (action !== 'start' && action !== 'stop') {
      return res.status(400).json({ ok: false, error: 'bad action' });
    }

    const ts = Date.now();

    const row = {
      freq_id: f,
      discord_user_id: discordUserId ? String(discordUserId) : null,
      radio_slot: (radioSlot === null || radioSlot === undefined) ? null : Number(radioSlot),
      action,
      ts_ms: ts,
      meta_json: meta ? JSON.stringify(meta) : null,
    };

    txStore.addEvent(row);

    const payload = {
      freqId: row.freq_id,
      discordUserId: row.discord_user_id,
      radioSlot: row.radio_slot,
      action: row.action,
      ts: row.ts_ms,
      meta: meta || null,
    };

    if (onTxEventFn) onTxEventFn(payload);

    // Count active listeners on this frequency
    const lcRow = db.prepare('SELECT COUNT(DISTINCT discord_user_id) as cnt FROM freq_listeners WHERE freq_id = ?').get(f);
    const listenerCount = lcRow ? lcRow.cnt : 0;

    res.json({ ok: true, data: payload, listener_count: listenerCount });
  });

  // TX: read
  app.get('/tx/recent', (req, res) => {
    if (!txStore) return res.status(500).json({ ok: false, error: 'txStore_not_configured' });

    const limit = Math.min(Number(req.query.limit || 200), 1000);
    const freq = req.query.freqId ? Number(req.query.freqId) : null;

    const rows = freq ? txStore.listRecentByFreq(freq, limit) : txStore.listRecent(limit);
    res.json({ ok: true, data: rows });
  });

  // Frequency listener registration
  app.post('/freq/join', (req, res) => {
    const { discordUserId, freqId, radioSlot } = req.body || {};
    if (!discordUserId || !freqId) {
      return res.status(400).json({ ok: false, error: 'missing discordUserId or freqId' });
    }
    const f = Number(freqId);
    db.prepare(
      'INSERT OR REPLACE INTO freq_listeners (discord_user_id, freq_id, radio_slot, connected_at_ms) VALUES (?,?,?,?)'
    ).run(String(discordUserId), f, Number(radioSlot) || 0, Date.now());

    const row = db.prepare('SELECT COUNT(DISTINCT discord_user_id) as cnt FROM freq_listeners WHERE freq_id = ?').get(f);
    res.json({ ok: true, listener_count: row ? row.cnt : 0 });
  });

  app.post('/freq/leave', (req, res) => {
    const { discordUserId, freqId } = req.body || {};
    if (!discordUserId || !freqId) {
      return res.status(400).json({ ok: false, error: 'missing discordUserId or freqId' });
    }
    const f = Number(freqId);
    db.prepare('DELETE FROM freq_listeners WHERE discord_user_id = ? AND freq_id = ?').run(String(discordUserId), f);

    const row = db.prepare('SELECT COUNT(DISTINCT discord_user_id) as cnt FROM freq_listeners WHERE freq_id = ?').get(f);
    res.json({ ok: true, listener_count: row ? row.cnt : 0 });
  });

  // Users: read
  app.get('/users/recent', (req, res) => {
    const limit = Math.min(Number(req.query.limit || 200), 1000);
    const rows = (usersStore && typeof usersStore.listRecent === 'function') ? usersStore.listRecent(limit) : [];
    res.json({ ok: true, data: rows || [] });
  });

  app.get('/users/:guildId/:discordUserId', (req, res) => {
    const { guildId, discordUserId } = req.params;
    const row = (usersStore && typeof usersStore.get === 'function') ? usersStore.get(discordUserId, guildId) : null;
    res.json({ ok: true, data: row });
  });

  app.post('/admin/reload', (req, res) => {
    const token = req.header('x-admin-token') || '';
    if (!adminToken || token !== adminToken) {
      return res.status(403).json({ ok: false, error: 'forbidden' });
    }
    mapping.reload();
    res.json({ ok: true, mappingSize: mapping.size() });
  });

  const server = http.createServer(app);
  server._setOnTxEvent = (fn) => { onTxEventFn = fn; };
  return server;
}

module.exports = { createHttpServer };
EOF
)"

# Ensure ownership for backend dir (best-effort)
chown -R "$ADMIN_USER:$ADMIN_USER" "$BACKEND_DIR" 2>/dev/null || true

# --------------------------------------------------
# Interaktive .env Konfiguration
# --------------------------------------------------
log_input ""
read -r -p "$(echo -e "${CYAN}Möchtest du die .env Datei jetzt interaktiv konfigurieren? (j/n) [j]:${NC} ")" CONFIGURE_ENV
CONFIGURE_ENV="${CONFIGURE_ENV:-j}"

if [[ "$CONFIGURE_ENV" =~ ^[jJ]$ ]]; then
  log_input ""
  log_input "=== Interaktive $ENV_FILE Konfiguration ==="
  log_input "(Token-Eingabe ist unsichtbar)"
  log_input ""

  read -s -p "$(echo -e "${CYAN}Discord Token:${NC} ")" DISCORD_TOKEN; echo ""
  read -r -p "$(echo -e "${CYAN}Discord Guild ID (leer für alle):${NC} ")" DISCORD_GUILD_ID
  read -r -p "$(echo -e "${CYAN}Bind Host [127.0.0.1]:${NC} ")" BIND_HOST
  BIND_HOST="${BIND_HOST:-127.0.0.1}"
  read -r -p "$(echo -e "${CYAN}Bind Port [3000]:${NC} ")" BIND_PORT
  BIND_PORT="${BIND_PORT:-3000}"
  read -s -p "$(echo -e "${CYAN}Admin Token (für /admin/reload):${NC} ")" ADMIN_TOKEN; echo ""

  cat > "$ENV_FILE" <<EOF
DISCORD_TOKEN=$DISCORD_TOKEN
$([ -n "$DISCORD_GUILD_ID" ] && echo "DISCORD_GUILD_ID=$DISCORD_GUILD_ID" || echo "# DISCORD_GUILD_ID=123456789012345678")

BIND_HOST=$BIND_HOST
BIND_PORT=$BIND_PORT

DB_PATH=$BACKEND_DIR/state.sqlite
CHANNEL_MAP_PATH=$CHANNEL_MAP

ADMIN_TOKEN=$ADMIN_TOKEN

# Voice relay UDP port (for Companion App audio)
VOICE_UDP_PORT=5060
EOF

  chown "$ADMIN_USER:$ADMIN_USER" "$ENV_FILE" || true
  chmod 600 "$ENV_FILE"
  log_ok ".env geschrieben: $ENV_FILE"
else
  log_info ".env Konfiguration übersprungen (kann später manuell bearbeitet werden)"
fi

# --------------------------------------------------
# Validierung: Skeleton vollständig?
# --------------------------------------------------
REQUIRED_FILES=(
  "$SRC_DIR/http.js"
  "$SRC_DIR/ws.js"
  "$SRC_DIR/db.js"
  "$SRC_DIR/state.js"
  "$SRC_DIR/discord.js"
  "$SRC_DIR/mapping.js"
  "$SRC_DIR/tx.js"
  "$SRC_DIR/users.js"
  "$SRC_DIR/voice.js"
  "$BACKEND_DIR/index.js"
)

for f in "${REQUIRED_FILES[@]}"; do
  if [ ! -f "$f" ]; then
    log_error "Installer unvollständig: fehlt $f"
    exit 1
  fi
done
log_ok "Backend Skeleton vollständig"

# --------------------------------------------------
# Backend Testlauf (nur wenn Port frei / Service nicht aktiv)
# --------------------------------------------------
log_info "Backend Testlauf: Prüfe Port & Service"

if systemctl is-active --quiet das-krt-backend; then
  log_warn "das-krt-backend läuft bereits – überspringe Testlauf (Port wäre belegt)"
elif port_in_use "127.0.0.1" "3000"; then
  log_warn "Port 127.0.0.1:3000 ist belegt – überspringe Testlauf"
else
  log_info "Starte Testlauf (node index.js) ..."
  cd "$BACKEND_DIR"

  sudo -u "$ADMIN_USER" node index.js > "$TEST_LOG" 2>&1 &
  TEST_PID=$!

  sleep 3

  if curl -sf "http://127.0.0.1:3000/health" > /dev/null; then
    log_ok "Backend Testlauf OK (Healthcheck erfolgreich)"
  else
    log_warn "Backend Testlauf fehlgeschlagen"
    log_warn "→ Log ansehen: tail -n 200 $TEST_LOG"
  fi

  kill "$TEST_PID" 2>/dev/null || true
  sleep 1
fi

# systemd Service starten
log_info "Starte/Restart systemd Service"
systemctl restart das-krt-backend
systemctl status das-krt-backend --no-pager || true

echo ""
log_ok "Bootstrap abgeschlossen | ${VERSION}"
echo ""

# --------------------------------------------------
# Menü (Wizard)
# --------------------------------------------------
while true; do
  echo ""
  log_input "=== das-krt Menü (${VERSION}) ==="
  echo -e "${CYAN}1) channels.json bearbeiten${NC}"
  echo -e "${CYAN}2) Backend Healthcheck testen${NC}"
  echo -e "${CYAN}3) Backend Testlog anzeigen (tail)${NC}"
  echo -e "${CYAN}4) Backend Live-Logs verfolgen (journalctl -f)${NC}"
  echo -e "${CYAN}5) TX Event senden (start/stop)${NC}"
  echo -e "${CYAN}6) TX Recent anzeigen${NC}"
  echo -e "${CYAN}7) Users Recent anzeigen${NC}"
  echo -e "${CYAN}0) Beenden${NC}"
  echo ""

  read -r -p "$(echo -e "${CYAN}Auswahl [0-7]: ${NC}")" CHOICE

  case "$CHOICE" in
    1)
      log_input "Öffne: $CHANNEL_MAP"
      nano "$CHANNEL_MAP"

      ADMIN_TOKEN_VAL="$(grep -E '^ADMIN_TOKEN=' "$ENV_FILE" | cut -d= -f2- || true)"
      if [ -n "${ADMIN_TOKEN_VAL:-}" ]; then
        if curl -sf -X POST "http://127.0.0.1:3000/admin/reload" -H "x-admin-token: $ADMIN_TOKEN_VAL" >/dev/null; then
          log_ok "channels.json neu geladen (/admin/reload)"
        else
          log_warn "Reload fehlgeschlagen – starte Backend neu"
          systemctl restart das-krt-backend
          log_ok "Backend neu gestartet (Mapping neu geladen)"
        fi
      else
        log_warn "ADMIN_TOKEN nicht gesetzt – starte Backend neu"
        systemctl restart das-krt-backend
        log_ok "Backend neu gestartet (Mapping neu geladen)"
      fi
      ;;
    2)
      log_info "Healthcheck: http://127.0.0.1:3000/health"
      if curl -sf "http://127.0.0.1:3000/health" > /dev/null; then
        log_ok "Healthcheck OK"
      else
        log_error "Healthcheck fehlgeschlagen"
      fi
      ;;
    3)
      log_info "Testlog (letzte 200 Zeilen): $TEST_LOG"
      tail -n 200 "$TEST_LOG" || true
      ;;
    4)
      log_info "Live Logs: journalctl -u das-krt-backend -f"
      journalctl -u das-krt-backend -f
      ;;
    5)
      log_input "TX Event senden"
      read -r -p "$(echo -e "${CYAN}freqId [1060]: ${NC}")" FREQ_ID_IN
      FREQ_ID_IN="${FREQ_ID_IN:-1060}"
      read -r -p "$(echo -e "${CYAN}action [start/stop] (default: start): ${NC}")" ACTION_IN
      ACTION_IN="${ACTION_IN:-start}"

      if curl -sf -X POST "http://127.0.0.1:3000/tx/event" \
        -H "content-type: application/json" \
        -d "{\"freqId\":${FREQ_ID_IN},\"action\":\"${ACTION_IN}\"}" >/dev/null; then
        log_ok "TX Event gesendet"
      else
        log_error "TX Event fehlgeschlagen"
      fi
      ;;
    6)
      log_info "TX Recent: http://127.0.0.1:3000/tx/recent?limit=10"
      curl -sS "http://127.0.0.1:3000/tx/recent?limit=10" || true
      echo ""
      ;;
    7)
      log_info "Users Recent: http://127.0.0.1:3000/users/recent?limit=10"
      curl -sS "http://127.0.0.1:3000/users/recent?limit=10" || true
      echo ""
      ;;
    0)
      log_ok "Bye."
      break
      ;;
    *)
      log_warn "Ungültige Auswahl."
      ;;
  esac
done