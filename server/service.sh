#!/usr/bin/env bash
##version alpha-0.0.10
## Service management & tools for das-krt Backend
## Usage: bash service.sh [start|stop|restart|status|logs|menu]

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

VERSION="Alpha 0.0.10"
SERVICE_NAME="das-krt-backend"

# --------------------------------------------------
# JSON Escape Helper (prevents injection via user input)
# --------------------------------------------------
json_escape() {
  local str="$1"
  # Escape backslashes, double quotes, and control characters (RFC 8259)
  str="${str//\\/\\\\}"
  str="${str//\"/\\\"}"
  str="${str//$'\n'/\\n}"
  str="${str//$'\r'/\\r}"
  str="${str//$'\t'/\\t}"
  str="${str//$'\b'/\\b}"
  str="${str//$'\f'/\\f}"
  # Remove null bytes and other ASCII control characters (0x00-0x1F except already handled)
  str="$(printf '%s' "$str" | tr -d '\000-\007\013\016-\037')"
  printf '%s' "$str"
}

# --------------------------------------------------
# Variablen
# --------------------------------------------------
APP_ROOT="/opt/das-krt"
BACKEND_DIR="$APP_ROOT/backend"
ENV_FILE="$BACKEND_DIR/.env"
CHANNEL_MAP="$APP_ROOT/config/channels.json"
TEST_LOG="$APP_ROOT/logs/backend-test.log"
TRAEFIK_SERVICE="traefik"
TRAEFIK_DIR="$APP_ROOT/traefik"

# --------------------------------------------------
# Service Commands
# --------------------------------------------------
do_start() {
  log_info "Starte $SERVICE_NAME ..."
  systemctl start "$SERVICE_NAME"
  sleep 1
  if systemctl is-active --quiet "$SERVICE_NAME"; then
    log_ok "$SERVICE_NAME gestartet"
  else
    log_error "$SERVICE_NAME konnte nicht gestartet werden"
    systemctl status "$SERVICE_NAME" --no-pager || true
    return 1
  fi
}

do_stop() {
  log_info "Stoppe $SERVICE_NAME ..."
  systemctl stop "$SERVICE_NAME"
  log_ok "$SERVICE_NAME gestoppt"
}

do_restart() {
  log_info "Restarte $SERVICE_NAME ..."
  systemctl restart "$SERVICE_NAME"
  sleep 1
  if systemctl is-active --quiet "$SERVICE_NAME"; then
    log_ok "$SERVICE_NAME neu gestartet"
  else
    log_error "$SERVICE_NAME konnte nicht gestartet werden"
    systemctl status "$SERVICE_NAME" --no-pager || true
    return 1
  fi
}

do_status() {
  echo ""
  systemctl status "$SERVICE_NAME" --no-pager || true
  echo ""

  # Healthcheck
  if curl -sf "http://127.0.0.1:3000/health" > /dev/null 2>&1; then
    log_ok "Healthcheck: OK"
  else
    log_warn "Healthcheck: FAILED (Backend nicht erreichbar auf Port 3000)"
  fi
}

do_logs() {
  log_info "Live Logs: journalctl -u $SERVICE_NAME -f"
  journalctl -u "$SERVICE_NAME" -f
}

# --------------------------------------------------
# Helper: Get admin token from .env
# --------------------------------------------------
get_admin_token() {
  grep -E '^ADMIN_TOKEN=' "$ENV_FILE" 2>/dev/null | cut -d= -f2- || true
}

# Helper: curl wrapper that hides admin token from process list.
# Usage: admin_curl [curl_args...]
# The admin token is passed via a temp config file (chmod 600) instead of -H on the CLI,
# so it does not appear in 'ps aux' output.
admin_curl() {
  local _ac_token
  _ac_token="$(get_admin_token)"
  if [ -z "${_ac_token:-}" ]; then
    echo ""
    return 1
  fi
  local _ac_cfg
  _ac_cfg="$(mktemp)"
  chmod 600 "$_ac_cfg"
  printf -- '-H "x-admin-token: %s"\n' "$_ac_token" > "$_ac_cfg"
  curl --config "$_ac_cfg" "$@"
  local _rc=$?
  rm -f "$_ac_cfg"
  return $_rc
}

# --------------------------------------------------
# DSGVO Compliance Functions
# --------------------------------------------------
show_dsgvo_warnings() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    return
  fi

  local RESPONSE
  RESPONSE="$(admin_curl -sS "http://127.0.0.1:3000/admin/dsgvo/status" 2>/dev/null || true)"
  if [ -z "$RESPONSE" ]; then
    return
  fi

  # Parse warnings from JSON (simple grep approach)
  local ENABLED
  ENABLED="$(echo "$RESPONSE" | grep -o '"dsgvoEnabled":[a-z]*' | cut -d: -f2 || true)"
  local DEBUG_ON
  DEBUG_ON="$(echo "$RESPONSE" | grep -o '"debugMode":[a-z]*' | cut -d: -f2 || true)"
  local DEBUG_TOOL
  DEBUG_TOOL="$(echo "$RESPONSE" | grep -o '"debugToolActive":[a-z]*' | cut -d: -f2 || true)"

  if [ "$ENABLED" = "false" ]; then
    log_warn "DSGVO Compliance Mode ist DEAKTIVIERT — Daten werden NICHT automatisch gelöscht"
  fi
  if [ "$DEBUG_TOOL" = "true" ]; then
    log_warn "Ein Debug-Tool ist aktiv — automatisches Cleanup ist pausiert"
  fi
}

do_dsgvo_status() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  log_info "DSGVO Status:"
  local RESPONSE
  RESPONSE="$(admin_curl -sS "http://127.0.0.1:3000/admin/dsgvo/status" 2>/dev/null || true)"
  if [ -z "$RESPONSE" ]; then
    log_error "Backend nicht erreichbar"
    return
  fi

  echo ""
  # Pretty-print key fields
  local ENABLED DEBUG_ON DEBUG_TOOL RETENTION LAST_CLEANUP
  ENABLED="$(echo "$RESPONSE" | grep -o '"dsgvoEnabled":[a-z]*' | cut -d: -f2)"
  DEBUG_ON="$(echo "$RESPONSE" | grep -o '"debugMode":[a-z]*' | cut -d: -f2)"
  DEBUG_TOOL="$(echo "$RESPONSE" | grep -o '"debugToolActive":[a-z]*' | cut -d: -f2)"
  RETENTION="$(echo "$RESPONSE" | grep -o '"retentionDays":[0-9]*' | cut -d: -f2)"
  LAST_CLEANUP="$(echo "$RESPONSE" | grep -o '"lastCleanup":"[^"]*"' | cut -d'"' -f4)"

  [ "$ENABLED" = "true" ] && log_ok "DSGVO Compliance: AKTIVIERT" || log_warn "DSGVO Compliance: DEAKTIVIERT"
  [ "$DEBUG_ON" = "true" ] && log_warn "Debug-Modus: AKTIV" || log_ok "Debug-Modus: INAKTIV"
  [ "$DEBUG_TOOL" = "true" ] && log_warn "Debug-Tool aktiv: JA" || log_ok "Debug-Tool aktiv: NEIN"
  log_info "Aufbewahrungsfrist: ${RETENTION:-?} Tage"
  log_info "Letztes Cleanup: ${LAST_CLEANUP:-noch nie}"
  echo ""
}

do_dsgvo_toggle() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  read -r -p "$(echo -e "${CYAN}DSGVO Compliance Modus aktivieren? (j/n): ${NC}")" TOGGLE
  local ENABLED_VAL="false"
  [[ "$TOGGLE" =~ ^[jJ]$ ]] && ENABLED_VAL="true"

  local RESPONSE
  RESPONSE="$(admin_curl -sS -X POST "http://127.0.0.1:3000/admin/dsgvo/toggle" \
    -H "content-type: application/json" \
    -d "{\"enabled\":${ENABLED_VAL}}" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    [ "$ENABLED_VAL" = "true" ] && log_ok "DSGVO Compliance Modus AKTIVIERT" || log_warn "DSGVO Compliance Modus DEAKTIVIERT"
  else
    log_error "Fehler beim Umschalten: $RESPONSE"
  fi
}

do_dsgvo_debug_toggle() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  read -r -p "$(echo -e "${CYAN}Debug-Modus aktivieren? (j/n): ${NC}")" TOGGLE
  local ENABLED_VAL="false"
  [[ "$TOGGLE" =~ ^[jJ]$ ]] && ENABLED_VAL="true"

  local RESPONSE
  RESPONSE="$(admin_curl -sS -X POST "http://127.0.0.1:3000/admin/dsgvo/debug" \
    -H "content-type: application/json" \
    -d "{\"enabled\":${ENABLED_VAL}}" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    if [ "$ENABLED_VAL" = "true" ]; then
      log_warn "Debug-Modus AKTIVIERT → DSGVO Compliance wurde automatisch DEAKTIVIERT, Aufbewahrung: 7 Tage"
    else
      log_ok "Debug-Modus DEAKTIVIERT"
      log_info "DSGVO Compliance wird automatisch wieder aktiviert, sofern kein Debug-Logging (>= debugLOG) aktiv ist."
    fi
  else
    log_error "Fehler beim Umschalten: $RESPONSE"
  fi
}

do_dsgvo_delete_user() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  read -r -p "$(echo -e "${CYAN}Discord User ID zum Löschen: ${NC}")" USER_ID
  if [ -z "$USER_ID" ]; then
    log_warn "Keine User ID eingegeben"
    return
  fi

  read -r -p "$(echo -e "${RED}WARNUNG: Alle Daten für User $USER_ID werden unwiderruflich gelöscht! Fortfahren? (j/n): ${NC}")" CONFIRM
  if [[ ! "$CONFIRM" =~ ^[jJ]$ ]]; then
    log_info "Abgebrochen"
    return
  fi

  local RESPONSE
  RESPONSE="$(admin_curl -sS -X POST "http://127.0.0.1:3000/admin/dsgvo/delete-user" \
    -H "content-type: application/json" \
    -d "{\"discordUserId\":\"$(json_escape "$USER_ID")\"}" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    local TOTAL
    TOTAL="$(echo "$RESPONSE" | grep -o '"totalRows":[0-9]*' | cut -d: -f2)"
    log_ok "Alle Daten für User $USER_ID gelöscht ($TOTAL Einträge)"
  else
    log_error "Fehler: $RESPONSE"
  fi
}

do_dsgvo_delete_guild() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  read -r -p "$(echo -e "${CYAN}Guild ID zum Löschen: ${NC}")" GUILD_ID
  if [ -z "$GUILD_ID" ]; then
    log_warn "Keine Guild ID eingegeben"
    return
  fi

  read -r -p "$(echo -e "${RED}WARNUNG: Alle Daten für Guild $GUILD_ID werden unwiderruflich gelöscht! Fortfahren? (j/n): ${NC}")" CONFIRM
  if [[ ! "$CONFIRM" =~ ^[jJ]$ ]]; then
    log_info "Abgebrochen"
    return
  fi

  local RESPONSE
  RESPONSE="$(admin_curl -sS -X POST "http://127.0.0.1:3000/admin/dsgvo/delete-guild" \
    -H "content-type: application/json" \
    -d "{\"guildId\":\"$(json_escape "$GUILD_ID")\"}" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    local TOTAL
    TOTAL="$(echo "$RESPONSE" | grep -o '"totalRows":[0-9]*' | cut -d: -f2)"
    log_ok "Alle Daten für Guild $GUILD_ID gelöscht ($TOTAL Einträge)"
  else
    log_error "Fehler: $RESPONSE"
  fi
}

do_dsgvo_cleanup() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  read -r -p "$(echo -e "${CYAN}DSGVO Cleanup jetzt manuell ausführen? (j/n): ${NC}")" CONFIRM
  if [[ ! "$CONFIRM" =~ ^[jJ]$ ]]; then
    log_info "Abgebrochen"
    return
  fi

  local RESPONSE
  RESPONSE="$(admin_curl -sS -X POST "http://127.0.0.1:3000/admin/dsgvo/cleanup" \
    -H "content-type: application/json" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    local TOTAL RETENTION
    TOTAL="$(echo "$RESPONSE" | grep -o '"totalRows":[0-9]*' | cut -d: -f2)"
    RETENTION="$(echo "$RESPONSE" | grep -o '"retentionDays":[0-9]*' | cut -d: -f2)"
    log_ok "Cleanup abgeschlossen: $TOTAL Einträge gelöscht (älter als $RETENTION Tage)"
  else
    log_error "Fehler: $RESPONSE"
  fi
}

# --------------------------------------------------
# Kanal-Sync Tools
# --------------------------------------------------
do_channel_sync_status() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  log_info "Kanal-Sync Status:"
  local RESPONSE
  RESPONSE="$(admin_curl -sS "http://127.0.0.1:3000/admin/channel-sync/status" 2>/dev/null || true)"
  if [ -z "$RESPONSE" ]; then
    log_error "Backend nicht erreichbar"
    return
  fi

  echo ""
  local INTERVAL LAST_SYNC SCHEDULER
  INTERVAL="$(echo "$RESPONSE" | grep -o '"intervalHours":[0-9]*' | cut -d: -f2)"
  LAST_SYNC="$(echo "$RESPONSE" | grep -o '"lastSync":"[^"]*"' | cut -d'"' -f4)"
  SCHEDULER="$(echo "$RESPONSE" | grep -o '"schedulerRunning":[a-z]*' | cut -d: -f2)"

  log_info "Sync-Intervall: alle ${INTERVAL:-?} Stunden"
  [ "$SCHEDULER" = "true" ] && log_ok "Scheduler: AKTIV" || log_warn "Scheduler: INAKTIV"
  log_info "Letzter Sync: ${LAST_SYNC:-noch nie}"

  # Show frequency names
  echo ""
  log_info "Frequenz → Kanal-Name Zuordnungen:"
  echo "$RESPONSE" | grep -o '"freqNames":{[^}]*}' | sed 's/"freqNames":{//;s/}$//' | tr ',' '\n' | while IFS=: read -r key val; do
    echo -e "  ${CYAN}${key//\"/}${NC} → ${val//\"/}"
  done
  echo ""
}

do_channel_sync_trigger() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  log_info "Löse Kanal-Sync manuell aus..."
  local RESPONSE
  RESPONSE="$(admin_curl -sS -X POST "http://127.0.0.1:3000/admin/channel-sync/trigger" \
    -H "content-type: application/json" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    log_ok "Kanal-Sync erfolgreich"
    # Show updated freq names
    echo "$RESPONSE" | grep -o '"freqNames":{[^}]*}' | sed 's/"freqNames":{//;s/}$//' | tr ',' '\n' | while IFS=: read -r key val; do
      echo -e "  ${CYAN}${key//\"/}${NC} → ${val//\"/}"
    done
  else
    log_error "Kanal-Sync fehlgeschlagen: $RESPONSE"
  fi
}

do_channel_sync_interval() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  read -r -p "$(echo -e "${CYAN}Neues Sync-Intervall in Stunden (min 1) [24]: ${NC}")" HOURS
  HOURS="${HOURS:-24}"

  # Validate numeric input
  if ! [[ "$HOURS" =~ ^[0-9]+$ ]] || [ "$HOURS" -lt 1 ]; then
    log_error "Ungültige Eingabe: '$HOURS' (muss eine Zahl >= 1 sein)"
    return
  fi

  local RESPONSE
  RESPONSE="$(admin_curl -sS -X POST "http://127.0.0.1:3000/admin/channel-sync/interval" \
    -H "content-type: application/json" \
    -d "{\"hours\":${HOURS}}" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    log_ok "Sync-Intervall auf ${HOURS} Stunden gesetzt"
  else
    log_error "Fehler: $RESPONSE"
  fi
}

# --------------------------------------------------
# Ban Management
# --------------------------------------------------
do_ban_user() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  read -r -p "$(echo -e "${CYAN}Discord User ID zum Bannen: ${NC}")" USER_ID
  if [ -z "$USER_ID" ]; then
    log_warn "Keine User ID eingegeben"
    return
  fi

  read -r -p "$(echo -e "${CYAN}Grund (optional): ${NC}")" BAN_REASON

  local RESPONSE
  RESPONSE="$(admin_curl -sS -X POST "http://127.0.0.1:3000/admin/ban" \
    -H "content-type: application/json" \
    -d "{\"discordUserId\":\"$(json_escape "$USER_ID")\",\"reason\":\"$(json_escape "$BAN_REASON")\"}" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    log_ok "User $USER_ID gebannt"
  else
    log_error "Fehler: $RESPONSE"
  fi
}

do_unban_user() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  read -r -p "$(echo -e "${CYAN}Discord User ID zum Entbannen: ${NC}")" USER_ID
  if [ -z "$USER_ID" ]; then
    log_warn "Keine User ID eingegeben"
    return
  fi

  local RESPONSE
  RESPONSE="$(admin_curl -sS -X POST "http://127.0.0.1:3000/admin/unban" \
    -H "content-type: application/json" \
    -d "{\"discordUserId\":\"$(json_escape "$USER_ID")\"}" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    log_ok "User $USER_ID entbannt"
  else
    log_error "Fehler: $RESPONSE"
  fi
}

do_list_bans() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  log_info "Banliste:"
  local RESPONSE
  RESPONSE="$(admin_curl -sS "http://127.0.0.1:3000/admin/bans" 2>/dev/null || true)"
  if [ -z "$RESPONSE" ]; then
    log_error "Backend nicht erreichbar"
    return
  fi

  echo "$RESPONSE" | python3 -m json.tool 2>/dev/null || echo "$RESPONSE"
  echo ""
}

do_delete_and_ban() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  read -r -p "$(echo -e "${CYAN}Discord User ID zum Löschen und Bannen: ${NC}")" USER_ID
  if [ -z "$USER_ID" ]; then
    log_warn "Keine User ID eingegeben"
    return
  fi

  read -r -p "$(echo -e "${RED}WARNUNG: Alle Daten für User $USER_ID werden unwiderruflich gelöscht und der User gebannt! Fortfahren? (j/n): ${NC}")" CONFIRM
  if [[ ! "$CONFIRM" =~ ^[jJ]$ ]]; then
    log_info "Abgebrochen"
    return
  fi

  local RESPONSE
  RESPONSE="$(admin_curl -sS -X POST "http://127.0.0.1:3000/admin/dsgvo/delete-and-ban" \
    -H "content-type: application/json" \
    -d "{\"discordUserId\":\"$(json_escape "$USER_ID")\"}" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    local TOTAL
    TOTAL="$(echo "$RESPONSE" | grep -o '"totalRows":[0-9]*' | cut -d: -f2)"
    log_ok "Alle Daten für User $USER_ID gelöscht ($TOTAL Einträge) und User gebannt"
  else
    log_error "Fehler: $RESPONSE"
  fi
}

# --------------------------------------------------
# Traefik Reverse Proxy Management
# --------------------------------------------------
do_traefik_status() {
  echo ""
  if ! systemctl list-unit-files "$TRAEFIK_SERVICE.service" &>/dev/null 2>&1; then
    log_warn "Traefik ist nicht installiert"
    return
  fi

  systemctl status "$TRAEFIK_SERVICE" --no-pager || true
  echo ""

  # Check if TLS cert exists in acme.json
  if [ -f "$TRAEFIK_DIR/acme.json" ]; then
    local ACME_SIZE
    ACME_SIZE="$(stat -c%s "$TRAEFIK_DIR/acme.json" 2>/dev/null || echo 0)"
    if [ "$ACME_SIZE" -gt 10 ]; then
      # Check if Certificates array actually has entries (not null)
      if python3 -c "
import json,sys
with open('$TRAEFIK_DIR/acme.json') as f:
  d=json.load(f)
for r in d.values():
  certs=r.get('Certificates') if isinstance(r,dict) else None
  if certs: sys.exit(0)
sys.exit(1)" 2>/dev/null; then
        log_ok "TLS Zertifikat: vorhanden in acme.json (${ACME_SIZE} Bytes)"
      else
        log_warn "TLS Zertifikat: acme.json vorhanden aber kein Zertifikat bezogen"
        log_info "Tipp: Nutze Menüpunkt 64 für eine detaillierte Zertifikatsprüfung"
      fi
    else
      log_warn "TLS Zertifikat: noch nicht bezogen (acme.json leer)"
    fi
  else
    log_warn "TLS Zertifikat: acme.json nicht gefunden"
  fi

  local CUR_DOMAIN
  CUR_DOMAIN="$(grep -E '^DOMAIN=' "$ENV_FILE" 2>/dev/null | cut -d= -f2- || true)"
  if [ -n "$CUR_DOMAIN" ]; then
    log_info "Domain: $CUR_DOMAIN"
    log_info "URL:    https://$CUR_DOMAIN"
  else
    log_warn "Keine Domain in .env konfiguriert"
  fi
  echo ""
}

do_traefik_restart() {
  if ! systemctl list-unit-files "$TRAEFIK_SERVICE.service" &>/dev/null 2>&1; then
    log_warn "Traefik ist nicht installiert"
    return
  fi

  log_info "Restarte Traefik ..."
  systemctl restart "$TRAEFIK_SERVICE"
  sleep 1
  if systemctl is-active --quiet "$TRAEFIK_SERVICE"; then
    log_ok "Traefik neu gestartet"
  else
    log_error "Traefik konnte nicht gestartet werden"
    systemctl status "$TRAEFIK_SERVICE" --no-pager || true
  fi
}

do_traefik_logs() {
  log_info "Traefik Live Logs:"
  journalctl -u "$TRAEFIK_SERVICE" -f
}

do_traefik_cert_check() {
  echo ""
  log_info "=== Let's Encrypt Zertifikatsprüfung ==="
  echo ""

  local CUR_DOMAIN
  CUR_DOMAIN="$(grep -E '^DOMAIN=' "$ENV_FILE" 2>/dev/null | cut -d= -f2- || true)"
  if [ -z "$CUR_DOMAIN" ]; then
    log_error "Keine Domain in .env konfiguriert — kann Zertifikat nicht prüfen"
    return
  fi
  log_info "Domain: $CUR_DOMAIN"
  echo ""

  # --- 1. Check acme.json ---
  log_info "--- acme.json Status ---"
  if [ ! -f "$TRAEFIK_DIR/acme.json" ]; then
    log_error "acme.json nicht gefunden unter $TRAEFIK_DIR/acme.json"
  else
    local ACME_PERMS
    ACME_PERMS="$(stat -c%a "$TRAEFIK_DIR/acme.json" 2>/dev/null || echo '?')"
    if [ "$ACME_PERMS" != "600" ]; then
      log_warn "acme.json Berechtigungen: $ACME_PERMS (sollte 600 sein)"
    else
      log_ok "acme.json Berechtigungen: 600"
    fi

    # Parse acme.json for certificate info
    python3 -c "
import json, sys, base64, subprocess, tempfile, os
try:
    with open('$TRAEFIK_DIR/acme.json') as f:
        data = json.load(f)
except Exception as e:
    print(f'  FEHLER: acme.json kann nicht gelesen werden: {e}')
    sys.exit(1)

found = False
for resolver_name, resolver in data.items():
    if not isinstance(resolver, dict):
        continue
    account = resolver.get('Account')
    if account and account.get('Registration'):
        reg = account['Registration']
        print(f'  ACME Account: registriert (URI: {reg.get("uri", "?")}'[:80] + ')')
    certs = resolver.get('Certificates')
    if not certs:
        print(f'  Zertifikate: KEINE (Certificates ist null oder leer)')
        continue
    for cert_entry in certs:
        domain = cert_entry.get('domain', {}).get('main', '?')
        sans = cert_entry.get('domain', {}).get('SANs', [])
        print(f'  Zertifikat für: {domain}')
        if sans:
            print(f'  SANs: {", ".join(sans)}')
        # Decode and inspect certificate
        cert_pem = cert_entry.get('certificate', '')
        if cert_pem:
            try:
                cert_bytes = base64.b64decode(cert_pem)
                with tempfile.NamedTemporaryFile(delete=False, suffix='.pem') as tf:
                    tf.write(cert_bytes)
                    tf_name = tf.name
                result = subprocess.run(
                    ['openssl', 'x509', '-in', tf_name, '-noout',
                     '-issuer', '-subject', '-dates', '-fingerprint'],
                    capture_output=True, text=True, timeout=5
                )
                os.unlink(tf_name)
                if result.returncode == 0:
                    for line in result.stdout.strip().split('\n'):
                        print(f'  {line}')
                found = True
            except Exception as e:
                print(f'  Zertifikat-Dekodierung fehlgeschlagen: {e}')
if not found:
    print('  WARNUNG: Kein gültiges Zertifikat in acme.json gefunden')
" 2>/dev/null || log_warn "python3/openssl für acme.json-Analyse nicht verfügbar"
  fi
  echo ""

  # --- 2. Live TLS check via openssl s_client ---
  log_info "--- Live TLS-Verbindungstest (openssl) ---"
  if ! command -v openssl &>/dev/null; then
    log_warn "openssl ist nicht installiert — Live-Check übersprungen"
    return
  fi

  local CERT_OUTPUT
  CERT_OUTPUT="$(echo | openssl s_client -servername "$CUR_DOMAIN" -connect "$CUR_DOMAIN:443" 2>/dev/null)"
  if [ -z "$CERT_OUTPUT" ]; then
    log_error "Keine TLS-Verbindung zu $CUR_DOMAIN:443 möglich"
    log_info "Ist Traefik gestartet? Läuft Port 443?"
    return
  fi

  # Extract certificate details
  local ISSUER SUBJECT NOT_BEFORE NOT_AFTER SERIAL
  ISSUER="$(echo "$CERT_OUTPUT" | openssl x509 -noout -issuer 2>/dev/null | sed 's/^issuer= *//' || echo '?')"
  SUBJECT="$(echo "$CERT_OUTPUT" | openssl x509 -noout -subject 2>/dev/null | sed 's/^subject= *//' || echo '?')"
  NOT_BEFORE="$(echo "$CERT_OUTPUT" | openssl x509 -noout -startdate 2>/dev/null | cut -d= -f2 || echo '?')"
  NOT_AFTER="$(echo "$CERT_OUTPUT" | openssl x509 -noout -enddate 2>/dev/null | cut -d= -f2 || echo '?')"

  echo "  Subject:    $SUBJECT"
  echo "  Issuer:     $ISSUER"
  echo "  Gültig ab:  $NOT_BEFORE"
  echo "  Gültig bis: $NOT_AFTER"

  # Check if self-signed (Traefik default cert)
  if echo "$ISSUER" | grep -qi 'TRAEFIK DEFAULT CERT'; then
    echo ""
    log_error "SELBST-SIGNIERTES ZERTIFIKAT (Traefik Default Cert)!"
    log_warn "Let's Encrypt Zertifikat wurde NICHT bezogen."
    echo ""
    log_info "Mögliche Ursachen:"
    log_info "  1. DNS für $CUR_DOMAIN zeigt nicht auf diesen Server"
    log_info "  2. Port 443 ist durch Firewall blockiert"
    log_info "  3. ACME Challenge schlägt fehl (prüfe Traefik Logs: Menüpunkt 62)"
    log_info "  4. acme.json Berechtigungen falsch (muss 600 sein)"
    echo ""
    log_info "Sofortmaßnahme: acme.json zurücksetzen und Traefik neustarten:"
    log_info "  echo '{}' > $TRAEFIK_DIR/acme.json"
    log_info "  chmod 600 $TRAEFIK_DIR/acme.json"
    log_info "  systemctl restart traefik"
  elif echo "$ISSUER" | grep -qi "Let's Encrypt\|R[0-9]\|E[0-9]\|ISRG"; then
    log_ok "Zertifikat: Let's Encrypt"

    # Calculate days remaining
    local EXPIRY_EPOCH NOW_EPOCH DAYS_LEFT
    EXPIRY_EPOCH="$(echo "$CERT_OUTPUT" | openssl x509 -noout -enddate 2>/dev/null | cut -d= -f2 | xargs -I{} date -d '{}' +%s 2>/dev/null || echo 0)"
    NOW_EPOCH="$(date +%s)"
    if [ "$EXPIRY_EPOCH" -gt 0 ] 2>/dev/null; then
      DAYS_LEFT=$(( (EXPIRY_EPOCH - NOW_EPOCH) / 86400 ))
      if [ "$DAYS_LEFT" -lt 0 ]; then
        log_error "Zertifikat ist ABGELAUFEN seit $(( -DAYS_LEFT )) Tagen!"
      elif [ "$DAYS_LEFT" -lt 7 ]; then
        log_warn "Zertifikat läuft in $DAYS_LEFT Tagen ab! (Erneuerung prüfen)"
      elif [ "$DAYS_LEFT" -lt 30 ]; then
        log_warn "Zertifikat läuft in $DAYS_LEFT Tagen ab (Erneuerung sollte automatisch erfolgen)"
      else
        log_ok "Zertifikat gültig für $DAYS_LEFT Tage"
      fi
    fi
  else
    log_info "Zertifikats-Aussteller: $ISSUER"
    log_warn "Kein Let's Encrypt Zertifikat — prüfe ob das gewollt ist"
  fi

  # --- 3. Verify chain ---
  local VERIFY_RESULT
  VERIFY_RESULT="$(echo | openssl s_client -servername "$CUR_DOMAIN" -connect "$CUR_DOMAIN:443" 2>&1 | grep -i 'verify return code' || true)"
  if echo "$VERIFY_RESULT" | grep -q 'verify return code: 0'; then
    log_ok "Zertifikatskette: gültig (verify return code: 0)"
  elif [ -n "$VERIFY_RESULT" ]; then
    log_warn "Zertifikatskette: $VERIFY_RESULT"
  fi

  echo ""
}

do_traefik_update_domain() {
  echo ""
  local CUR_DOMAIN
  CUR_DOMAIN="$(grep -E '^DOMAIN=' "$ENV_FILE" 2>/dev/null | cut -d= -f2- || true)"
  log_info "Aktuelle Domain: ${CUR_DOMAIN:-(nicht gesetzt)}"
  echo ""

  read -r -p "$(echo -e "${CYAN}Neue Domain (z.B. das-krt.com):${NC} ")" NEW_DOMAIN
  if [ -z "$NEW_DOMAIN" ]; then
    log_warn "Keine Domain eingegeben"
    return
  fi

  read -r -p "$(echo -e "${CYAN}E-Mail für Let's Encrypt [admin@${NEW_DOMAIN}]:${NC} ")" NEW_EMAIL
  NEW_EMAIL="${NEW_EMAIL:-admin@${NEW_DOMAIN}}"

  # Update .env
  sed -i '/^#\?\s*DOMAIN=/d' "$ENV_FILE"
  echo "DOMAIN=$NEW_DOMAIN" >> "$ENV_FILE"

  # Update Traefik routes
  if [ -f "$TRAEFIK_DIR/routes.yml" ]; then
    sed -i "s/Host(\`[^)]*\`)/Host(\`${NEW_DOMAIN}\`)/" "$TRAEFIK_DIR/routes.yml"
    log_ok "Traefik routes.yml aktualisiert"
  fi

  # Update Traefik static config (email)
  if [ -f "$TRAEFIK_DIR/traefik.yml" ]; then
    sed -i "s/email: .*/email: ${NEW_EMAIL}/" "$TRAEFIK_DIR/traefik.yml"
    log_ok "Traefik traefik.yml aktualisiert (E-Mail: ${NEW_EMAIL})"
  fi

  # Update redirect URI if it uses old domain
  local CUR_REDIRECT
  CUR_REDIRECT="$(grep -E '^DISCORD_REDIRECT_URI=' "$ENV_FILE" 2>/dev/null | cut -d= -f2- || true)"
  if [ -n "$CUR_REDIRECT" ] && [ -n "$CUR_DOMAIN" ]; then
    local NEW_REDIRECT="${CUR_REDIRECT//$CUR_DOMAIN/$NEW_DOMAIN}"
    sed -i "s|^DISCORD_REDIRECT_URI=.*|DISCORD_REDIRECT_URI=$NEW_REDIRECT|" "$ENV_FILE"
    log_ok "OAuth2 Redirect URI aktualisiert: $NEW_REDIRECT"
    log_warn "Vergiss nicht, die Redirect URI auch im Discord Developer Portal zu ändern!"
  fi

  log_ok "Domain auf $NEW_DOMAIN geändert"
  echo ""

  read -r -p "$(echo -e "${CYAN}Traefik und Backend jetzt neustarten? (j/n): ${NC}")" DO_RESTART
  if [[ "$DO_RESTART" =~ ^[jJ]$ ]]; then
    # Reset acme.json for new domain cert
    if [ -f "$TRAEFIK_DIR/acme.json" ]; then
      echo "{}" > "$TRAEFIK_DIR/acme.json"
      chmod 600 "$TRAEFIK_DIR/acme.json"
      log_info "acme.json zurückgesetzt für neues Zertifikat"
    fi
    do_traefik_restart
    do_restart
  else
    log_warn "Änderungen werden erst nach einem Neustart wirksam"
  fi
}

# --------------------------------------------------
# OAuth2 Credentials Update
# --------------------------------------------------
do_update_oauth() {
  echo ""
  log_info "Discord OAuth2 Zugangsdaten aktualisieren"
  log_info "Aktuelle Werte werden aus .env gelesen."
  echo ""

  # Read current values
  local CUR_CLIENT_ID CUR_REDIRECT_URI
  CUR_CLIENT_ID="$(grep -E '^DISCORD_CLIENT_ID=' "$ENV_FILE" 2>/dev/null | cut -d= -f2- || true)"
  CUR_REDIRECT_URI="$(grep -E '^DISCORD_REDIRECT_URI=' "$ENV_FILE" 2>/dev/null | cut -d= -f2- || true)"

  log_info "Aktuelle Client ID:    ${CUR_CLIENT_ID:-(nicht gesetzt)}"
  log_info "Aktuelle Redirect URI: ${CUR_REDIRECT_URI:-(nicht gesetzt)}"
  echo ""
  log_info "Leer lassen = aktuellen Wert beibehalten."
  echo ""

  read -r -p "$(echo -e "${CYAN}Neue Discord Client ID [${CUR_CLIENT_ID:-leer}]:${NC} ")" NEW_CLIENT_ID
  NEW_CLIENT_ID="${NEW_CLIENT_ID:-$CUR_CLIENT_ID}"

  read -s -p "$(echo -e "${CYAN}Neues Discord Client Secret (Eingabe unsichtbar, leer=beibehalten):${NC} ")" NEW_CLIENT_SECRET; echo ""
  if [ -z "$NEW_CLIENT_SECRET" ]; then
    NEW_CLIENT_SECRET="$(grep -E '^DISCORD_CLIENT_SECRET=' "$ENV_FILE" 2>/dev/null | cut -d= -f2- || true)"
  fi

  read -r -p "$(echo -e "${CYAN}Neue Redirect URI [${CUR_REDIRECT_URI:-leer}]:${NC} ")" NEW_REDIRECT_URI
  NEW_REDIRECT_URI="${NEW_REDIRECT_URI:-$CUR_REDIRECT_URI}"

  # Validate
  if [ -z "$NEW_CLIENT_ID" ] || [ -z "$NEW_CLIENT_SECRET" ] || [ -z "$NEW_REDIRECT_URI" ]; then
    log_warn "Nicht alle Werte gesetzt — OAuth2 bleibt möglicherweise unvollständig konfiguriert."
  fi

  # Update .env: remove old OAuth2 lines (including commented-out)
  sed -i '/^#\?\s*DISCORD_CLIENT_ID=/d' "$ENV_FILE"
  sed -i '/^#\?\s*DISCORD_CLIENT_SECRET=/d' "$ENV_FILE"
  sed -i '/^#\?\s*DISCORD_REDIRECT_URI=/d' "$ENV_FILE"
  sed -i '/^# Discord OAuth2 (Login with Discord)$/d' "$ENV_FILE"

  # Append new values
  {
    echo ""
    echo "# Discord OAuth2 (Login with Discord)"
    [ -n "$NEW_CLIENT_ID" ] && echo "DISCORD_CLIENT_ID=$NEW_CLIENT_ID" || echo "# DISCORD_CLIENT_ID="
    [ -n "$NEW_CLIENT_SECRET" ] && echo "DISCORD_CLIENT_SECRET=$NEW_CLIENT_SECRET" || echo "# DISCORD_CLIENT_SECRET="
    [ -n "$NEW_REDIRECT_URI" ] && echo "DISCORD_REDIRECT_URI=$NEW_REDIRECT_URI" || echo "# DISCORD_REDIRECT_URI="
  } >> "$ENV_FILE"

  log_ok "OAuth2-Werte in .env aktualisiert"
  echo ""

  read -r -p "$(echo -e "${CYAN}Backend jetzt neustarten damit die Änderungen wirksam werden? (j/n): ${NC}")" DO_RESTART
  if [[ "$DO_RESTART" =~ ^[jJ]$ ]]; then
    do_restart
  else
    log_warn "Änderungen werden erst nach einem Neustart wirksam: bash service.sh restart"
  fi
}

# --------------------------------------------------
# Security: Debug-Login Toggle
# --------------------------------------------------
do_toggle_debug_login() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  # Check current debug mode state
  local STATUS_RESP
  STATUS_RESP="$(admin_curl -sS "http://127.0.0.1:3000/admin/dsgvo/status" 2>/dev/null || true)"
  local CURRENT_DEBUG
  CURRENT_DEBUG="$(echo "$STATUS_RESP" | grep -o '"debugMode":[a-z]*' | cut -d: -f2 || true)"

  echo ""
  if [ "$CURRENT_DEBUG" = "true" ]; then
    log_warn "Debug-Modus ist AKTIV → POST /auth/login (direkter Login ohne OAuth) ist AKTIVIERT"
  else
    log_ok "Debug-Modus ist INAKTIV → POST /auth/login ist DEAKTIVIERT (nur OAuth2 Login)"
  fi
  echo ""
  log_info "Der Debug-Login (POST /auth/login) erlaubt Login mit manueller Discord User ID + Guild ID."
  log_info "Dies ist ein Sicherheitsrisiko und sollte nur für Tests verwendet werden."
  log_warn "ACHTUNG: Debug-Login teilt den Debug-Modus-Schalter (DSGVO Compliance wird beeinflusst)."
  echo ""

  read -r -p "$(echo -e "${CYAN}Debug-Modus (und damit Debug-Login) umschalten? (j/n): ${NC}")" TOGGLE
  if [[ ! "$TOGGLE" =~ ^[jJ]$ ]]; then
    log_info "Abgebrochen"
    return
  fi

  local NEW_STATE="true"
  [ "$CURRENT_DEBUG" = "true" ] && NEW_STATE="false"

  local RESPONSE
  RESPONSE="$(admin_curl -sS -X POST "http://127.0.0.1:3000/admin/dsgvo/debug" \
    -H "content-type: application/json" \
    -d "{\"enabled\":${NEW_STATE}}" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    if [ "$NEW_STATE" = "true" ]; then
      log_warn "Debug-Modus AKTIVIERT → POST /auth/login ist jetzt erreichbar"
      log_warn "DSGVO Compliance wurde automatisch DEAKTIVIERT, Aufbewahrung: 7 Tage"
    else
      log_ok "Debug-Modus DEAKTIVIERT → POST /auth/login ist jetzt gesperrt (nur OAuth2)"
      log_info "DSGVO Compliance wird automatisch wieder aktiviert, sofern kein Debug-Logging (>= debugLOG) aktiv ist."
    fi
  else
    log_error "Fehler beim Umschalten: $RESPONSE"
  fi
}

# --------------------------------------------------
# Logging Management
# --------------------------------------------------
do_logging_status() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  log_info "Log-Level Status:"
  local HTTP_CODE BODY FULL_RESP
  FULL_RESP="$(admin_curl -sS -w '\n%{http_code}' "http://127.0.0.1:3000/admin/log-level" 2>/dev/null || true)"
  HTTP_CODE="$(echo "$FULL_RESP" | tail -1)"
  BODY="$(echo "$FULL_RESP" | head -n -1)"
  if [ -z "$BODY" ]; then
    log_error "Backend nicht erreichbar"
    return
  fi

  if [ "$HTTP_CODE" != "200" ]; then
    log_error "Backend hat HTTP $HTTP_CODE zurückgegeben — Logging-Endpunkt nicht verfügbar"
    log_info "Bitte Backend neu deployen (install.sh), damit /admin/log-level verfügbar wird."
    return
  fi

  # Verify it's actually JSON
  if ! echo "$BODY" | grep -q '"global"'; then
    log_error "Ungültige Antwort vom Backend (kein JSON)"
    log_info "Bitte Backend neu deployen (install.sh), damit /admin/log-level verfügbar wird."
    return
  fi

  echo ""
  # Pretty-print each service level
  local GLOBAL_LVL
  GLOBAL_LVL="$(echo "$BODY" | grep -o '"global":"[^"]*"' | cut -d'"' -f4)"
  log_info "Globales Level: ${GLOBAL_LVL:-?}"
  echo ""

  for SVC in voice http discord dsgvo ws oauth; do
    local LVL
    LVL="$(echo "$BODY" | grep -o "\"${SVC}\":\"[^\"]*\"" | cut -d'"' -f4)"
    if echo "$LVL" | grep -q '(override)'; then
      log_warn "  $SVC: $LVL"
    else
      log_ok "  $SVC: ${LVL:-$GLOBAL_LVL}"
    fi
  done
  echo ""
  log_info "Verfügbare Levels: minimalLOG | debugLOG | attackLOG"
  echo ""
}

do_logging_set() {
  local ADMIN_TOKEN_VAL
  ADMIN_TOKEN_VAL="$(get_admin_token)"
  if [ -z "${ADMIN_TOKEN_VAL:-}" ]; then
    log_error "ADMIN_TOKEN nicht gesetzt"
    return
  fi

  # Pre-check if endpoint exists
  local CHECK_CODE
  CHECK_CODE="$(admin_curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:3000/admin/log-level" 2>/dev/null || echo 0)"
  if [ "$CHECK_CODE" != "200" ]; then
    log_error "Logging-Endpunkt nicht verfügbar (HTTP $CHECK_CODE)"
    log_info "Bitte Backend neu deployen (install.sh), damit /admin/log-level verfügbar wird."
    return
  fi

  echo ""
  log_info "Verfügbare Services:"
  echo -e "  ${CYAN}all${NC}      — Alle Services (globales Level)"
  echo -e "  ${CYAN}voice${NC}    — Voice Relay"
  echo -e "  ${CYAN}http${NC}     — HTTP Server & OAuth"
  echo -e "  ${CYAN}discord${NC}  — Discord Bot"
  echo -e "  ${CYAN}dsgvo${NC}    — DSGVO Compliance"
  echo -e "  ${CYAN}ws${NC}       — WebSocket Hub"
  echo -e "  ${CYAN}oauth${NC}    — OAuth2 Login"
  echo ""
  echo -e "  Verfügbare Levels: ${GREEN}minimalLOG${NC} | ${GREEN}debugLOG${NC} | ${GREEN}attackLOG${NC}"
  echo -e "    minimalLOG = nur kritische Meldungen (Workflow-relevant)"
  echo -e "    debugLOG   = erweiterte Logs (Debug + Error + Critical)"
  echo -e "    attackLOG  = wie debugLOG (Platzhalter für zukünftige Angriffsanalyse)"
  echo ""

  read -r -p "$(echo -e "${CYAN}Service (all/voice/http/discord/dsgvo/ws/oauth): ${NC}")" SERVICE
  if [ -z "$SERVICE" ]; then
    log_warn "Kein Service eingegeben"
    return
  fi

  # Validate service name
  case "$SERVICE" in
    all|voice|http|discord|dsgvo|ws|oauth) ;;
    *)
      log_error "Unbekannter Service: $SERVICE (erlaubt: all, voice, http, discord, dsgvo, ws, oauth)"
      return
      ;;
  esac

  read -r -p "$(echo -e "${CYAN}Log-Level (minimalLOG/debugLOG/attackLOG): ${NC}")" LEVEL
  if [ -z "$LEVEL" ]; then
    log_warn "Kein Level eingegeben"
    return
  fi

  # Validate log level
  case "$LEVEL" in
    minimalLOG|debugLOG|attackLOG) ;;
    *)
      log_error "Unbekanntes Log-Level: $LEVEL (erlaubt: minimalLOG, debugLOG, attackLOG)"
      return
      ;;
  esac

  local JSON_BODY
  if [ "$SERVICE" = "all" ]; then
    JSON_BODY="{\"level\":\"$LEVEL\"}"
  else
    JSON_BODY="{\"service\":\"$SERVICE\",\"level\":\"$LEVEL\"}"
  fi

  local RESPONSE
  RESPONSE="$(admin_curl -sS -X POST "http://127.0.0.1:3000/admin/log-level" \
    -H "content-type: application/json" \
    -d "$JSON_BODY" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    log_ok "Log-Level für ${SERVICE} auf ${LEVEL} gesetzt"
    if [ "$LEVEL" != "minimalLOG" ]; then
      log_warn "Debug-Logging aktiv → DSGVO Compliance wurde automatisch DEAKTIVIERT"
    else
      log_info "DSGVO Compliance wird automatisch wieder aktiviert, sofern keine Debug-Features mehr aktiv sind."
    fi
  else
    log_error "Fehler: $RESPONSE"
  fi
}

# --------------------------------------------------
# Interaktives Menü (Tools)
# --------------------------------------------------
do_menu() {
  echo -e "${GREEN}=== das-krt Service | ${VERSION} ===${NC}"

  # Show DSGVO status warning on menu start
  show_dsgvo_warnings

  while true; do
    echo ""
    log_input "=== das-krt Menü (${VERSION}) ==="
    echo -e "${CYAN} 1) Service starten${NC}"
    echo -e "${CYAN} 2) Service stoppen${NC}"
    echo -e "${CYAN} 3) Service neustarten${NC}"
    echo -e "${CYAN} 4) Service Status & Healthcheck${NC}"
    echo -e "${CYAN} 6) Backend Healthcheck testen${NC}"
    echo -e "${CYAN} 7) Backend Testlog anzeigen (tail)${NC}"
    echo -e "${CYAN} 8) Backend Live-Logs verfolgen (journalctl -f)${NC}"
    echo -e "${CYAN} 9) TX Event senden (start/stop)${NC}"
    echo -e "${CYAN}10) TX Recent anzeigen${NC}"
    echo -e "${CYAN}11) Users Recent anzeigen${NC}"
    echo -e "${CYAN}--- DSGVO Compliance ---${NC}"
    echo -e "${CYAN}20) DSGVO Status anzeigen${NC}"
    echo -e "${CYAN}21) DSGVO Compliance Modus an/aus${NC}"
    echo -e "${CYAN}23) Userdaten löschen (Discord ID)${NC}"
    echo -e "${CYAN}24) Guilddaten löschen (Guild ID)${NC}"
    echo -e "${CYAN}25) DSGVO Cleanup manuell ausführen${NC}"
    echo -e "${CYAN}--- Kanal-Sync ---${NC}"
    echo -e "${CYAN}30) Kanal-Sync Status anzeigen${NC}"
    echo -e "${CYAN}31) Kanal-Sync jetzt auslösen${NC}"
    echo -e "${CYAN}32) Kanal-Sync Intervall ändern${NC}"
    echo -e "${CYAN}33) channels.json bearbeiten${NC}"
    echo -e "${CYAN}--- Ban Management ---${NC}"
    echo -e "${CYAN}40) Benutzer bannen${NC}"
    echo -e "${CYAN}41) Benutzer entbannen${NC}"
    echo -e "${CYAN}42) Banliste anzeigen${NC}"
    echo -e "${CYAN}43) Löschen und Bannen${NC}"
    echo -e "${CYAN}--- Security ---${NC}"
    echo -e "${CYAN}50) Debug-Login an/aus (POST /auth/login)${NC}"
    echo -e "${CYAN}51) Discord OAuth2 Zugangsdaten ändern${NC}"
    echo -e "${CYAN}--- Traefik (Reverse Proxy / TLS) ---${NC}"
    echo -e "${CYAN}60) Traefik Status & TLS Zertifikat${NC}"
    echo -e "${CYAN}61) Traefik neustarten${NC}"
    echo -e "${CYAN}62) Traefik Live-Logs${NC}"
    echo -e "${CYAN}63) Domain ändern${NC}"
    echo -e "${CYAN}64) Let's Encrypt Zertifikat prüfen${NC}"
    echo -e "${CYAN}--- Logging ---${NC}"
    echo -e "${CYAN}70) Log-Level Status anzeigen${NC}"
    echo -e "${CYAN}71) Log-Level setzen${NC}"
    echo -e "${CYAN} 0) Beenden${NC}"
    echo ""

    read -r -p "$(echo -e "${CYAN}Auswahl: ${NC}")" CHOICE

    case "$CHOICE" in
      1)
        do_start
        ;;
      2)
        do_stop
        ;;
      3)
        do_restart
        ;;
      4)
        do_status
        ;;
      5)
        log_warn "Menüpunkt 5 wurde nach 33 verschoben (Kanal-Sync Bereich)."
        ;;
      6)
        log_info "Healthcheck: http://127.0.0.1:3000/health"
        if curl -sf "http://127.0.0.1:3000/health" > /dev/null; then
          log_ok "Healthcheck OK"
        else
          log_error "Healthcheck fehlgeschlagen"
        fi
        ;;
      7)
        log_info "Testlog (letzte 200 Zeilen): $TEST_LOG"
        tail -n 200 "$TEST_LOG" || true
        ;;
      8)
        do_logs
        ;;
      9)
        log_warn "Entfernt — TX/Users-Endpunkte erfordern jetzt Bearer-Token-Authentifizierung (nur über Companion App)."
        ;;
      10)
        log_warn "Entfernt — TX/Users-Endpunkte erfordern jetzt Bearer-Token-Authentifizierung (nur über Companion App)."
        ;;
      11)
        log_warn "Entfernt — TX/Users-Endpunkte erfordern jetzt Bearer-Token-Authentifizierung (nur über Companion App)."
        ;;

      # --- DSGVO Compliance ---
      20)
        do_dsgvo_status
        ;;
      21)
        do_dsgvo_toggle
        ;;
      22)
        log_warn "Menüpunkt 22 wurde entfernt. Debug-Modus kann über Menüpunkt 50 gesteuert werden."
        ;;
      23)
        do_dsgvo_delete_user
        ;;
      24)
        do_dsgvo_delete_guild
        ;;
      25)
        do_dsgvo_cleanup
        ;;

      # --- Kanal-Sync ---
      30)
        do_channel_sync_status
        ;;
      31)
        do_channel_sync_trigger
        ;;
      32)
        do_channel_sync_interval
        ;;
      33)
        log_input "Öffne: $CHANNEL_MAP"
        nano "$CHANNEL_MAP"

        ADMIN_TOKEN_VAL="$(grep -E '^ADMIN_TOKEN=' "$ENV_FILE" | cut -d= -f2- || true)"
        if [ -n "${ADMIN_TOKEN_VAL:-}" ]; then
          if admin_curl -sf -X POST "http://127.0.0.1:3000/admin/reload" >/dev/null; then
            log_ok "channels.json neu geladen (/admin/reload)"
          else
            log_warn "Reload fehlgeschlagen – starte Backend neu"
            do_restart
          fi
        else
          log_warn "ADMIN_TOKEN nicht gesetzt – starte Backend neu"
          do_restart
        fi
        ;;

      # --- Ban Management ---
      40)
        do_ban_user
        ;;
      41)
        do_unban_user
        ;;
      42)
        do_list_bans
        ;;
      43)
        do_delete_and_ban
        ;;

      # --- Security ---
      50)
        do_toggle_debug_login
        ;;
      51)
        do_update_oauth
        ;;

      # --- Traefik ---
      60)
        do_traefik_status
        ;;
      61)
        do_traefik_restart
        ;;
      62)
        do_traefik_logs
        ;;
      63)
        do_traefik_update_domain
        ;;
      64)
        do_traefik_cert_check
        ;;

      # --- Logging ---
      70)
        do_logging_status
        ;;
      71)
        do_logging_set
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
}

# --------------------------------------------------
# CLI entry point
# --------------------------------------------------
case "${1:-menu}" in
  start)
    do_start
    ;;
  stop)
    do_stop
    ;;
  restart)
    do_restart
    ;;
  status)
    do_status
    ;;
  logs)
    do_logs
    ;;
  menu)
    do_menu
    ;;
  *)
    echo ""
    echo -e "${GREEN}das-krt Service Manager | ${VERSION}${NC}"
    echo ""
    echo "Usage: bash service.sh [command]"
    echo ""
    echo "Commands:"
    echo "  start     Starte den Backend Service"
    echo "  stop      Stoppe den Backend Service"
    echo "  restart   Restarte den Backend Service"
    echo "  status    Zeige Service-Status & Healthcheck"
    echo "  logs      Folge den Live-Logs (journalctl -f)"
    echo "  menu      Öffne das interaktive Menü (default)"
    echo ""
    ;;
esac
