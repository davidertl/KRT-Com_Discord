#!/usr/bin/env bash
##version alpha-0.0.4
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

VERSION="Alpha 0.0.4"
echo -e "${GREEN}=== das-krt Install | ${VERSION} ===${NC}"

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

TRAEFIK_DIR="$APP_ROOT/traefik"
TRAEFIK_VERSION="3.3.3"
TRAEFIK_SERVICE_FILE="/etc/systemd/system/traefik.service"

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

# ==========================================================
# PHASE 1: System Dependencies
# ==========================================================

# --------------------------------------------------
# Basis-Pakete
# --------------------------------------------------
log_info "[1/12] Installiere Basis-Pakete"
apt update
apt -y install sudo curl wget git fail2ban ca-certificates gnupg lsb-release
log_ok "Basis-Pakete installiert"
apt upgrade -y

# --------------------------------------------------
# Zeitzone
# --------------------------------------------------
log_info "[2/12] Setze Zeitzone"
timedatectl set-timezone Europe/Berlin
log_ok "Zeitzone gesetzt: Europe/Berlin"

# --------------------------------------------------
# Admin-User (idempotent)
# --------------------------------------------------
log_info "[3/12] Erstelle Admin-User"
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
log_info "[4/12] SSH-Härtung übersprungen (Script bewusst neutral halten)"

# --------------------------------------------------
# Fail2ban
# --------------------------------------------------
log_info "[5/12] Aktiviere Fail2ban"
systemctl enable fail2ban >/dev/null 2>&1 || true
systemctl start fail2ban >/dev/null 2>&1 || true
log_ok "Fail2ban läuft (oder war bereits aktiv)"

# --------------------------------------------------
# Node.js 24
# --------------------------------------------------
log_info "[6/12] Installiere Node.js ${NODE_VERSION}"
curl -fsSL "https://deb.nodesource.com/setup_${NODE_VERSION}.x" | bash -
apt -y install nodejs
log_ok "Node.js installiert: $(node -v) | npm: $(npm -v)"

# --------------------------------------------------
# Traefik Reverse Proxy
# --------------------------------------------------
log_info "[7/12] Installiere Traefik ${TRAEFIK_VERSION}"
if command -v traefik &>/dev/null; then
  log_ok "Traefik bereits installiert: $(traefik version --short 2>/dev/null || echo 'vorhanden')"
else
  cd /tmp
  TRAEFIK_ARCH="amd64"
  TRAEFIK_TAR="traefik_v${TRAEFIK_VERSION}_linux_${TRAEFIK_ARCH}.tar.gz"
  wget -q "https://github.com/traefik/traefik/releases/download/v${TRAEFIK_VERSION}/${TRAEFIK_TAR}" -O "${TRAEFIK_TAR}"
  tar xzf "${TRAEFIK_TAR}" traefik
  mv traefik /usr/local/bin/traefik
  chmod +x /usr/local/bin/traefik
  rm -f "${TRAEFIK_TAR}"
  log_ok "Traefik ${TRAEFIK_VERSION} installiert"
fi

# ==========================================================
# PHASE 2: Project Structure & npm Dependencies
# ==========================================================

# --------------------------------------------------
# Projektstruktur
# --------------------------------------------------
log_info "[8/12] Lege Projektverzeichnisse an"
mkdir -p "$BACKEND_DIR" "$APP_ROOT/config" "$APP_ROOT/logs" "$SRC_DIR" "$TRAEFIK_DIR"
chown -R "$ADMIN_USER:$ADMIN_USER" "$APP_ROOT" || true
log_ok "Projektstruktur bereit: $APP_ROOT"

# --------------------------------------------------
# Backend Initialisierung (Dependencies)
# --------------------------------------------------
log_info "[9/12] Initialisiere Backend (npm)"
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
log_info "[10/12] Erstelle/Update systemd Service"
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

# Privacy Policy (default, can be customized by operator)
PRIVACY_POLICY_FILE="$APP_ROOT/config/privacy-policy.md"
if [ ! -f "$PRIVACY_POLICY_FILE" ]; then
  cat > "$PRIVACY_POLICY_FILE" <<'POLICYEOF'
# Privacy Policy

This project is a **self-hosted open-source software**.
Responsibility for operation, configuration, and legal compliance lies entirely with the **server operator**.

## 1. Principles

- Only data strictly required for technical operation is processed
- No hidden data collection, telemetry, or analytics
- All data remains exclusively on the operator's server

## 2. Data Processed

- **User Identifiers**: Discord OAuth2 login (identify + guilds scopes). Discord access token is immediately revoked after use. Only server nicknames stored for display (changeable by user, no history kept).
- **Authentication**: HMAC-SHA256 signed tokens with 24h automatic expiration. Debug login (POST /auth/login) disabled by default, only available in debug mode.
- **Sessions**: Active connection state (ephemeral, cleared on restart)
- **Logs**: Connection events, errors (configurable retention, no audio content)
- **Audio**: Never recorded or stored - live transmission only
- **Admin Token**: Not persisted by companion app (runtime-only, visible only in debug mode)

## 3. Data Retention

Retention periods are configurable by the server operator:
- DSGVO compliance mode: 2 days automatic cleanup
- Debug mode: 7 days automatic cleanup
- Retention can be disabled entirely

## 4. Data Deletion

A hard delete removes all stored data for a user (logs, sessions, tokens, mappings, policy acceptance).
Deletion is irreversible. Deleted users are added to a ban list (ID + timestamp only) to prevent re-registration.

## 5. Debugging

Debug mode can be enabled by the server administrator. When active:
- The companion app displays a visible warning
- Manual login endpoint becomes available
- DSGVO compliance mode is automatically disabled
- Data retention extends to 7 days

## 6. Data Sharing

No data is shared with third parties. Discord API is contacted only for OAuth2 login (immediately revoked), guild member verification, and channel name synchronization.

## 7. Server Operator Responsibility

The server operator is responsible for log retention configuration, compliance with local data protection laws, secure infrastructure operation, and TLS/HTTPS configuration.

## 8. Open Source

The complete source code is publicly available and auditable.

## 9. Changes

Any changes affecting data handling are documented in the changelog and trigger a re-acceptance prompt in the companion app.
POLICYEOF
  chown "$ADMIN_USER:$ADMIN_USER" "$PRIVACY_POLICY_FILE" 2>/dev/null || true
  log_ok "Privacy Policy erstellt: $PRIVACY_POLICY_FILE"
else
  log_ok "Privacy Policy existiert bereits: $PRIVACY_POLICY_FILE (nicht überschrieben)"
fi

# --------------------------------------------------
# Traefik Konfiguration
# --------------------------------------------------
log_info "[11/12] Konfiguriere Traefik Reverse Proxy"

# Ask for domain
log_input ""
log_input "=== Traefik / TLS Konfiguration ==="
log_input "Für Let's Encrypt TLS muss eine Domain auf die Server-IP zeigen."
log_input ""
read -r -p "$(echo -e "${CYAN}Domain (z.B. das-krt.com, leer = kein TLS):${NC} ")" DOMAIN
read -r -p "$(echo -e "${CYAN}E-Mail für Let's Encrypt Zertifikat:${NC} ")" ACME_EMAIL

if [ -n "$DOMAIN" ]; then
  ACME_EMAIL="${ACME_EMAIL:-admin@${DOMAIN}}"

  # Static configuration
  cat > "$TRAEFIK_DIR/traefik.yml" <<TRAEFIKEOF
# Traefik static configuration — das-krt (${VERSION})
# Auto-generated by install.sh

api:
  dashboard: false

log:
  level: WARN
  filePath: "$APP_ROOT/logs/traefik.log"

accessLog:
  filePath: "$APP_ROOT/logs/traefik-access.log"
  bufferingSize: 100

entryPoints:
  web:
    address: ":80"
    http:
      redirections:
        entryPoint:
          to: websecure
          scheme: https
          permanent: true
  websecure:
    address: ":443"
    http:
      tls:
        certResolver: letsencrypt
    transport:
      respondingTimeouts:
        readTimeout: 0
        writeTimeout: 0
        idleTimeout: 180s

certificatesResolvers:
  letsencrypt:
    acme:
      email: ${ACME_EMAIL}
      storage: ${TRAEFIK_DIR}/acme.json
      httpChallenge:
        entryPoint: web

providers:
  file:
    filename: ${TRAEFIK_DIR}/routes.yml
    watch: true
TRAEFIKEOF

  # Dynamic routes
  cat > "$TRAEFIK_DIR/routes.yml" <<ROUTESEOF
# Traefik dynamic configuration — das-krt (${VERSION})

http:
  routers:
    das-krt:
      rule: "Host(\`${DOMAIN}\`)"
      entryPoints:
        - websecure
      service: backend
      tls:
        certResolver: letsencrypt

    das-krt-http:
      rule: "Host(\`${DOMAIN}\`)"
      entryPoints:
        - web
      middlewares:
        - redirect-to-https
      service: backend

  middlewares:
    redirect-to-https:
      redirectScheme:
        scheme: https
        permanent: true

  services:
    backend:
      loadBalancer:
        servers:
          - url: "http://127.0.0.1:3000"
        passHostHeader: true
ROUTESEOF

  # Ensure acme.json exists with correct permissions
  touch "$TRAEFIK_DIR/acme.json"
  chmod 600 "$TRAEFIK_DIR/acme.json"

  # Traefik systemd service
  cat > "$TRAEFIK_SERVICE_FILE" <<EOF
[Unit]
Description=Traefik Reverse Proxy (das-krt ${VERSION})
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/usr/local/bin/traefik --configFile=${TRAEFIK_DIR}/traefik.yml
Restart=always
RestartSec=5
LimitNOFILE=65536

# Security hardening
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=${TRAEFIK_DIR} ${APP_ROOT}/logs
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
AmbientCapabilities=CAP_NET_BIND_SERVICE

[Install]
WantedBy=multi-user.target
EOF

  chown -R "$ADMIN_USER:$ADMIN_USER" "$TRAEFIK_DIR" 2>/dev/null || true
  # acme.json must be owned by root (Traefik runs as root for port 80/443 binding)
  chown root:root "$TRAEFIK_DIR/acme.json" 2>/dev/null || true

  systemctl daemon-reload
  systemctl enable traefik >/dev/null 2>&1 || true
  log_ok "Traefik konfiguriert für Domain: $DOMAIN"
  log_ok "Traefik systemd Service erstellt und aktiviert"
  log_ok "Let's Encrypt TLS mit HTTP-Challenge"
else
  log_warn "Keine Domain angegeben — Traefik wird NICHT konfiguriert"
  log_warn "Ohne TLS ist kein sicherer Betrieb möglich (DSGVO Compliance eingeschränkt)"
  DOMAIN=""
fi

# ==========================================================
# PHASE 3: Backend Source Code Deployment
# ==========================================================
log_info "[12/12] Deploye Backend Source Code"

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
const { createDsgvo } = require('./src/dsgvo');
const { hashUserId } = require('./src/crypto');
const fs = require('fs');

function mustEnv(name) {
  const v = process.env[name];
  if (!v) throw new Error(`Missing env var: ${name}`);
  return v;
}

/**
 * One-time migration: hash all existing raw Discord user IDs in every table.
 * Idempotent — skips rows that are already 64-char hex (hashed).
 */
function migrateUserIdHashing(db) {
  // 1. Ensure raw_discord_id column exists on banned_users (for fresh vs upgraded DBs)
  const cols = db.prepare('PRAGMA table_info(banned_users)').all();
  if (!cols.some(c => c.name === 'raw_discord_id')) {
    db.exec('ALTER TABLE banned_users ADD COLUMN raw_discord_id TEXT');
    console.log('[migration] Added raw_discord_id column to banned_users');
  }

  // 2. Check if migration is needed (heuristic: raw Discord snowflakes are 17-20 decimal digits)
  const tables = ['voice_state', 'tx_events', 'discord_users', 'freq_listeners', 'voice_sessions', 'auth_tokens', 'policy_acceptance'];
  let needsMigration = false;
  for (const table of tables) {
    const sample = db.prepare(`SELECT discord_user_id FROM ${table} WHERE discord_user_id IS NOT NULL LIMIT 1`).get();
    if (sample && !/^[0-9a-f]{64}$/.test(sample.discord_user_id)) {
      needsMigration = true;
      break;
    }
  }

  // Also check banned_users
  const bannedSample = db.prepare('SELECT discord_user_id FROM banned_users LIMIT 1').get();
  if (bannedSample && !/^[0-9a-f]{64}$/.test(bannedSample.discord_user_id)) {
    needsMigration = true;
  }

  if (!needsMigration) {
    console.log('[migration] User ID hashing: no migration needed');
    return;
  }

  console.log('[migration] Hashing existing raw discord_user_id values...');

  // 3. Migrate each standard table
  for (const table of tables) {
    const rows = db.prepare(`SELECT DISTINCT discord_user_id FROM ${table} WHERE discord_user_id IS NOT NULL`).all();
    let migrated = 0;
    const migrate = db.transaction(() => {
      for (const row of rows) {
        const raw = row.discord_user_id;
        if (/^[0-9a-f]{64}$/.test(raw)) continue; // already hashed
        const hashed = hashUserId(raw);
        db.prepare(`UPDATE ${table} SET discord_user_id = ? WHERE discord_user_id = ?`).run(hashed, raw);
        migrated++;
      }
    });
    migrate();
    if (migrated > 0) console.log(`[migration]   ${table}: hashed ${migrated} user IDs`);
  }

  // 4. Migrate banned_users (preserve raw ID for admin display)
  const bannedRows = db.prepare('SELECT discord_user_id, banned_at_ms, reason FROM banned_users').all();
  let bannedMigrated = 0;
  const migrateBanned = db.transaction(() => {
    for (const row of bannedRows) {
      const raw = row.discord_user_id;
      if (/^[0-9a-f]{64}$/.test(raw)) continue;
      const hashed = hashUserId(raw);
      db.prepare('UPDATE banned_users SET discord_user_id = ?, raw_discord_id = ? WHERE discord_user_id = ?').run(hashed, raw, raw);
      bannedMigrated++;
    }
  });
  migrateBanned();
  if (bannedMigrated > 0) console.log(`[migration]   banned_users: hashed ${bannedMigrated} entries (raw IDs preserved)`);

  console.log('[migration] User ID hashing migration complete');
}

(async () => {
  const bindHost = process.env.BIND_HOST || '127.0.0.1';
  const bindPort = Number(process.env.BIND_PORT || '3000');

  const dbPath = mustEnv('DB_PATH');
  const mapPath = mustEnv('CHANNEL_MAP_PATH');

  const db = initDb(dbPath);

  // Run one-time user-ID hashing migration (idempotent)
  migrateUserIdHashing(db);

  const mapping = createMappingStore(mapPath);
  const stateStore = createStateStore(db);
  const txStore = createTxStore(db);
  const usersStore = createUsersStore(db);

  const tokenSecret = process.env.TOKEN_SECRET || '';
  if (!tokenSecret) console.warn('[WARN] TOKEN_SECRET not set - token-based auth will be disabled');

  // Discord OAuth2 config
  const discordClientId = process.env.DISCORD_CLIENT_ID || '';
  const discordClientSecret = process.env.DISCORD_CLIENT_SECRET || '';
  const discordRedirectUri = process.env.DISCORD_REDIRECT_URI || '';
  if (!discordClientId || !discordClientSecret || !discordRedirectUri) {
    console.warn('[WARN] Discord OAuth2 not fully configured (DISCORD_CLIENT_ID, DISCORD_CLIENT_SECRET, DISCORD_REDIRECT_URI)');
  }

  const policyVersion = process.env.POLICY_VERSION || '1.0';
  let policyText = 'No privacy policy configured.';
  const policyPath = process.env.POLICY_PATH || path.join(__dirname, '..', 'config', 'privacy-policy.md');
  try { policyText = fs.readFileSync(policyPath, 'utf-8'); } catch { console.warn('[WARN] Privacy policy file not found:', policyPath); }

  const dsgvo = createDsgvo({
    db,
    dsgvoEnabled: process.env.DSGVO_ENABLED === 'true',
    debugMode: process.env.DEBUG_MODE === 'true',
  });
  dsgvo.startScheduler();

  const httpServer = createHttpServer({
    db,
    mapping,
    stateStore,
    txStore,
    usersStore,
    dsgvo,
    bot: null, // set after bot creation
    adminToken: process.env.ADMIN_TOKEN || '',
    allowedGuildIds: process.env.DISCORD_GUILD_ID
      ? process.env.DISCORD_GUILD_ID.split(',')
      : [],
    tokenSecret,
    policyVersion,
    policyText,
    discordClientId,
    discordClientSecret,
    discordRedirectUri,
  });

  const wsHub = createWsHub({ stateStore });

  // Voice relay (WebSocket control + binary WS audio)
  const voiceRelay = createVoiceRelay({
    db,
    usersStore,
    allowedGuildIds: process.env.DISCORD_GUILD_ID
      ? process.env.DISCORD_GUILD_ID.split(',') 
      : [],
    tokenSecret,
    dsgvo,
  });
  voiceRelay.start();

  // Route WebSocket upgrades by path
  // DSGVO HTTPS enforcement: reject WS upgrades that didn't come through TLS (Traefik)
  httpServer.on('upgrade', (req, socket, head) => {
    const proto = (req.headers['x-forwarded-proto'] || '').toLowerCase();
    if (dsgvo && typeof dsgvo.getStatus === 'function') {
      const status = dsgvo.getStatus();
      if (status.dsgvoEnabled && proto !== 'https') {
        socket.write('HTTP/1.1 403 Forbidden\r\n\r\nHTTPS required (DSGVO compliance)\r\n');
        socket.destroy();
        return;
      }
    }

    const pathname = new URL(req.url, 'http://localhost').pathname;
    if (pathname === '/voice') {
      voiceRelay.handleUpgrade(req, socket, head);
    } else if (pathname === '/ws') {
      wsHub.handleUpgrade(req, socket, head);
    } else {
      socket.destroy();
    }
  });

  // Wire TX broadcast (keine circular deps)
  if (typeof httpServer._setOnTxEvent === 'function') {
    httpServer._setOnTxEvent((payload) => {
      wsHub.broadcast({ type: 'tx_event', payload });
      voiceRelay.notifyTxEvent(payload);
    });
  }

  // Discord voice_state broadcast bleibt wie gehabt
  const bot = createDiscordBot({
    token: mustEnv('DISCORD_TOKEN'),
    guildId: process.env.DISCORD_GUILD_ID || null,
    mapping,
    stateStore,
    usersStore,
    onStateChange: (payload) => wsHub.broadcast({ type: 'voice_state', payload }),
    channelSyncIntervalHours: Number(process.env.CHANNEL_SYNC_INTERVAL_HOURS || 24),
  });

  // Wire bot reference into httpServer for channel sync endpoints
  if (typeof httpServer._setBot === 'function') {
    httpServer._setBot(bot);
  }

  httpServer.listen(bindPort, bindHost, async () => {
    console.log(`[http] listening on http://${bindHost}:${bindPort}`);
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

    -- Banned users (minimal: user ID + timestamp + optional reason)
    CREATE TABLE IF NOT EXISTS banned_users (
      discord_user_id TEXT PRIMARY KEY,
      raw_discord_id  TEXT,
      banned_at_ms    INTEGER NOT NULL,
      reason          TEXT
    );

    -- Auth tokens (issued by POST /auth/login)
    CREATE TABLE IF NOT EXISTS auth_tokens (
      token_id        TEXT PRIMARY KEY,
      discord_user_id TEXT NOT NULL,
      guild_id        TEXT NOT NULL,
      display_name    TEXT,
      created_at_ms   INTEGER NOT NULL,
      expires_at_ms   INTEGER NOT NULL
    );

    CREATE INDEX IF NOT EXISTS idx_auth_tokens_user
      ON auth_tokens(discord_user_id);

    CREATE INDEX IF NOT EXISTS idx_auth_tokens_expires
      ON auth_tokens(expires_at_ms);

    -- Privacy policy acceptance tracking
    CREATE TABLE IF NOT EXISTS policy_acceptance (
      discord_user_id TEXT NOT NULL,
      policy_version  TEXT NOT NULL,
      accepted_at_ms  INTEGER NOT NULL,
      PRIMARY KEY (discord_user_id, policy_version)
    );
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

# src/voice.js - Voice relay (WebSocket control + binary WS audio)
write_file_backup "$SRC_DIR/voice.js" "$(cat <<'EOF'
'use strict';

const { WebSocketServer } = require('ws');
const crypto = require('crypto');
const { verifyToken, hashUserId } = require('./crypto');

/**
 * Voice Relay
 * - Companion clients connect via WebSocket to /voice for control signaling
 *   (auth, join/leave frequency, heartbeat)
 * - Opus audio is exchanged as binary WebSocket frames
 * - Packet format: [4 bytes freqId BE][4 bytes sequence BE][opus data]
 */
function createVoiceRelay({ db, usersStore, allowedGuildIds = [], tokenSecret = '', dsgvo = null }) {
  // Session management
  const sessions = new Map();       // sessionToken -> { discordUserId, guildId, displayName, ws, frequencies: Set, lastSeen }

  // Frequency subscriptions: freqId -> Set<sessionToken>
  const freqSubscribers = new Map();

  // Clean up stale DB rows from a previous crash/restart
  db.prepare('DELETE FROM freq_listeners').run();
  db.prepare('DELETE FROM voice_sessions').run();
  console.log('[voice] Cleaned stale DB sessions on startup');

  const wss = new WebSocketServer({ noServer: true });

  function start() {

    wss.on('connection', (ws, req) => {
      let sessionToken = null;

      ws.on('message', (raw, isBinary) => {
        // Binary = audio frame
        if (isBinary) {
          if (sessionToken) handleAudio(sessionToken, raw);
          return;
        }

        // Text = control message
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
          case 'mute':
            if (sessionToken) handleMute(sessionToken, msg);
            break;
          case 'unmute':
            if (sessionToken) handleUnmute(sessionToken, msg);
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

  // --- Audio handling (binary WS frames) ---
  function handleAudio(senderToken, buf) {
    if (buf.length < 9) return; // min: 4 freqId + 4 seq + 1 byte opus

    const freqId = buf.readUInt32BE(0);

    const senderSession = sessions.get(senderToken);
    if (!senderSession) return;

    // Verify sender is subscribed to this frequency
    if (!senderSession.frequencies.has(freqId)) return;

    const subscribers = freqSubscribers.get(freqId);
    if (!subscribers) return;

    // Forward audio to all other subscribers as binary WS frame
    for (const subToken of subscribers) {
      if (subToken === senderToken) continue;
      const sub = sessions.get(subToken);
      if (!sub || !sub.ws || sub.ws.readyState !== 1) continue;
      // Skip if receiver has muted this frequency
      if (sub.mutedFreqs.has(freqId)) continue;
      sub.ws.send(buf);
    }
  }

  function handleAuth(ws, msg, setToken) {
    const { discordUserId, guildId, authToken } = msg;

    let resolvedUserId = discordUserId;
    let resolvedGuildId = guildId;
    let resolvedDisplayName = null;

    // Token-based auth (preferred): verify signed token from /auth/login
    if (authToken && tokenSecret) {
      const payload = verifyToken(authToken, tokenSecret);
      if (!payload) {
        ws.send(JSON.stringify({ type: 'auth_error', reason: 'invalid or expired token' }));
        return;
      }
      resolvedUserId = payload.uid;
      resolvedGuildId = payload.gid;
      resolvedDisplayName = payload.name;
    }

    if (!resolvedUserId || !resolvedGuildId) {
      ws.send(JSON.stringify({ type: 'auth_error', reason: 'missing credentials' }));
      return;
    }

    // When no signed token was used, the resolvedUserId is a raw Discord snowflake.
    // Hash it so all downstream code (sessions, DB, ban checks) uses the hashed form.
    // When a signed token WAS used, payload.uid is already hashed.
    if (!(authToken && tokenSecret)) {
      resolvedUserId = hashUserId(String(resolvedUserId));
    }

    // Check allowed guilds
    if (allowedGuildIds.length > 0 && !allowedGuildIds.includes(String(resolvedGuildId))) {
      ws.send(JSON.stringify({ type: 'auth_error', reason: 'guild not allowed' }));
      return;
    }

    // Check if banned
    if (dsgvo && typeof dsgvo.isBanned === 'function' && dsgvo.isBanned(String(resolvedUserId))) {
      ws.send(JSON.stringify({ type: 'auth_error', reason: 'access denied' }));
      return;
    }

    // Look up user (skip if token already provided display name)
    const user = usersStore ? usersStore.get(String(resolvedUserId), String(resolvedGuildId)) : null;
    if (!user && !resolvedDisplayName) {
      ws.send(JSON.stringify({ type: 'auth_error', reason: 'user not found in guild' }));
      return;
    }

    // Generate session token
    const sessionToken = crypto.randomBytes(24).toString('hex');
    const now = Date.now();

    const displayName = resolvedDisplayName || (user ? user.display_name : null) || String(resolvedUserId);
    const session = {
      discordUserId: String(resolvedUserId),
      guildId: String(resolvedGuildId),
      displayName,
      ws,
      frequencies: new Set(),
      mutedFreqs: new Set(),   // freqIds where this user is RX-muted (server won't forward audio)
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

    // Persist to freq_listeners DB
    db.prepare(
      'INSERT OR REPLACE INTO freq_listeners (discord_user_id, freq_id, radio_slot, connected_at_ms) VALUES (?,?,?,?)'
    ).run(session.discordUserId, freqId, 0, Date.now());

    console.log('[voice] Join freq', freqId, 'by', session.discordUserId);

    const listenerCount = freqSubscribers.get(freqId).size;

    session.ws.send(JSON.stringify({
      type: 'join_ok',
      freqId,
      listenerCount,
    }));

    // Notify other subscribers about updated listener count
    for (const subToken of freqSubscribers.get(freqId)) {
      if (subToken === sessionToken) continue;
      const sub = sessions.get(subToken);
      if (sub && sub.ws && sub.ws.readyState === 1) {
        sub.ws.send(JSON.stringify({ type: 'listener_update', freqId, listenerCount }));
      }
    }
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

    // Remove from freq_listeners DB
    db.prepare('DELETE FROM freq_listeners WHERE discord_user_id = ? AND freq_id = ?').run(session.discordUserId, freqId);

    console.log('[voice] Leave freq', freqId, 'by', session.discordUserId);

    session.ws.send(JSON.stringify({
      type: 'leave_ok',
      freqId,
    }));

    // Notify remaining subscribers about updated listener count
    const remainingSubs = freqSubscribers.get(freqId);
    if (remainingSubs) {
      const listenerCount = remainingSubs.size;
      for (const subToken of remainingSubs) {
        const sub = sessions.get(subToken);
        if (sub && sub.ws && sub.ws.readyState === 1) {
          sub.ws.send(JSON.stringify({ type: 'listener_update', freqId, listenerCount }));
        }
      }
    }
  }

  function handleMute(sessionToken, msg) {
    const session = sessions.get(sessionToken);
    if (!session) return;

    const freqId = Number(msg.freqId);
    if (!Number.isInteger(freqId) || freqId < 1000 || freqId > 9999) {
      session.ws.send(JSON.stringify({ type: 'mute_error', reason: 'bad freqId' }));
      return;
    }

    session.mutedFreqs.add(freqId);
    console.log('[voice] Mute freq', freqId, 'by', session.discordUserId);

    session.ws.send(JSON.stringify({
      type: 'mute_ok',
      freqId,
      muted: true,
    }));
  }

  function handleUnmute(sessionToken, msg) {
    const session = sessions.get(sessionToken);
    if (!session) return;

    const freqId = Number(msg.freqId);
    if (!Number.isInteger(freqId) || freqId < 1000 || freqId > 9999) {
      session.ws.send(JSON.stringify({ type: 'mute_error', reason: 'bad freqId' }));
      return;
    }

    session.mutedFreqs.delete(freqId);
    console.log('[voice] Unmute freq', freqId, 'by', session.discordUserId);

    session.ws.send(JSON.stringify({
      type: 'mute_ok',
      freqId,
      muted: false,
    }));
  }

  function cleanupSession(token) {
    const session = sessions.get(token);
    if (!session) return;

    // Remove from all frequency subscriptions and notify remaining subscribers
    for (const freqId of session.frequencies) {
      const subs = freqSubscribers.get(freqId);
      if (subs) {
        subs.delete(token);
        // Notify remaining subscribers about updated listener count
        if (subs.size > 0) {
          const listenerCount = subs.size;
          for (const subToken of subs) {
            const sub = sessions.get(subToken);
            if (sub && sub.ws && sub.ws.readyState === 1) {
              sub.ws.send(JSON.stringify({ type: 'listener_update', freqId, listenerCount }));
            }
          }
        } else {
          freqSubscribers.delete(freqId);
        }
      }
    }

    // Remove all freq_listeners for this user
    db.prepare('DELETE FROM freq_listeners WHERE discord_user_id = ?').run(session.discordUserId);

    // Remove DB session
    db.prepare('DELETE FROM voice_sessions WHERE session_token = ?').run(token);

    sessions.delete(token);
    console.log('[voice] Session cleaned up:', session.discordUserId);
  }

  /**
   * Notify voice relay subscribers about a TX event (from REST API).
   * Sends an 'rx' message to all subscribers on the frequency except the transmitter.
   */
  function notifyTxEvent(payload) {
    const freqId = Number(payload.freqId);
    const discordUserId = String(payload.discordUserId || '');
    const action = payload.action;

    const subs = freqSubscribers.get(freqId);
    if (!subs || subs.size === 0) return;

    // Look up sender's display name from their session
    let username = discordUserId;
    for (const [, session] of sessions) {
      if (session.discordUserId === discordUserId) {
        username = session.displayName || username;
        break;
      }
    }

    const msg = JSON.stringify({
      type: 'rx',
      freqId,
      discordUserId,
      username,
      action,
    });

    for (const subToken of subs) {
      const sub = sessions.get(subToken);
      if (!sub || !sub.ws || sub.ws.readyState !== 1) continue;
      if (sub.discordUserId === discordUserId) continue; // don't echo to sender
      sub.ws.send(msg);
    }

    // On TX stop, broadcast updated listener count to ALL subscribers (including sender)
    // so everyone sees the correct count after a transmission ends
    if (action === 'stop') {
      const listenerCount = subs.size;
      const luMsg = JSON.stringify({ type: 'listener_update', freqId, listenerCount });
      for (const subToken of subs) {
        const sub = sessions.get(subToken);
        if (sub && sub.ws && sub.ws.readyState === 1) {
          sub.ws.send(luMsg);
        }
      }
    }
  }

  return {
    start,
    wss,
    notifyTxEvent,
    handleUpgrade: (req, socket, head) => {
      wss.handleUpgrade(req, socket, head, (ws) => {
        wss.emit('connection', ws, req);
      });
    },
  };
}

module.exports = { createVoiceRelay };
EOF
)"

# src/crypto.js - Token signing & verification utilities
write_file_backup "$SRC_DIR/crypto.js" "$(cat <<'EOF'
'use strict';

const crypto = require('crypto');

/**
 * Sign a token payload using HMAC-SHA256.
 * Returns: base64url(payload).base64url(signature)
 */
function signToken(payload, secret) {
  const payloadStr = JSON.stringify(payload);
  const payloadB64 = Buffer.from(payloadStr).toString('base64url');
  const sig = crypto.createHmac('sha256', secret).update(payloadB64).digest('base64url');
  return payloadB64 + '.' + sig;
}

/**
 * Verify and decode a signed token. Returns payload object or null.
 */
function verifyToken(token, secret) {
  if (!token || typeof token !== 'string') return null;
  const parts = token.split('.');
  if (parts.length !== 2) return null;
  const [payloadB64, sig] = parts;
  const expectedSig = crypto.createHmac('sha256', secret).update(payloadB64).digest('base64url');
  if (sig !== expectedSig) return null;
  try {
    const payloadStr = Buffer.from(payloadB64, 'base64url').toString('utf-8');
    const payload = JSON.parse(payloadStr);
    if (payload.exp && Date.now() > payload.exp) return null;
    return payload;
  } catch {
    return null;
  }
}

/**
 * Generate a random session token.
 */
function generateSessionToken() {
  return crypto.randomBytes(32).toString('hex');
}

/**
 * Hash a Discord user ID using HMAC-SHA256 for privacy-safe storage.
 * Raw Discord snowflake IDs are never stored in the database — only these
 * 64-character hex digests.  The HMAC key is TOKEN_SECRET from the .env file.
 */
function hashUserId(rawDiscordId) {
  const secret = process.env.TOKEN_SECRET;
  if (!secret) throw new Error('TOKEN_SECRET not set — cannot hash user ID');
  return crypto.createHmac('sha256', secret).update(String(rawDiscordId)).digest('hex');
}

module.exports = { signToken, verifyToken, generateSessionToken, hashUserId };
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
  let freqToName = new Map();         // freqId -> channelName (for client display)

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

    // Load channel names from config if present
    const names = json.channelNames || {};
    for (const [channelId, name] of Object.entries(names)) {
      channelNames.set(String(channelId), String(name));
      // Also populate freqToName from saved config
      const fId = mapping.get(String(channelId)) ?? dynamicMapping.get(String(channelId));
      if (fId) freqToName.set(fId, String(name));
    }
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
      // Extract display name: remove the "(1050)" suffix and trim
      const displayName = channelName.replace(/\s*\(\d{4}\)$/, '').trim() || channelName;
      freqToName.set(freqId, displayName);
      console.log(`[mapping] Registered default freq ${freqId} from channel "${channelName}" → "${displayName}"`);
    }
  }

  // Save current discovered mappings + channel names to channels.json
  function save() {
    try {
      // Merge static + dynamic mappings
      const merged = {};
      for (const [chId, fId] of mapping) merged[chId] = fId;
      for (const [chId, fId] of dynamicMapping) merged[chId] = fId;

      // Channel names
      const names = {};
      for (const [chId, name] of channelNames) names[chId] = name;

      const json = {
        discordChannelToFreqId: merged,
        channelNames: names,
      };

      fs.writeFileSync(mapPath, JSON.stringify(json, null, 2), 'utf-8');
      console.log(`[mapping] Saved ${Object.keys(merged).length} mappings + ${Object.keys(names).length} names to ${mapPath}`);
      return { mappings: Object.keys(merged).length, names: Object.keys(names).length };
    } catch (e) {
      console.error('[mapping] Failed to save:', e.message);
      throw e;
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
    // Get display name for a frequency ID (e.g., "testkanal" for freq 1050)
    getFreqName: (freqId) => freqToName.get(Number(freqId)) ?? null,
    // Get all freq → name mappings
    getFreqNames: () => {
      const result = {};
      for (const [fId, name] of freqToName) result[fId] = name;
      return result;
    },
    // Register channel with name
    registerChannel,
    // Parse freq from name utility
    parseFreqIdFromName,
    // Get all default frequencies (from Discord channel names)
    getDefaultFrequencies: () => [...defaultFrequencies],
    // Save discovered mappings to disk
    save,
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
const { hashUserId } = require('./crypto');

function createDiscordBot({ token, guildId, mapping, stateStore, usersStore, onStateChange, channelSyncIntervalHours = 24 }) {
  // Wichtig: "Server Members Intent" muss im Discord Developer Portal aktiviert sein,
  // sonst liefert member teilweise keine Daten.
  const client = new Client({
    intents: [
      GatewayIntentBits.Guilds,
      GatewayIntentBits.GuildVoiceStates,
      GatewayIntentBits.GuildMembers,
    ],
  });

  let _syncIntervalHours = channelSyncIntervalHours;
  let _syncHandle = null;
  let _lastChannelSync = null;

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

  // Sync all guild members into discord_users on startup
  async function syncGuildMembers() {
    if (!usersStore) return;
    const guilds = guildId
      ? [client.guilds.cache.get(guildId)].filter(Boolean)
      : [...client.guilds.cache.values()];

    let count = 0;
    const now = Date.now();
    for (const guild of guilds) {
      try {
        const members = await guild.members.fetch();
        for (const [memberId, member] of members) {
          if (member.user.bot) continue;
          const displayName = member.nickname || member.displayName || member.user.username;
          const hashedId = hashUserId(String(memberId));
          usersStore.upsert({
            discord_user_id: hashedId,
            guild_id: String(guild.id),
            display_name: String(displayName),
            updated_at_ms: now,
          });
          count++;
        }
      } catch (e) {
        console.error(`[discord] Failed to sync members for guild ${guild.id}:`, e.message);
      }
    }
    console.log(`[discord] Synced ${count} guild members into user directory`);
  }

  client.once('clientReady', async () => {
    console.log(`[discord] logged in as ${client.user?.tag}`);
    await scanVoiceChannels();
    await syncGuildMembers();
    startChannelSyncScheduler();
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

  // Keep user directory up-to-date when members join or change nickname
  client.on('guildMemberAdd', (member) => {
    if (member.user.bot) return;
    if (guildId && member.guild.id !== guildId) return;
    if (!usersStore) return;
    const displayName = member.nickname || member.displayName || member.user.username;
    const hashedId = hashUserId(String(member.id));
    usersStore.upsert({
      discord_user_id: hashedId,
      guild_id: String(member.guild.id),
      display_name: String(displayName),
      updated_at_ms: Date.now(),
    });
    console.log(`[discord] New member added to user directory: ${displayName}`);
  });

  client.on('guildMemberUpdate', (oldMember, newMember) => {
    if (newMember.user.bot) return;
    if (guildId && newMember.guild.id !== guildId) return;
    if (!usersStore) return;
    const oldName = oldMember.nickname || oldMember.displayName;
    const newName = newMember.nickname || newMember.displayName || newMember.user.username;
    if (oldName !== newName) {
      const hashedId = hashUserId(String(newMember.id));
      usersStore.upsert({
        discord_user_id: hashedId,
        guild_id: String(newMember.guild.id),
        display_name: String(newName),
        updated_at_ms: Date.now(),
      });
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

      // Hash the raw Discord user ID before storing or broadcasting
      const hashedUserId = hashUserId(String(discordUserId));

      const payload = {
        discordUserId: hashedUserId,
        guildId: gId,
        channelId: channelId || null,
        freqId: freqId || null,
        ts: Date.now(),
      };

      // Voice state persist
      stateStore.upsert({
        discord_user_id: hashedUserId,
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
            discord_user_id: hashedUserId,
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

  // --- Channel sync scheduler ---
  function startChannelSyncScheduler() {
    if (_syncHandle) clearInterval(_syncHandle);
    const intervalMs = _syncIntervalHours * 60 * 60 * 1000;
    _syncHandle = setInterval(async () => {
      console.log('[discord] Scheduled channel sync...');
      await triggerChannelSync();
    }, intervalMs);
    console.log(`[discord] Channel sync scheduler started (every ${_syncIntervalHours}h)`);
  }

  async function triggerChannelSync() {
    try {
      await scanVoiceChannels();
      mapping.save();
      _lastChannelSync = new Date().toISOString();
      console.log('[discord] Channel sync completed');
      return { ok: true, lastSync: _lastChannelSync, freqNames: mapping.getFreqNames() };
    } catch (e) {
      console.error('[discord] Channel sync failed:', e.message);
      return { ok: false, error: e.message };
    }
  }

  function setSyncInterval(hours) {
    _syncIntervalHours = Math.max(1, Number(hours) || 24);
    startChannelSyncScheduler();
    console.log(`[discord] Sync interval set to ${_syncIntervalHours}h`);
  }

  function getSyncStatus() {
    return {
      intervalHours: _syncIntervalHours,
      lastSync: _lastChannelSync,
      schedulerRunning: !!_syncHandle,
      freqNames: mapping.getFreqNames(),
    };
  }

  /**
   * Live-fetch a single guild member from Discord API.
   * Returns { discordUserId, guildId, displayName } or null.
   * Also upserts into usersStore on success.
   */
  async function fetchGuildMember(discordUserId, targetGuildId) {
    try {
      const resolvedGuildId = targetGuildId || guildId;
      if (!resolvedGuildId) return null;
      const guild = client.guilds.cache.get(resolvedGuildId);
      if (!guild) return null;
      const member = await guild.members.fetch(discordUserId).catch(() => null);
      if (!member || member.user.bot) return null;
      const displayName = member.nickname || member.displayName || member.user.username;
      const hashedId = hashUserId(String(discordUserId));
      if (usersStore) {
        usersStore.upsert({
          discord_user_id: hashedId,
          guild_id: String(resolvedGuildId),
          display_name: String(displayName),
          updated_at_ms: Date.now(),
        });
      }
      return { discordUserId: hashedId, guildId: String(resolvedGuildId), displayName: String(displayName) };
    } catch (e) {
      console.error('[discord] fetchGuildMember error:', e.message);
      return null;
    }
  }

  return {
    start: async () => { await client.login(token); },
    stop: async () => { clearInterval(_syncHandle); await client.destroy(); },
    triggerChannelSync,
    setSyncInterval,
    getSyncStatus,
    fetchGuildMember,
  };
}

module.exports = { createDiscordBot };
EOF
)"

# src/ws.js
write_file_backup "$SRC_DIR/ws.js" "$(cat <<'EOF'
'use strict';

const WebSocket = require('ws');

function createWsHub({ stateStore }) {
  const wss = new WebSocket.Server({ noServer: true });

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
    wss,
    handleUpgrade: (req, socket, head) => {
      wss.handleUpgrade(req, socket, head, (ws) => {
        wss.emit('connection', ws, req);
      });
    },
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

# src/dsgvo.js - DSGVO / Privacy compliance module
write_file_backup "$SRC_DIR/dsgvo.js" "$(cat <<'EOF'
'use strict';

/**
 * DSGVO (GDPR) Compliance Module
 *
 * - Delete all data for a specific user (discord_user_id)
 * - Delete all data for a specific guild (guild_id)
 * - Automatic cleanup of old data (2 days in compliance mode, 7 days in debug mode)
 * - Debug mode disables automatic cleanup and extends retention to 7 days
 * - Scheduled task runs every 24 hours
 */
function createDsgvo({ db, dsgvoEnabled = false, debugMode = false }) {
  let _enabled = dsgvoEnabled;
  let _debugMode = debugMode;
  let _debugToolActive = false;   // set by external tools to pause auto-cleanup
  let _lastCleanup = null;
  let _schedulerHandle = null;

  const RETENTION_NORMAL_MS = 2 * 24 * 60 * 60 * 1000;   // 2 days
  const RETENTION_DEBUG_MS  = 7 * 24 * 60 * 60 * 1000;   // 7 days
  const SCHEDULER_INTERVAL  = 24 * 60 * 60 * 1000;        // 24 hours

  /**
   * Delete all data for a specific Discord user across all tables.
   * @param {string} hashedUserId  Pre-hashed discord user ID (64-char hex)
   */
  function deleteUser(hashedUserId) {
    const uid = String(hashedUserId);
    const deleted = {
      voice_state: 0,
      tx_events: 0,
      discord_users: 0,
      freq_listeners: 0,
      voice_sessions: 0,
      auth_tokens: 0,
      policy_acceptance: 0,
    };

    deleted.voice_state       = db.prepare('DELETE FROM voice_state WHERE discord_user_id = ?').run(uid).changes;
    deleted.tx_events         = db.prepare('DELETE FROM tx_events WHERE discord_user_id = ?').run(uid).changes;
    deleted.discord_users     = db.prepare('DELETE FROM discord_users WHERE discord_user_id = ?').run(uid).changes;
    deleted.freq_listeners    = db.prepare('DELETE FROM freq_listeners WHERE discord_user_id = ?').run(uid).changes;
    deleted.voice_sessions    = db.prepare('DELETE FROM voice_sessions WHERE discord_user_id = ?').run(uid).changes;
    deleted.auth_tokens       = db.prepare('DELETE FROM auth_tokens WHERE discord_user_id = ?').run(uid).changes;
    deleted.policy_acceptance = db.prepare('DELETE FROM policy_acceptance WHERE discord_user_id = ?').run(uid).changes;

    const total = Object.values(deleted).reduce((a, b) => a + b, 0);
    console.log(`[dsgvo] Deleted ${total} rows for user ${uid.substring(0, 12)}...`, deleted);
    return { hashedUserId: uid, deleted, totalRows: total };
  }

  /**
   * Delete all data for a specific guild across all tables.
   */
  function deleteGuild(guildId) {
    const gid = String(guildId);
    const deleted = {
      voice_state: 0,
      discord_users: 0,
      voice_sessions: 0,
    };

    deleted.voice_state    = db.prepare('DELETE FROM voice_state WHERE guild_id = ?').run(gid).changes;
    deleted.discord_users  = db.prepare('DELETE FROM discord_users WHERE guild_id = ?').run(gid).changes;
    deleted.voice_sessions = db.prepare('DELETE FROM voice_sessions WHERE guild_id = ?').run(gid).changes;

    const total = Object.values(deleted).reduce((a, b) => a + b, 0);
    console.log(`[dsgvo] Deleted ${total} rows for guild ${gid}`, deleted);
    return { guildId: gid, deleted, totalRows: total };
  }

  /**
   * Run cleanup: delete all data older than retention period.
   * Returns summary of what was deleted.
   */
  function runCleanup() {
    const retentionMs = _debugMode ? RETENTION_DEBUG_MS : RETENTION_NORMAL_MS;
    const cutoff = Date.now() - retentionMs;
    const retentionDays = _debugMode ? 7 : 2;

    const deleted = {
      voice_state: 0,
      tx_events: 0,
      discord_users: 0,
      voice_sessions: 0,
    };

    deleted.voice_state    = db.prepare('DELETE FROM voice_state WHERE updated_at_ms < ?').run(cutoff).changes;
    deleted.tx_events      = db.prepare('DELETE FROM tx_events WHERE ts_ms < ?').run(cutoff).changes;
    deleted.discord_users  = db.prepare('DELETE FROM discord_users WHERE updated_at_ms < ?').run(cutoff).changes;
    deleted.voice_sessions = db.prepare('DELETE FROM voice_sessions WHERE last_seen_ms < ?').run(cutoff).changes;

    const total = Object.values(deleted).reduce((a, b) => a + b, 0);
    _lastCleanup = new Date().toISOString();

    console.log(`[dsgvo] Cleanup: deleted ${total} rows older than ${retentionDays} days`, deleted);
    return { deleted, totalRows: total, retentionDays, cutoffTs: cutoff, lastCleanup: _lastCleanup };
  }

  /**
   * Scheduled auto-cleanup (runs every 24h if DSGVO is enabled).
   */
  function scheduledCleanup() {
    if (!_enabled) {
      console.log('[dsgvo] Scheduled cleanup skipped: DSGVO compliance mode disabled');
      return;
    }
    if (_debugToolActive) {
      console.log('[dsgvo] Scheduled cleanup skipped: debug tool is active');
      return;
    }
    console.log('[dsgvo] Running scheduled cleanup...');
    runCleanup();
  }

  function startScheduler() {
    if (_schedulerHandle) return;
    _schedulerHandle = setInterval(scheduledCleanup, SCHEDULER_INTERVAL);
    console.log(`[dsgvo] Scheduler started (every 24h). Enabled=${_enabled}, DebugMode=${_debugMode}`);
  }

  function stopScheduler() {
    if (_schedulerHandle) {
      clearInterval(_schedulerHandle);
      _schedulerHandle = null;
    }
  }

  function setEnabled(enabled) {
    _enabled = !!enabled;
    console.log(`[dsgvo] Compliance mode ${_enabled ? 'ENABLED' : 'DISABLED'}`);
  }

  function setDebugMode(enabled) {
    _debugMode = !!enabled;
    if (_debugMode) {
      _enabled = false;
      console.log('[dsgvo] Debug mode ENABLED — DSGVO compliance mode auto-disabled, retention extended to 7 days');
    } else {
      console.log('[dsgvo] Debug mode DISABLED');
    }
  }

  function setDebugToolActive(active) {
    _debugToolActive = !!active;
    if (_debugToolActive) {
      console.log('[dsgvo] Debug tool active — automatic cleanup paused');
    }
  }

  function getStatus() {
    const warnings = [];
    if (!_enabled) {
      if (_debugMode) {
        warnings.push('DSGVO compliance mode is DISABLED because debug mode is active');
      } else {
        warnings.push('DSGVO compliance mode is DISABLED — user data will NOT be auto-deleted');
      }
    }
    if (_debugToolActive) {
      warnings.push('A debug tool is currently active — automatic cleanup is paused');
    }

    // Ban count
    const banCountRow = db.prepare('SELECT COUNT(*) as cnt FROM banned_users').get();
    const bannedCount = banCountRow ? banCountRow.cnt : 0;

    return {
      dsgvoEnabled: _enabled,
      debugMode: _debugMode,
      debugToolActive: _debugToolActive,
      retentionDays: _debugMode ? 7 : 2,
      schedulerRunning: !!_schedulerHandle,
      lastCleanup: _lastCleanup,
      bannedCount,
      warnings,
    };
  }

  // --- Ban management ---
  // All functions receive pre-hashed discord user IDs.
  // banUser additionally stores the raw ID for admin display.

  function banUser(hashedUserId, rawUserId, reason) {
    const hid = String(hashedUserId);
    db.prepare(
      'INSERT OR REPLACE INTO banned_users (discord_user_id, raw_discord_id, banned_at_ms, reason) VALUES (?, ?, ?, ?)'
    ).run(hid, rawUserId ? String(rawUserId) : null, Date.now(), reason || null);
    console.log('[dsgvo] Banned user', hid.substring(0, 12) + '...');
  }

  function unbanUser(hashedUserId) {
    const hid = String(hashedUserId);
    const result = db.prepare('DELETE FROM banned_users WHERE discord_user_id = ?').run(hid);
    console.log('[dsgvo] Unbanned user', hid.substring(0, 12) + '...', 'rows:', result.changes);
    return result.changes > 0;
  }

  function isBanned(hashedUserId) {
    const row = db.prepare('SELECT 1 FROM banned_users WHERE discord_user_id = ?').get(String(hashedUserId));
    return !!row;
  }

  function listBanned() {
    return db.prepare('SELECT discord_user_id, raw_discord_id, banned_at_ms, reason FROM banned_users ORDER BY banned_at_ms DESC').all();
  }

  /**
   * Delete all user data and add to ban list (prevents re-registration).
   * @param {string} hashedUserId  Pre-hashed discord user ID
   * @param {string} rawUserId     Raw discord user ID (for admin display)
   */
  function deleteAndBanUser(hashedUserId, rawUserId, reason) {
    const deleteResult = deleteUser(hashedUserId);
    banUser(hashedUserId, rawUserId, reason || 'data deletion');
    return { ...deleteResult, banned: true };
  }

  // --- Policy acceptance ---

  function hasPolicyAcceptance(hashedUserId, policyVersion) {
    const row = db.prepare(
      'SELECT 1 FROM policy_acceptance WHERE discord_user_id = ? AND policy_version = ?'
    ).get(String(hashedUserId), String(policyVersion));
    return !!row;
  }

  function acceptPolicy(hashedUserId, policyVersion) {
    db.prepare(
      'INSERT OR REPLACE INTO policy_acceptance (discord_user_id, policy_version, accepted_at_ms) VALUES (?, ?, ?)'
    ).run(String(hashedUserId), String(policyVersion), Date.now());
    console.log('[dsgvo] User', String(hashedUserId).substring(0, 12) + '...', 'accepted policy version', policyVersion);
  }

  return {
    deleteUser,
    deleteGuild,
    runCleanup,
    startScheduler,
    stopScheduler,
    setEnabled,
    setDebugMode,
    setDebugToolActive,
    getStatus,
    banUser,
    unbanUser,
    isBanned,
    listBanned,
    deleteAndBanUser,
    hasPolicyAcceptance,
    acceptPolicy,
  };
}

module.exports = { createDsgvo };
EOF
)"

# src/http.js
write_file_backup "$SRC_DIR/http.js" "$(cat <<'EOF'
'use strict';

const express = require('express');
const http = require('http');

function createHttpServer({ db, mapping, stateStore, txStore, usersStore, dsgvo, bot, adminToken, allowedGuildIds, tokenSecret, policyVersion, policyText, discordClientId, discordClientSecret, discordRedirectUri }) {
  const app = express();
  app.use(express.json());

  // Trust proxy headers from Traefik (X-Forwarded-Proto, X-Forwarded-For)
  app.set('trust proxy', 'loopback');

  // DSGVO HTTPS enforcement: when DSGVO compliance mode is enabled,
  // reject any request that did not arrive via HTTPS (through Traefik).
  // Health endpoint is exempted so internal monitoring keeps working.
  app.use((req, res, next) => {
    if (req.path === '/health') return next();
    if (dsgvo && typeof dsgvo.getStatus === 'function') {
      const status = dsgvo.getStatus();
      if (status.dsgvoEnabled && req.protocol !== 'https') {
        return res.status(403).json({
          ok: false,
          error: 'HTTPS required — DSGVO compliance mode is active. Connect via https:// through the reverse proxy.'
        });
      }
    }
    next();
  });

  const { signToken, verifyToken, hashUserId } = require('./crypto');

  let onTxEventFn = null;
  let _bot = bot;

  // In-memory pending OAuth states: state -> { token, displayName, timestamp } or null (pending)
  const pendingOAuth = new Map();
  // Cleanup old pending states every 5 minutes
  setInterval(() => {
    const now = Date.now();
    for (const [state, val] of pendingOAuth) {
      if (!val && (pendingOAuthTimestamps.get(state) || 0) < now - 5 * 60 * 1000) {
        pendingOAuth.delete(state);
        pendingOAuthTimestamps.delete(state);
      }
      if (val && val.timestamp < now - 5 * 60 * 1000) {
        pendingOAuth.delete(state);
        pendingOAuthTimestamps.delete(state);
      }
    }
  }, 5 * 60 * 1000);

  // Track timestamps for pending states
  const pendingOAuthTimestamps = new Map();

  app.get('/health', (req, res) => res.json({ ok: true }));

  // --- Public endpoints (no auth required) ---

  // Server status: version, DSGVO mode, debug mode, policy version, OAuth URL
  app.get('/server-status', (req, res) => {
    const status = dsgvo ? dsgvo.getStatus() : {};
    const oauthConfigured = !!(discordClientId && discordClientSecret && discordRedirectUri);
    res.json({
      ok: true,
      data: {
        version: 'Alpha 0.0.4',
        dsgvoEnabled: status.dsgvoEnabled || false,
        debugMode: status.debugMode || false,
        retentionDays: status.retentionDays || 0,
        policyVersion: policyVersion || '1.0',
        oauthEnabled: oauthConfigured,
        debugLoginEnabled: status.debugMode || false,
      },
    });
  });

  // Privacy policy text
  app.get('/privacy-policy', (req, res) => {
    res.json({
      ok: true,
      data: {
        version: policyVersion || '1.0',
        text: policyText || 'No privacy policy configured on this server.',
      },
    });
  });

  // --- Auth endpoints ---

  // Login: verify user exists in guild, issue signed token
  // SECURITY: Only available in debug mode — use Discord OAuth2 for production login
  app.post('/auth/login', async (req, res) => {
    const debugActive = dsgvo ? dsgvo.getStatus().debugMode : false;
    if (!debugActive) {
      return res.status(410).json({ ok: false, error: 'direct_login_disabled', message: 'Direct login is disabled. Use Discord OAuth2 to log in. Enable debug mode via service.sh to re-enable.' });
    }

    const { discordUserId, guildId } = req.body || {};
    if (!discordUserId || !guildId) {
      return res.status(400).json({ ok: false, error: 'missing discordUserId or guildId' });
    }

    // Hash the raw Discord ID — raw IDs are never stored
    const hashedId = hashUserId(discordUserId);

    // Check allowed guilds
    if (allowedGuildIds.length > 0 && !allowedGuildIds.includes(String(guildId))) {
      return res.status(403).json({ ok: false, error: 'guild not allowed' });
    }

    // Check if banned
    if (dsgvo && typeof dsgvo.isBanned === 'function' && dsgvo.isBanned(hashedId)) {
      return res.status(403).json({ ok: false, error: 'access denied' });
    }

    // Look up user in local cache
    let user = usersStore ? usersStore.get(hashedId, String(guildId)) : null;

    // Fallback: live Discord API lookup if not in cache
    if (!user && _bot && typeof _bot.fetchGuildMember === 'function') {
      const fetched = await _bot.fetchGuildMember(String(discordUserId), String(guildId));
      if (fetched) {
        user = usersStore ? usersStore.get(hashedId, String(guildId)) : { display_name: fetched.displayName };
      }
    }

    if (!user) {
      return res.status(404).json({ ok: false, error: 'user not found in guild' });
    }

    // Check policy acceptance
    const policyAccepted = dsgvo
      ? dsgvo.hasPolicyAcceptance(hashedId, policyVersion || '1.0')
      : true;

    if (!tokenSecret) {
      return res.status(500).json({ ok: false, error: 'server token secret not configured' });
    }

    // Issue signed token (24h expiry) — uid is the hashed ID
    const payload = {
      uid: hashedId,
      gid: String(guildId),
      name: user.display_name || hashedId.substring(0, 12) + '...',
      iat: Date.now(),
      exp: Date.now() + 24 * 60 * 60 * 1000,
    };
    const token = signToken(payload, tokenSecret);

    // Store token reference in DB (hashed user ID)
    db.prepare(
      'INSERT INTO auth_tokens (token_id, discord_user_id, guild_id, display_name, created_at_ms, expires_at_ms) VALUES (?,?,?,?,?,?)'
    ).run(token.substring(0, 64), hashedId, String(guildId), payload.name, payload.iat, payload.exp);

    res.json({
      ok: true,
      data: {
        token,
        displayName: payload.name,
        policyVersion: policyVersion || '1.0',
        policyAccepted,
      },
    });
  });

  // Accept privacy policy
  app.post('/auth/accept-policy', (req, res) => {
    const authHeader = req.header('authorization') || '';
    const token = authHeader.startsWith('Bearer ') ? authHeader.slice(7) : '';
    if (!token || !tokenSecret) {
      return res.status(401).json({ ok: false, error: 'unauthorized' });
    }
    const payload = verifyToken(token, tokenSecret);
    if (!payload) {
      return res.status(401).json({ ok: false, error: 'invalid or expired token' });
    }

    const { version } = req.body || {};
    const pv = version || policyVersion || '1.0';

    if (dsgvo && typeof dsgvo.acceptPolicy === 'function') {
      dsgvo.acceptPolicy(payload.uid, pv);
    }

    res.json({ ok: true, data: { accepted: true, version: pv } });
  });

  // --- Discord OAuth2 endpoints ---

  // Step 1: Companion app opens this URL in browser → redirects to Discord authorize
  app.get('/auth/discord/redirect', (req, res) => {
    const { state } = req.query;
    if (!state) return res.status(400).send('Missing state parameter');
    if (!discordClientId || !discordRedirectUri) {
      return res.status(500).send('Discord OAuth2 not configured on this server');
    }
    pendingOAuth.set(state, null);
    pendingOAuthTimestamps.set(state, Date.now());

    const scope = 'identify guilds';
    const url = 'https://discord.com/oauth2/authorize?response_type=code'
      + '&client_id=' + encodeURIComponent(discordClientId)
      + '&scope=' + encodeURIComponent(scope)
      + '&state=' + encodeURIComponent(state)
      + '&redirect_uri=' + encodeURIComponent(discordRedirectUri)
      + '&prompt=consent';
    res.redirect(url);
  });

  // Step 2: Discord redirects here after user authorizes → exchange code → issue token
  app.get('/auth/discord/callback', async (req, res) => {
    const { code, state } = req.query;
    if (!code || !state) return res.status(400).send('Missing code or state');
    if (!pendingOAuth.has(state)) return res.status(400).send('Unknown or expired state');

    try {
      // Exchange code for Discord access token
      const tokenResp = await fetch('https://discord.com/api/v10/oauth2/token', {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
          grant_type: 'authorization_code',
          code: String(code),
          redirect_uri: discordRedirectUri,
          client_id: discordClientId,
          client_secret: discordClientSecret,
        }),
      });
      if (!tokenResp.ok) {
        const errBody = await tokenResp.text();
        console.error('[oauth] Token exchange failed:', tokenResp.status, errBody);
        return res.status(500).send('<html><body style="background:#1a1a2e;color:#ff4a4a;font-family:sans-serif;text-align:center;padding:60px"><h2>Login Failed</h2><p>Could not exchange authorization code.</p><p style="color:#888">You can close this window.</p></body></html>');
      }
      const tokenData = await tokenResp.json();
      const discordAccessToken = tokenData.access_token;

      // Fetch user identity
      const userResp = await fetch('https://discord.com/api/v10/users/@me', {
        headers: { Authorization: 'Bearer ' + discordAccessToken },
      });
      if (!userResp.ok) {
        return res.status(500).send('<html><body style="background:#1a1a2e;color:#ff4a4a;font-family:sans-serif;text-align:center;padding:60px"><h2>Login Failed</h2><p>Could not fetch user identity.</p><p style="color:#888">You can close this window.</p></body></html>');
      }
      const discordUser = await userResp.json();
      const discordUserId = discordUser.id;
      const discordUsername = discordUser.global_name || discordUser.username || discordUser.id;

      // Hash the raw Discord ID — raw IDs are never stored in the database
      const hashedId = hashUserId(discordUserId);

      // Fetch user guilds to find matching guild
      const guildsResp = await fetch('https://discord.com/api/v10/users/@me/guilds', {
        headers: { Authorization: 'Bearer ' + discordAccessToken },
      });
      let userGuilds = [];
      if (guildsResp.ok) {
        userGuilds = await guildsResp.json();
      }

      // Find matching allowed guild
      let matchedGuildId = null;
      if (allowedGuildIds.length > 0) {
        for (const g of userGuilds) {
          if (allowedGuildIds.includes(String(g.id))) {
            matchedGuildId = String(g.id);
            break;
          }
        }
        if (!matchedGuildId) {
          pendingOAuth.set(state, { error: 'not_in_guild', timestamp: Date.now() });
          return res.send('<html><body style="background:#1a1a2e;color:#ff4a4a;font-family:sans-serif;text-align:center;padding:60px"><h2>Login Failed</h2><p>You are not a member of the allowed Discord server.</p><p style="color:#888">You can close this window.</p></body></html>');
        }
      } else if (userGuilds.length > 0) {
        matchedGuildId = String(userGuilds[0].id);
      }

      if (!matchedGuildId) {
        pendingOAuth.set(state, { error: 'no_guild', timestamp: Date.now() });
        return res.send('<html><body style="background:#1a1a2e;color:#ff4a4a;font-family:sans-serif;text-align:center;padding:60px"><h2>Login Failed</h2><p>No guilds found for your account.</p><p style="color:#888">You can close this window.</p></body></html>');
      }

      // Check ban (using hashed ID)
      if (dsgvo && typeof dsgvo.isBanned === 'function' && dsgvo.isBanned(hashedId)) {
        pendingOAuth.set(state, { error: 'banned', timestamp: Date.now() });
        return res.send('<html><body style="background:#1a1a2e;color:#ff4a4a;font-family:sans-serif;text-align:center;padding:60px"><h2>Access Denied</h2><p>Your account has been banned from this server.</p><p style="color:#888">You can close this window.</p></body></html>');
      }

      // Fetch/upsert guild member via bot for display name
      let displayName = discordUsername;
      if (_bot && typeof _bot.fetchGuildMember === 'function') {
        const member = await _bot.fetchGuildMember(String(discordUserId), matchedGuildId);
        if (member) displayName = member.displayName;
      }

      // Issue signed auth token (uid = hashed ID)
      const authPayload = {
        uid: hashedId,
        gid: matchedGuildId,
        name: displayName,
        iat: Date.now(),
        exp: Date.now() + 24 * 60 * 60 * 1000,
      };
      const authToken = signToken(authPayload, tokenSecret);

      // Store token in DB (hashed user ID)
      db.prepare(
        'INSERT INTO auth_tokens (token_id, discord_user_id, guild_id, display_name, created_at_ms, expires_at_ms) VALUES (?,?,?,?,?,?)'
      ).run(authToken.substring(0, 64), hashedId, matchedGuildId, displayName, authPayload.iat, authPayload.exp);

      // Store result for companion app polling
      // NOTE: Do NOT store raw discordUserId/guildId — the signed token contains all needed info
      pendingOAuth.set(state, {
        token: authToken,
        displayName,
        policyVersion: policyVersion || '1.0',
        policyAccepted: dsgvo ? dsgvo.hasPolicyAcceptance(hashedId, policyVersion || '1.0') : true,
        timestamp: Date.now(),
      });

      // Revoke Discord access token (we don't need it anymore)
      try {
        await fetch('https://discord.com/api/v10/oauth2/token/revoke', {
          method: 'POST',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
          body: new URLSearchParams({
            token: discordAccessToken,
            token_type_hint: 'access_token',
            client_id: discordClientId,
            client_secret: discordClientSecret,
          }),
        });
      } catch (e) { console.warn('[oauth] Token revoke failed:', e.message); }

      console.log('[oauth] Login OK: ' + hashedId.substring(0, 12) + '... (' + displayName + ') in guild ' + matchedGuildId);
      res.send('<html><body style="background:#1a1a2e;color:#4AFF9E;font-family:sans-serif;text-align:center;padding:60px"><h2>&#10003; Login Successful</h2><p style="color:#ccc">Welcome, <strong>' + displayName + '</strong></p><p style="color:#888">You can close this window and return to the Companion App.</p></body></html>');
    } catch (e) {
      console.error('[oauth] Callback error:', e);
      pendingOAuth.set(state, { error: 'server_error', timestamp: Date.now() });
      res.status(500).send('<html><body style="background:#1a1a2e;color:#ff4a4a;font-family:sans-serif;text-align:center;padding:60px"><h2>Login Failed</h2><p>An unexpected error occurred.</p><p style="color:#888">You can close this window.</p></body></html>');
    }
  });

  // Step 3: Companion app polls this endpoint for the OAuth result
  app.get('/auth/discord/poll', (req, res) => {
    const { state } = req.query;
    if (!state) return res.status(400).json({ ok: false, error: 'missing state' });
    if (!pendingOAuth.has(state)) return res.json({ ok: true, data: { status: 'unknown' } });

    const result = pendingOAuth.get(state);
    if (result === null) {
      return res.json({ ok: true, data: { status: 'pending' } });
    }

    if (result.error) {
      pendingOAuth.delete(state);
      pendingOAuthTimestamps.delete(state);
      return res.json({ ok: true, data: { status: 'error', error: result.error } });
    }

    // Success – return token and clean up
    pendingOAuth.delete(state);
    pendingOAuthTimestamps.delete(state);
    res.json({
      ok: true,
      data: {
        status: 'success',
        token: result.token,
        displayName: result.displayName,
        policyVersion: result.policyVersion,
        policyAccepted: result.policyAccepted,
      },
    });
  });

  app.get('/state/:discordUserId', (req, res) => {
    const hashedId = hashUserId(req.params.discordUserId);
    const row = stateStore.get(hashedId);
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

    const hashedUid = discordUserId ? hashUserId(discordUserId) : null;

    const row = {
      freq_id: f,
      discord_user_id: hashedUid,
      radio_slot: (radioSlot === null || radioSlot === undefined) ? null : Number(radioSlot),
      action,
      ts_ms: ts,
      meta_json: meta ? JSON.stringify(meta) : null,
    };

    txStore.addEvent(row);

    const payload = {
      freqId: row.freq_id,
      discordUserId: hashedUid,
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
    const hashedUid = hashUserId(discordUserId);
    db.prepare(
      'INSERT OR REPLACE INTO freq_listeners (discord_user_id, freq_id, radio_slot, connected_at_ms) VALUES (?,?,?,?)'
    ).run(hashedUid, f, Number(radioSlot) || 0, Date.now());

    const row = db.prepare('SELECT COUNT(DISTINCT discord_user_id) as cnt FROM freq_listeners WHERE freq_id = ?').get(f);
    res.json({ ok: true, listener_count: row ? row.cnt : 0 });
  });

  app.post('/freq/leave', (req, res) => {
    const { discordUserId, freqId } = req.body || {};
    if (!discordUserId || !freqId) {
      return res.status(400).json({ ok: false, error: 'missing discordUserId or freqId' });
    }
    const f = Number(freqId);
    const hashedUid = hashUserId(discordUserId);
    db.prepare('DELETE FROM freq_listeners WHERE discord_user_id = ? AND freq_id = ?').run(hashedUid, f);

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
    const hashedId = hashUserId(discordUserId);
    const row = (usersStore && typeof usersStore.get === 'function') ? usersStore.get(hashedId, guildId) : null;
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

  // --- DSGVO / Privacy compliance endpoints ---

  // Helper: admin auth check
  function requireAdmin(req, res) {
    const token = req.header('x-admin-token') || '';
    if (!adminToken || token !== adminToken) {
      res.status(403).json({ ok: false, error: 'forbidden' });
      return false;
    }
    return true;
  }

  // Get DSGVO status
  app.get('/admin/dsgvo/status', (req, res) => {
    if (!requireAdmin(req, res)) return;
    res.json({ ok: true, data: dsgvo.getStatus() });
  });

  // Enable/disable DSGVO compliance mode
  app.post('/admin/dsgvo/toggle', (req, res) => {
    if (!requireAdmin(req, res)) return;
    const { enabled } = req.body || {};
    if (typeof enabled !== 'boolean') {
      return res.status(400).json({ ok: false, error: 'missing boolean "enabled"' });
    }
    dsgvo.setEnabled(enabled);
    res.json({ ok: true, data: dsgvo.getStatus() });
  });

  // Enable/disable debug mode
  app.post('/admin/dsgvo/debug', (req, res) => {
    if (!requireAdmin(req, res)) return;
    const { enabled } = req.body || {};
    if (typeof enabled !== 'boolean') {
      return res.status(400).json({ ok: false, error: 'missing boolean "enabled"' });
    }
    dsgvo.setDebugMode(enabled);
    res.json({ ok: true, data: dsgvo.getStatus() });
  });

  // Delete all data for a specific user
  app.post('/admin/dsgvo/delete-user', (req, res) => {
    if (!requireAdmin(req, res)) return;
    const { discordUserId } = req.body || {};
    if (!discordUserId) {
      return res.status(400).json({ ok: false, error: 'missing discordUserId' });
    }
    const hashedId = hashUserId(String(discordUserId));
    const result = dsgvo.deleteUser(hashedId);
    res.json({ ok: true, data: result });
  });

  // Delete all data for a specific guild
  app.post('/admin/dsgvo/delete-guild', (req, res) => {
    if (!requireAdmin(req, res)) return;
    const { guildId } = req.body || {};
    if (!guildId) {
      return res.status(400).json({ ok: false, error: 'missing guildId' });
    }
    const result = dsgvo.deleteGuild(String(guildId));
    res.json({ ok: true, data: result });
  });

  // Manually trigger DSGVO cleanup
  app.post('/admin/dsgvo/cleanup', (req, res) => {
    if (!requireAdmin(req, res)) return;
    const result = dsgvo.runCleanup();
    res.json({ ok: true, data: result });
  });

  // --- Channel sync endpoints ---

  // Get frequency → channel name mappings (public, no auth required)
  app.get('/freq/names', (req, res) => {
    res.json({ ok: true, data: mapping.getFreqNames() });
  });

  // Get channel sync status
  app.get('/admin/channel-sync/status', (req, res) => {
    if (!requireAdmin(req, res)) return;
    if (!_bot || !_bot.getSyncStatus) {
      return res.status(500).json({ ok: false, error: 'bot not available' });
    }
    res.json({ ok: true, data: _bot.getSyncStatus() });
  });

  // Trigger manual channel sync
  app.post('/admin/channel-sync/trigger', async (req, res) => {
    if (!requireAdmin(req, res)) return;
    if (!_bot || !_bot.triggerChannelSync) {
      return res.status(500).json({ ok: false, error: 'bot not available' });
    }
    const result = await _bot.triggerChannelSync();
    res.json({ ok: true, data: result });
  });

  // Set channel sync interval
  app.post('/admin/channel-sync/interval', (req, res) => {
    if (!requireAdmin(req, res)) return;
    if (!_bot || !_bot.setSyncInterval) {
      return res.status(500).json({ ok: false, error: 'bot not available' });
    }
    const { hours } = req.body || {};
    if (!hours || typeof hours !== 'number' || hours < 1) {
      return res.status(400).json({ ok: false, error: 'missing or invalid "hours" (min 1)' });
    }
    _bot.setSyncInterval(hours);
    res.json({ ok: true, data: _bot.getSyncStatus() });
  });

  // --- Ban management endpoints ---

  app.post('/admin/ban', (req, res) => {
    if (!requireAdmin(req, res)) return;
    const { discordUserId, reason } = req.body || {};
    if (!discordUserId) {
      return res.status(400).json({ ok: false, error: 'missing discordUserId' });
    }
    if (dsgvo && typeof dsgvo.banUser === 'function') {
      const hashedId = hashUserId(String(discordUserId));
      dsgvo.banUser(hashedId, String(discordUserId), reason || null);
      res.json({ ok: true });
    } else {
      res.status(500).json({ ok: false, error: 'dsgvo module not available' });
    }
  });

  app.post('/admin/unban', (req, res) => {
    if (!requireAdmin(req, res)) return;
    const { discordUserId } = req.body || {};
    if (!discordUserId) {
      return res.status(400).json({ ok: false, error: 'missing discordUserId' });
    }
    if (dsgvo && typeof dsgvo.unbanUser === 'function') {
      const hashedId = hashUserId(String(discordUserId));
      const removed = dsgvo.unbanUser(hashedId);
      res.json({ ok: true, data: { removed } });
    } else {
      res.status(500).json({ ok: false, error: 'dsgvo module not available' });
    }
  });

  app.get('/admin/bans', (req, res) => {
    if (!requireAdmin(req, res)) return;
    if (dsgvo && typeof dsgvo.listBanned === 'function') {
      res.json({ ok: true, data: dsgvo.listBanned() });
    } else {
      res.json({ ok: true, data: [] });
    }
  });

  app.post('/admin/dsgvo/delete-and-ban', (req, res) => {
    if (!requireAdmin(req, res)) return;
    const { discordUserId, reason } = req.body || {};
    if (!discordUserId) {
      return res.status(400).json({ ok: false, error: 'missing discordUserId' });
    }
    if (dsgvo && typeof dsgvo.deleteAndBanUser === 'function') {
      const hashedId = hashUserId(String(discordUserId));
      const result = dsgvo.deleteAndBanUser(hashedId, String(discordUserId), reason);
      res.json({ ok: true, data: result });
    } else {
      res.status(500).json({ ok: false, error: 'dsgvo module not available' });
    }
  });

  const server = http.createServer(app);
  server._setOnTxEvent = (fn) => { onTxEventFn = fn; };
  server._setBot = (b) => { _bot = b; };
  return server;
}

module.exports = { createHttpServer };
EOF
)"

# Ensure ownership for backend dir (best-effort)
chown -R "$ADMIN_USER:$ADMIN_USER" "$BACKEND_DIR" 2>/dev/null || true

# ==========================================================
# PHASE 4: .env Configuration & Validation
# ==========================================================

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

  # Generate TOKEN_SECRET for auth token signing
  TOKEN_SECRET="$(openssl rand -hex 32)"
  log_ok "TOKEN_SECRET generiert"

  # Discord OAuth2 configuration (optional)
  log_input ""
  log_input "=== Discord OAuth2 (optional – für Login mit Discord) ==="
  log_input "Erstelle eine Application auf https://discord.com/developers/applications"
  log_input "Unter OAuth2 → Redirects muss die Redirect URI eingetragen sein."
  if [ -n "${DOMAIN:-}" ]; then
    log_input "Format: https://<Domain>/auth/discord/callback"
  else
    log_input "Format: http://<IP-oder-Domain>:<Port>/auth/discord/callback"
  fi
  log_input ""
  read -r -p "$(echo -e "${CYAN}Discord Client ID (Application ID, leer = kein OAuth):${NC} ")" DISCORD_CLIENT_ID
  if [ -n "$DISCORD_CLIENT_ID" ]; then
    read -s -p "$(echo -e "${CYAN}Discord Client Secret:${NC} ")" DISCORD_CLIENT_SECRET; echo ""
    if [ -n "${DOMAIN:-}" ]; then
      DEFAULT_REDIRECT="https://${DOMAIN}/auth/discord/callback"
    else
      DEFAULT_REDIRECT="http://${BIND_HOST}:${BIND_PORT}/auth/discord/callback"
    fi
    read -r -p "$(echo -e "${CYAN}Discord Redirect URI [${DEFAULT_REDIRECT}]:${NC} ")" DISCORD_REDIRECT_URI
    DISCORD_REDIRECT_URI="${DISCORD_REDIRECT_URI:-$DEFAULT_REDIRECT}"
  else
    DISCORD_CLIENT_SECRET=""
    DISCORD_REDIRECT_URI=""
  fi

  cat > "$ENV_FILE" <<EOF
DISCORD_TOKEN=$DISCORD_TOKEN
$([ -n "$DISCORD_GUILD_ID" ] && echo "DISCORD_GUILD_ID=$DISCORD_GUILD_ID" || echo "# DISCORD_GUILD_ID=123456789012345678")

BIND_HOST=$BIND_HOST
BIND_PORT=$BIND_PORT
$([ -n "${DOMAIN:-}" ] && echo "DOMAIN=$DOMAIN" || echo "# DOMAIN=your-domain.com")

DB_PATH=$BACKEND_DIR/state.sqlite
CHANNEL_MAP_PATH=$CHANNEL_MAP

ADMIN_TOKEN=$ADMIN_TOKEN

# Token signing secret for auth (auto-generated)
TOKEN_SECRET=$TOKEN_SECRET

# Discord OAuth2 (Login with Discord)
$([ -n "$DISCORD_CLIENT_ID" ] && echo "DISCORD_CLIENT_ID=$DISCORD_CLIENT_ID" || echo "# DISCORD_CLIENT_ID=")
$([ -n "$DISCORD_CLIENT_SECRET" ] && echo "DISCORD_CLIENT_SECRET=$DISCORD_CLIENT_SECRET" || echo "# DISCORD_CLIENT_SECRET=")
$([ -n "$DISCORD_REDIRECT_URI" ] && echo "DISCORD_REDIRECT_URI=$DISCORD_REDIRECT_URI" || echo "# DISCORD_REDIRECT_URI=https://your-domain.com/auth/discord/callback")

# Privacy policy
POLICY_VERSION=1.0
POLICY_PATH=$APP_ROOT/config/privacy-policy.md

# DSGVO compliance mode: auto-delete user data older than 2 days (7 in debug mode)
DSGVO_ENABLED=false
DEBUG_MODE=false

# Channel sync: how often to re-scan Discord channels for freq mappings (in hours)
CHANNEL_SYNC_INTERVAL_HOURS=24
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
  "$SRC_DIR/dsgvo.js"
  "$SRC_DIR/crypto.js"
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

# systemd Services starten
log_info "Starte/Restart systemd Services"
systemctl restart das-krt-backend
systemctl status das-krt-backend --no-pager || true

if [ -f "$TRAEFIK_SERVICE_FILE" ]; then
  log_info "Starte/Restart Traefik"
  systemctl restart traefik
  sleep 2
  if systemctl is-active --quiet traefik; then
    log_ok "Traefik läuft"
    if [ -n "${DOMAIN:-}" ]; then
      log_info "TLS-Zertifikat wird automatisch per Let's Encrypt bezogen"
      log_info "Erreichbar unter: https://${DOMAIN}"
    fi
  else
    log_warn "Traefik konnte nicht gestartet werden"
    systemctl status traefik --no-pager || true
  fi
fi

echo ""
log_ok "Installation abgeschlossen | ${VERSION}"
echo ""
log_info "Verwende service.sh für Start/Stop/Restart und Tools:"
log_info "  bash service.sh start|stop|restart|status|menu"
if [ -n "${DOMAIN:-}" ]; then
  log_info "  Backend erreichbar: https://${DOMAIN}"
  log_info "  OAuth2 Redirect:    https://${DOMAIN}/auth/discord/callback"
fi
echo ""
