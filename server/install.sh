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
# Mumble Server
# --------------------------------------------------
log_info "[6/10] Installiere Mumble Server"
apt -y install mumble-server
log_ok "mumble-server installiert"

if [ -f "$MUMBLE_CONFIG" ] && ! grep -q "bandwidth=" "$MUMBLE_CONFIG"; then
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
if [ -f "$MUMBLE_CONFIG" ] && ! grep -q '^ice=' "$MUMBLE_CONFIG"; then
  echo 'ice="tcp -h 127.0.0.1 -p 6502"' >> "$MUMBLE_CONFIG"
  log_ok "Ice API (localhost) ergänzt"
else
  log_ok "Ice API bereits konfiguriert"
fi

# Ensure icesecretwrite is empty so authenticator can connect via Ice
if [ -f "$MUMBLE_CONFIG" ] && ! grep -q '^icesecretwrite=' "$MUMBLE_CONFIG"; then
  echo 'icesecretwrite=' >> "$MUMBLE_CONFIG"
  log_ok "icesecretwrite (leer) ergänzt für Authenticator"
fi

systemctl restart mumble-server >/dev/null 2>&1 || true
log_ok "Mumble Server neu gestartet"

# --------------------------------------------------
# Node.js 24
# --------------------------------------------------
log_info "[7/10] Installiere Node.js ${NODE_VERSION}"
curl -fsSL "https://deb.nodesource.com/setup_${NODE_VERSION}.x" | bash -
apt -y install nodejs
log_ok "Node.js installiert: $(node -v) | npm: $(npm -v)"

# --------------------------------------------------
# Mumble Authenticator (Python + zeroc-ice)
# --------------------------------------------------
log_info "[7b/10] Installiere Mumble Authenticator Abhängigkeiten"
apt -y install python3 python3-pip python3-venv >/dev/null 2>&1 || true

MUMBLE_AUTH_DIR="$APP_ROOT/mumble-auth"
MUMBLE_AUTH_VENV="$MUMBLE_AUTH_DIR/venv"
mkdir -p "$MUMBLE_AUTH_DIR"

if [ ! -d "$MUMBLE_AUTH_VENV" ]; then
  python3 -m venv "$MUMBLE_AUTH_VENV"
  log_ok "Python venv erstellt: $MUMBLE_AUTH_VENV"
else
  log_ok "Python venv existiert bereits"
fi

"$MUMBLE_AUTH_VENV/bin/pip" install --quiet zeroc-ice requests >/dev/null 2>&1 || true
log_ok "zeroc-ice + requests installiert"

# Write the authenticator script
write_file_backup "$MUMBLE_AUTH_DIR/mumble-auth.py" "$(cat <<'PYEOF'
#!/usr/bin/env python3
\"\"\"
Mumble Ice Authenticator for das-krt.
Validates users against the das-krt backend:
  username = discord_user_id
  password = guild_id

If the backend confirms the user is a member of the guild,
the user is allowed in and assigned to the 'discord' group
so that Mumble ACLs can grant channel create/join rights.
\"\"\"

import os
import sys
import json
import time
import logging
import requests
import Ice

# Load Murmur Ice interface definition
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ICE_FILE = os.path.join(SCRIPT_DIR, 'Murmur.ice')
if not os.path.exists(ICE_FILE):
    # Try system locations
    for p in ['/usr/share/slice/Murmur.ice', '/usr/share/mumble-server/Murmur.ice', '/usr/share/mumble/Murmur.ice']:
        if os.path.exists(p):
            ICE_FILE = p
            break

Ice.loadSlice(ICE_FILE)
import Murmur

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [mumble-auth] %(levelname)s %(message)s',
)
log = logging.getLogger('mumble-auth')

BACKEND_URL = os.environ.get('MUMBLE_AUTH_BACKEND', 'http://127.0.0.1:3000')
ICE_HOST = os.environ.get('MUMBLE_ICE_HOST', '127.0.0.1')
ICE_PORT = os.environ.get('MUMBLE_ICE_PORT', '6502')

# Texture and comment are not used
FALLBACK = -2  # -2 = let Mumble handle it (fall through to default auth)
AUTH_REFUSED = -1  # reject


class KrtAuthenticator(Murmur.ServerAuthenticator):
    \"\"\"Ice authenticator callback object.\"\"\"

    def __init__(self, server):
        self.server = server

    def authenticate(self, name, pw, certificates, certhash, certstrong, current=None):
        \"\"\"
        Called by Mumble for every login attempt.
        Returns (userId, displayName, groups).
          userId >= 0  -> authenticated (Mumble will auto-register if needed)
          userId == -1 -> authentication refused
          userId == -2 -> fall through to default Mumble auth
        \"\"\"
        # Let SuperUser through to default auth
        if name == 'SuperUser':
            log.info('SuperUser login -> fall through to default auth')
            return (FALLBACK, name, [])

        # Skip empty credentials
        if not name or not pw:
            log.info(f'Empty credentials for "{name}" -> refused')
            return (AUTH_REFUSED, name, [])

        try:
            resp = requests.post(
                f'{BACKEND_URL}/mumble/auth',
                json={'username': name, 'password': pw},
                timeout=5,
            )
            data = resp.json()
        except Exception as e:
            log.error(f'Backend request failed: {e}')
            # On backend error, fall through to let Mumble handle it
            return (FALLBACK, name, [])

        if data.get('ok'):
            display_name = data.get('displayName', name)
            groups = data.get('groups', [])
            # Use a stable numeric ID derived from discord_user_id
            # Mumble needs a positive int; we hash the string
            user_id = abs(hash(name)) % (2**30)
            if user_id == 0:
                user_id = 1  # 0 is reserved for SuperUser

            log.info(f'AUTH OK: {name} -> uid={user_id} display="{display_name}" groups={groups}')
            return (user_id, display_name, groups)
        else:
            reason = data.get('reason', 'unknown')
            log.info(f'AUTH DENIED: {name} reason={reason}')
            return (AUTH_REFUSED, name, [])

    def getInfo(self, id, current=None):
        \"\"\"Return user info. Not implemented - let Mumble handle it.\"\"\"
        return (False, {})

    def nameToId(self, name, current=None):
        \"\"\"Map name to user ID. Return -2 to fall through.\"\"\"
        return FALLBACK

    def idToName(self, id, current=None):
        \"\"\"Map user ID to name. Return empty to fall through.\"\"\"
        return ''

    def idToTexture(self, id, current=None):
        \"\"\"Return user texture/avatar. Not implemented.\"\"\"
        return bytes()


def main():
    log.info(f'Starting Mumble authenticator (backend={BACKEND_URL}, ice={ICE_HOST}:{ICE_PORT})')

    # Initialize Ice
    props = Ice.createProperties()
    props.setProperty('Ice.ImplicitContext', 'Shared')
    props.setProperty('Ice.MessageSizeMax', '65536')

    init_data = Ice.InitializationData()
    init_data.properties = props

    ice = Ice.initialize(init_data)

    try:
        # Connect to Murmur Ice endpoint
        proxy_str = f'Meta:tcp -h {ICE_HOST} -p {ICE_PORT}'
        base = ice.stringToProxy(proxy_str)
        meta = Murmur.MetaPrx.checkedCast(base)
        if not meta:
            log.error('Could not connect to Murmur Ice interface')
            sys.exit(1)

        # Get default virtual server (id=1)
        servers = meta.getBootedServers()
        if not servers:
            log.error('No booted Mumble servers found')
            sys.exit(1)

        server = servers[0]
        log.info(f'Connected to Mumble server id={server.id()}')

        # Create authenticator adapter
        adapter = ice.createObjectAdapterWithEndpoints(
            'Authenticator', 'tcp -h 127.0.0.1'
        )
        auth = KrtAuthenticator(server)
        auth_proxy = adapter.addWithUUID(auth)
        adapter.activate()

        # Register authenticator with the server
        server.setAuthenticator(Murmur.ServerAuthenticatorPrx.uncheckedCast(auth_proxy))
        log.info('Authenticator registered with Mumble server')

        # Keep running
        ice.waitForShutdown()
    except KeyboardInterrupt:
        log.info('Shutting down')
    except Exception as e:
        log.error(f'Fatal error: {e}', exc_info=True)
    finally:
        ice.destroy()


if __name__ == '__main__':
    main()
PYEOF
)"

# The authenticator needs the Murmur Ice stubs (Murmur.ice -> Murmur.py)
# We generate them from the Murmur.ice file that ships with mumble-server
MURMUR_ICE_FILE="/usr/share/slice/Murmur.ice"
if [ ! -f "$MURMUR_ICE_FILE" ]; then
  # Try alternative locations
  MURMUR_ICE_FILE="/usr/share/mumble-server/Murmur.ice"
fi
if [ ! -f "$MURMUR_ICE_FILE" ]; then
  MURMUR_ICE_FILE="/usr/share/mumble/Murmur.ice"
fi

if [ -f "$MURMUR_ICE_FILE" ]; then
  cd "$MUMBLE_AUTH_DIR"
  "$MUMBLE_AUTH_VENV/bin/python3" -c "import Ice; Ice.loadSlice('$MURMUR_ICE_FILE')" 2>/dev/null && \
    log_ok "Murmur.ice slice loaded successfully" || \
    log_warn "Could not pre-load Murmur.ice slice (will try at runtime)"
  # Copy the .ice file so the script can find it
  cp "$MURMUR_ICE_FILE" "$MUMBLE_AUTH_DIR/Murmur.ice" 2>/dev/null || true
else
  log_warn "Murmur.ice not found - authenticator may not work. Install mumble-server first."
fi

chown -R "$ADMIN_USER:$ADMIN_USER" "$MUMBLE_AUTH_DIR" || true

# Systemd service for the authenticator
write_file_backup "/etc/systemd/system/das-krt-mumble-auth.service" "$(cat <<EOF
[Unit]
Description=das-krt Mumble Authenticator
After=mumble-server.service das-krt-backend.service
Requires=mumble-server.service

[Service]
Type=simple
User=$ADMIN_USER
WorkingDirectory=$MUMBLE_AUTH_DIR
Environment=MUMBLE_AUTH_BACKEND=http://127.0.0.1:3000
Environment=MUMBLE_ICE_HOST=127.0.0.1
Environment=MUMBLE_ICE_PORT=6502
ExecStart=$MUMBLE_AUTH_VENV/bin/python3 $MUMBLE_AUTH_DIR/mumble-auth.py
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
)"

systemctl daemon-reload
systemctl enable das-krt-mumble-auth >/dev/null 2>&1 || true
log_ok "Mumble Authenticator installiert (Service: das-krt-mumble-auth)"

# --------------------------------------------------
# Mumble ACL Setup (via Ice)
# --------------------------------------------------
log_info "[7c/10] Setze Mumble ACL für 'discord'-Gruppe"

write_file_backup "$MUMBLE_AUTH_DIR/setup-acl.py" "$(cat <<'ACLEOF'
#!/usr/bin/env python3
"""
One-shot script: set Mumble ACLs on the Root channel via Ice.

Grants the 'discord' group:
  - Traverse, Enter (join channels)
  - Speak, MuteDeafen, SelfMute, SelfDeafen
  - TextMessage
  - MakeTempChannel (create temporary channels)

Also removes the default @all write-permissions so only
authenticated Discord users can actually do things.
"""

import os
import sys
import Ice

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ICE_FILE = os.path.join(SCRIPT_DIR, 'Murmur.ice')
if not os.path.exists(ICE_FILE):
    for p in ['/usr/share/slice/Murmur.ice',
              '/usr/share/mumble-server/Murmur.ice',
              '/usr/share/mumble/Murmur.ice']:
        if os.path.exists(p):
            ICE_FILE = p
            break

Ice.loadSlice(ICE_FILE)
import Murmur

# Mumble permission bits (from Murmur.ice / Mumble source)
PERM_NONE            = 0x00000
PERM_WRITE           = 0x00001
PERM_TRAVERSE        = 0x00002
PERM_ENTER           = 0x00004
PERM_SPEAK           = 0x00008
PERM_MUTE_DEAFEN     = 0x00010
PERM_MOVE            = 0x00020
PERM_MAKE_CHANNEL    = 0x00040
PERM_LINK_CHANNEL    = 0x00080
PERM_WHISPER         = 0x00100
PERM_TEXT_MESSAGE     = 0x00200
PERM_MAKE_TEMP       = 0x00400
PERM_LISTEN          = 0x00800
# Shortcuts
PERM_SELF_MUTE       = 0x10000
PERM_SELF_DEAFEN     = 0x20000
PERM_KICK            = 0x010000
PERM_BAN             = 0x020000
PERM_REGISTER        = 0x040000
PERM_REGISTER_SELF   = 0x080000

ICE_HOST = os.environ.get('MUMBLE_ICE_HOST', '127.0.0.1')
ICE_PORT = os.environ.get('MUMBLE_ICE_PORT', '6502')
ROOT_CHANNEL_ID = 0


def main():
    props = Ice.createProperties()
    props.setProperty('Ice.ImplicitContext', 'Shared')
    init_data = Ice.InitializationData()
    init_data.properties = props
    ice = Ice.initialize(init_data)

    try:
        proxy_str = f'Meta:tcp -h {ICE_HOST} -p {ICE_PORT}'
        base = ice.stringToProxy(proxy_str)
        meta = Murmur.MetaPrx.checkedCast(base)
        if not meta:
            print('ERROR: Could not connect to Murmur Ice interface')
            sys.exit(1)

        servers = meta.getBootedServers()
        if not servers:
            print('ERROR: No booted Mumble servers found')
            sys.exit(1)

        server = servers[0]
        print(f'Connected to Mumble server id={server.id()}')

        # Get current ACLs for Root channel
        acls, groups, inherit = server.getACL(ROOT_CHANNEL_ID)

        # ---- Ensure 'discord' group exists ----
        discord_grp = None
        for g in groups:
            if g.name == 'discord':
                discord_grp = g
                break

        if not discord_grp:
            discord_grp = Murmur.Group()
            discord_grp.name = 'discord'
            discord_grp.inherited = False
            discord_grp.inherit = True
            discord_grp.inheritable = True
            discord_grp.add = []
            discord_grp.remove = []
            discord_grp.members = []
            groups.append(discord_grp)
            print('Created "discord" group on Root channel')
        else:
            print('"discord" group already exists')

        # ---- Build ACL entries ----
        # We keep existing ACLs but ensure our entries are present.
        # Strategy: remove any old 'discord' ACLs we created, then append fresh ones.

        new_acls = []
        for a in acls:
            # Keep ACLs that are NOT for the 'discord' group (preserve admin / @all defaults)
            if a.group != 'discord':
                new_acls.append(a)

        # 1. Discord group: full radio-user permissions on Root (inherited to all sub-channels)
        discord_allow = (
            PERM_TRAVERSE |
            PERM_ENTER |
            PERM_SPEAK |
            PERM_WHISPER |
            PERM_TEXT_MESSAGE |
            PERM_MAKE_TEMP |
            PERM_LISTEN |
            PERM_SELF_MUTE |
            PERM_SELF_DEAFEN
        )

        discord_acl = Murmur.ACL()
        discord_acl.applyHere = True
        discord_acl.applySubs = True
        discord_acl.inherited = False
        discord_acl.userid = -1       # -1 = group-based ACL
        discord_acl.group = 'discord'
        discord_acl.allow = discord_allow
        discord_acl.deny = PERM_NONE
        new_acls.append(discord_acl)

        # 2. Deny @all most permissions so unauthenticated users can't do much
        #    but keep Traverse so they can at least connect and be rejected by auth
        #    (Remove any existing @all deny we added before to avoid duplicates)
        final_acls = []
        has_all_deny = False
        for a in new_acls:
            if a.group == 'all' and not a.inherited and a.deny != PERM_NONE:
                has_all_deny = True
            final_acls.append(a)

        if not has_all_deny:
            all_deny_acl = Murmur.ACL()
            all_deny_acl.applyHere = True
            all_deny_acl.applySubs = True
            all_deny_acl.inherited = False
            all_deny_acl.userid = -1
            all_deny_acl.group = 'all'
            all_deny_acl.allow = PERM_TRAVERSE  # need traverse to connect at all
            all_deny_acl.deny = (
                PERM_SPEAK |
                PERM_MAKE_CHANNEL |
                PERM_MAKE_TEMP |
                PERM_MUTE_DEAFEN |
                PERM_MOVE |
                PERM_LINK_CHANNEL
            )
            # Insert @all deny BEFORE the discord allow so discord overrides it
            final_acls.insert(len(final_acls) - 1, all_deny_acl)

        # Apply
        server.setACL(ROOT_CHANNEL_ID, final_acls, groups, inherit)
        print(f'ACLs applied: {len(final_acls)} rules, discord group has full radio-user permissions')
        print('Done.')

    except Exception as e:
        print(f'ERROR: {e}')
        import traceback
        traceback.print_exc()
        sys.exit(1)
    finally:
        ice.destroy()


if __name__ == '__main__':
    main()
ACLEOF
)"

chown "$ADMIN_USER:$ADMIN_USER" "$MUMBLE_AUTH_DIR/setup-acl.py" || true

# Run ACL setup (Mumble server must be running + Ice accessible)
# Give mumble-server a moment to start Ice listener
sleep 2
if "$MUMBLE_AUTH_VENV/bin/python3" "$MUMBLE_AUTH_DIR/setup-acl.py" 2>&1; then
  log_ok "Mumble ACLs für 'discord'-Gruppe gesetzt"
else
  log_warn "ACL-Setup fehlgeschlagen (kann später manuell ausgeführt werden: $MUMBLE_AUTH_VENV/bin/python3 $MUMBLE_AUTH_DIR/setup-acl.py)"
fi

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
const { createMumbleChannelManager } = require('./src/mumble');

function mustEnv(name) {
  const v = process.env[name];
  if (!v) throw new Error(`Missing env var: ${name}`);
  return v;
}

(async () => {
  const bindHost = process.env.BIND_HOST || '127.0.0.1';
  const bindPort = Number(process.env.BIND_PORT || '3000');

  // Mumble Ice API settings (from mumble-server.ini: ice="tcp -h 127.0.0.1 -p 6502")
  const mumbleIceHost = process.env.MUMBLE_ICE_HOST || '127.0.0.1';
  const mumbleIcePort = Number(process.env.MUMBLE_ICE_PORT || '6502');
  // Cleanup unused Mumble channels after this many hours (default: 24 = once a day)
  const mumbleCleanupHours = Number(process.env.MUMBLE_CLEANUP_HOURS || '24');

  const dbPath = mustEnv('DB_PATH');
  const mapPath = mustEnv('CHANNEL_MAP_PATH');

  const db = initDb(dbPath);
  const mapping = createMappingStore(mapPath);
  const stateStore = createStateStore(db);
  const txStore = createTxStore(db);
  const usersStore = createUsersStore(db);

  // Mumble channel manager
  const mumbleManager = createMumbleChannelManager({
    db,
    iceHost: mumbleIceHost,
    icePort: mumbleIcePort,
    cleanupIntervalHours: mumbleCleanupHours,
  });
  mumbleManager.startCleanupScheduler();

  const httpServer = createHttpServer({
    db,
    mapping,
    stateStore,
    txStore,
    usersStore,
    mumbleManager,
    adminToken: process.env.ADMIN_TOKEN || '',
    allowedGuildIds: process.env.DISCORD_GUILD_ID
      ? process.env.DISCORD_GUILD_ID.split(',')
      : [],
  });

  const wsHub = createWsHub(httpServer, { stateStore });

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
    mumbleManager,
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

    -- Mumble channel tracking
    CREATE TABLE IF NOT EXISTS mumble_channels (
      freq_id         INTEGER PRIMARY KEY,
      channel_name    TEXT,
      is_default      INTEGER NOT NULL DEFAULT 0,
      created_at_ms   INTEGER NOT NULL,
      last_used_at_ms INTEGER NOT NULL
    );

    CREATE INDEX IF NOT EXISTS idx_mumble_channels_last_used
      ON mumble_channels(last_used_at_ms);
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

# src/mumble.js - Mumble channel management via Ice API
write_file_backup "$SRC_DIR/mumble.js" "$(cat <<'EOF'
'use strict';

const net = require('net');

/**
 * Mumble Channel Manager
 * Manages Mumble channels via Ice API (Murmur Ice interface)
 * 
 * Mumble uses Ice for RPC - we send simple commands over TCP.
 * The Ice endpoint is configured in mumble-server.ini as:
 *   ice="tcp -h 127.0.0.1 -p 6502"
 */
function createMumbleChannelManager({ db, iceHost = '127.0.0.1', icePort = 6502, cleanupIntervalHours = 24 }) {
  // Prepared statements for channel tracking
  const upsertChannelStmt = db.prepare(`
    INSERT INTO mumble_channels (freq_id, channel_name, is_default, created_at_ms, last_used_at_ms)
    VALUES (@freq_id, @channel_name, @is_default, @created_at_ms, @last_used_at_ms)
    ON CONFLICT(freq_id) DO UPDATE SET
      channel_name = excluded.channel_name,
      last_used_at_ms = excluded.last_used_at_ms
  `);

  const getChannelStmt = db.prepare(`
    SELECT freq_id, channel_name, is_default, created_at_ms, last_used_at_ms
    FROM mumble_channels
    WHERE freq_id = ?
  `);

  const listChannelsStmt = db.prepare(`
    SELECT freq_id, channel_name, is_default, created_at_ms, last_used_at_ms
    FROM mumble_channels
    ORDER BY freq_id
  `);

  const getUnusedChannelsStmt = db.prepare(`
    SELECT freq_id, channel_name, is_default, created_at_ms, last_used_at_ms
    FROM mumble_channels
    WHERE is_default = 0 AND last_used_at_ms < ?
  `);

  const deleteChannelStmt = db.prepare(`
    DELETE FROM mumble_channels
    WHERE freq_id = ?
  `);

  // Track which channels exist in Mumble (synced on startup)
  const knownChannels = new Set();

  // Note: Full Ice/Slice implementation is complex. 
  // For production, consider using murmur-rest or grumble-rest REST API wrapper.
  // This is a simplified approach that tracks channels in DB and logs actions.

  /**
   * Register a default frequency channel (from Discord channel name)
   */
  function registerDefaultChannel(freqId, channelName) {
    const now = Date.now();
    upsertChannelStmt.run({
      freq_id: freqId,
      channel_name: channelName || `Freq-${freqId}`,
      is_default: 1,
      created_at_ms: now,
      last_used_at_ms: now,
    });
    knownChannels.add(freqId);
    console.log(`[mumble] Registered default channel: Freq-${freqId}`);
  }

  /**
   * Ensure a channel exists for a frequency (create on-demand if needed)
   */
  function ensureChannel(freqId) {
    const existing = getChannelStmt.get(freqId);
    const now = Date.now();

    if (existing) {
      // Update last used time
      upsertChannelStmt.run({
        freq_id: freqId,
        channel_name: existing.channel_name,
        is_default: existing.is_default,
        created_at_ms: existing.created_at_ms,
        last_used_at_ms: now,
      });
      return { created: false, channel: existing };
    }

    // Create new on-demand channel
    const channelName = `Freq-${freqId}`;
    upsertChannelStmt.run({
      freq_id: freqId,
      channel_name: channelName,
      is_default: 0,
      created_at_ms: now,
      last_used_at_ms: now,
    });
    knownChannels.add(freqId);

    console.log(`[mumble] Created on-demand channel: ${channelName}`);

    // TODO: Actually create channel in Mumble via Ice
    // For now, channels should be created manually or via murmur-rest
    // sendIceCommand('addChannel', { parent: 0, name: channelName });

    return { created: true, channel: { freq_id: freqId, channel_name: channelName } };
  }

  /**
   * Mark a channel as used (updates last_used_at_ms)
   */
  function touchChannel(freqId) {
    const existing = getChannelStmt.get(freqId);
    if (existing) {
      upsertChannelStmt.run({
        ...existing,
        last_used_at_ms: Date.now(),
      });
    }
  }

  /**
   * List all tracked channels
   */
  function listChannels() {
    return listChannelsStmt.all();
  }

  /**
   * Cleanup unused non-default channels
   */
  function cleanupUnusedChannels(maxAgeMs = cleanupIntervalHours * 60 * 60 * 1000) {
    const cutoff = Date.now() - maxAgeMs;
    const unused = getUnusedChannelsStmt.all(cutoff);

    for (const ch of unused) {
      console.log(`[mumble] Deleting unused channel: ${ch.channel_name} (last used: ${new Date(ch.last_used_at_ms).toISOString()})`);
      deleteChannelStmt.run(ch.freq_id);
      knownChannels.delete(ch.freq_id);

      // TODO: Actually delete channel in Mumble via Ice
      // sendIceCommand('removeChannel', { id: lookupChannelId(ch.freq_id) });
    }

    return unused.length;
  }

  /**
   * Initialize default channels from mapping
   */
  function initDefaultChannels(defaultFrequencies) {
    for (const freqId of defaultFrequencies) {
      registerDefaultChannel(freqId, null);
    }
    console.log(`[mumble] Initialized ${defaultFrequencies.length} default channels`);
  }

  /**
   * Start cleanup scheduler
   */
  let cleanupTimer = null;
  function startCleanupScheduler() {
    // Run cleanup once per hour, but only delete channels older than cleanupIntervalHours
    cleanupTimer = setInterval(() => {
      const deleted = cleanupUnusedChannels();
      if (deleted > 0) {
        console.log(`[mumble] Cleanup: deleted ${deleted} unused channels`);
      }
    }, 60 * 60 * 1000); // Check every hour
  }

  function stopCleanupScheduler() {
    if (cleanupTimer) {
      clearInterval(cleanupTimer);
      cleanupTimer = null;
    }
  }

  return {
    registerDefaultChannel,
    ensureChannel,
    touchChannel,
    listChannels,
    cleanupUnusedChannels,
    initDefaultChannels,
    startCleanupScheduler,
    stopCleanupScheduler,
    isKnown: (freqId) => knownChannels.has(freqId),
  };
}

module.exports = { createMumbleChannelManager };
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

function createDiscordBot({ token, guildId, mapping, stateStore, usersStore, mumbleManager, onStateChange }) {
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

    // Initialize default Mumble channels
    if (mumbleManager && defaultFreqs.length > 0) {
      mumbleManager.initDefaultChannels(defaultFreqs);
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
      const freqId = mapping.parseFreqIdFromName(newChannel.name);
      if (freqId && mumbleManager) {
        mumbleManager.registerDefaultChannel(freqId, newChannel.name);
      }
    }
  });

  client.on('channelCreate', (channel) => {
    if (channel.type === ChannelType.GuildVoice) {
      mapping.registerChannel(channel.id, channel.name);
      const freqId = mapping.parseFreqIdFromName(channel.name);
      if (freqId && mumbleManager) {
        mumbleManager.registerDefaultChannel(freqId, channel.name);
      }
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

      // Ensure Mumble channel exists when someone joins a frequency
      if (freqId && mumbleManager) {
        mumbleManager.ensureChannel(freqId);
      }

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

function createHttpServer({ db, mapping, stateStore, txStore, usersStore, mumbleManager, adminToken, allowedGuildIds }) {
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

  // Mumble channels: list all tracked channels
  app.get('/mumble/channels', (req, res) => {
    if (!mumbleManager) {
      return res.json({ ok: true, data: [], warning: 'mumbleManager not available' });
    }
    const channels = mumbleManager.listChannels();
    res.json({ ok: true, data: channels });
  });

  // Mumble channels: ensure channel exists (create on-demand)
  app.post('/mumble/channels/:freqId', (req, res) => {
    const token = req.header('x-admin-token') || '';
    if (!adminToken || token !== adminToken) {
      return res.status(403).json({ ok: false, error: 'forbidden' });
    }
    if (!mumbleManager) {
      return res.status(500).json({ ok: false, error: 'mumbleManager not available' });
    }
    const freqId = Number(req.params.freqId);
    if (!Number.isInteger(freqId) || freqId < 1000 || freqId > 9999) {
      return res.status(400).json({ ok: false, error: 'bad freqId (must be 1000-9999)' });
    }
    const result = mumbleManager.ensureChannel(freqId);
    res.json({ ok: true, created: result.created, data: result.channel });
  });

  // Mumble channels: trigger cleanup manually
  app.post('/mumble/cleanup', (req, res) => {
    const token = req.header('x-admin-token') || '';
    if (!adminToken || token !== adminToken) {
      return res.status(403).json({ ok: false, error: 'forbidden' });
    }
    if (!mumbleManager) {
      return res.status(500).json({ ok: false, error: 'mumbleManager not available' });
    }
    const maxAgeHours = Number(req.query.maxAgeHours || 24);
    const deleted = mumbleManager.cleanupUnusedChannels(maxAgeHours * 60 * 60 * 1000);
    res.json({ ok: true, deletedCount: deleted });
  });

  // -------------------------------------------------------
  // Mumble authenticator endpoint
  // Called by the Python Ice authenticator script.
  // username = discord_user_id, password = guild_id
  // -------------------------------------------------------
  app.post('/mumble/auth', (req, res) => {
    const { username, password } = req.body || {};
    if (!username || !password) {
      return res.json({ ok: false, reason: 'missing credentials' });
    }

    const discordUserId = String(username).trim();
    const guildId = String(password).trim();

    // 1. Check if the guild_id is in the allowed list
    if (allowedGuildIds && allowedGuildIds.length > 0) {
      if (!allowedGuildIds.includes(guildId)) {
        console.log(`[mumble/auth] DENIED ${discordUserId} - guild ${guildId} not allowed`);
        return res.json({ ok: false, reason: 'guild not allowed' });
      }
    }

    // 2. Check if the Discord bot has seen this user in that guild
    const user = usersStore ? usersStore.get(discordUserId, guildId) : null;
    if (!user) {
      console.log(`[mumble/auth] DENIED ${discordUserId} - not found in guild ${guildId}`);
      return res.json({ ok: false, reason: 'user not found in guild' });
    }

    // 3. Authenticated! Return user info + groups for ACL
    console.log(`[mumble/auth] OK ${discordUserId} (${user.display_name}) guild=${guildId}`);
    return res.json({
      ok: true,
      userId: discordUserId,
      displayName: user.display_name || discordUserId,
      groups: ['discord', `guild-${guildId}`],
    });
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

# Mumble authenticator backend URL (used by mumble-auth.py)
MUMBLE_AUTH_BACKEND=http://127.0.0.1:3000

BIND_HOST=$BIND_HOST
BIND_PORT=$BIND_PORT

DB_PATH=$BACKEND_DIR/state.sqlite
CHANNEL_MAP_PATH=$CHANNEL_MAP

ADMIN_TOKEN=$ADMIN_TOKEN

# Mumble Ice API (for channel management)
# MUMBLE_ICE_HOST=127.0.0.1
# MUMBLE_ICE_PORT=6502

# Cleanup unused Mumble channels after N hours (default: 24)
# MUMBLE_CLEANUP_HOURS=24
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
  "$SRC_DIR/mumble.js"
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
  echo -e "${CYAN}2) Mumble SuperUser Passwort setzen/ändern${NC}"
  echo -e "${CYAN}3) Backend Healthcheck testen${NC}"
  echo -e "${CYAN}4) Backend Testlog anzeigen (tail)${NC}"
  echo -e "${CYAN}5) Backend Live-Logs verfolgen (journalctl -f)${NC}"
  echo -e "${CYAN}6) TX Event senden (start/stop)${NC}"
  echo -e "${CYAN}7) TX Recent anzeigen${NC}"
  echo -e "${CYAN}8) Users Recent anzeigen${NC}"
  echo -e "${CYAN}9) Mumble ACLs neu setzen (discord-Gruppe)${NC}"
  echo -e "${CYAN}10) Mumble Authenticator Logs (journalctl -f)${NC}"
  echo -e "${CYAN}0) Beenden${NC}"
  echo ""

  read -r -p "$(echo -e "${CYAN}Auswahl [0-10]: ${NC}")" CHOICE

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
    7)
      log_info "TX Recent: http://127.0.0.1:3000/tx/recent?limit=10"
      curl -sS "http://127.0.0.1:3000/tx/recent?limit=10" || true
      echo ""
      ;;
    8)
      log_info "Users Recent: http://127.0.0.1:3000/users/recent?limit=10"
      curl -sS "http://127.0.0.1:3000/users/recent?limit=10" || true
      echo ""
      ;;
    9)
      log_info "Setze Mumble ACLs für 'discord'-Gruppe..."
      if "$MUMBLE_AUTH_VENV/bin/python3" "$MUMBLE_AUTH_DIR/setup-acl.py" 2>&1; then
        log_ok "ACLs erfolgreich gesetzt"
      else
        log_error "ACL-Setup fehlgeschlagen"
      fi
      ;;
    10)
      log_info "Mumble Authenticator Logs: journalctl -u das-krt-mumble-auth -f"
      journalctl -u das-krt-mumble-auth -f
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