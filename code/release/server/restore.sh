#!/usr/bin/env bash
# Replaces the Nadlan database with a dump: a backup.sh file, or your local database (5-copy-local-db.ps1).
# Takes a safety dump of the current data first, stops the app meanwhile, then brings the schema up to this release.
# If anything fails after the old data was dropped, the safety dump is put back and the app started again.
#   restore.sh <file.sql.gz> --yes                          everything from the dump (a backup of this server)
#   restore.sh <file.sql.gz> --yes --keep-config            keep this server's app_config (URLs, storage, secrets)
#   restore.sh <file.sql.gz> --yes --keep-server-identity   keep app_config AND this server's users, roles, grants,
#                                                           API tokens, reset links and cookie-signing keys (used for a
#                                                           copy of another database: its sign-in data must never come along)
set -Eeuo pipefail  # -E: the ERR trap (rollback below) also fires inside functions such as run_dbtool
SELF_DIR="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"
. "$SELF_DIR/lib.sh"
require_root
FILE="${1:?usage: restore.sh <file.sql.gz> --yes [--keep-config|--keep-server-identity]}"
[[ "${2:-}" == "--yes" ]] || die "This replaces ALL data in the database. Add --yes to confirm."
MODE="${3:-}"
case "$MODE" in
    "") KEEP_TABLES=() ;;
    --keep-config) KEEP_TABLES=(app_config) ;;
    # Sign-in and access data stays the server's own, like its settings.
    --keep-server-identity) KEEP_TABLES=(app_config security_role permission role_permission app_user resource_access
                                         api_token password_token data_protection_key) ;;
    *) die "Unknown option $MODE" ;;
esac
[[ -f "$FILE" ]] || die "No file $FILE (backups: ls -lt $BACKUP_DIR)"
gzip -t "$FILE" || die "$FILE is not a valid .gz file."

RELEASE_DIR="$(dirname "$SELF_DIR")"
CNF=$(make_client_cnf "$RELEASE_DIR")
KEEP_DUMP=$(mktemp /root/.nadlan-keep.XXXXXX)
trap 'rm -f "$CNF" "$KEEP_DUMP"' EXIT
DB=$(cnf_database "$CNF")
[[ "$DB" =~ ^[A-Za-z0-9_]+$ ]] || die "Unexpected database name '$DB'."

step "Stopping the app"
systemctl stop "$SERVICE" || true

step "Safety dump of the current data"
SAFETY=$(bash "$SELF_DIR/backup.sh" before-restore | tail -n 1) || SAFETY="" # a failure must not exit here: the app is stopped
[[ -f "$SAFETY" ]] || { systemctl start "$SERVICE" || true; die "Safety dump failed; nothing was changed."; }

if [[ ${#KEEP_TABLES[@]} -gt 0 ]]; then
    mysqldump --defaults-file="$CNF" --single-transaction --no-tablespaces --set-gtid-purged=OFF \
        --default-character-set=utf8mb4 "$DB" "${KEEP_TABLES[@]}" > "$KEEP_DUMP"
    echo "Kept from this server: ${KEEP_TABLES[*]}"
fi

# From here on the old data is gone until the restore finishes: any failure puts the safety dump back.
rollback() {
    echo "Restore FAILED - putting the previous data back from $SAFETY" >&2
    mysql --defaults-file="$CNF" -e "DROP DATABASE IF EXISTS \`$DB\`; CREATE DATABASE \`$DB\` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;" &&
        gunzip -c "$SAFETY" | mysql --defaults-file="$CNF" --default-character-set=utf8mb4 "$DB" &&
        echo "Previous data restored." >&2 || echo "Rollback failed too: restore $SAFETY by hand (restore.sh $SAFETY --yes)." >&2
    systemctl start "$SERVICE" || true
}
trap 'rollback' ERR

step "Restoring '$DB' from $(basename "$FILE")"
mysql --defaults-file="$CNF" -e "DROP DATABASE IF EXISTS \`$DB\`; CREATE DATABASE \`$DB\` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;"
gunzip -c "$FILE" | mysql --defaults-file="$CNF" --default-character-set=utf8mb4 "$DB"
if [[ ${#KEEP_TABLES[@]} -gt 0 ]]; then
    mysql --defaults-file="$CNF" --default-character-set=utf8mb4 "$DB" < "$KEEP_DUMP"
    echo "This server's ${KEEP_TABLES[*]} put back."
fi

if [[ "$MODE" == "--keep-server-identity" ]]; then
    # Ids from the server's old data now name other rows: drop links that would point at the wrong Contact/object, and
    # "who did it" ids from the copied database, which would show this server's users with the same id.
    mysql --defaults-file="$CNF" "$DB" -e "
        UPDATE app_user SET contact_id = NULL;
        DELETE FROM resource_access;
        DELETE FROM activity WHERE entity_type IN ('User', 'Role');
        UPDATE activity SET user_id = NULL;
        UPDATE parcel SET created_by_user_id = NULL;
        UPDATE asset SET created_by_user_id = NULL;
        UPDATE portfolio SET created_by_user_id = NULL;
        UPDATE file_attachment SET uploaded_by_user_id = NULL;"
    echo "Users keep their sign-in; re-link them to Contacts and re-grant object access in Admin > Users."
fi

step "Schema"
run_dbtool "$RELEASE_DIR" migrate

trap - ERR
step "Starting the app"
systemctl start "$SERVICE"
wait_healthy "" 90
