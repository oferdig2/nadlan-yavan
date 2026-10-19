# Release notes: 2026-10-19 (GreekPlot) - customer remarks of 19 Oct, items 1-3

For the agent who deploys this release and then tells the customer what to look at.

- **Code:** `main` with the commit "Assets hover with price, card fixes, paste a Google Maps link" or later.
- **Deploy:** software only. No migration, no new settings, no data change.
- **Tests:** builds clean. A headless-browser run with Google Maps simulated passed all 38 checks on a test database
  (2026-10-19). The server side of the link feature was also checked against Google itself: an unknown short link gets
  a clear message.
- **Not tried yet:** a real `maps.app.goo.gl` share link from the customer's phone, real Google Maps on screen, and a
  real tablet.
- **The server must reach Google:** it opens short share links itself, so it needs outbound HTTPS to `maps.app.goo.gl`
  and `google.com`. EC2 allows outbound traffic by default.

## What changed

| Item | Where | Notes |
|---|---|---|
| **1. Hover in the Assets view** | Assets view: mouse over a parcel | A dark card next to the cursor, like the one in the Parcels view. It shows the **asking price** in large type ("Price hidden" if the user may not see it, "No price yet" if none was set). Below it: the status (with its colour), the property type and the managing contact. Last line: the KAEK and the area. A parcel with several Assets lists up to 4, then "+N more - click for all". |
| **2a. No "#13" on the card** | Parcel card, Assets list | The Asset number is no longer shown in the line. It is still there as the row's mouse-over tooltip ("Asset #13"), for support. |
| **2b. "Edit" (parcel) only in the Parcels view** | Parcel card header | In the Assets view the card no longer offers the parcel's **Edit**, which was confusing there. History, Expand, Files and the Assets' own Edit buttons stay. Switching back to Parcels brings it back, even on a card that is already open. |
| **3. Paste a Google Maps link** | Map, bottom right: the **🔗** button, next to "where am I" | Click 🔗, paste, press **Enter**. A red pin drops at the location and the map zooms in on it (zoom 18). The status line then names the parcel the pin is in, and that parcel's card opens. If the pin is outside every parcel or where parcels overlap, the status line says so. **×** removes the pin and **Esc** closes the box. |

### What can be pasted (item 3)
- The **share link** from the Google Maps app (Share → Copy link, `https://maps.app.goo.gl/…`). The server opens it, because
  the browser can't (Google doesn't allow it).
- Any full Google Maps address from the browser bar: a place (its exact pin is used), a search, or directions to coordinates.
- Plain coordinates, `38.6213, 23.2801`, or `geo:38.6213,23.2801`.
- An address that only shows a map **view** (`…/@38.62,23.28,17z`) has no pin. The pin then goes to the view's centre,
  and the status line says it is only "the pasted map view's centre".
- Only Google links are opened by the server; any other site is refused.

## What to tell the customer
Items 1-3 of the 19 Oct remarks are live. To try the link: in Google Maps on the phone, long-press a spot or open a
place, then Share → Copy link. On the GreekPlot map, click 🔗, paste and press Enter. Items 4 (drone/phone photos
placed by their GPS) and 5 (drone mission export) are still for discussion, as he asked.
