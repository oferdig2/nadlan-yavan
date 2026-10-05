#!/usr/bin/env bash
# Sets a Nadlan user's password. The password is read from the first line of stdin, so it never appears on a
# command line or in the shell history (4-install-server.ps1 and 7-server-admin.ps1 send it that way).
# Usage (root): set-password.sh <email>  < password-on-stdin
set -euo pipefail
EMAIL="${1:?usage: set-password.sh <email>}"
IFS= read -r NADLAN_NEW_PASSWORD || true
NADLAN_NEW_PASSWORD="${NADLAN_NEW_PASSWORD%$'\r'}"              # a Windows line ending is not part of the password
NADLAN_NEW_PASSWORD="${NADLAN_NEW_PASSWORD#$'\xEF\xBB\xBF'}"    # nor is a UTF-8 byte order mark
[[ -n "$NADLAN_NEW_PASSWORD" ]] || { echo "No password on stdin." >&2; exit 1; }
export NADLAN_NEW_PASSWORD
exec /usr/bin/nadlan-db user set-password "$EMAIL"
