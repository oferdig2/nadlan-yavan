# Nadlan BeYavan

Map-first GIS real-estate platform (Parcels → Assets → Portfolios). ASP.NET Core 9 + MySQL + jQuery + Google Maps, files on S3.

## Prerequisites

- .NET 9 SDK, MySQL 8+ (local), Google Maps JavaScript API key
- For files: AWS CLI + an S3 bucket (see `code/aws/setup-s3.ps1`)

## Run (from `code/`)

```powershell
.\start-nadlan.ps1                     # asks for the MySQL password once per window, updates the schema, runs on http://localhost:5515
.\config.ps1 set ms:host Nadlan:Maps:GoogleApiKey <key>      # then restart
```

| Script | Purpose |
|---|---|
| `update-db.ps1` | Create/upgrade the `nadlanyavan` schema from `src/Nadlan.Persistence.MySql/Sql/NNN_*.sql` (`-Status`, `-Reset` for dev) |
| `config.ps1` | Read/write `app_config` (`list`, `show ms:host`, `set ms:host <path> <value>`) |
| `load-demo-parcels.ps1` | Load legacy Airtable polygons as demo Parcels (needs the legacy CSV, not in the repo) |
| `aws/setup-s3.ps1` | One-time bucket + CORS + lifecycle + least-privilege IAM user (run with an admin AWS profile) |

> **Local / private network only for now:** there is no login yet (auth is a later slice), so every API is open.

## Configuration

DB-first, like Futuristic SaaS: the MySQL password is the only bootstrap secret (`NADLAN_MYSQL_CS`, built by the scripts).
Everything else lives in the `app_config` table (seeded from `appsettings.json`; new settings are added, DB values never overwritten). AWS credentials are never stored:
an AWS profile on dev machines, an IAM role on EC2/Fargate. File storage setup, moving buckets and CloudFront: `code/aws/STORAGE.md`.

## Layout

```
code/src/Nadlan.Core               domain, services, rules (no I/O)
code/src/Nadlan.Persistence.MySql  Dapper stores + Sql/ migrations
code/src/Nadlan.Config.MySql       app_config loader
code/src/Nadlan.Storage.S3         S3 multipart + CloudFront URL signing
code/src/Nadlan.Host               web app: endpoints per feature, wwwroot/js one file per component
code/src/Nadlan.DbTool             schema/config tool used by the scripts
code/src/Nadlan.Import             legacy Airtable import
code/tests/Nadlan.Core.Tests       unit tests (dotnet test)
```
