#!/usr/bin/env bash
# The Nadlan DB tool on the server, with the app's connection string. Installed as /usr/bin/nadlan-db.
#   sudo nadlan-db status
#   sudo nadlan-db config show ms:host
#   sudo nadlan-db config set ms:host Nadlan:Maps:GoogleApiKey AIza...     (then: sudo systemctl restart nadlan)
#   sudo nadlan-db user list
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Run it with sudo: sudo nadlan-db $*" >&2; exit 1; }
set -a
. /etc/nadlan/nadlan.env
set +a
exec /opt/nadlan/current/dbtool/Nadlan.DbTool "$@"
