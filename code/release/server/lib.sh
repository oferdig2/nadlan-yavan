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
