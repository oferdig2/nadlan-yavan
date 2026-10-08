#!/usr/bin/env bash
# Deploys an unpacked release package (binaries only) on a server prepared by install.sh:
#   unpack to /opt/nadlan/releases/<id> -> stop app -> back up DB -> migrate schema -> switch "current" -> start
#   -> wait for /api/health to report the new release. If it doesn't, "current" goes back to the previous release.
# Usage (root): deploy.sh <unpacked-package-dir>      (4-install-server.ps1 / 5-update-server.ps1 call it)
set -euo pipefail
PKG="$(cd "${1:?usage: deploy.sh <unpacked-package-dir>}" && pwd)"
. "$PKG/server/lib.sh"
require_root
[[ -f "$ENV_FILE" ]] || die "$ENV_FILE is missing: this server isn't installed yet (run 4-install-server.ps1)."

ID=$(tr -d '[:space:]' < "$PKG/RELEASE")
[[ "$ID" =~ ^[A-Za-z0-9._-]+$ ]] || die "Bad release id '$ID' in the package."
NEW="$RELEASES_DIR/$ID"
PREV=$(current_release_dir)
if [[ "$PREV" == "$NEW" ]]; then
    warn "Release $ID is already the current one; deploying it again (there is no older release to roll back to)."
    PREV=""
fi

step "Unpacking release $ID"
mkdir -p "$RELEASES_DIR"
STAGE="$RELEASES_DIR/.incoming-$ID"
rm -rf "$STAGE"
mkdir -p "$STAGE"
cp -a "$PKG/app" "$PKG/dbtool" "$PKG/server" "$PKG/RELEASE" "$STAGE/"
if [[ -f "$PKG/BUILD-INFO" ]]; then cp "$PKG/BUILD-INFO" "$STAGE/"; fi
# Read-only for the service user: it runs the binaries but can't change them.
chown -R root:root "$STAGE"
find "$STAGE" -type d -exec chmod 755 {} +
find "$STAGE" -type f -exec chmod 644 {} +
chmod 755 "$STAGE/app/Nadlan.Host" "$STAGE/dbtool/Nadlan.DbTool" "$STAGE"/server/*.sh
find "$STAGE" -type f \( -name '*.so' -o -name createdump \) -exec chmod 755 {} +

step "Database"
STATUS=$(run_dbtool "$STAGE" status 2>&1 || true)
echo "$STATUS"
if [[ "$STATUS" == *"ERROR"* ]]; then
    rm -rf "$STAGE"
    die "Can't reach the database; nothing was changed. Check: sudo systemctl status mysqld"
fi

# Before the switch "current" still points at the previous release: starting the service runs it again.
abort_before_switch() {
    echo "$1 Release $ID was NOT deployed." >&2
    rm -rf "$STAGE"
    if [[ -n "$PREV" ]]; then systemctl start "$SERVICE" || true; fi
    exit 1
}

step "Stopping the app"
systemctl stop "$SERVICE" 2>/dev/null || true

BACKUP_FILE=""
if [[ "$STATUS" != *"does not exist"* ]]; then
    step "Backing up the database"
    BACKUP_FILE=$(bash "$STAGE/server/backup.sh" "before-$ID" | tail -n 1) || abort_before_switch "Backup failed."
    echo "$BACKUP_FILE"
fi

step "Updating the schema"
if ! run_dbtool "$STAGE" migrate; then
    if [[ -n "$BACKUP_FILE" ]]; then
        echo "Backup taken just before: $BACKUP_FILE  (restore: sudo $CURRENT_LINK/server/restore.sh $BACKUP_FILE)" >&2
    fi
    abort_before_switch "Schema update failed."
fi

install_units() {
    install -m 644 "$1/server/nadlan.service" /etc/systemd/system/nadlan.service
    install -m 644 "$1/server/nadlan-backup.service" /etc/systemd/system/nadlan-backup.service
    install -m 644 "$1/server/nadlan-backup.timer" /etc/systemd/system/nadlan-backup.timer
    # Restart helper for the admin page (Server tab). Releases before it don't have these files: leave what is installed.
    if [[ -f "$1/server/control.sh" ]]; then
        install -m 755 "$1/server/control.sh" /usr/local/sbin/nadlan-control
        install -m 644 "$1/server/nadlan-control.path" /etc/systemd/system/nadlan-control.path
        install -m 644 "$1/server/nadlan-control.service" /etc/systemd/system/nadlan-control.service
        # Root-owned, so the app can't swap the folders for links; it may only drop files into requests (group nadlan,
        # sticky: its own files only).
        install -d -m 755 -o root -g root /var/lib/nadlan-control /var/lib/nadlan-control/results
        install -d -m 1770 -o root -g nadlan /var/lib/nadlan-control/requests
    fi
    systemctl daemon-reload
}

switch_to() {
    ln -sfn "$1" "$CURRENT_LINK.next"
    mv -Tf "$CURRENT_LINK.next" "$CURRENT_LINK"
}

step "Switching to release $ID"
rm -rf "$NEW"
mv "$STAGE" "$NEW"
switch_to "$NEW"
install_units "$NEW"
ln -sfn "$CURRENT_LINK/server/nadlan-db.sh" /usr/bin/nadlan-db
systemctl enable "$SERVICE" >/dev/null 2>&1
systemctl enable --now nadlan-backup.timer >/dev/null 2>&1
if [[ -f /etc/systemd/system/nadlan-control.path ]]; then
    systemctl enable --now nadlan-control.path >/dev/null 2>&1 || warn "nadlan-control.path did not start: the admin page's Restart buttons won't work."
fi
systemctl start "$SERVICE"

step "Waiting for the app"
if ! wait_healthy "$ID" 120; then
    echo "--- last log lines ---"
    journalctl -u "$SERVICE" -n 60 --no-pager || true
    echo "----------------------"
    if [[ -n "$PREV" && -d "$PREV" ]]; then
        echo "Release $ID did not start; rolling back to $(basename "$PREV")." >&2
        systemctl stop "$SERVICE" || true
        switch_to "$PREV"
        install_units "$PREV"
        systemctl start "$SERVICE" || true
        wait_healthy "" 60 || true
        rm -rf "$NEW"   # never ran: keep it out of the rollback choices
        if [[ -n "$BACKUP_FILE" ]]; then
            echo "The schema stays updated (older releases run on a newer schema). Backup from before the update: $BACKUP_FILE" >&2
        fi
    fi
    exit 1
fi

step "Cleaning up old releases (keeping $KEEP_RELEASES)"
CUR=$(current_release_dir)
{ ls -1dt "$RELEASES_DIR"/*/ 2>/dev/null || true; } | sed 's:/$::' | { grep -vxF "$CUR" || true; } |
    tail -n +"$KEEP_RELEASES" | while read -r old; do
        echo "removing $(basename "$old")"
        rm -rf "$old"
    done

echo
echo "Release $ID is live."
