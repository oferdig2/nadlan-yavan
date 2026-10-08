#!/usr/bin/env bash
# Dumps the Nadlan database to /var/backups/nadlan/<db>-<utc time>-<label>.sql.gz and prints the file's path.
# Runs daily from nadlan-backup.timer and before every update. Keeps 14 days, but never fewer than the 5 newest.
# Usage (root): backup.sh [label]       e.g.  sudo /opt/nadlan/current/server/backup.sh manual
#               backup.sh --pin-target  fix where the S3 copies go to the current Nadlan:Storage settings (after moving the bucket)
# Each dump is also copied to the app's S3 bucket (below); 3-create-ec2-server.ps1 adds daily EBS snapshots of the disk.
set -euo pipefail
SELF_DIR="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"
. "$SELF_DIR/lib.sh"
require_root
ops_lock 3600 # not during a deploy or restore (both take their own backup through here)
LABEL="${1:-manual}"
[[ "$LABEL" =~ ^[A-Za-z0-9._-]+$ ]] || die "Label may only contain letters, digits, '.', '_' and '-'."

CNF=$(make_client_cnf "$(dirname "$SELF_DIR")")
trap 'rm -f "$CNF"' EXIT
if [[ "$LABEL" == "--pin-target" ]]; then pin_backup_target "$CNF" --force; exit 0; fi
DB=$(cnf_database "$CNF")

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
FILE="$BACKUP_DIR/$DB-$(date -u +%Y%m%d-%H%M%S)-$LABEL.sql.gz"
echo "Dumping '$DB' ..." >&2
mysqldump --defaults-file="$CNF" --single-transaction --quick --no-tablespaces --routines --triggers \
    --hex-blob --set-gtid-purged=OFF --default-character-set=utf8mb4 "$DB" | gzip -6 > "$FILE.part"
mv "$FILE.part" "$FILE"
chmod 600 "$FILE"
echo "Saved $(du -h "$FILE" | cut -f1)" >&2

# Off this server: a copy next to the app's files in S3 (s3://<bucket>/<root>/_backups/db/), through the instance role
# the app already uses. Losing the instance or its disk then loses at most a day. Kept there 35 days (setup-s3.ps1's
# lifecycle rule) and, with bucket versioning, an overwritten or deleted copy stays recoverable for 30 days more.
# A failed upload never fails the local backup (restore.sh relies on it); it is reported, and the next run tries again.
# The target is pinned in BACKUP_TARGET_FILE (lib.sh), never taken from the editable settings at run time.
[[ -f "$BACKUP_TARGET_FILE" ]] || pin_backup_target "$CNF" >&2 || true   # a server installed before the pin existed: once
BUCKET=""; ROOT=""; REGION=""
if [[ -f "$BACKUP_TARGET_FILE" ]]; then
    # shellcheck disable=SC1090
    . "$BACKUP_TARGET_FILE"
    BUCKET="${BACKUP_S3_BUCKET:-}"; ROOT="${BACKUP_S3_ROOT:-}"; REGION="${BACKUP_S3_REGION:-}"
fi
if [[ -z "$BUCKET" ]]; then
    warn "No S3 bucket configured (Nadlan:Storage:Bucket): this dump exists only on this server's disk."
elif ! command -v aws >/dev/null 2>&1; then
    warn "The aws CLI is missing: this dump was not copied to S3 (sudo dnf install -y awscli-2)."
else
    DEST="s3://$BUCKET/${ROOT:+$ROOT/}_backups/db/$(basename "$FILE")"
    if timeout 900 aws s3 cp "$FILE" "$DEST" --only-show-errors --sse AES256 ${REGION:+--region "$REGION"} >&2; then
        echo "Copied to $DEST" >&2
    else
        warn "Copy to $DEST FAILED: this dump exists only on this server's disk. Check the instance role (3-create-ec2-server.ps1)."
    fi
fi

# Retention: drop dumps older than 14 days, always keeping the 5 newest.
ls -1t "$BACKUP_DIR"/*.sql.gz 2>/dev/null | tail -n +6 | while read -r old; do
    if [[ -n "$(find "$old" -mtime +14 2>/dev/null)" ]]; then rm -f "$old"; fi
done

echo "$FILE"
