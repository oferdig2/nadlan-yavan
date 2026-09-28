# Nadlan KAEK Importer: install packages

Build every package from Windows:

```powershell
cd code\installer
.\Publish-KaekImporter.ps1 -ApiUrl https://nadlan.example.com -Version 1.0.1
```

The packages are written to `code\dist` (git-ignored). Each one carries `importer.json` with `-ApiUrl`, so its users reach the right Nadlan server with no setup. The packages are self-contained: customers don't need .NET installed.

| Package | For | Customer steps |
|---|---|---|
| `NadlanKaekImporter-<v>-windows.zip` | Windows 10/11, 64-bit | Extract, then run `Install.cmd` (per user, no admin rights). It adds Start Menu and desktop shortcuts and an entry in Settings > Apps for uninstalling. |
| `NadlanKaekImporter-<v>-mac-apple-silicon.tar.gz` | M1–M4 Macs, macOS 13+ | Unpack, drag the app to Applications, and approve it once in Privacy & Security. |
| `NadlanKaekImporter-<v>-mac-intel.tar.gz` | Intel Macs, macOS 13+ | Same as Apple Silicon. |

Each package includes `INSTALL.txt` with these steps for the end user.

## How it runs on the customer machine

- **Browser:** the importer uses the installed Edge (Windows) or Chrome (Mac). It downloads Playwright's Chromium (~150 MB, once) only if neither is found. Override with `"browser": "chromium"` in `importer.json`, or with `--browser`.
- **Connecting to Nadlan:** the first time, the panel shows **Connect to Nadlan**. It opens Nadlan in a new tab of the importer's browser; sign in with email and password (Google usually refuses automated browsers) and click **Connect**. The tab closes by itself and the token is saved in the user's `importer.json`, so later starts are connected at once. The user must be allowed to create Parcels. If the token is revoked, the running import stops and the panel asks to connect again. (A token made by an Admin, Admin → Users → API tokens, can still be put in `importer.json` as `token` or passed with `--token`.)
- **Settings:** `importer.json` next to the program (from the package) is read first. The user's own `importer.json` then takes priority: `%LOCALAPPDATA%\Nadlan\` on Windows, `~/Library/Application Support/Nadlan/` on Mac. Command-line options override both. Supported keys: `apiUrl`, `token`, `delay`, `missDelay`, `maxViewMetres`, `browser`, `offline`.
- **Offline (demo) mode:** if the Nadlan server can't be reached within 8 seconds, or with `--offline` / `"offline": true`, the importer runs the whole process but saves nothing. The panel says so, found parcels are drawn in purple, and the CSV report lists them. The server can be given with `--api <url>` (default `http://localhost:5515`) or `apiUrl` in `importer.json`.
- **Logs:** daily files in the same user folder under `logs/`. A startup failure (e.g. a bad settings file, or no browser could be started) is shown in a dialog on Mac, or in the console on Windows.

## Why the warnings, and how to remove them

The packages aren't code-signed yet, so both systems warn once:

- **Windows (SmartScreen):** "Windows protected your PC", then *More info > Run anyway*. To remove it, sign the program with a code-signing certificate (an OV/EV certificate from a CA, or Azure Trusted Signing). Then the packaging script needs a `signtool sign` step.
- **Mac (Gatekeeper):** the app has to be approved in *Privacy & Security* the first time. Apple Silicon also refuses unsigned programs outright. That's why the app's launcher (`mac/nadlan-launcher`) gives the program Apple's free ad-hoc signature on first start. To remove the prompt entirely, sign it with an Apple **Developer ID** ($99/year Apple Developer Program) and **notarize** it. Both can be done from Windows with [rcodesign](https://github.com/indygreg/apple-platform-rs), so no Mac is needed to build.

The Mac packages are `.tar.gz`, not `.zip`. A zip made on Windows drops the Unix "executable" permission, and without it macOS won't start the app. Windows' own `tar.exe` can set the permission explicitly.
