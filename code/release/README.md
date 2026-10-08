# Nadlan release scripts

Everything runs from a Windows PC in PowerShell (5.1 or 7). Start the menu, which lists these scripts and asks for what each one needs:

```powershell
cd code\release
.\run.ps1
```

Each script also runs on its own (`.\4-install-server.ps1`). Your answers (server address, key path, bucket, AWS profile per task…) are suggested the next time. They're kept in `release.local.json` (git-ignored). Passwords and secrets are never stored.

| Script | What it does |
|---|---|
| `1-build-importer.ps1` | Builds the KAEK polygon importer for Windows, Mac Apple Silicon and Mac Intel (self-contained), with checksums and an admin README, into `code\dist` |
| `2-upload-importer-s3.ps1` | Asks which AWS identity to use and uploads a built version into the server's storage folder (`<root>/_downloads/kaek-importer`, private). It then shows in GreekPlot under Admin → Downloads; the script also prints download links valid up to 7 days |
| `3-create-ec2-server.ps1` | Optional. Creates the EC2 instance (t3.micro, Amazon Linux 2023) with a key pair, firewall, S3 role and Elastic IP |
| `4-install-server.ps1` | Installs everything on the server: MySQL, nginx, HTTPS, the app, backups. Safe to re-run |
| `5-copy-local-db.ps1` | Replaces the server's data with your local database, keeping the server's settings. Can also copy the S3 files |
| `6-update-server.ps1` | Deploys the current code: backup, migrate, switch, health check, automatic rollback |
| `7-server-admin.ps1` | Status, logs, settings (Google sign-in, SMTP, Maps), passwords, backups and restore, release rollback, shell |
| `8-set-keys.ps1` | Shows which outside keys the server has (Google Maps, Google sign-in, SMTP), asks for the missing ones or replaces them (rotation), restarts the app |
| `9-admin-passwords.ps1` | Lists the server's Admins and sets their sign-in passwords (hidden, typed twice) |
| `10-mysql-admin-user.ps1` | Creates the MySQL user nadlan_admin (full rights) for MySQL Workbench over SSH, or changes its password; prints the Workbench settings |

**Needs on the PC:** .NET 9 SDK, Git, the Windows OpenSSH client (built in), and AWS CLI v2 for scripts 2, 3 and 5. Script 5 also needs local MySQL (`mysqldump`). Script 1 additionally relies on `code\installer` (see its README).

## First production install

1. `3-create-ec2-server.ps1`, or use an existing Amazon Linux 2023 x86_64 instance with ports 22, 80 and 443 open.
2. DNS: point an A record (e.g. `nadlan.example.com`) at the server's IP.
3. `4-install-server.ps1`: give the domain, the Maps key, the S3 bucket and folder, and the Admin's password.
4. `5-copy-local-db.ps1` if you want your local data there. The server starts with an empty database: reference data and the Admin only, no legacy parcels.
5. `7-server-admin.ps1` → *Change a setting* for Google sign-in (`Nadlan:Auth:Google:*`; redirect URI `https://<domain>/signin-google`) and SMTP.
6. `1-build-importer.ps1` with the address `https://<domain>`, then `2-upload-importer-s3.ps1`.

## How production runs

- **Binaries only.** The package is `dotnet publish -r linux-x64 --self-contained` of the app and the DB tool. The server has no source code, no SDK, and no .NET install, and it compiles nothing. Its scripts and configs come from `release\server` and travel inside every package, so an update also updates them.
- **One input from the environment:** `NADLAN_MYSQL_CS` in `/etc/nadlan/nadlan.env` (root-only), read by systemd. Every other setting is in the `app_config` table (`sudo nadlan-db config ...`, or script 7). Runtime tuning (GC limits, production environment) is fixed in `nadlan.service`.
- **Layout:** `/opt/nadlan/releases/<release>/` (the last 3 are kept) and the `/opt/nadlan/current` symlink. The release id is `<UTC time>-<git commit>` and `/api/health` reports it.
- **Memory (t3.micro, 1 GB RAM + 1 GB swap):** MySQL about 250–300 MB (128 MB buffer pool, performance schema off, 40 connections; see `server/mysql-nadlan.cnf`). The app's managed heap is capped at 200 MB and its systemd memory limit is 450 MB. Nginx uses a few MB.
- **Database:** MySQL 8.4 LTS listens on 127.0.0.1 only. The app user is `nadlan@127.0.0.1` with rights on `nadlanyavan` only, and the root password is in `/root/.my.cnf`. The schema changes only through the DB tool (`migrate`), during install, update and restore.
- **Backups:** daily at 02:30 UTC and before every update or restore, in `/var/backups/nadlan` (the last 14 days, never fewer than 5 files). Each dump is also copied off the server to `s3://<bucket>/<root>/_backups/db/` (kept 35 days; with bucket versioning, `aws/setup-s3.ps1`, a deleted copy stays recoverable 30 days more). `3-create-ec2-server.ps1` keeps the disk if the instance is terminated and takes a daily EBS snapshot (last 7 kept).
- **Dropped connection:** install, update, restore and rollback run on the server as their own systemd unit, so they finish (or roll back) even if your SSH connection drops; the script reconnects and keeps showing the output. Their logs are in `/var/lib/nadlan-ops` for 30 days.
- **HTTPS:** Let's Encrypt via certbot, with automatic renewal (`certbot-renew.timer`). The app trusts nginx's `X-Forwarded-*` headers, so cookies are `Secure` and Google sign-in builds `https` links.
- **Files:** the browser uploads straight to S3. The server reaches S3 through its EC2 instance role, so no keys are stored on it.
- **Admin page → Server, Web files, Downloads, Settings** (only oferdig2@gmail.com and alon.schwarz@gmail.com, fixed in `MachineAdminAccess.cs`; everyone else gets 404):
  - *Server:* CPU (with t3 "steal"), memory, swap, disk and load, and per service (app, MySQL, nginx) CPU and memory for the last hour, MySQL internals, and **Restart** buttons. The app can't run `systemctl` (no sudo), so it drops a request file into `/var/lib/nadlan-control/requests`; the root unit `nadlan-control.path` runs `/usr/local/sbin/nadlan-control` (`server/control.sh`), which acts only on the names `restart-nadlan`, `restart-mysqld` and `restart-nginx`. `deploy.sh` installs it with every update.
  - *Web files:* hot patches of HTML/JS/CSS (and images, fonts) without a deploy: edit in the browser or upload. A patch never touches the release (it stays read-only); it goes into `/var/lib/nadlan/hotfix/<release>/wwwroot`, served in front of the release's file, with its `.br` and `.gz` made on the server. It lasts until the next deploy, so commit the same change to git. Revert = back to the release's file.
  - *Downloads:* the KAEK importer for Windows, Mac (Apple chip) and Mac (Intel), every uploaded version, with checksums and the admin README. Each click gets a fresh 10-minute S3 link through the server's own role, so nothing expires on the page.
  - *Settings:* the `app_config` rows as a form (or raw JSON). Secrets stay on the server, and the app reads settings when it starts: use *Restart the app* after saving. A save that could stop the app is refused: numbers outside their range (e.g. session hours 1-720), removing a setting (the app would put back its development value at the next start), and in the app's own rows anything outside `Nadlan:` (Urls, Kestrel, Logging...). **Every change keeps the version before it** (also `nadlan-db config set/remove` and startup): if a save still goes wrong, `7-server-admin.ps1` → *Undo the last settings change* (on the server: `sudo nadlan-db config undo ms:host` - each undo one more step back -, `config history ms:host`, `config restore ms:host <id>`), then restart. Server tasks (deploy, restore, backup, Restart buttons) take one shared lock; the backup target is pinned in `/etc/nadlan/backup-target.env` (`backup.sh --pin-target`).
- **Compression:** the app serves the `.br`/`.gz` copies that `dotnet publish` makes for every page, script and style (brotli at maximum level) to browsers that accept them; nginx's own gzip still covers the API's JSON.

## On the server

```bash
sudo /opt/nadlan/current/server/status.sh              # release, health, memory, disk, backups, certificate
journalctl -u nadlan -f                                # app log
sudo nadlan-db status | config show ms:host | user list
sudo /opt/nadlan/current/server/backup.sh manual
sudo /opt/nadlan/current/server/restore.sh <file.sql.gz> --yes [--keep-config]
sudo /opt/nadlan/current/server/rollback.sh [release]  # run an earlier release (database unchanged)
```
