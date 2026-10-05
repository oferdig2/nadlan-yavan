#!/usr/bin/env bash
# First-time setup of a Nadlan server on Amazon Linux 2023, then deploys the release in the package.
# Safe to re-run (e.g. to add HTTPS once DNS points here): every step checks before it changes anything, and existing
# passwords, data and certificates are kept.
#   packages, swap, service user -> MySQL 8.4 (small-memory config, local only) + DB user -> /etc/nadlan/nadlan.env
#   -> nginx (+ Let's Encrypt when a domain is given) -> deploy.sh -> app_config settings for this server
# Usage (root): install.sh <unpacked-package-dir>     settings come from <dir>/install.conf (4-install-server.ps1 writes it)
set -euo pipefail
PKG="$(cd "${1:?usage: install.sh <unpacked-package-dir>}" && pwd)"
. "$PKG/server/lib.sh"
require_root

DOMAIN=""            # e.g. nadlan.example.com (DNS A record -> this server). Empty = plain HTTP on the IP address
CERT_EMAIL=""        # Let's Encrypt expiry notices
CONNECTION_STRING="" # empty = install MySQL here; else an external MySQL 8 (e.g. RDS) and nothing is installed
MAPS_KEY=""          # Nadlan:Maps:GoogleApiKey (empty = keep the current value)
STORAGE_BUCKET=""    # Nadlan:Storage:* (empty = keep the current values)
STORAGE_ROOT="nadlan/prod"
STORAGE_REGION=""
if [[ -f "$PKG/install.conf" ]]; then . "$PKG/install.conf"; fi

MEM_MB=$(awk '/MemTotal/ {print int($2/1024)}' /proc/meminfo)

step "1/8 System packages"
grep -q 'Amazon Linux' /etc/os-release || warn "Written for Amazon Linux 2023; this is $(. /etc/os-release; echo "$PRETTY_NAME")."
dnf -y update
dnf -y install nginx tar gzip libicu awscli-2   # aws: backup.sh copies each dump to S3

step "2/8 Swap"
if [[ -z "$(swapon --show --noheadings)" && $MEM_MB -lt 2048 ]]; then
    fallocate -l 1G /swapfile
    chmod 600 /swapfile
    mkswap /swapfile >/dev/null
    swapon /swapfile
    grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
    echo 'vm.swappiness=10' > /etc/sysctl.d/90-nadlan.conf
    sysctl -q -p /etc/sysctl.d/90-nadlan.conf
    echo "1 GB swap file added (RAM: ${MEM_MB} MB)."
else
    echo "Kept as is (RAM: ${MEM_MB} MB, swap: $(swapon --show --noheadings | awk '{print $3}' | head -n 1))."
fi

step "3/8 Service user and folders"
id nadlan >/dev/null 2>&1 || useradd --system --home-dir /var/lib/nadlan --shell /sbin/nologin nadlan
mkdir -p "$RELEASES_DIR" /etc/nadlan "$BACKUP_DIR"
chmod 700 /etc/nadlan "$BACKUP_DIR"

step "4/8 MySQL"
# The MySQL client (mysqldump for backups) is needed even with an external database.
if ! rpm -q mysql84-community-release >/dev/null 2>&1; then
    rpm --import https://repo.mysql.com/RPM-GPG-KEY-mysql-2023
    dnf -y install https://dev.mysql.com/get/mysql84-community-release-el9-1.noarch.rpm ||
        dnf -y install https://repo.mysql.com/mysql84-community-release-el9.rpm
fi

new_password() {
    # MySQL's validate_password wants upper, lower, digit and a special character; '-' and '_' are also safe in the
    # env file and the connection string unquoted.
    echo "$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 28)Aa1-_"
}

if [[ -n "$CONNECTION_STRING" ]]; then
    dnf -y install mysql-community-client
    [[ "$CONNECTION_STRING" != *"'"* ]] || die "The connection string may not contain a single quote (')."
    if [[ -f "$ENV_FILE" ]]; then cp -p "$ENV_FILE" "$ENV_FILE.bak"; fi
    umask 077
    printf "NADLAN_MYSQL_CS='%s'\n" "$CONNECTION_STRING" > "$ENV_FILE"
    umask 022
    echo "External database: connection string written to $ENV_FILE."
else
    if ! rpm -q mysql-community-server >/dev/null 2>&1; then
        dnf -y install mysql-community-server
    fi
    mkdir -p /etc/my.cnf.d
    install -m 644 "$PKG/server/mysql-nadlan.cnf" /etc/my.cnf.d/nadlan.cnf
    grep -q '^!includedir /etc/my.cnf.d' /etc/my.cnf || printf '\n!includedir /etc/my.cnf.d\n' >> /etc/my.cnf
    systemctl enable mysqld >/dev/null 2>&1
    systemctl restart mysqld

    if [[ ! -f /root/.my.cnf ]]; then
        # First start: MySQL logged a temporary root password that must be replaced before anything else works.
        TEMP_PW=$(grep 'temporary password' /var/log/mysqld.log | tail -n 1 | awk '{print $NF}')
        [[ -n "$TEMP_PW" ]] || die "No temporary root password in /var/log/mysqld.log, and /root/.my.cnf is missing. Create /root/.my.cnf ([client] user=root password=...) and re-run."
        ROOT_PW=$(new_password)
        mysql --connect-expired-password -uroot -p"$TEMP_PW" -e "ALTER USER 'root'@'localhost' IDENTIFIED BY '$ROOT_PW';"
        umask 077
        printf '[client]\nuser=root\npassword="%s"\n' "$ROOT_PW" > /root/.my.cnf
        umask 022
        echo "MySQL root password set; it is in /root/.my.cnf (root only)."
    fi

    if [[ ! -f "$ENV_FILE" ]]; then
        APP_PW=$(new_password)
        mysql -e "CREATE DATABASE IF NOT EXISTS nadlanyavan CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
                  CREATE USER IF NOT EXISTS 'nadlan'@'127.0.0.1' IDENTIFIED BY '$APP_PW';
                  ALTER USER 'nadlan'@'127.0.0.1' IDENTIFIED BY '$APP_PW';
                  GRANT ALL PRIVILEGES ON nadlanyavan.* TO 'nadlan'@'127.0.0.1';"
        umask 077
        printf 'NADLAN_MYSQL_CS="Server=127.0.0.1;Port=3306;User ID=nadlan;Password=%s;Database=nadlanyavan;Max Pool Size=20;Connection Timeout=20;Default Command Timeout=300"\n' "$APP_PW" > "$ENV_FILE"
        umask 022
        echo "Database nadlanyavan and user nadlan created; connection string in $ENV_FILE (root only)."
    else
        echo "Kept the existing $ENV_FILE."
    fi
fi
chmod 600 "$ENV_FILE"

step "5/8 nginx"
if [[ ! -f /etc/nginx/nginx.conf.orig ]]; then cp -p /etc/nginx/nginx.conf /etc/nginx/nginx.conf.orig; fi
install -m 644 "$PKG/server/nginx.conf" /etc/nginx/nginx.conf
SERVER_NAME="${DOMAIN:-_}"
SITE=/etc/nginx/conf.d/nadlan.conf
if [[ ! -f "$SITE" ]] || ! grep -q "server_name $SERVER_NAME;" "$SITE"; then
    sed "s/__SERVER_NAME__/$SERVER_NAME/" "$PKG/server/nginx-site.conf" > "$SITE"
    echo "Wrote $SITE for '$SERVER_NAME'."
fi
if command -v getenforce >/dev/null && [[ "$(getenforce)" == "Enforcing" ]]; then
    setsebool -P httpd_can_network_connect 1   # SELinux: let nginx reach the app
fi
nginx -t
systemctl enable nginx >/dev/null 2>&1
systemctl reload-or-restart nginx

step "6/8 HTTPS"
HTTPS=0
if [[ -n "$DOMAIN" ]]; then
    rpm -q python3-certbot-nginx >/dev/null 2>&1 || dnf -y install certbot python3-certbot-nginx
    EMAIL_ARGS=(--register-unsafely-without-email)
    if [[ -n "$CERT_EMAIL" ]]; then EMAIL_ARGS=(-m "$CERT_EMAIL"); fi
    if certbot --nginx -d "$DOMAIN" --non-interactive --agree-tos --keep-until-expiring --redirect "${EMAIL_ARGS[@]}"; then
        HTTPS=1
        systemctl enable --now certbot-renew.timer >/dev/null 2>&1 || true
    else
        warn "No certificate yet (does the DNS A record of $DOMAIN point to this server, and is port 80 open?). Serving plain HTTP; run the install again once DNS is ready."
    fi
else
    echo "No domain given: plain HTTP on the server's address. Sign-in cookies need HTTPS to be secure - add a domain before real use."
fi

step "7/8 Deploying the release"
bash "$PKG/server/deploy.sh" "$PKG"

step "8/8 Settings for this server (app_config, ms:host)"
set_config() { run_dbtool "$CURRENT_LINK" config set ms:host "$1" "$2" >/dev/null && echo "  $1 = $3"; }
set_config Nadlan:Auth:TrustForwardedHeaders true "true (behind nginx)"
if [[ -n "$DOMAIN" ]]; then
    URL="http://$DOMAIN"
    if [[ $HTTPS -eq 1 ]]; then URL="https://$DOMAIN"; fi
    set_config Nadlan:Auth:PublicBaseUrl "$URL" "$URL"
else
    # No domain: password links must still use a fixed address, never the request's Host header.
    IMDS_TOKEN=$(curl -s -m 3 -X PUT http://169.254.169.254/latest/api/token -H "X-aws-ec2-metadata-token-ttl-seconds: 60" || true)
    PUBLIC_IP=$(curl -s -m 3 -H "X-aws-ec2-metadata-token: $IMDS_TOKEN" http://169.254.169.254/latest/meta-data/public-ipv4 || true)
    if [[ "$PUBLIC_IP" =~ ^[0-9.]+$ ]]; then
        set_config Nadlan:Auth:PublicBaseUrl "http://$PUBLIC_IP" "http://$PUBLIC_IP (no domain yet)"
    else
        warn "Could not find this server's public IP. Set it before using password links: 7-server-admin.ps1 > Nadlan:Auth:PublicBaseUrl"
    fi
fi
if [[ -n "$MAPS_KEY" ]]; then set_config Nadlan:Maps:GoogleApiKey "$MAPS_KEY" "(set)"; fi
if [[ -n "$STORAGE_BUCKET" ]]; then
    set_config Nadlan:Storage:Bucket "$STORAGE_BUCKET" "$STORAGE_BUCKET"
    set_config Nadlan:Storage:RootFolder "${STORAGE_ROOT:---empty}" "${STORAGE_ROOT:-(bucket root)}"
    if [[ -n "$STORAGE_REGION" ]]; then set_config Nadlan:Storage:Region "$STORAGE_REGION" "$STORAGE_REGION"; fi
    set_config Nadlan:Storage:AwsProfile --empty "(empty: the EC2 instance role)"
elif run_dbtool "$CURRENT_LINK" config show ms:host | grep -q '"RootFolder": "nadlan/dev"'; then
    # No bucket yet: still move off the developer default, so a bucket set later gets production's own folder.
    set_config Nadlan:Storage:RootFolder nadlan/prod "nadlan/prod (set Nadlan:Storage:Bucket later)"
    set_config Nadlan:Storage:AwsProfile --empty "(empty: the EC2 instance role)"
fi
systemctl restart "$SERVICE"
wait_healthy "" 90

echo
echo "Installed. Open: ${URL:-http://<server address>}"
echo "Server tools:  sudo /opt/nadlan/current/server/status.sh   |   sudo nadlan-db ...   |   journalctl -u nadlan -f"
