#!/usr/bin/env bash
# Sets one app_config value. The value is read from the first line of stdin (an empty line = empty value), so secrets
# such as Nadlan:Auth:Google:ClientSecret stay out of command lines and the sudo log. Restart the app afterwards.
# Usage (root): config-set.sh <configKey> <path>  < value-on-stdin       e.g. config-set.sh ms:host Nadlan:Maps:GoogleApiKey
set -euo pipefail
KEY="${1:?usage: config-set.sh <configKey> <path>}"
PATH_IN_CONFIG="${2:?usage: config-set.sh <configKey> <path>}"
IFS= read -r VALUE || true
VALUE="${VALUE%$'\r'}"              # drop a Windows line ending
VALUE="${VALUE#$'\xEF\xBB\xBF'}"    # and a UTF-8 byte order mark
exec /usr/bin/nadlan-db config set "$KEY" "$PATH_IN_CONFIG" "${VALUE:---empty}"
