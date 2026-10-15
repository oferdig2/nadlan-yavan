# Working rules for Claude agents in this repo

## Production server (GreekPlot, 13.48.247.154)

- Reading production is allowed without asking: SSH, logs, status, read-only database queries.
  **Always say so in your message when you do it**, as you do it.
- Changing production (data, settings, files, deploys, restarts, firewall) needs the user's explicit OK first.
- Deploys must change only the software, never the data.

## Reporting facts about the system

Every fact you report about the system must say **where it came from and when** (UTC). Examples:

- "4,450 parcels - production DB, read-only query over SSH, 2026-10-15 09:10 UTC"
- "Alon downloaded the Intel package - app log on the server (journalctl), read 2026-10-14 10:30 UTC"
- "The 3D view needs Assets - local code, js/results-panel.js"

If a number may be out of date (read earlier in the session), say when it was read.
