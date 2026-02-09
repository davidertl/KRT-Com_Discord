#!/usr/bin/env bash
##version alpha-0.0.3
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

VERSION="Alpha 0.0.3"
SERVICE_NAME="das-krt-backend"

# --------------------------------------------------
# Variablen
# --------------------------------------------------
APP_ROOT="/opt/das-krt"
BACKEND_DIR="$APP_ROOT/backend"
ENV_FILE="$BACKEND_DIR/.env"
CHANNEL_MAP="$APP_ROOT/config/channels.json"
TEST_LOG="$APP_ROOT/logs/backend-test.log"

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
  RESPONSE="$(curl -sS "http://127.0.0.1:3000/admin/dsgvo/status" -H "x-admin-token: $ADMIN_TOKEN_VAL" 2>/dev/null || true)"
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
    echo ""
    if [ "$DEBUG_ON" = "true" ]; then
      log_warn "DSGVO Compliance Modus ist DEAKTIVIERT (Debug-Modus aktiv)"
    else
      log_warn "DSGVO Compliance Modus ist DEAKTIVIERT — Userdaten werden NICHT automatisch gelöscht"
    fi
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
  RESPONSE="$(curl -sS "http://127.0.0.1:3000/admin/dsgvo/status" -H "x-admin-token: $ADMIN_TOKEN_VAL" 2>/dev/null || true)"
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
  RESPONSE="$(curl -sS -X POST "http://127.0.0.1:3000/admin/dsgvo/toggle" \
    -H "x-admin-token: $ADMIN_TOKEN_VAL" \
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
  RESPONSE="$(curl -sS -X POST "http://127.0.0.1:3000/admin/dsgvo/debug" \
    -H "x-admin-token: $ADMIN_TOKEN_VAL" \
    -H "content-type: application/json" \
    -d "{\"enabled\":${ENABLED_VAL}}" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    if [ "$ENABLED_VAL" = "true" ]; then
      log_warn "Debug-Modus AKTIVIERT — DSGVO Compliance automatisch deaktiviert, Aufbewahrung: 7 Tage"
    else
      log_ok "Debug-Modus DEAKTIVIERT"
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
  RESPONSE="$(curl -sS -X POST "http://127.0.0.1:3000/admin/dsgvo/delete-user" \
    -H "x-admin-token: $ADMIN_TOKEN_VAL" \
    -H "content-type: application/json" \
    -d "{\"discordUserId\":\"${USER_ID}\"}" 2>/dev/null || true)"

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
  RESPONSE="$(curl -sS -X POST "http://127.0.0.1:3000/admin/dsgvo/delete-guild" \
    -H "x-admin-token: $ADMIN_TOKEN_VAL" \
    -H "content-type: application/json" \
    -d "{\"guildId\":\"${GUILD_ID}\"}" 2>/dev/null || true)"

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
  RESPONSE="$(curl -sS -X POST "http://127.0.0.1:3000/admin/dsgvo/cleanup" \
    -H "x-admin-token: $ADMIN_TOKEN_VAL" \
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
  RESPONSE="$(curl -sS "http://127.0.0.1:3000/admin/channel-sync/status" -H "x-admin-token: $ADMIN_TOKEN_VAL" 2>/dev/null || true)"
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
  RESPONSE="$(curl -sS -X POST "http://127.0.0.1:3000/admin/channel-sync/trigger" \
    -H "x-admin-token: $ADMIN_TOKEN_VAL" \
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

  local RESPONSE
  RESPONSE="$(curl -sS -X POST "http://127.0.0.1:3000/admin/channel-sync/interval" \
    -H "x-admin-token: $ADMIN_TOKEN_VAL" \
    -H "content-type: application/json" \
    -d "{\"hours\":${HOURS}}" 2>/dev/null || true)"

  if echo "$RESPONSE" | grep -q '"ok":true'; then
    log_ok "Sync-Intervall auf ${HOURS} Stunden gesetzt"
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
    echo -e "${CYAN} 5) channels.json bearbeiten${NC}"
    echo -e "${CYAN} 6) Backend Healthcheck testen${NC}"
    echo -e "${CYAN} 7) Backend Testlog anzeigen (tail)${NC}"
    echo -e "${CYAN} 8) Backend Live-Logs verfolgen (journalctl -f)${NC}"
    echo -e "${CYAN} 9) TX Event senden (start/stop)${NC}"
    echo -e "${CYAN}10) TX Recent anzeigen${NC}"
    echo -e "${CYAN}11) Users Recent anzeigen${NC}"
    echo -e "${CYAN}--- DSGVO Compliance ---${NC}"
    echo -e "${CYAN}20) DSGVO Status anzeigen${NC}"
    echo -e "${CYAN}21) DSGVO Compliance Modus an/aus${NC}"
    echo -e "${CYAN}22) Debug Modus an/aus${NC}"
    echo -e "${CYAN}23) Userdaten löschen (Discord ID)${NC}"
    echo -e "${CYAN}24) Guilddaten löschen (Guild ID)${NC}"
    echo -e "${CYAN}25) DSGVO Cleanup manuell ausführen${NC}"
    echo -e "${CYAN}--- Kanal-Sync ---${NC}"
    echo -e "${CYAN}30) Kanal-Sync Status anzeigen${NC}"
    echo -e "${CYAN}31) Kanal-Sync jetzt auslösen${NC}"
    echo -e "${CYAN}32) Kanal-Sync Intervall ändern${NC}"
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
        log_input "Öffne: $CHANNEL_MAP"
        nano "$CHANNEL_MAP"

        ADMIN_TOKEN_VAL="$(grep -E '^ADMIN_TOKEN=' "$ENV_FILE" | cut -d= -f2- || true)"
        if [ -n "${ADMIN_TOKEN_VAL:-}" ]; then
          if curl -sf -X POST "http://127.0.0.1:3000/admin/reload" -H "x-admin-token: $ADMIN_TOKEN_VAL" >/dev/null; then
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
      10)
        log_info "TX Recent: http://127.0.0.1:3000/tx/recent?limit=10"
        curl -sS "http://127.0.0.1:3000/tx/recent?limit=10" || true
        echo ""
        ;;
      11)
        log_info "Users Recent: http://127.0.0.1:3000/users/recent?limit=10"
        curl -sS "http://127.0.0.1:3000/users/recent?limit=10" || true
        echo ""
        ;;

      # --- DSGVO Compliance ---
      20)
        do_dsgvo_status
        ;;
      21)
        do_dsgvo_toggle
        ;;
      22)
        do_dsgvo_debug_toggle
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
