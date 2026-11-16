#!/bin/sh
# Reports server health to healthchecks.io every few minutes (cron).
set -u
. /opt/cocorra/infra/healthchecks.env
URL="${HC_HEALTH_URL:?HC_HEALTH_URL missing}"

PROBLEMS=""
add() { PROBLEMS="${PROBLEMS}${1}
"; }

# Disk
DISK=$(df --output=pcent / | tail -1 | tr -dc '0-9')
[ "$DISK" -ge 80 ] && add "Disk / is at ${DISK}%"

# Memory
MEM_AVAIL=$(awk '/MemAvailable/ {print $2}' /proc/meminfo)
MEM_TOTAL=$(awk '/MemTotal/ {print $2}' /proc/meminfo)
MEM_PCT=$((MEM_AVAIL * 100 / MEM_TOTAL))
[ "$MEM_PCT" -lt 10 ] && add "Available memory is ${MEM_PCT}%"

# Containers
for c in caddy livekit-server minio cocorra_db cocorra-api; do
  S=$(docker inspect -f '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$c" 2>/dev/null || echo "missing")
  case "$S" in
    "running healthy"|"running none"|"running starting") ;;
    *) add "Container $c: $S" ;;
  esac
done
# Bandwidth (monthly total vs plan limit)
BW_LIMIT_GB=8000
if command -v vnstat >/dev/null 2>&1; then
  BW_BYTES=$(vnstat --oneline b 2>/dev/null | cut -d';' -f11)
  if [ -n "$BW_BYTES" ] && [ "$BW_BYTES" -eq "$BW_BYTES" ] 2>/dev/null; then
    BW_GB=$((BW_BYTES / 1000000000))
    BW_PCT=$((BW_GB * 100 / BW_LIMIT_GB))
    [ "$BW_PCT" -ge 80 ] && add "Monthly bandwidth at ${BW_PCT}% (${BW_GB}GB of ${BW_LIMIT_GB}GB)"
  fi
fi
SUMMARY="disk=${DISK}% mem_available=${MEM_PCT}% bandwidth_month=${BW_GB:-?}GB"
if [ -n "$PROBLEMS" ]; then