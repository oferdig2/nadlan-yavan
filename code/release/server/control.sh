#!/usr/bin/env bash
# Root helper behind the Restart buttons of the admin page (Server tab). deploy.sh installs it as /usr/local/sbin/nadlan-control
# with nadlan-control.path, which runs it (nadlan-control.service) as soon as the request folder isn't empty.
# The app (user nadlan, no sudo) drops an EMPTY file named after an action into /var/lib/nadlan-control/requests. This script
# only looks at file NAMES, acts on the three below and nothing else, and removes every entry. The outcome goes to
# results/<action>.json, which the app reads. Both folders sit in a root-owned folder, so the app can't redirect them.
set -uo pipefail
DIR=/var/lib/nadlan-control
REQ=$DIR/requests
RES=$DIR/results
declare -A UNITS=([restart-mysqld]=mysqld [restart-nginx]=nginx [restart-nadlan]=nadlan)

wanted=" "
shopt -s nullglob dotglob
for entry in "$REQ"/*; do
    name=${entry##*/}
    rm -rf -- "$entry"   # unknown names too: a leftover would keep the path unit firing
    case "$name" in
        restart-mysqld | restart-nginx | restart-nadlan) wanted+="$name " ;;
    esac
done

json_text() { printf '%s' "$1" | tr '\n\r\t' '   ' | sed 's/\\/\\\\/g; s/"/\\"/g' | cut -c1-400; }

# Not in the middle of a deploy, restore or backup: they hold this lock (lib.sh ops_lock) - refuse instead of disrupting.
busy=false
exec 9>/run/lock/nadlan-ops.lock
flock -n 9 || busy=true

# The app last: it may be the one asking.
for action in restart-mysqld restart-nginx restart-nadlan; do
    [[ "$wanted" == *" $action "* ]] || continue
    unit=${UNITS[$action]}
    started=$(date -u +%FT%TZ)
    if $busy; then
        ok=false; msg="A deploy, restore or backup is running right now - not restarted. Try again when it has finished."
    elif ! systemctl cat "$unit.service" >/dev/null 2>&1; then
        ok=false; msg="$unit is not installed on this server."
    elif [[ "$unit" == nginx ]] && ! out=$(nginx -t 2>&1); then
        ok=false; msg="nginx's configuration test failed, so nginx was NOT restarted (it keeps running as it is): $out"
    elif out=$(systemctl restart "$unit" 2>&1); then
        sleep 2
        state=$(systemctl is-active "$unit" 2>/dev/null || true)
        if [[ "$state" == active ]]; then ok=true; msg="Restarted."; else ok=false; msg="Restarted, but it is now '$state'. See: journalctl -u $unit"; fi
    else
        ok=false; msg="systemctl restart $unit failed: $out"
    fi
    install -d -m 755 "$RES"
    tmp=$(mktemp "$RES/.result.XXXXXX")
    printf '{"action":"%s","unit":"%s","ok":%s,"startedUtc":"%s","finishedUtc":"%s","message":"%s"}\n' \
        "$action" "$unit" "$ok" "$started" "$(date -u +%FT%TZ)" "$(json_text "$msg")" > "$tmp"
    chmod 644 "$tmp"
    mv -f "$tmp" "$RES/$action.json"
    logger -t nadlan-control "$action: $msg"
done
exit 0
