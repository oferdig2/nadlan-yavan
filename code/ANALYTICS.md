# In-house analytics (migration 026)

A generic event module (owner, 2026-11-09 / 2026-11-10): one row per event with its own JSON properties, the visit's
context once per session. **Recorded now:** anonymous visitors of public presentation links (`show.html#token`) only.
Signed-in staff are not recorded. Kept forever (no clean-up job). No notice on the public page yet.

| Piece | Where |
|---|---|
| Tables | `app_event`, `app_event_context`, `app_event_batch` + permission `VIEW_ANALYTICS` (Admin, Data person) - `Sql/026_app_events.sql` |
| Rules (validation, clock, properties, reports, timeline) | `Nadlan.Core/Analytics/` |
| Store | `Nadlan.Persistence.MySql/Analytics/MySqlEventStore.cs` |
| Endpoints, geo, user agent, daily job | `Nadlan.Host/Analytics/` |
| Page tracker | `wwwroot/js/events.js` (loaded by `show.html` only) |
| Reports UI | `wwwroot/js/analytics-report.js` + `css/analytics.css` (the Present dialog's public-link list, map page) |

## Endpoints

- `POST /api/present-link/events` - the page's batches. It sits in the public link's own group
  (`PresentationLinkEndpoints`, one line), so the `X-Present-Link` token, the password cookie and the "link isn't active"
  answers are exactly the link's: unknown / disabled / expired = 404, password link without its cookie = 401. The link id
  comes from the token, never from the body; a session id first seen on another link is refused (400). Requires
  `X-Nadlan-Request: 1` like every write. At most 64 KB, 200 events, 60 batches per minute per IP and per visitor (429).
  A bad event (name, time, subject, properties over 2 KB or deeper than 3) is dropped, the rest kept.
- `GET /api/analytics/links?ids=1&ids=2` - the line under each link; `GET /api/analytics/links/{id}` - visits (no IP) and
  per-property numbers; `GET /api/analytics/visits/{id}` - one visit with its IP, context and timeline. `VIEW_ANALYTICS`.

## The IP and its place (MaxMind GeoLite2)

The IP is the client's real one: behind nginx the app reads `X-Forwarded-For` (`Nadlan:Auth:TrustForwardedHeaders` = true,
already so on the server). The place and network come from two local files - no call to anyone per visit:

| Setting (Admin > Settings > "Visitor places (analytics)") | File |
|---|---|
| `Nadlan:Geo:CityDbPath` | `GeoLite2-City.mmdb` (country, region, city, approximate lat/lng, accuracy) |
| `Nadlan:Geo:AsnDbPath` | `GeoLite2-ASN.mmdb` (network number and organisation, e.g. "OTEnet S.A.") |

What the app does with them:
- Empty setting or missing / unreadable file: the IP is stored, the place left empty, the request still succeeds; one
  warning in the log. A daily job (`GeoBackfill`, first run 3 minutes after start) fills the place in once the files exist.
- The files are read into memory (City ~50 MB + ASN ~9 MB of RAM), so they can be replaced while the app runs. The app
  looks at each file at most once a minute; a newer one (other time or size) is opened, the old one released 30 s later.
  A half-copied file fails to open and the old one stays in use until the next try.
- The settings are read at startup: after setting the two paths, restart the app.
- Attribution ("This product includes GeoLite2 data created by MaxMind") is in the report dialog's footer.

### Server steps (owner / reviewer, with the owner's OK - Amazon Linux 2023, x86_64)

The licence key stays with the owner; it goes only into `/etc/GeoIP.conf` on the server, never into the repo or app_config.

1. Install `geoipupdate` (not in the AL2023 repos): the x86_64 `.rpm` of the current release from
   https://github.com/maxmind/geoipupdate/releases, e.g. `sudo dnf install -y <url of geoipupdate_X.Y.Z_linux_amd64.rpm>`.
2. `/etc/GeoIP.conf` (owner fills the key), then `sudo chmod 600 /etc/GeoIP.conf`:
   ```
   AccountID 1445567
   LicenseKey <owner's GeoLite2 licence key>
   EditionIDs GeoLite2-City GeoLite2-ASN
   DatabaseDirectory /var/lib/GeoIP
   ```
3. First download: `sudo mkdir -p /var/lib/GeoIP && sudo geoipupdate -v`, then `ls -l /var/lib/GeoIP` - the two `.mmdb`
   files must be readable by the `nadlan` user (with the default umask they are 0644; geoipupdate replaces a file by renaming a new one
   over it, so the app never sees half a file).
4. Weekly update (MaxMind publishes on Tuesdays and Fridays; AL2023 has no cron) - a systemd timer:
   ```
   # /etc/systemd/system/geoipupdate.service
   [Unit]
   Description=Update MaxMind GeoLite2 databases
   [Service]
   Type=oneshot
   ExecStart=/usr/bin/geoipupdate

   # /etc/systemd/system/geoipupdate.timer
   [Unit]
   Description=Weekly GeoLite2 update
   [Timer]
   OnCalendar=Wed *-*-* 04:00:00
   RandomizedDelaySec=1h
   Persistent=true
   [Install]
   WantedBy=timers.target
   ```
   `sudo systemctl daemon-reload && sudo systemctl enable --now geoipupdate.timer`
5. Admin > Settings > "Visitor places (analytics)": `/var/lib/GeoIP/GeoLite2-City.mmdb` and
   `/var/lib/GeoIP/GeoLite2-ASN.mmdb`; restart the app. The log then says "GeoLite2 City database opened" at the first visit.

## Tests

`AnalyticsRulesTests` (no database; MaxMind's tiny test databases in `tests/.../TestData/GeoIP`, MIT / Apache-2.0) and
`AnalyticsEndpointTests` (the whole app in-process on a throwaway schema; needs `NADLAN_TEST_MYSQL`).
