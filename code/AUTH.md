# Sign-in, users and permissions

There is **no self-registration**. An Admin creates every user (Admin → Users). A user then signs in with:

- **Google**: the Google account's (verified) email must be the user's email. This works whether or not the user also has a password.
- **Email + password**: the Admin sets one and hands it over (by default the user must change it at first sign-in), or sends a one-time "choose your password" link.

Users can change their password from the user menu (top right). A Google-only user can add a password there too.
"Forgot password" emails a reset link, but only once SMTP is configured. Until then the login page tells people to ask an Admin, who creates a link in Admin → Users → the user → **Password link…**.

The first Admin, `oferdig2@gmail.com`, is created by migration `007_auth.sql`.

## First sign-in (before Google is configured)

```powershell
.\update-db.ps1                                  # applies 007_auth (users, roles, permissions)
.\user-password.ps1 -Email oferdig2@gmail.com    # asks for a password (10+ characters)
.\start-nadlan.ps1                               # sign in at http://localhost:5515
```

`user-password.ps1` also gets an Admin back in after a lock-out. `.\user-password.ps1 -List` lists the users.

## Google sign-in: what to create in Google Cloud

1. Open <https://console.cloud.google.com/> and pick a project (the one with the Maps key is fine).
2. **APIs & Services → OAuth consent screen**:
   - Choose External (or Internal for Google Workspace).
   - Set the app name "Nadlan" and your support email.
   - Scopes: `email`, `profile`, `openid`.
   - While the app is in *Testing*, add the Google accounts that may sign in as test users. Publish it to allow anyone who has a Nadlan user.
3. **APIs & Services → Credentials → Create credentials → OAuth client ID**, type **Web application**:
   - **Authorised redirect URIs**: `http://localhost:5515/signin-google`, and later `https://<your-domain>/signin-google`.
   - **Authorised JavaScript origins**: `http://localhost:5515` (and later your domain).
4. Copy the **Client ID** and **Client secret** into app_config, then restart:

```powershell
.\config.ps1 set ms:host Nadlan:Auth:Google:ClientId <client-id>.apps.googleusercontent.com
.\config.ps1 set ms:host Nadlan:Auth:Google:ClientSecret <client-secret>
```

The "Sign in with Google" button shows only when both values are set.

## Settings (`ms:host` → `Nadlan:Auth`)

| Key | Default | Meaning |
|---|---|---|
| `Google:ClientId` / `Google:ClientSecret` | empty | Google sign-in; empty hides the button |
| `SessionHours` | 12 | Sign-in cookie lifetime (sliding) |
| `MaxFailedLogins` / `LockoutMinutes` | 5 / 15 | Password lockout |
| `ResetLinkHours` / `InviteLinkHours` | 2 / 72 | Emailed reset link / Admin-created link |
| `PublicBaseUrl` | empty | Base of links in emails (e.g. `https://nadlan.example.com`); empty = the request's host |
| `TrustForwardedHeaders` | false | Set `true` behind an ALB/CloudFront that terminates HTTPS, so Google gets the `https` redirect URI |
| `Email:SmtpHost`, `SmtpPort`, `SmtpUser`, `SmtpPassword`, `From`, `EnableSsl` | empty / 587 / … / true | SMTP for reset emails (Amazon SES SMTP works) |

## Permissions

These are enforced on the server. The UI only hides what the server would refuse anyway.

- **Admin** sees and does everything (Appendix 1 §2.1).
- **Role permissions** (Admin → Roles):
  - what a role can do in general: see all Parcels/Assets/Portfolios/Contacts, edit all, create own Assets, see prices, manage Portfolios/Contacts/users/lists;
  - which **file categories** it can see (Legal, Engineering, Marketing, Cadastral, Permission, General).
- **Own Assets:** with See/Edit own Assets, a user linked to a Contact gets the Assets that Contact manages. Agents create Assets for themselves only.
- **Grants** (Admin → Users → Access): `VIEW_`/`EDIT_` on one Asset, Parcel, Portfolio or Contact, optionally with an expiry date.
  - A Portfolio grant reaches every Asset in it.
  - Files inside a granted object still follow the user's file categories. So a lawyer with "View this Asset" sees only Legal files.
- **Hidden means not found:** an object the user may not see answers 404, so a competing Agent's Asset can't be discovered by trying ids.
  - Lists, Parcel cards, price filters and history all apply the same rule, in SQL.
- **Prices:** without "See prices" a user still sees the price of their own and editable Assets. Other prices show as "Price hidden".

Seeded roles:

| Role | Can do |
|---|---|
| Admin | Everything |
| Global viewer | Sees everything, changes nothing |
| Agent | Own Assets only |
| Sales | All Assets and prices; builds Portfolios |
| Engineer | Granted objects only, with engineering files |
| Attorney | Granted objects only, with legal files |
| Buyer | Granted objects only, with marketing files |
| Seller | Granted objects only, with marketing and legal files |
| Viewer | Granted objects only, with marketing files |

Adjust the roles in Admin → Roles.

## Revoking access

| Action | Effect |
|---|---|
| Untick "Can sign in" | Login revoked: every session ends at the next click, Google included |
| Sign out everywhere | Ends all sessions; the user can sign in again |
| Remove password | Google sign-in only from now on |
| Delete user | Removes the login, its grants and tokens; Contacts, Assets and history stay |

The last active Admin can't be deactivated, demoted or deleted, and nobody can deactivate or delete themselves.

## API tokens (tools)

The KAEK importer and other tools send `Authorization: Bearer nad_…`.

To set one up:

1. Admin → Users → the user → **API tokens…** → Create.
2. Copy the token. It's shown once.
3. Give it to the tool: `--token <token>`, or `"token"` in `importer.json`.

A token acts as its user, with that user's permissions. Revoke it on the same screen; deactivating the user stops it too.

## Production notes

- **HTTPS:** serve over HTTPS (CloudFront/ALB). Cookies become `Secure` automatically on HTTPS. Set `TrustForwardedHeaders` when TLS ends at the load balancer.
- **Multiple instances:** the keys that sign the session cookie live in the `data_protection_key` table, so every instance accepts the same cookies and restarts don't sign people out.
- **CSRF:** the session cookie is `SameSite=Lax`, and every state-changing API call must also carry the `X-Nadlan-Request` header (the pages' `api.js` adds it). Bearer-token calls don't need the header.
