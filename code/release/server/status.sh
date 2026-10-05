#!/usr/bin/env bash
# One-screen health report of a Nadlan server. Usage (root): status.sh
set -uo pipefail
SELF_DIR="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"
. "$SELF_DIR/lib.sh"

step "Release"
echo "current: $(basename "$(current_release_dir)")"
echo "on disk: $(ls -1t "$RELEASES_DIR" 2>/dev/null | tr '\n' ' ')"

step "App"
systemctl is-active "$SERVICE" >/dev/null && echo "service: running since $(systemctl show -p ActiveEnterTimestamp --value "$SERVICE")" || echo "service: NOT running"
echo "health:  $(curl -fsS --max-time 3 "$APP_URL/api/health" 2>/dev/null || echo 'no answer')"
run_dbtool "$(current_release_dir)" status 2>&1 | sed 's/^/schema:  /'

step "Memory (MB)"
free -m
for proc in mysqld Nadlan.Host nginx; do
    rss=$(ps -C "$proc" -o rss= 2>/dev/null | awk '{s+=$1} END {printf "%d", s/1024}')
    printf '  %-12s %4s MB resident\n' "$proc" "${rss:-0}"
done

step "Disk"
df -h / | tail -n 1
du -sh "$RELEASES_DIR" "$BACKUP_DIR" /var/lib/mysql 2>/dev/null

step "Backups (newest 5)"
ls -lht "$BACKUP_DIR"/*.sql.gz 2>/dev/null | head -n 5 | awk '{print "  " $5 "  " $9}'
systemctl list-timers nadlan-backup.timer --no-pager 2>/dev/null | sed -n 2p

step "HTTPS certificate"
if command -v certbot >/dev/null; then
    certbot certificates 2>/dev/null | grep -E 'Certificate Name|Domains|Expiry' || echo "  none"
else
    echo "  none (HTTP only)"
fi
