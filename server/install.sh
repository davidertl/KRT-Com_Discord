#!/usr/bin/env bash
set -euo pipefail

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

echo -e "${GREEN}=== das-krt Bootstrap | Alpha 0.0.1 ===${NC}"

# --------------------------------------------------
# Variablen
# --------------------------------------------------
ADMIN_USER="ops"
APP_ROOT="/opt/das-krt"
NODE_VERSION="24"

MUMBLE_CONFIG="/etc/mumble-server.ini"
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
  ss -lnt "( sport = :$port )" | awk 'NR>1{print $4}' | grep -q "${host}:${port}" 2>/dev/null
}

# --------------------------------------------------
# Helper: write / patch files safely
# --------------------------------------------------
backup_file() {
  local f="$1"
  local ts
  ts="$(date +%Y%m%d-%H%M%S)"
  if [ -f "$f" ]; then
    cp -a "$f" "$f.bak.$ts"
    log_ok "Backup: $f -> $f.bak.$ts"
  fi
}

write_if_missing() {
  local path="$1"
  shift
  if [ ! -f "$path" ]; then
    cat > "$path" <<EOF
$@
EOF
    chown "$ADMIN_USER:$ADMIN_USER" "$path" || true
    log_ok "Erstellt: $(basename "$path")"
  else
    log_ok "Existiert: $(basename "$path") (nicht überschrieben)"
  fi
}

# Patch if marker missing (for upgrade-on-rerun)
write_or_patch_if_missing_marker() {
  local path="$1"
  local marker="$2"
  shift 2
  if [ ! -f "$path" ]; then
    cat > "$path" <<EOF
$@
EOF
    chown "$ADMIN_USER:$ADMIN_USER" "$path" || true
    log_ok "Erstellt: $(basename "$path")"
    return 0
  fi

  if ! grep -q "$marker" "$path"; then
    backup_file "$path"
    cat > "$path" <<EOF
$@
EOF
    chown "$ADMIN_USER:$ADMIN_USER" "$path" || true
    log_ok "Patched: $(basename "$path") (Marker fehlte: $marker)"
  else
    log_ok "OK: $(basename "$path") (Marker vorhanden)"
  fi
}

# --------------------------------------------------
# Preconditions
# --------------------------------------------------
if [ "$(id -u)" -ne 0 ]; then
  log_error "Bitte als root ausführen: sudo bash install.sh"
  exit 1
fi

# --------------------------------------------------
# Basis-Pakete
# --------------------------------------------------
log_info "[1/10] Installiere Basis-Pakete"
apt update
apt -y install sudo curl wget git fail2ban ca-certificates gnupg lsb-release
log_ok "Basis-Pakete installiert"

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
# SSH-Härtung (du hattest das später auskommentiert; hier bleibt es aktiv)
# --------------------------------------------------
log_info "[4/10] Härte SSH-Konfiguration"
SSH_CONFIG="/etc/ssh/sshd_config"

sed -i 's/^#\?PermitRootLogin.*/PermitRootLogin no/' "$SSH_CONFIG"
sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' "$SSH_CONFIG"
sed -i 's/^#\?PubkeyAuthentication.*/PubkeyAuthentication yes/' "$SSH_CONFIG"

systemctl restart ssh
log_ok "SSH Konfiguration angewendet & Dienst neu gestartet"

# --------------------------------------------------
# Fail2ban
# --------------------------------------------------
log_info "[5/10] Aktiviere Fail2ban"
systemctl enable fail2ban
systemctl start fail2ban
log_ok "Fail2ban läuft"

# --------------------------------------------------
# Mumble Server
# --------------------------------------------------
log_info "[6/10] Installiere Mumble Server"
apt -y install mumble-server
log_ok "mumble-server installiert"

if ! grep -q "bandwidth=" "$MUMBLE_CONFIG"; then
  cat >> "$MUMBLE_CONFIG" <<EOF

welcometext="Willkommen bei das-krt"
port=64738
users=500
bandwidth=72000
EOF
  log_ok "Mumble Grundkonfiguration ergänzt"
else
  log_ok "Mumble Grundkonfiguration bereits vorhanden"
fi

# Ice API (nur localhost)
if ! grep -q '^ice=' "$MUMBLE_CONFIG"; then
  echo 'ice="tcp -h 127.0.0.1 -p 6502"' >> "$MUMBLE_CONFIG"
  log_ok "Ice API (localhost) ergänzt"
else
  log_ok "Ice API bereits konfiguriert"
fi

systemctl restart mumble-server
log_ok "Mumble Server neu gestartet"

# --------------------------------------------------
# Node.js 24 (NodeSource)
# --------------------------------------------------
log_info "[7/10] Installiere Node.js ${NODE_VERSION} (NodeSource)"
curl -fsSL "https://deb.nodesource.com/setup_${NODE_VERSION}.x" | bash -
apt -y install nodejs
log_ok "Node.js installiert: $(node -v) | npm: $(npm -v)"
log_warn "Hinweis: Bitte NICHT zusätzlich 'apt install npm' nutzen (kollidiert gerne mit NodeSource)."

# --------------------------------------------------
# Build deps für better-sqlite3
# --------------------------------------------------
log_info "[8/10] Installiere Build-Dependencies (better-sqlite3)"
apt -y install build-essential python3 pkg-config sqlite3 libsqlite3-dev
log_ok "Build-Dependencies installiert"

# --------------------------------------------------
# Projektstruktur
# --------------------------------------------------
log_info "[9/10] Lege Projektverzeichnisse an"
mkdir -p "$BACKEND_DIR" "$APP_ROOT/config" "$APP_ROOT/logs" "$SRC_DIR"
chown -R "$ADMIN_USER:$ADMIN_USER" "$APP_ROOT" || true
log_ok "Projektstruktur erstellt: $APP_ROOT"

# --------------------------------------------------
# Backend Initialisierung (Dependencies)
# --------------------------------------------------
log_info "[10/10] Initialisiere Backend (npm)"
cd "$BACKEND_DIR"

if [ ! -f package.json ]; then
  sudo -u "$ADMIN_USER" npm init -y >/dev/null
  log_ok "package.json erstellt"
else
  log_ok "package.json existiert bereits"
fi

sudo -u "$ADMIN_USER" npm install discord.js express ws dotenv better-sqlite3 >/dev/null
log_ok "npm Dependencies installiert/aktualisiert"

# --------------------------------------------------
# systemd Service
# --------------------------------------------------
log_info "[systemd] Erstelle/prüfe systemd Service"
if [ ! -f "$SERVICE_FILE" ]; then
  cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=das-krt Backend (Alpha 0.0.1)
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
  log_ok "systemd Service erstellt: $SERVICE_FILE"
else
  log_ok "systemd Service existiert bereits: $SERVICE_FILE"
fi

systemctl daemon-reload
systemctl enable das-krt-backend
log_ok "systemd Service enabled"

# --------------------------------------------------
# Backend Skeleton - Dateien erzeugen (inkl. TX)
# --------------------------------------------------
log_info "[Backend] Erzeuge Backend Skeleton Dateien (inkl. TX)"

# channels.json Beispiel falls fehlt
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

# index.js (PATCH: createTxStore + WS broadcast tx_event)
write_or_patch_if_missing_marker "$BACKEND_DIR/index.js" "createTxStore" "$(cat <<'EOF'
'use strict';

const path = require('path');
require('dotenv').config({ path: path.join(__dirname, '.env') });

const { createHttpServer } = require('./src/http');
const { createWsHub } = require('./src/ws');
const { initDb } = require('./src/db');
const { createTxStore } = require('./src/tx');

const { createDiscordBot } = require('./src/discord');
const { createMappingStore } = require('./src/mapping');
const { createStateStore } = require('./src/state');

function mustEnv(name) {
  const v = process.env[name];
  if (!v) throw new Error(`Missing env var: ${name}`);
  return v;
}

(async () => {
  const bindHost = process.env.BIND_HOST || '127.0.0.1';
  const bindPort = Number(process.env.BIND_PORT || '3000');

  const dbPath = mustEnv('DB_PATH');
  const mapPath = mustEnv('CHANNEL_MAP_PATH');

  const db = initDb(dbPath);
  const mapping = createMappingStore(mapPath);
  const stateStore = createStateStore(db);
  const txStore = createTxStore(db);

  const httpServer = createHttpServer({
    mapping,
    stateStore,
    txStore,
    adminToken: process.env.ADMIN_TOKEN || '',
  });

  const wsHub = createWsHub(httpServer, { stateStore });

  // allow http.js to trigger WS broadcast without circular deps
  if (typeof httpServer._setOnTxEvent === 'function') {
    httpServer._setOnTxEvent((payload) => wsHub.broadcast({ type: 'tx_event', payload }));
  }

  const bot = createDiscordBot({
    token: mustEnv('DISCORD_TOKEN'),
    guildId: process.env.DISCORD_GUILD_ID || null,
    mapping,
    stateStore,
    onStateChange: (payload) => wsHub.broadcast({ type: 'voice_state', payload }),
  });

  httpServer.listen(bindPort, bindHost, async () => {
    console.log(`[http] listening on http://${bindHost}:${bindPort}`);
    console.log(`[map] loaded ${mapping.size()} channel mappings from ${mapPath}`);
    await bot.start();
  });
})();
EOF
)"

# src/tx.js (NEW)
write_if_missing "$SRC_DIR/tx.js" "$(cat <<'EOF'
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

# src/mapping.js
write_if_missing "$SRC_DIR/mapping.js" "$(cat <<'EOF'
'use strict';

const fs = require('fs');

function createMappingStore(mapPath) {
  let mapping = new Map();

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

  return {
    getFreqIdForChannelId: (channelId) => mapping.get(String(channelId)) ?? null,
    reload: () => load(),
    size: () => mapping.size,
  };
}

module.exports = { createMappingStore };
EOF
)"

# src/db.js (PATCH: tx_events)
write_or_patch_if_missing_marker "$SRC_DIR/db.js" "CREATE TABLE IF NOT EXISTS tx_events" "$(cat <<'EOF'
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
  `);

  return db;
}

module.exports = { initDb };
EOF
)"

# src/state.js
write_if_missing "$SRC_DIR/state.js" "$(cat <<'EOF'
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
write_if_missing "$SRC_DIR/discord.js" "$(cat <<'EOF'
'use strict';

const { Client, GatewayIntentBits } = require('discord.js');

function createDiscordBot({ token, guildId, mapping, stateStore, onStateChange }) {
  const client = new Client({
    intents: [GatewayIntentBits.Guilds, GatewayIntentBits.GuildVoiceStates],
  });

  client.once('clientReady', () => {
    console.log(`[discord] logged in as ${client.user?.tag}`);
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

      stateStore.upsert({
        discord_user_id: payload.discordUserId,
        guild_id: payload.guildId,
        channel_id: payload.channelId,
        freq_id: payload.freqId,
        updated_at_ms: payload.ts,
      });

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

# src/ws.js (marker only to ensure present; behavior gleich)
write_or_patch_if_missing_marker "$SRC_DIR/ws.js" "broadcast: (obj)" "$(cat <<'EOF'
'use strict';

const WebSocket = require('ws');

function createWsHub(httpServer, { stateStore }) {
  const wss = new WebSocket.Server({ server: httpServer });

  function send(ws, obj) {
    if (ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify(obj));
  }

  wss.on('connection', (ws) => {
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

# src/http.js (PATCH: /tx/event + /tx/recent + _setOnTxEvent hook)
write_or_patch_if_missing_marker "$SRC_DIR/http.js" "app.post('/tx/event'" "$(cat <<'EOF'
'use strict';

const express = require('express');
const http = require('http');

function createHttpServer({ mapping, stateStore, txStore, adminToken }) {
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

    res.json({ ok: true, data: payload });
  });

  // TX: read recent
  app.get('/tx/recent', (req, res) => {
    if (!txStore) return res.status(500).json({ ok: false, error: 'txStore_not_configured' });

    const limit = Math.min(Number(req.query.limit || 200), 1000);
    const freq = req.query.freqId ? Number(req.query.freqId) : null;

    const rows = freq ? txStore.listRecentByFreq(freq, limit) : txStore.listRecent(limit);
    res.json({ ok: true, data: rows });
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
log_ok "Bootstrap abgeschlossen | Alpha 0.0.1"
echo ""

# --------------------------------------------------
# Menü (Wizard)
# --------------------------------------------------
while true; do
  echo ""
  log_input "=== das-krt Menü (Alpha 0.0.1) ==="
  echo -e "${CYAN}1) channels.json bearbeiten${NC}"
  echo -e "${CYAN}2) Mumble SuperUser Passwort setzen/ändern (mumble-server -supw)${NC}"
  echo -e "${CYAN}3) Backend Healthcheck testen${NC}"
  echo -e "${CYAN}4) Backend Testlog anzeigen (tail)${NC}"
  echo -e "${CYAN}5) Backend Live-Logs verfolgen (journalctl -f)${NC}"
  echo -e "${CYAN}6) TX Event senden (start/stop)${NC}"
  echo -e "${CYAN}7) TX Recent anzeigen${NC}"
  echo -e "${CYAN}8) Beenden${NC}"
  echo ""

  read -r -p "$(echo -e "${CYAN}Auswahl [1-8]: ${NC}")" CHOICE

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
      log_input "SuperUser Passwort setzen/ändern (Eingabe unsichtbar)"
      while true; do
        read -s -p "$(echo -e "${CYAN}Neues SuperUser Passwort:${NC} ")" MUMBLE_PW_1
        echo ""
        read -s -p "$(echo -e "${CYAN}Wiederholen:${NC} ")" MUMBLE_PW_2
        echo ""

        if [ -z "${MUMBLE_PW_1:-}" ]; then
          log_error "Passwort darf nicht leer sein."
          continue
        fi

        if [ "$MUMBLE_PW_1" != "$MUMBLE_PW_2" ]; then
          log_error "Passwörter stimmen nicht überein. Bitte erneut."
          continue
        fi

        if mumble-server -supw "$MUMBLE_PW_1" >/dev/null 2>&1; then
          log_ok "SuperUser Passwort erfolgreich gesetzt"
        else
          log_error "Fehler beim Setzen des SuperUser Passworts"
        fi

        unset MUMBLE_PW_1 MUMBLE_PW_2
        break
      done
      ;;

    3)
      log_info "Healthcheck: http://127.0.0.1:3000/health"
      if curl -sf "http://127.0.0.1:3000/health" > /dev/null; then
        log_ok "Healthcheck OK"
      else
        log_error "Healthcheck fehlgeschlagen"
      fi
      ;;

    4)
      log_info "Testlog (letzte 200 Zeilen): $TEST_LOG"
      tail -n 200 "$TEST_LOG" || true
      ;;

    5)
      log_info "Live Logs: journalctl -u das-krt-backend -f"
      journalctl -u das-krt-backend -f
      ;;

    6)
      log_input "TX Event senden"
      read -r -p "$(echo -e "${CYAN}freqId [1060]: ${NC}")" TX_FREQ
      TX_FREQ="${TX_FREQ:-1060}"
      read -r -p "$(echo -e "${CYAN}action [start/stop] (default: start): ${NC}")" TX_ACTION
      TX_ACTION="${TX_ACTION:-start}"

      if curl -sS -X POST "http://127.0.0.1:3000/tx/event" \
        -H "content-type: application/json" \
        -d "{\"freqId\":${TX_FREQ},\"action\":\"${TX_ACTION}\"}" >/dev/null; then
        log_ok "TX Event gesendet"
      else
        log_error "TX Event fehlgeschlagen"
      fi
      ;;

    7)
      log_info "TX Recent: http://127.0.0.1:3000/tx/recent?limit=10"
      curl -sS "http://127.0.0.1:3000/tx/recent?limit=10" || true
      echo ""
      ;;

    8)
      log_ok "Bye."
      break
      ;;

    *)
      log_warn "Ungültige Auswahl."
      ;;
  esac
done
