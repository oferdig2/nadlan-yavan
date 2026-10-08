# Shared by the Nadlan server scripts (sourced, not run). Layout on the server:
#   /opt/nadlan/releases/<release-id>/{app,dbtool,server,RELEASE}   one folder per deployed release (binaries only)
#   /opt/nadlan/current -> releases/<release-id>                     what systemd runs
#   /etc/nadlan/nadlan.env                                            NADLAN_MYSQL_CS - the app's only input (root, 600)
#   /var/backups/nadlan/*.sql.gz                                      database dumps (daily + before every update)

NADLAN_HOME=/opt/nadlan
RELEASES_DIR=$NADLAN_HOME/releases
CURRENT_LINK=$NADLAN_HOME/current
ENV_FILE=/etc/nadlan/nadlan.env
BACKUP_DIR=/var/backups/nadlan
SERVICE=nadlan
APP_URL=http://127.0.0.1:5515
KEEP_RELEASES=3

step() { echo; echo "== $*"; }
warn() { echo "WARNING: $*" >&2; }
die() { echo "ERROR: $*" >&2; exit 1; }

require_root() { [[ $EUID -eq 0 ]] || die "Run as root (sudo)."; }

# Runs the DB tool of a release folder with the app's connection string.
run_dbtool() {
    local release_dir="$1"; shift
    ( set -a; . "$ENV_FILE"; set +a; exec "$release_dir/dbtool/Nadlan.DbTool" "$@" )
}

# Writes the connection as a MySQL option file (600) and prints its path; the caller deletes it.
make_client_cnf() {
    local release_dir="$1" cnf
    cnf=$(mktemp /root/.nadlan-cnf.XXXXXX)
    chmod 600 "$cnf"
    run_dbtool "$release_dir" client-cnf > "$cnf"
    echo "$cnf"
}

cnf_database() { sed -n 's/^database=//p' "$1"; }

# Waits until /api/health answers for the expected release id (empty = any release). Returns 1 on timeout.
wait_healthy() {
    local expected="$1" seconds="${2:-90}" body=""
    for ((i = 0; i < seconds; i += 2)); do
        body=$(curl -fsS --max-time 3 "$APP_URL/api/health" 2>/dev/null || true)
        if [[ -n "$body" && ( -z "$expected" || "$body" == *"\"version\":\"$expected\""* ) ]]; then
            echo "Healthy: $body"
            return 0
        fi
        sleep 2
    done
    echo "No healthy answer within ${seconds}s (last: ${body:-none})." >&2
    return 1
}

current_release_dir() { if [[ -L "$CURRENT_LINK" ]]; then readlink -f "$CURRENT_LINK"; fi; }

# One server task at a time: deploy, restore, backup and the admin page's Restart buttons (control.sh) share this lock, so
# a restart can't hit a half-done deploy or restore. Nested calls (deploy -> backup) inherit it.
OPS_LOCK=/run/lock/nadlan-ops.lock
ops_lock() {   # ops_lock [seconds to wait]
    [[ -n "${NADLAN_OPS_LOCKED:-}" ]] && return 0
    exec 9>"$OPS_LOCK"
    flock -w "${1:-900}" 9 || die "Another server task (deploy, restore or backup) is still running; try again when it has finished."
    export NADLAN_OPS_LOCKED=1
}

# Where backup.sh copies the database dumps off the server: fixed in this root-only file at install / first deploy, not
# read from app_config at each run - the Settings page edits app_config, and must not be able to send the dumps (all data,
# all secrets) elsewhere. backup.sh --pin-target writes it again from the current settings (after moving the bucket).
BACKUP_TARGET_FILE=/etc/nadlan/backup-target.env
pin_backup_target() {   # pin_backup_target <client.cnf> [--force]
    local cnf="$1" db bucket root region
    if [[ -f "$BACKUP_TARGET_FILE" && "${2:-}" != --force ]]; then return 0; fi
    db=$(cnf_database "$cnf")
    storage_value() {
        mysql --defaults-file="$cnf" -N -B -e "SELECT COALESCE(JSON_UNQUOTE(JSON_EXTRACT(json_text, '\$.Nadlan.Storage.$1')), '')
            FROM app_config WHERE config_key = 'ms:host'" "$db" 2>/dev/null || true
    }
    bucket=$(storage_value Bucket); root=$(storage_value RootFolder); region=$(storage_value Region)
    root="${root#/}"; root="${root%/}"
    [[ -z "$bucket" || "$bucket" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] || { warn "Odd bucket name '$bucket' in app_config; the backup target was not pinned."; return 1; }
    ( umask 077; printf 'BACKUP_S3_BUCKET=%q\nBACKUP_S3_ROOT=%q\nBACKUP_S3_REGION=%q\n' "$bucket" "$root" "$region" > "$BACKUP_TARGET_FILE" )
    echo "Backup copies go to: ${bucket:+s3://$bucket/${root:+$root/}_backups/db/}${bucket:-(no S3 bucket: kept on this disk only)} (pinned in $BACKUP_TARGET_FILE)"
}
