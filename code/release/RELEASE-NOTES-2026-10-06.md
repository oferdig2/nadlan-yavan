# Release notes: 2026-10-06 (GreekPlot)

For the agent who deploys this release and then tells the customer what to look at.
Code: `main` at `ca216f1` or later. Builds clean, 175 automated tests pass, and the new features were checked on a test
database and in a headless browser. The real Google 3D imagery and a real phone have **not** been tried yet.

---

## 1. What changed today

### New for users

| Feature | Where | Notes |
|---|---|---|
| **Parcel colour codes** | Main map, Parcels view | Fill colour shows how the parcel is identified. **Blue** = real KAEK. **Orange** = provisional, OT (block) known. **Red** = provisional, no OT. A **purple outline** = the parcel has an Asset the viewer may see (a competitor's hidden Asset never shows). |
| **Filter by colour** | Map legend (bottom left) | Each legend row has a checkbox, plus *Has Asset(s)* and *No Asset*. Unticking one re-runs the search. The results list shows the same colour dots. |
| **3D customer presentation** | Portfolio panel → **Present**, or select Assets → **Present** | A dialog asks for an optional title the customer sees, then **Open** or **Copy link**. Anyone who can see the properties can present them; a title needs edit rights on them. The page shows Google's photorealistic 3D with parcel outlines, numbered pins, an info card with photos, a strip of all properties, a **Tour** button (flies to each property and circles it) and **Me**. |
| **Where am I** | Main map: round button above the zoom buttons. Presentation: **◎ Me** (key M) | Centres on the user's position with a blue dot. On the map it says which parcel they stand on (only if the fix is ±30 m or better; all parcels are named when they overlap). In the presentation it opens that property's card. Tracking stops by itself after 5 minutes. |
| **Brand: GreekPlot** | Everywhere users look | Page titles, sign-in page, presentation, emails, KAEK importer panel and installers. Internal names, settings and folders keep "Nadlan" on purpose. |
| **Role "Data person"** | Admin → Users → choose the role | Sees and edits all data: Parcels (polygons, KAEK, OT/plot, legal owners), Assets with any Managing Contact and prices, Portfolios, Contacts, documents in every category (uploads too), the KAEK importer, titled presentations. No administration: users and roles, lookup lists (tick *Manage lookup lists* on the role to add them), deleting Parcels and re-categorising file types stay with Admins. |

### Fixes from today's review (round 6) and yesterday's (round 5)
- **Presentation fallback:**
  - The satellite fallback no longer crashes.
  - It now also takes over when 3D loads but never draws (no graphics acceleration, Map Tiles API off, quota used up).
- **Presentation privacy:**
  - Only **Marketing** photos and videos are shown, whoever presents, so no deeds or ID scans. Captions never show file names.
  - The Portfolio's internal name and notes are never shown. The title is sealed by the server into the link: only someone who may edit the Portfolio (or every chosen Asset) can set one, and text typed into the address is ignored.
  - New uploads keep the file name out of the storage address, and buyer-facing photo links download as "photo.jpg". Files uploaded before this release still have their name in the address (S3 delivery hides it from the download name only).
- **Large portfolios:** only the first 100 properties are shown, in the Portfolio's own order, with a "first 100 of N" notice.
- **Touch stops the tour.** Shortcuts ignore Ctrl, Alt and ⌘.
- **3D fallback:** any failure on the 3D path ends in the satellite map. Only a fully drawn 3D view counts as working (waiting time counts only while the tab is visible), an error later in the meeting switches to satellite too, and a **🛰 Satellite** button in the top bar lets the presenter switch at any time.
- **"Where am I"** never uses a position older than 30 seconds, or one from before the phone was locked.
- **Map after saving an Asset:** it refreshes in Parcels view too (purple outline, filter).
- **Admin lockout prevented:** an Admin without a password no longer counts as "another Admin" while Google sign-in is off. See §2, step 4.
- **User management is Admin-only.** "Manage users" can no longer be given to another role (the role editor doesn't offer it); the admin pages, user and role history are for Admins.
- **Sign-in redirect:** sign-in can no longer be bounced to another site.
- **Numbers:** build factor, inclination and ownership % keep their decimals (`1.125` stays 1.125). Prices and areas keep the Greek rule (`250.000` = 250,000).
- **Restore script:** its rollback now really runs on failure, and a failed safety backup restarts the app instead of leaving it stopped.

### Release tooling (another session, same day)
- SSH reconnects before giving up.
- `www.` domains also serve the bare name.
- The MySQL repositories are pinned to el/9 on Amazon Linux 2023.
- New script `10-mysql-admin-user.ps1`, for MySQL Workbench access.
- Script 5 states plainly that users and keys are not copied.

### Database
New migrations: `010_data_person_role` adds the role **Data person** (see above); `011_manage_users_admin_only`
removes "Manage users" from every role but Admin (it is Admin-only now), so nobody else sees the Admin pages. Earlier ones still apply if missing: `008_file_thumbnails` and `009_second_admin`. **009 adds Admin
`alon.schwarz@gmail.com` with no password.** The deploy applies any that are missing.

---

## 2. How to deploy and switch it on

From a Windows PC, in `code\release`: `.\run.ps1`, or the scripts directly.

1. **Deploy:** `6-update-server.ps1`. It backs up, migrates, switches release, runs a health check, and rolls back automatically on failure.
   - It survives a dropped SSH connection: the script reconnects and keeps following.
   - Check: `https://<domain>/api/health` reports the new release id (`<UTC time>-<commit>`).
2. **Google Maps key:** in Google Cloud, for the key shown by `8-set-keys.ps1`:
   - Enable **Maps JavaScript API** *and* **Map Tiles API**. 3D needs Map Tiles; without it the presentation shows the satellite map.
   - If the key has API restrictions, allow both APIs.
   - Website restriction: allow `https://<domain>/*`.
3. **3D channel** (no action normally): the setting `Nadlan:Maps:Maps3dChannel` is added automatically with value `beta`. Once Google moves 3D to its stable channel, set it to `weekly` (`7-server-admin.ps1` → *Change a setting*) and restart.
4. **Second Admin (required):** run `9-admin-passwords.ps1` and give `alon.schwarz@gmail.com` a password, or configure Google sign-in.
   - Until then, the main Admin can't demote themselves or remove their own password. The app refuses with a clear message.
5. **HTTPS:** "Where am I" works only on the `https://` address; browsers refuse location on plain `http://<IP>`.
   - The server's public address (`Nadlan:Auth:PublicBaseUrl`) must be the https domain, so emailed links are right.
6. **KAEK importer (new name):** rebuild with `1-build-importer.ps1` (address `https://<domain>`), upload with `2-upload-importer-s3.ps1`, and send the new links.
   - Windows: the new installer removes the old "Nadlan KAEK Importer" by itself.
   - Mac: users delete the old app from Applications.
   - Saved connections and reports are kept.
7. **Give a buyer access to a presentation:** Admin → Users → create the buyer (role *Buyer / Customer*) → access → *View this Portfolio*. The presentation link then works for them after they sign in.

### Smoke test after deploying (10 minutes, as Admin)
- [ ] Map opens, the legend shows 3 colours plus Has Asset / No Asset, and unticking a row changes the results.
- [ ] Open a Portfolio → **Present** → type a title → **Open**:
  - [ ] the 3D view draws, or you get "3D view isn't available - showing the satellite map";
  - [ ] the title is yours;
  - [ ] the photos are marketing ones;
  - [ ] **Tour** runs, and touching the map stops it.
- [ ] Try the same link signed in as a test buyer: only that Portfolio's properties appear, with prices per the buyer's role.
- [ ] On a phone outdoors at a parcel: the map button centres on you and names the parcel.
- [ ] Page titles and the sign-in page say **GreekPlot**. A password-link email (Admin → Users) says GreekPlot.

---

## 3. What to tell the customer to look at

Suggested points for the message; adapt the tone:

1. **Colours on the map.** Blue parcels have their real KAEK. Orange ones have a temporary number but a known block (OT). Red ones have neither yet. A purple border means there is a property listed on it. Use the checkboxes in the legend to show only the colours you want, e.g. only parcels without a listing.
2. **Presenting to a buyer.** Open a Portfolio (or tick properties in the list) and press **Present**. Type the headline the buyer should see; your internal Portfolio name is never shown. **Copy link** lets you send it. The buyer needs a GreekPlot account with access to that Portfolio and signs in once.
   - In the presentation, **Tour** flies around each property. **◀ ▶** or the strip moves between them. Touch the map to look around yourself.
   - Only marketing photos and videos appear, so legal and engineering documents stay private.
3. **On site.** On a phone, the round button on the map (or **Me** in the presentation) shows where you stand and which parcel you're on. Allow location when the phone asks. Outdoors GPS is accurate to a few metres; indoors or on Wi-Fi the position is too rough to name a parcel, and the app says so.
4. **The name is now GreekPlot**, in the app, the emails and the KAEK importer. Importer users get a new download; their settings and connection are kept.

### Set expectations
- **3D detail** depends on Google's coverage. Around Skroponeria and rural Evia it may be terrain with satellite photos rather than full 3D buildings and trees. Where 3D isn't available, the presentation switches to the satellite map by itself.
- **Large portfolios:** a presentation shows up to 100 properties; split bigger portfolios.
- **Long meetings:** photo links in a presentation last a limited time. If photos stop opening after a long meeting, reload the page.
- **Number entry:** in prices and areas `250.000` means 250,000. In build factor and percentages `1.125` means 1.125.
- **Admins:** users who manage other users (but aren't Admins) can now only hand out rights they have themselves. If someone suddenly can't give a role, that's why: an Admin has to do it.

### Still open (not for the customer, for planning)
- Uploader edge cases (stalled uploads, closing the dialog mid-upload).
- The app's own AWS access can delete database backups in S3.
- Login rate limits and security headers.
- Smaller items are listed in the review notes.
