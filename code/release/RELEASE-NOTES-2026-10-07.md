# Release notes: 2026-10-07 (GreekPlot) - Change request #1, OT / plot data entry

For the agent who deploys this release and then tells the customer what to look at.

- **Code:** `main` with the commit "Settings page can no longer take the site down" or later.
- **Tests:** builds clean, 233 automated tests pass, and the new features were checked on a test database (at production's
  connection limit of 20) and in a headless browser with Google Maps simulated.
- **A separate review checked it too:** on a copy with 4,000 parcels, an OT search went from about 1.1 s to 16 ms, five
  people saving 50-parcel rows at once finished in under 0.2 s, and the new migration applied in under 4 s.
- **Not tried yet:** real Google Maps with the new features, and a real tablet.

The customer's request is `AI/Remarks/ChangeRequest#1.pdf`. §3 maps each of his points to what was built.

**If the 2026-10-06 release is not on the server yet:**
- His screenshots still show the old "Nadlan" title and the old legend, so check `https://<domain>/api/health` first.
- If yesterday's release isn't there, this deploy brings it too. Also do the steps in `RELEASE-NOTES-2026-10-06.md` §2:
  the Map Tiles API, a password for the second Admin, and HTTPS.
- Its "colour codes" part is replaced by today's colours (§1 below).

---

## 1. What changed today

| Feature | Where | Notes |
|---|---|---|
| **Search by OT / Plot** | Side panel, under KAEK: **OT** and **Plot** fields | Exact match: OT 4 does not find 14 or 47. "47" finds 47, 47A, 47B. "47A" finds only 47A. The match is forgiving: "047", "47 a", "47-A" and Greek "47Α" all find the same parcels. It works with "Search in" (visible map by default, drawn rectangle, everywhere) and with the area boxes. Works in the Assets view too. |
| **New map colours = OT / plot status** | Map legend, "OT / plot entry" | **Green** = OT and plot entered. **Amber** = only one of them. **Blue** = still to do. Each row shows **how many** parcels match the current search, even when unticked. Ticking or unticking a row filters the map and the list. The **purple outline** still means "has an Asset". |
| **Real / provisional KAEK filter** | Side panel, "KAEK" boxes (Parcels view) | This was a colour until today and is now a filter. Tick one box to see only real or only provisional (TMP-) KAEKs. Both or neither ticked shows all parcels. |
| **Hover card** | Mouse over any parcel | A dark card next to the cursor: OT / Plot in large type (or "No OT / plot yet"), then the KAEK, area, size and "Has Asset". |
| **Quick OT/plot entry** | Header checkbox **Quick OT/plot entry** (Parcels view; Admin and Data person) | Click a parcel and a small form opens with the cursor in OT, so the user can type at once. **Enter** in OT moves to Plot, **Enter** in Plot saves, **Esc** closes, **Card** opens the normal card. The form sits over the side panel, so it never covers the parcels; if the user drags it elsewhere, it opens there from then on (per browser). After a save the parcel changes colour **in place**: no map reload, so the next parcel can be clicked at once. The legend counts and the list follow. |
| **OT digits** | Box next to the checkbox (appears when the mode is on) | Optional, remembered per browser. If every OT has, say, 3 digits, the cursor jumps to Plot after the 3rd digit, so a whole sheet can be typed on the numeric keypad. It only jumps when the OT is typed from empty, never while correcting one. A letter typed right after the jump still goes to the OT (171, a → 171a); the first digit starts the plot. Leave it empty to switch the jump off. |
| **Group entry (a row of plots)** | In quick entry: **Ctrl+click** (⌘+click on Mac), or the **Row** button in the quick form (also on tablets) | While a row is open, **every click** (or tap) on a parcel adds it, and clicking it again takes it out; there is no need to keep holding Ctrl. The click order is the numbering order. One form: "OT for all of them" plus each row's plot, counting up from the first (1, 2, 3...). Type over a row to skip a number (4 → 6) or to split (4a, 4b); the next rows continue from it. A row whose plot is cleared keeps its plot as it is, and the counting goes on past it. The planned "OT / plot" shows **inside each polygon** before saving. Plots that already have different numbers are flagged in amber. **Enter** or **Save all** saves everything at once (up to 50 parcels); **Esc** or **Cancel** ends the row. |
| **Who entered OT / plot** | Parcel card, under "OT / Plot": "by *name*, *date*" | Stored on each parcel and updated by every save (quick entry, group entry, edit form, new parcel). The parcel's History shows old → new values, e.g. "OT/plot set to 171a / 3 (was 12A / 5C)". Re-saving the same number (e.g. an old "47A" saved as 47 + A) credits nobody. |
| **Per-person history (Admins)** | API only for now: `GET /api/activity/by-user?userId=<id>&entityType=Parcel&from=2026-10-07&to=2026-10-08` | Everything one user did in a period, newest first, with each parcel's KAEK. There is no screen for it yet (see §5). |
| **Number rule** | Every save of OT / plot | A new or changed OT or plot must be a number (up to 12 digits) with at most 3 letters: 47, 47A, 171α. Typing "47A" in one field is stored as 47 + A. Keypad slips such as "47+", "47.", "47/3" or a lone "-" are refused with a clear message, so nothing half-typed turns a parcel green. Odd values already in the data don't block other edits. |
| **Divided / united parcels** (added 2026-10-08) | Parcel edit form: **Parcel** = Regular / Divided / United; the parcel card; the hover card | Every parcel starts as Regular. For a divided or united parcel a box **Other OT / plot numbers** appears, for free text such as "171a/3 and 171a/4". The card shows "Parcel: Divided – other: …", the hover card shows a "Divided" / "United" tag, and the history records the change. V1 doesn't search this text; it may become structured, and searchable, later. |
| **Clearer selected parcel** (2026-10-08) | Map, both views | The selected parcel now has a bright yellow outline over a dark halo, all the way round and above its neighbours, visible on any colour and on the satellite photo. Parcels picked for a row are outlined in white. Before, it was a thin black line partly hidden by the neighbouring parcels. |

### Behind the scenes
- **Group saves are all-or-nothing:**
  - one database connection and one transaction;
  - if one parcel was changed by someone else meanwhile, nothing is saved, the current values are shown, and the user saves
    again;
  - the quick single save uses the same route.
- **Faster map:** the map makes one database query fewer per refresh, and OT / plot searches use an index.

### Database
- **New migrations, applied by the deploy automatically:**
  - `012_ot_plot_entry` adds who/when columns, search keys and indexes to `parcel`, and a (user, time) index to the
    history. It rebuilds the parcel table: a few seconds at 4,000 parcels.
  - `013_ot_plot_key_digit_groups` keeps "47/3" apart from "473" in search.
  - `014_parcel_division` adds the Regular / Divided / United status (every existing parcel: Regular) and the free-text
    "other OT / plot numbers".
  - `015_app_config_history` keeps the previous version of the settings at every change (see "Settings safety" below).
- No new settings, keys or permissions.

---

## 2. How to deploy

The same as last time, from a Windows PC in `code\release`:

1. **Deploy:** `6-update-server.ps1`. It backs up, migrates (012-015 above), switches release, runs a health check,
   and rolls back automatically on failure. Check that `https://<domain>/api/health` reports the new release id
   (`<UTC time>-<commit>`).
2. **Nothing else is required for today's features.** If yesterday's release wasn't deployed, follow its §2 as well (see
   the top of this file).

Note: the admin pages Server, Web files, Downloads and Settings (for Ofer and Alon only) were built in another session and
are described in `README.md`, not here. Use what is committed on `main`.

### Settings safety (review of 2026-10-08)
- **What the Settings page refuses**, so a save can no longer take the site down at the next restart:
  - numbers outside their range (e.g. session hours 1–720, link minutes 1–10,080, zoom 1–21);
  - removing a setting, because at the next start the app would put back its development value, e.g. the development
    storage folder;
  - adding or changing anything outside `Nadlan:` in the app's own settings (Urls, Kestrel, Logging…).
- **The same ranges apply in `7-server-admin.ps1` → *Change a setting*.**
- **Every change keeps the version before it.** If a change still goes wrong, even when the site is down:
  `7-server-admin.ps1` → *Undo the last settings change* (or on the server `sudo nadlan-db config undo ms:host`), then
  restart. *Settings history* lists the kept versions; `sudo nadlan-db config restore ms:host <id>` picks one.

### Smoke test after deploying (10 minutes, as Admin)
- [ ] The legend says "OT / plot entry" with green / amber / blue and a count on each row. Unticking green empties the
  list of green parcels while green still shows its count.
- [ ] Hovering a parcel shows the dark card with OT / Plot.
- [ ] Side panel: type an OT you know (e.g. one from the customer's screenshot, 171) → **Apply** → only those parcels are
  listed. Try "0171" too: the same result.
- [ ] Tick **Quick OT/plot entry**, click a blue parcel, type an OT, **Enter**, a plot, **Enter**:
  - [ ] the form opened over the side panel, not over the map;
  - [ ] the status line says "... saved";
  - [ ] the parcel turns green at once, without the map reloading;
  - [ ] its card shows "by <your name>, <date>".
- [ ] Ctrl+click one parcel, then plain-click two neighbours (or click one, press **Row**, then click two more) → type an
  OT, **Enter**, "1":
  - [ ] each polygon shows its position (1, 2, 3) and the planned "<OT> / 1", "<OT> / 2", "<OT> / 3";
  - [ ] **Enter** saves all three.
  - Then put back the real values, or use test parcels.
- [ ] Type "47+" as a plot in the quick form → it is refused with the message about numbers and letters.
- [ ] Click a parcel (quick entry off) → it has a yellow outline all the way round. Open its card → **Edit** → set
  **Parcel** to Divided, type "test 1/2" in **Other OT / plot numbers** → **Save parcel** → the card shows "Divided –
  other: test 1/2" and so does the hover card. Set it back to Regular.

---

## 3. What to tell the customer: his requests and how we answered them

Suggested points for the message; adapt the tone. His numbering is from `ChangeRequest#1.pdf`.

**1–2. "Make OT/Plot input as efficient as possible – a special mode, click a plot, a window just for these two fields,
typed on a numeric keypad."**
Done: the **Quick OT/plot entry** checkbox at the top of the map.
- Click a parcel, type the OT, **Enter**, the plot, **Enter**: all on the keypad, no mouse between parcels. The keypad's
  Enter key works too.
- The parcel turns green right away and the map stays where it is, so he can click the next one immediately.
- The small form sits at the side, so it never hides the parcels. He can drag it anywhere he prefers, and it stays there.
- If all OTs on a sheet have the same length, put that number in **OT digits** and the cursor jumps to Plot by itself. An
  OT letter typed right after the jump (171, a) still lands in the OT.
- We used a normal click inside the mode instead of a right-click; it does the same job.

**3. "Hovering over a plot can give a shortened display of OT/Plot."**
Done, for every parcel: a small card next to the mouse with OT / Plot in large type, plus the KAEK, area and size.

**4. "Click two plots at the ends of a line, enter the first and last numbers, the system fills the middle (+1 each)."**
We built this a bit differently, and more safely. In the quick mode, **Ctrl+click** (⌘ on a Mac) the first plot of the
line, or press **Row** in the small form; then simply click the other plots in order. On a tablet the Row button and taps
do the same.
- He chooses exactly which plots are in the line, so the system never guesses wrongly around bends, missing plots or
  merged plots.
- He types the OT once and the first plot number; the others count up by themselves.
- The planned numbers appear **inside the polygons** before he saves, so mistakes are seen before they happen.
- Where a number is skipped or a plot is split (4a, 4b), he types over that one row and the rest follow on.
- Up to 50 plots at once; the save is all-or-nothing.

**5. "Plots that have OT/Plot shown green, blue ones still need to be fed."**
Done as he asked.
- **Green** = OT and plot entered, **blue** = still to do. **Amber** is our addition, for parcels that have only one of
  the two.
- Each colour in the legend shows a **count** (e.g. "312 still to do" in the current area), and its checkbox shows only
  those parcels: data workers can tick only blue and work through them.
- The purple outline still marks parcels with a listed property.
- Real vs provisional KAEK, the old colours, is now a filter in the side panel.
- He offered "numbers always written in the polygon" as an alternative. We went with the colours plus the hover card; the
  numbers inside the polygons appear while entering a row.

**6. "Keep in the database who entered/edited the OT/Plot, to check the data-entry people."**
Done.
- Every parcel stores who last entered or changed its OT / plot, and when, shown on its card.
- The parcel's History lists each change with the old and new values and the person.
- The system can already list everything one person did in a day. A screen for this is the next step; until then we can
  run it for him.

**Search by OT / Plot (page 2): "super important; together with an indicated area; if no area, the visible map."**
Done, exactly so.
- OT and Plot fields in the side panel, each usable alone or together.
- Combined with the drawn rectangle or the area boxes. With no area chosen it searches the visible map (that is the
  default), or "Everywhere" if chosen.
- It tolerates how numbers are typed: leading zeros, Greek or Latin letters, spaces or dashes.

**Also new: divided and united parcels.**
- In a parcel's edit form, **Parcel** can be set to Regular (the default), Divided or United.
- For divided and united parcels he notes the other OT / plot numbers in his own words, e.g. "171a/3 and 171a/4". They
  show on the parcel card and when hovering.
- For now this text is for reading, not for searching; we can make it searchable later.

**Also improved: the selected parcel** now stands out with a yellow outline all the way round, on any colour and on the
satellite photo.

### Set expectations
- **Who can use it:** quick entry and group entry are for users who may edit all parcels (Admin and **Data person**). Give
  data-entry people the Data person role (Admin → Users).
- **Tablets:** both quick entry and rows work by touch (rows via the **Row** button). Typing is easiest with a keyboard or
  keypad.
- **While a row is open,** every click adds or removes a parcel. To go back to single parcels, save the row or press
  **Esc** / **Cancel**.
- **What a valid number is:** a number with up to 3 letters (47, 47A). Entries like "47/3" or "Α12" are refused. Tell us
  if real OTs ever look like that.
- **"By whom" starts with this release:** OT / plot entered earlier, or imported, shows no name.
- **After saving,** the map is updated in place. A parcel saved while a search filter is active (e.g. OT 10) stays on the
  map until the next search or pan, even if it no longer matches.

---

## 4. Implementation notes (for anyone checking the code)

- **Server:**
  - `POST /api/parcels/numbers`: `ParcelService.SetNumbersAsync` → `MySqlParcelStore.SetNumbersAsync`, with per-parcel edit
    locks on one connection, then one transaction (version check, update of the 4 number columns + who/when, history
    rows).
  - Search keys: `ParcelNumberKey` (C#) mirrors the generated columns `ot_key/ot_base/plot_key/plot_base`
    (migrations 012, 013); the two must stay identical.
  - Colours: `ParcelKinds` (done / partial / todo) by those keys; the per-colour counts come back as `kindCounts` from
    `/api/parcels` and `/api/parcels/count`.
- **Browser:**
  - `parcel-quick-entry.js`: single form and group entry.
  - `parcel-map-overlay.js`: colours, counts, hover card, labels in the polygons.
  - `map-filter-panel.js`: the OT / Plot / KAEK filters.
  - `map-entity-popup.js`: the "by" line on the card.

## 5. Still open (not for the customer, for planning)
- **A screen for the per-person history:** the API exists, there is no page yet.
- **The full edit form under heavy load:** the reviewer saw a ~20 s stall with 20 map loads plus 10 edit-form saves at
  once (an older issue). Quick entry no longer uses that path. It could not be reproduced on our test copy.
- **Small:** "0" counts as an entered number.
