#!/usr/bin/env bash
# Switches the app back to an earlier release that is still on the server (deploy.sh keeps the last 3).
# The database is not changed: older releases run on a newer schema. To also go back in data, use restore.sh.
# Usage (root): rollback.sh [release-id]      (default: the newest release that isn't the current one)
set -euo pipefail
SELF_DIR="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"
. "$SELF_DIR/lib.sh"
require_root

CUR=$(current_release_dir)
if [[ -n "${1:-}" ]]; then
    [[ "$1" =~ ^[A-Za-z0-9._-]+$ ]] || die "Bad release id '$1'."
    TARGET="$RELEASES_DIR/$1"
else
    TARGET=$({ ls -1dt "$RELEASES_DIR"/*/ 2>/dev/null || true; } | sed 's:/$::' | { grep -vxF "$CUR" || true; } | head -n 1)
fi
[[ -n "$TARGET" && -d "$TARGET" ]] || die "No such release on this server. On disk: $(ls -1t "$RELEASES_DIR" | tr '\n' ' ')"
[[ "$TARGET" != "$CUR" ]] || die "$(basename "$TARGET") is already the current release."

run_release() {
    systemctl stop "$SERVICE" || true
    ln -sfn "$1" "$CURRENT_LINK.next"
    mv -Tf "$CURRENT_LINK.next" "$CURRENT_LINK"
    install -m 644 "$1/server/nadlan.service" /etc/systemd/system/nadlan.service
    systemctl daemon-reload
    systemctl start "$SERVICE" || true
}

step "Switching from $(basename "$CUR") to $(basename "$TARGET")"
run_release "$TARGET"
if ! wait_healthy "$(basename "$TARGET")" 120; then
    journalctl -u "$SERVICE" -n 30 --no-pager || true
    if [[ -n "$CUR" && -d "$CUR" ]]; then
        echo "$(basename "$TARGET") did not start; going back to $(basename "$CUR")." >&2
        run_release "$CUR"
        wait_healthy "$(basename "$CUR")" 120 || true
    fi
    exit 1
fi
