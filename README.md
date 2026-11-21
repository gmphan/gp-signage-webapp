# GP Signage Webapp

A digital signage web app for a nail salon. Admins build a **playlist per TV** in a web UI;
each TV just runs a browser (a Raspberry Pi in kiosk mode) that cycles through its slideshow.

- **Backend:** ASP.NET Core `.NET 10` minimal API + **SignalR**
- **Database:** PostgreSQL + Entity Framework Core 10
- **Server host:** Dell OptiPlex 7070 running Ubuntu (Docker host)
- **Display client:** Raspberry Pi running Chromium in **kiosk mode** — browser only, no app runtime

---

## How it works (TL;DR)

1. An **admin** opens `/admin/`, manages **displays** (create / rename / delete) and the
   **slide library** (upload images & videos, set each slide's duration).
2. The admin builds a **per-display playlist** (add / remove / reorder slides).
3. Each **TV** loads `https://signage.giangmichaelphan.com/player/?display=<slug>`.
4. The player **rotates through that display's playlist**, crossfading between images (each shown
   for its own duration) and playing videos to their end, looping forever.
5. Any admin change is **pushed live over SignalR** — the TV updates without a reload.

---

## Architecture / Topology

```
                  Internet / remote network
                            ▲
                            │ HTTPS (Cloudflare Tunnel)
                            │
┌──────────────────────────────┐                    ┌───────────────────────┐
│ Dell OptiPlex 7070 (Ubuntu)  │     (remote)       │ Raspberry Pi          │
│  - Docker host               │ ◄────────────────► │  - Chromium kiosk     │
│  - postgres (shared)         │  via Cloudflare    │  - loads player URL   │
│  - signage webapp (HTTP:80)  │  Tunnel hostname   │    only               │
│  - cloudflared (standalone)  │                    │  - no app / no DB     │
│  - SignalR server            │                    │                       │
│  - TLS terminated at CF edge │                    │                       │
└──────────────────────────────┘                    └───────────────────────┘
```

- **Everything server-side runs on the OptiPlex; the app itself is HTTP-only** (TLS is terminated
  by Cloudflare at the edge).
- **The Pi is a dumb display:** it only opens the player URL in a kiosk browser (works from any
  network). It never runs Docker, the app, or the database — it only holds a *client* SignalR
  connection.

---

## Playback model (current design)

The app evolved from "show a single slide" to a **per-display playlist slideshow**:

- Each `Display` owns an **ordered playlist** (`display_slides` join table).
- The player **cycles** the playlist: images dwell for their `DurationSeconds` with a crossfade;
  videos play to their end, then advance. It loops.
- **Per-slide Fit** controls how each image meets the screen: `Contain` (whole image, letterbox bars —
  the default) or `Cover` (fill the whole screen edge-to-edge, cropping overflow). Set it per image in
  the admin. Videos always use `Contain` so their content isn't cropped.
- The rotation timer runs **in the browser**, so it's resilient to brief network blips — the server
  only needs to send the playlist on connect and whenever it changes.
- A 1-slide playlist is equivalent to the old "show just this" behavior.

### Live update flow
```
Admin UI ──PUT/POST/DELETE /api/displays/{slug}/playlist…──► server
                                                                │ 1. persist playlist
                                                                │ 2. hub: Clients.Group(slug).SendAsync("ShowPlaylist", playlist)
                                                                ▼
Player (browser) ◄──── SignalR "ShowPlaylist" ────────────────┘  (on connect: JoinDisplay → server sends playlist)
```

---

## Project Layout

```
.
├── .vscode/
│   ├── launch.json              # F5 debug (.NET Core Launch (web)); loads variables.<env>.env
│   └── tasks.json               # build / publish / watch / clean
├── Ubuntu/
│   └── app/
│       ├── docker-compose.prd.yml   # production compose: webapp only (OptiPlex)
│       └── variables.prd.env        # prod env: DB + ASPNETCORE_URLS + first-run admin (git-ignored)
├── src/
│   ├── src.slnx                 # solution
│   └── GpSignageWebapp/
│       ├── Program.cs           # entry; runs all IInjector impls; maps endpoints + hub; static files
│       ├── Injectors/
│       │   ├── IInjector.cs / Injector.cs   # InjectServicesFromAssemblies() discovery
│       │   ├── DbInjector.cs                # DbContext + repositories
│       │   ├── SignalRInjector.cs           # AddSignalR()
│       │   └── StorageInjector.cs           # IStorageService (local uploads)
│       ├── Models/
│       │   ├── Slide.cs                     # + MediaType enum (Image/Video)
│       │   ├── Display.cs                   # + DisplayOrientation enum
│       │   ├── DisplaySlide.cs              # playlist join (DisplayId, SlideId, SortOrder)
│       │   └── AdminUser.cs                 # admin credentials (hashed password)
│       ├── Auth/
│       │   ├── AuthSetup.cs                 # cookie auth config + AuthConstants
│       │   └── PasswordHasher.cs            # PBKDF2 (SHA-256) hash/verify
│       ├── Dtos/Dtos.cs                     # SlideDto, DisplayDto, PlaylistDto, AdminUserDto, request records
│       ├── Endpoints/
│       │   ├── DisplayEndpoints.cs          # displays CRUD + playlist + show
│       │   ├── SlideEndpoints.cs            # upload / list / update / delete slides
│       │   ├── AuthEndpoints.cs             # login / logout / me
│       │   └── UserEndpoints.cs             # admin user management
│       ├── Hubs/SignageHub.cs               # JoinDisplay, Heartbeat; sends ShowPlaylist
│       ├── Storage/
│       │   ├── LocalStorageService.cs        # saves uploads under wwwroot/uploads (or mounted path)
│       │   └── ImageOptimizer.cs             # downscale + re-encode uploads (SkiaSharp) → TV-friendly
│       ├── Data/
│       │   ├── AppDbContext.cs
│       │   ├── Configurations/              # Slide, Display, DisplaySlide
│       │   └── Repositories/                # generic + Slide + Display repositories
│       └── Migrations/
├── Dockerfile
├── build-and-publish.sh
└── README.md
```

---

## Data Model

### `Slide` (table `slides`)
| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | PK |
| `Title` | varchar(200) | required |
| `MediaUrl` | varchar(1000) | relative URL under wwwroot, e.g. `/uploads/slide1.jpg` |
| `MediaType` | varchar(20) | enum as string: `Image` / `Video` |
| `Fit` | varchar(20) | enum as string: `Contain` (whole image, bars — default) / `Cover` (fill screen, crop). Ignored for video (always `Contain`). |
| `DurationSeconds` | int | default 10; **ignored for video** (plays to end) |
| `SortOrder` | int | |
| `IsActive` | bool | default true |
| `CreatedAtUtc` / `UpdatedAtUtc` | timestamptz | |

### `Display` (table `displays`)
| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | PK |
| `Name` | varchar(200) | e.g. "Reception" |
| `Slug` | varchar(100) | **unique**; URL key + SignalR group name, e.g. `reception` |
| `Orientation` | varchar(20) | enum as string: `Portrait` / `Landscape` (default Landscape) |
| `CurrentSlideId` | uuid? | FK → `slides.Id`, **ON DELETE SET NULL** (last-shown convenience) |
| `LastSeenUtc` | timestamptz? | heartbeat (used for online/offline) |
| `CreatedAtUtc` / `UpdatedAtUtc` | timestamptz | |

### `DisplaySlide` (table `display_slides`) — the playlist
| Column | Type | Notes |
|---|---|---|
| `DisplayId` | uuid | FK → `displays.Id`, **cascade** |
| `SlideId` | uuid | FK → `slides.Id`, **cascade** |
| `SortOrder` | int | position within this display's playlist (0-based) |
| `CreatedAtUtc` | timestamptz | |

Primary key `(DisplayId, SlideId)` (a slide appears at most once per display).
Index `(DisplayId, SortOrder)` for ordered reads.

### `AdminUser` (table `admin_users`)
| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | PK |
| `Username` | varchar(100) | **unique**, stored lowercase (case-insensitive login) |
| `DisplayName` | varchar(200) | friendly name |
| `PasswordHash` | varchar(400) | PBKDF2 `iterations.saltB64.hashB64` (never plaintext) |
| `CreatedAtUtc` | timestamptz | |
| `LastLoginUtc` | timestamptz? | updated on successful sign-in |

### Seeded displays
Migration `SeedDisplays` seeds **Reception / Window / Pedicure / Manicure** (all Portrait) with
fixed GUIDs. They are fully editable/removable from the admin UI afterward.

### Migrations applied
- `20261116154513_InitialCreate` → `slides`
- `20261116161131_AddDisplay` → `displays`
- `20261118205057_SeedDisplays` → default TVs
- `20261118210301_AddDisplayOrientation` → `Orientation` column
- `20261118230444_AddDisplayPlaylist` → `display_slides`
- `20261118234726_AddAdminUsers` → `admin_users`
- `AddSlideFit` → `slides.Fit` (Contain / Cover, default Contain)

---

## HTTP API

### Slides — `/api/slides`
| Method | Path | Purpose |
|---|---|---|
| `GET` | `/` | list the (active) slide library |
| `POST` | `/` | upload media + create slide (`multipart/form-data`: `file`, optional `title`, `durationSeconds`, `fit`) |
| `PATCH` | `/{id}` | update `title` / `durationSeconds` / `sortOrder` / `isActive` / `fit` (all optional) |
| `DELETE` | `/{id}` | delete slide (removes its file + all playlist references) |

Uploads are validated: **≤ 50 MB**, extensions `.jpg .jpeg .png .gif .webp .mp4 .webm .mov`.
Stored under `wwwroot/uploads/` (or `Storage:UploadsPath` if configured to a mounted volume).

**Images are optimized on upload** (`Storage/ImageOptimizer.cs`, **SkiaSharp**): downscaled to fit
**1920×1080** (aspect preserved, no upscaling), **EXIF orientation applied**, and re-encoded as **JPEG q85**
(or **PNG** when the image has transparency). A multi‑MB PNG screenshot becomes a few‑hundred‑KB JPEG,
so it decodes fast on the Pi and the player doesn't stall between slides. Videos pass through untouched.
If optimization fails, the original bytes are stored (uploads never blocked).

> **EXIF orientation is applied explicitly** — the optimizer reads `SKCodec.EncodedOrigin` and rotates/
> flips the pixels to upright before resizing (all 8 orientations), so phone/camera photos aren't
> stored sideways/rotated. (Earlier versions didn't reliably apply EXIF, so uploads could come out
> **rotated to the right**.)
> **This only affects NEW uploads.** Images uploaded before this fix keep their stored orientation —
> **re-upload** any that appear rotated (delete the slide, then upload it again). New uploads get a
> unique filename each time, so re-uploading always bypasses any browser/CDN cache.

### Displays — `/api/displays`
| Method | Path | Purpose |
|---|---|---|
| `GET` | `/` | list all displays (each includes its ordered `playlist`) |
| `GET` | `/{slug}` | one display (with playlist) |
| `POST` | `/` | create display (`name` required; `slug` auto-derived from name if omitted; `orientation`) |
| `PATCH` | `/{slug}` | rename / re-slug / change orientation (all optional) |
| `DELETE` | `/{slug}` | delete display (its playlist rows cascade; slides stay in the library) |
| `POST` | `/{slug}/show` | replace playlist with a **single** slide + push (legacy 1-slide convenience) |
| `GET` | `/{slug}/playlist` | the display's ordered playlist |
| `PUT` | `/{slug}/playlist` | **replace** whole playlist with ordered `{ slideIds: [...] }` (reorder / clear) |
| `POST` | `/{slug}/playlist/{slideId}` | **append** a slide |
| `DELETE` | `/{slug}/playlist/{slideId}` | **remove** a slide |

Slug rules: lowercase `a-z`, `0-9`, `-`; runs collapsed and trimmed. Duplicate slug → `409`.

Every playlist mutation **pushes `ShowPlaylist`** to that display's SignalR group.

---

## SignalR Hub — `/hubs/signage`

**Client → server**
- `JoinDisplay(slug)` — joins the display's group and is sent the current `ShowPlaylist`.
- `Heartbeat(slug)` — updates `LastSeenUtc` (drives online/offline in admin).

**Server → client**
- `ShowPlaylist(playlist)` — a new/changed playlist (sent on join and after every edit).
- `ShowSlide(slide)` — legacy single-slide push (treated as a 1-item playlist by the player).
- `SlugChanged(newSlug)` — admin re-slugged the display; the player re-joins under the new slug.
- `DisplayDeleted()` — admin deleted the display; the player goes idle.

---

## Authentication & Users

The admin UI + management API are protected by **cookie authentication**. Kiosk players and public
read endpoints stay open so TVs keep working without logging in.

### What's protected
- **Admin page** (`/admin/`) — redirects to `/login.html` when not signed in.
- **Write actions** — all display/slide/playlist **mutations** and all `/api/users*` endpoints
  return **401** unless an admin cookie is present.
- **Public:** `/player/*`, `/uploads/*`, `/lib/*`, `GET /api/displays*`, `GET /api/displays/{slug}/playlist`,
  `GET /api/slides`, `/hubs/signage`, and `POST /api/auth/login|logout`.

### How it works
- Passwords are hashed with **PBKDF2 (SHA-256, 100k iterations, random salt)** — see `Auth/PasswordHasher.cs`.
  Stored format: `iterations.saltB64.hashB64`.
- Auth is configured in `Auth/AuthSetup.cs` (registered by `AuthInjector`). Cookie name `gp_signage_auth`,
  HttpOnly, SameSite=Lax, 12h sliding expiry. API requests get **401/403** instead of redirects.
- Usernames are stored **lowercased** and unique; login is case-insensitive.

### First-run admin (bootstrap)
On startup, if there are **no** admin users, one is created from config (`Program.BootstrapAdminAsync`):
```dotenv
Auth__InitialAdmin__Username=admin          # default "admin"
Auth__InitialAdmin__Password=<a-strong-password>   # if omitted, a random one is logged ONCE
Auth__InitialAdmin__DisplayName=Administrator      # optional
```
> ⚠️ If you don't set a password, a **random password is printed to the logs** on first start — sign in
> with it and change it. Set `Auth__InitialAdmin__Password` for a deterministic first login.

### Managing users (in the admin UI)
Click **Users** (header) to open the Users modal:
- **Add user** — username, optional display name, password (min 6).
- **Rename** — change display name. **Reset password** — set a new one.
- **Delete** — remove a user (you can't delete yourself or the last admin).
- **Log out** — clears the cookie and returns to `/login.html`. Current user is shown in the header.

### Auth API
| Method | Path | Auth | Purpose |
|---|---|---|---|
| `POST` | `/api/auth/login` | public | verify credentials, set cookie, returns the user |
| `POST` | `/api/auth/logout` | public | clear cookie |
| `GET` | `/api/auth/me` | required | the current admin (401 if not signed in) |
| `GET` | `/api/users` | required | list users |
| `POST` | `/api/users` | required | create user (`username`, `password`, `displayName?`) |
| `PATCH` | `/api/users/{id}` | required | change `displayName` and/or `password` |
| `DELETE` | `/api/users/{id}` | required | delete user (not self / not last admin) |

---

## Front-end pages (`wwwroot`)

- **`/player/?display=<slug>`** — the TV page. Connects via SignalR, joins its group, and runs the
  rotation engine (`player.js`): crossfade between images (two stacked layers), videos play to end,
  loops, survives brief disconnects via auto-reconnect. No reload needed for live updates.
  Each image's **Fit** (`Contain`/`Cover`) is applied per slide (sets `object-fit` on the layer caption).
  - **Preloading:** upcoming slides are decoded in the background (`preloadUpcoming`) so a heavy
    image doesn't cause a visible pause between slides.
  - **One-advance guarantees:** image loads use a `settled`/`loadSeq` guard (a cached image fires
    `load` twice in Chromium — without the guard the rotation skips/stalls), and videos have a
    hard **safety timeout = video length + 1s** so a video that never fires `ended` can't wedge the loop.
- **`/login.html`** — sign-in page (sets the auth cookie, then redirects to `/admin/`).
- **`/admin/`** — the control UI (`admin.js`), **auth-gated**. Header shows the signed-in user with
  **Users** (manage admins) and **Log out**. Three panels:
  - **Displays** — create (+ New), edit (✎), delete (🗑), select.
  - **Slide Library** — upload, delete, **per-slide duration** editor, **per-slide Fit** (whole image vs. fill/crop), `+ Add` to the selected display.
  - **Playlist** — ordered list for the selected display: reorder (↑/↓), remove (✕), Clear.
- **`/lib/signalr/signalr.min.js`** — vendored SignalR client (no CDN dependency; works offline).

---

## DI / Injector Pattern

`Program.cs` does **not** register services directly. It discovers all `IInjector` implementations
and runs them:

```csharp
builder.Services.InjectServicesFromAssemblies(builder.Configuration);
```

`Injector.InjectServicesFromAssemblies` scans the assembly for `IInjector` classes and calls
`InjectServices(services, configuration)` on each. **To add services, create a new `IInjector`.**
Existing injectors: `DbInjector`, `SignalRInjector`, `StorageInjector`.

> The Unit-of-Work was intentionally **removed**. Repositories are injected directly
> (`ISlideRepository`, `IDisplayRepository`); use the repository's `SaveChangesAsync()`.

---

## Database / Postgres

### Sharing the DB server (important)
The OptiPlex runs **another app** (the portfolio site) that owns the `local_postgres` container.
The signage app **reuses that Postgres server** — it does **NOT** start its own.

- `local_postgres` **publishes host port `5432`**, so the signage stack (on its **own** network)
  connects via the host: **`Host=<optiplex-lan-ip>` (e.g. `192.168.68.130`), Port 5432**.
- **Use a separate database name** to avoid colliding with the portfolio app's `giang_michael_phan`.
  Signage uses **`gp_signage`**.

> ⚠️ Because signage is on its own network, use the **host LAN IP** for the DB (`192.168.68.130`),
> **not** `Host=postgres` and **not** `localhost` (a container's `localhost` is itself).

### Create the signage database (one-time)
On the OptiPlex (shared server):
```shell
docker exec -it local_postgres psql -U ocbuumaster -d postgres -c "CREATE DATABASE gp_signage OWNER ocbuumaster;"
```

### Local dev database (Mac)
- Local Postgres runs via **Homebrew `postgresql@14`** on `localhost:5432`, `trust` auth.
- Dedicated signage DB: **`gp_signage`**.
- The portfolio app's DB is `giang_michael_phan` — **do not** point signage at it.

Local connection string:
```dotenv
ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=gp_signage;Username=ocbuumaster;Password=
```

---

## Environment Variables

Env files (`variables.*.env`) hold secrets and are **git-ignored**. `.vscode/launch.json`
loads `variables.<Environment>.env` at the repo root (`Development` / `QA` / `Production`).

```dotenv
ConnectionStrings__DefaultConnection=Host=<optiplex-lan-ip>;Port=5432;Database=gp_signage;Username=ocbuumaster;Password=<secret>

# App is HTTP-only; Cloudflare terminates TLS at the edge.
ASPNETCORE_URLS=http://*:80

# NOTE: no TUNNEL_TOKEN here — the Cloudflare Tunnel is a separate standalone project now.

# Optional: store uploads outside wwwroot (e.g. a mounted volume).
# Storage__UploadsPath=/var/gp-signage/uploads

# First-run admin (only used when no admin users exist yet).
Auth__InitialAdmin__Username=admin
Auth__InitialAdmin__Password=<strong-password>   # if omitted, a random one is logged once
# Auth__InitialAdmin__DisplayName=Administrator
```

> Only host keys like `ASPNETCORE_URLS` / `ASPNETCORE_ENVIRONMENT` take the `ASPNETCORE_` prefix.
> There is **no Kestrel certificate** (TLS is at Cloudflare), so no `Kestrel__Certificates__*` keys.

---

## HTTPS / TLS

- **TLS is terminated by Cloudflare** at its edge. Chain: browser → Cloudflare (real cert) → tunnel
  → `http://` origin on the OptiPlex.
- The app runs **HTTP-only**; there is **no Kestrel certificate** and **no `/https` mount**.
- **No self-signed cert to trust on the Pis** — Cloudflare serves a public cert browsers already trust.

---

## Docker

### Dockerfile (summary)
- Base: `mcr.microsoft.com/dotnet/aspnet:10.0`; installs `tzdata`, sets **`TZ=America/New_York`**,
  full globalization.
- `COPY ./src/GpSignageWebapp/bin/Release/net10.0/publish /app` — relies on the **pre-built publish output**.
- Entry point: `dotnet GpSignageWebapp.dll`. **No cert copy.**
- Image optimization uses **SkiaSharp** with `SkiaSharp.NativeAssets.Linux.NoDependencies` (no
  libfontconfig/libc++ needed on the Debian `aspnet:10.0` base) — both are NuGet refs, so they ship
  in the publish output automatically; no Dockerfile change required.

### Build & publish
`build-and-publish.sh`:
```sh
dotnet build -c Release src/GpSignageWebapp
dotnet publish -c Release src/GpSignageWebapp
docker buildx build --platform linux/amd64 -t ghcr.io/gmphan/gp-signage-webapp:latest --push .
```
- **linux/amd64 only** (server is x86_64 OptiPlex). The Pi is a browser → **no arm64 image**.
- Because the Dockerfile COPYs the pre-built publish output, `dotnet publish` MUST run before
  `docker build`. A multi-stage Dockerfile would remove this dependency — a future improvement.

### Production compose (`Ubuntu/app/docker-compose.prd.yml`)
- `name: gpsignage` — **own compose project**, independent of the portfolio's `stack-prd` project.
- Services: **`webapp` only** (HTTP-only, `8080:80`, own network, uploads on the `signage_uploads` volume).
- **No `cloudflared` service here** — the Cloudflare Tunnel now runs as a **standalone project**
  shared by multiple apps. This stack only exposes the app on host `:8080`; the tunnel routes
  `signage.giangmichaelphan.com → http://<optiplex-lan-ip>:8080`.
- **No postgres service** — reuses shared `local_postgres` via its **published host port**.
- **No ports to the internet**, no cert mount.

> On the OptiPlex only two files exist for this app: `docker-compose.prd.yml` and `variables.prd.env`
> (no source, no SDK). Schema changes are applied **from your Mac** (see below), not on the server.

---

## Deploying to production (OptiPlex)

Order matters: **push the image → update the DB schema → restart the stack.**

### 1. Build & push the image (from your Mac, repo root)
```shell
./build-and-publish.sh
```
(Publishes and pushes `ghcr.io/gmphan/gp-signage-webapp:latest`, linux/amd64.)

### 2. Apply DB migrations from your Mac against the prod DB
The app does **not** auto-migrate on startup, so run EF from your dev machine pointed at the shared
Postgres on the OptiPlex. The connection string must be supplied via the **`ConnectionStrings__DefaultConnection`
env var** (a `--connection` flag will NOT work — `DbInjector` reads it from configuration at design time):

```shell
cd src/GpSignageWebapp
export ConnectionStrings__DefaultConnection="Host=192.168.68.130;Port=5432;Database=gp_signage;Username=ocbuumaster;Password=<PROD_PASSWORD>"

dotnet ef migrations list      # optional: shows applied vs pending
dotnet ef database update      # applies any pending migrations (e.g. AddAdminUsers)
```
> Your Mac must be able to reach `192.168.68.130:5432` (same LAN, or a tunnel/forward to 5432).
> If the DB was freshly created, this applies **all** migrations in order. `Done.` = success.

Verify the tables exist:
```shell
# from the OptiPlex:
docker exec -it local_postgres psql -U ocbuumaster -d gp_signage -c "\dt"
#   -> admin_users, display_slides, displays, slides, __EFMigrationsHistory
```

### 3. Deploy the container (on the OptiPlex)
First-run admin: add `Auth__InitialAdmin__Password=<strong-password>` to `variables.prd.env`
(used only when no admin users exist yet). Then, from the directory holding the compose + env files:

```shell
docker compose -p gpsignage --env-file variables.prd.env -f docker-compose.prd.yml pull
docker compose -p gpsignage --env-file variables.prd.env -f docker-compose.prd.yml up -d
docker compose -p gpsignage --env-file variables.prd.env -f docker-compose.prd.yml ps
```

> Pass the file with **`-f docker-compose.prd.yml`** — Compose only auto-discovers files named
> `compose.yaml` / `docker-compose.yml`, so the `-f` flag is required for this name.

Check the first-run admin + startup:
```shell
docker logs gp_signage_webapp_prd 2>&1 | grep -iE "initial admin|generated password|Now listening"
```
If you didn't set `Auth__InitialAdmin__Password`, a **generated password is logged once** — sign in
with it and change it via the **Users** panel.

### 4. Smoke test
```shell
curl -s http://192.168.68.130:8080/                                          # "GP Signage is running."
curl -s -o /dev/null -w "%{http_code}\n" http://192.168.68.130:8080/api/displays          # 200 (public)
curl -s -o /dev/null -w "%{http_code} -> %{redirect_url}\n" http://192.168.68.130:8080/admin/  # 302 -> /login.html
```
Then sign in at `https://signage.giangmichaelphan.com/login.html`.

### Updating an existing deploy (no schema change)
```shell
docker compose -p gpsignage --env-file variables.prd.env -f docker-compose.prd.yml pull
docker compose -p gpsignage --env-file variables.prd.env -f docker-compose.prd.yml up -d
```
If the release **added/changed the schema**, also re-run **step 2** first.

### ⚠️ Stale JS after a deploy (Cloudflare edge cache)
Cloudflare's edge cache can serve an **old `player.js` / `admin.js` for up to 4 h** (its default
caches static `.js` with `max-age=14400`). Symptom: you rebuild, but the browser still runs the
previous code (e.g. rotation still misbehaves, or changes don't appear). **The container is fine —
the CDN is serving a stale copy.** Confirm and fix:

```shell
# from the OptiPlex — HIT + a large age means Cloudflare is serving a cached (old) file
curl -sI "https://signage.giangmichaelphan.com/player/player.js" | grep -iE "cf-cache-status|age"
# compare against the container directly (source of truth):
curl -s "http://192.168.68.130:8080/player/player.js" | grep -c 'function preloadUpcoming'   # -> 1
```

**Immediate fix:** Cloudflare → **Caching → Configuration → Purge Cache → Purge by URL**:
`https://signage.giangmichaelphan.com/player/player.js` (or **Purge Everything**).

**Durable fix (two layers, both applied here):**
1. **App sends `Cache-Control: no-cache, no-store, must-revalidate`** for `/player/*`, `/admin/*`,
   `/lib/*`, and `/login.html` (see `Program.cs` `StaticFileOptions.OnPrepareResponse`). This alone
   makes browsers + CDNs revalidate these assets. Uploads (`/uploads/*`) stay cacheable — their
   filenames are content-unique.
2. **Cloudflare Cache Rule** (optional belt-and-suspenders): **Caching → Cache Rules → Create rule**
   → name `Bypass app assets` → expression:
   ```
   (starts_with(http.request.uri.path, "/player/")) or
   (starts_with(http.request.uri.path, "/admin/"))  or
   (starts_with(http.request.uri.path, "/lib/"))
   ```
   → **Then: Bypass cache** → Deploy.

> The `&v=$(date +%s)` in `kiosk.sh` busts the **page** URL, but not the `<script src="/player/player.js">`.
> The cache headers above are what protect the **scripts**.

### ⚠️ Image optimization only affects NEW uploads
Upload optimization (1920×1080 JPEG + **EXIF orientation correction**) runs at upload time. Files
uploaded **before** a given fix are still in the volume unchanged. After deploying an optimizer change,
**re-upload** any affected images (delete the slide in the admin UI, then upload it again) so they get
re-processed — e.g. large images to compress them, or sideways/rotated images to fix orientation. Verify:
```shell
docker logs gp_signage_webapp_prd 2>&1 | grep -i "optimized"   # "optimized to N bytes" on new uploads
docker exec gp_signage_webapp_prd sh -c 'ls -lh /app/uploads/' # large old files vs. small new ones
```

---

## VS Code

- **`.vscode/launch.json`** — "`.NET Core Launch (web)`", prompts to pick environment, loads
  `variables.<env>.env`, launches a browser on "Now listening on".
- **`.vscode/tasks.json`** — `build`, `publish`, `watch`, `clean` against `src/src.slnx`.

---

## Deployment & Networking (read first when returning later)

```
                  Internet (any browser, any network)
                        │  HTTPS 443
                        ▼
        ┌───────────────────────────────┐
        │  Cloudflare (DNS lives here)  │  nameservers moved OFF Namecheap
        │  giangmichaelphan.com         │  terminates TLS, real certs
        └───────────────┬───────────────┘
                        │  outbound connection (no inbound ports)
                        ▼
        ┌───────────────────────────────┐
        │  Cloudflare Tunnel "gp-signage"│
        └───────────────┬───────────────┘
                        ▼
   OptiPlex 7070  —  LAN 192.168.68.130  |  public (ATT) 104.8.78.220
   ├─ cloudflared (standalone)──► http://192.168.68.130:8080
   ├─ gp_signage_webapp_prd    host 8080 → container 80 (HTTP-only; TLS done by Cloudflare)
   ├─ gmphan_webapp_prd        portfolio app — SEPARATE stack, owns host 80/443 directly
   ├─ local_postgres           shared Postgres, publishes host 5432
   └─ portainer, ddns_updater
                        ▲
                        │  (any network — salon, cellular, anywhere)
                        │
   Raspberry Pi kiosk browsers ──► https://signage.giangmichaelphan.com/?display=<slug>
```

### URLs / hostnames
| URL | Serves | Notes |
|---|---|---|
| `https://signage.giangmichaelphan.com` | **This app** | Via Cloudflare Tunnel, real cert, works from **anywhere**. **The Pis use this.** |
| `https://giangmichaelphan.com` | **Portfolio app** | Separate stack; served on host 443 — **not** this tunnel. |
| `http://192.168.68.130:8080` (LAN) | Signage directly | LAN-only bypass for testing. |

### Cloudflare (what exists)
- **DNS moved to Cloudflare.** Nameservers = `adi.ns.cloudflare.com` / `tadeo.ns.cloudflare.com`.
  Domain still **registered at Namecheap**; DNS now at Cloudflare.
- **Tunnel `gp-signage`** = an **outbound** connection from the OptiPlex (`cloudflared`) to Cloudflare.
  → **No router ports opened for signage**, and it survives **dynamic public IP / CGNAT**.
- **Route:** `signage.giangmichaelphan.com → http://192.168.68.130:8080` (service **HTTP**).
- **TLS terminates at Cloudflare** → app is **HTTP-only** (`ASPNETCORE_URLS=http://*:80`).
- **Tunnel runs as a standalone project** (shared by multiple apps), not in this app's compose.
  This app's stack only exposes host `:8080`; the tunnel routes `signage.giangmichaelphan.com` to it.
- **Token safety:** the tunnel token lives in the **standalone tunnel project's** env — never commit it.
  If exposed → Cloudflare → Networks → Tunnels → gp-signage → Refresh token.

### ⚠️ Cloudflare forwards HTTP/HTTPS only
This setup proxies **only 80/443**. It does **not** forward other ports such as **SSH (2222)**.

### SSH access (port 2222, user `gphan`) — use IP, NOT the domain
| Where you are | Command |
|---|---|
| **At home (LAN)** | `ssh -p 2222 gphan@192.168.68.130` |
| **Away** | `ssh -p 2222 gphan@104.8.78.220` ← public IP; needs router forward `2222 → 192.168.68.130:2222` (exists) |
| **Wrong / times out** | `ssh ubuntu-server` / `…@giangmichaelphan.com` → resolves to Cloudflare → **timeout** |

Mac `~/.ssh/config`:
```sshconfig ~/.ssh/config
Host ubuntu-server
    HostName 104.8.78.220     # public IP, NOT giangmichaelphan.com
    User gphan
    Port 2222
```

### Which IP to use — quick rule
- **LAN `192.168.68.130`** → only inside the home network (testing, SSH at home).
- **Public `104.8.78.220`** → works anywhere, but only for router-forwarded ports (`2222` SSH). Breaks if IP changes.
- **Domain `signage.giangmichaelphan.com`** → works anywhere over **HTTPS only**, via the tunnel —
  no port-forward, independent of home IP. **Intended path for the Pis.**

### Networking TODO / known issues
- [ ] **`ddns_updater` is stale** — DNS is at Cloudflare now; the Namecheap-based updater does nothing.
      If the ATT public IP rotates, the portfolio root A record and SSH-by-public-IP go stale. Point DDNS
      at Cloudflare's API (or tunnel the portfolio too). (Signage CNAME is unaffected.)
- [ ] **DHCP reservation** for the OptiPlex — pin `192.168.68.130`.
- [ ] **Remote SSH without open ports** — `cloudflared access ssh` or **Tailscale** (optional).
- [ ] **Access login on signage** — app is public with no auth yet. Add Cloudflare Access
      (Zero Trust → Access → Applications → `signage.giangmichaelphan.com`) or app-level auth.

---

## Raspberry Pi (kiosk display)

Each TV runs a **Raspberry Pi in Chromium kiosk mode** — browser only, no app runtime, no Docker,
no database. It just loads the player URL and rotates the playlist. All content is driven by the
server over SignalR (see [Playback model](#playback-model-current-design)).

```
┌─────────────────────────────┐        HTTPS (Cloudflare)        ┌──────────────────────────┐
│ Raspberry Pi                │ ───────────────────────────────► │ signage.giangmichaelphan.com │
│  - Raspberry Pi OS (Wayland)│                                  │  (webapp on OptiPlex)     │
│  - Chromium, kiosk mode     │ ◄─── SignalR "ShowPlaylist" ──── │                          │
│  - systemd --user kiosk     │                                  └──────────────────────────┘
│  - loads /player/?display=… │
└─────────────────────────────┘
```

### Prerequisites
- Raspberry Pi with **Raspberry Pi OS** (Bookworm/Trixie, **Wayland/labwc** desktop).
- Connected to the internet (any network — the Pi reaches the app via the Cloudflare HTTPS URL).
- The display's **slug** is set (e.g. `signage-slug-one`) and it has a playlist in the admin UI.

### Packages
```shell
sudo apt update && sudo apt install -y chromium wlr-randr wlopm
# (`chromium` may be `chromium-browser` on older images; wlopm may be optional)
```

### The kiosk launcher — `/home/pi/kiosk.sh`
Launches Chromium full-screen (kiosk), rotates the screen to the wanted orientation, keeps the
display awake, and relaunches Chromium forever if it exits. **Must be executable.**

```bash
#!/bin/bash
# GP Signage kiosk launcher (Wayland / labwc, Raspberry Pi OS)
set -u

# ---- Settings (this Pi) ----
SLUG="signage-slug-one"                 # display slug from the admin UI
ROTATE="90"                             # portrait: 90 or 270; landscape: normal
URL_HOST="signage.giangmichaelphan.com"
# -----------------------------

export XDG_RUNTIME_DIR="/run/user/$(id -u)"
export WAYLAND_DISPLAY="wayland-0"

# Wait for the Wayland compositor socket
for i in $(seq 1 30); do
  [ -S "$XDG_RUNTIME_DIR/$WAYLAND_DISPLAY" ] && break
  sleep 1
done

# Auto-detect the connected HDMI output and apply rotation
detect_output() {
  wlr-randr 2>/dev/null | grep -E '^HDMI-A-[0-9]+ ' | head -n1 | awk '{print $1}'
}
OUTPUT="$(detect_output)"
if [ -n "${OUTPUT}" ]; then
  for i in $(seq 1 20); do
    wlr-randr --output "$OUTPUT" --transform "$ROTATE" 2>/dev/null && break
    sleep 1
  done
fi

# Keep the screen awake (best-effort)
wlopm --on '*' 2>/dev/null || true

# Cache-buster: unique per launch so the page is always fetched fresh.
# (App assets like player.js are additionally sent with Cache-Control: no-cache by the server.)
URL="https://${URL_HOST}/player/?display=${SLUG}&v=$(date +%s)"

# Relaunch Chromium forever if it ever exits
while true; do
  chromium \
    --ozone-platform=wayland \
    --kiosk \
    --noerrdialogs \
    --disable-infobars \
    --disable-session-crashed-bubble \
    --no-first-run \
    --autoplay-policy=no-user-gesture-required \
    --check-for-update-interval=31536000 \
    --disable-features=Translate \
    "$URL"
  sleep 3
done
```

> **Make it executable** — a missing execute bit is the #1 reason the service fails with
> `status=203/EXEC`:
> ```shell
> chmod +x /home/pi/kiosk.sh
> ```

### The systemd user service — `~/.config/systemd/user/kiosk.service`
Runs the launcher as a **user** service (so it has access to the Wayland session).

```ini
[Unit]
Description=GP Signage Kiosk
After=graphical-session.target
Wants=graphical-session.target

[Service]
Type=simple
ExecStart=/home/pi/kiosk.sh
Restart=always
RestartSec=3
Environment=XDG_RUNTIME_DIR=/run/user/%U

[Install]
WantedBy=graphical-session.target
```

### Enable & start
User services only start when the user is "lingered" (logged in even at the login screen) and
after the graphical session is up:

```shell
# allow the user service to run without an interactive login
sudo loginctl enable-linger pi

# enable + start the kiosk
systemctl --user daemon-reload
systemctl --user enable kiosk.service
systemctl --user start kiosk.service

# check it
systemctl --user status kiosk.service --no-pager
journalctl --user -u kiosk.service -b --no-pager | tail -30
```

It also auto-starts on every boot (after the desktop) thanks to `enable-linger` + `enable`.

### Orientation / rotation
- Set `ROTATE` in `kiosk.sh`: `90` for portrait (one direction) or `270` (the other, if it's upside
  down); leave unset/normal for landscape.
- The script applies it at startup with `wlr-randr --output <HDMI> --transform "$ROTATE"`.
- Find the output name with `wlr-randr` (e.g. `HDMI-A-1`, `HDMI-A-2`). The script auto-detects it.

### Verifying / troubleshooting the Pi

| Symptom | Cause | Fix |
|---|---|---|
| `status=203/EXEC` | `kiosk.sh` not executable (or bad shebang) | `chmod +x /home/pi/kiosk.sh`; ensure the first line is `#!/bin/bash` with Unix line endings (`sed -i 's/\r$//' ~/kiosk.sh`) |
| `status=203/EXEC` after restart loop | crash before the `while true` loop | `bash -x /home/pi/kiosk.sh` to see the failing command |
| Service **not** running at boot | linger not enabled / wrong target | `sudo loginctl enable-linger pi`; check `After=graphical-session.target` |
| Screen blank / never done rotating | Wayland/compositor not ready | the script waits for `wayland-0`; check `wlr-randr` lists your HDMI output |
| **Stuck on one slide** (rotation doesn't advance) | Chromium served a **stale cached `player.js`** | clear Chromium's HTTP cache: `rm -rf ~/.cache/chromium ~/.config/chromium/Default/Cache ~/.config/chromium/Default/"Code Cache"` then `systemctl --user restart kiosk.service` |
| Changes to slides don't appear | stale page or CDN cache | restart kiosk (URL has `&v=$(date +%s)`); server sends `Cache-Control: no-cache` for `/player/*`; purge Cloudflare if needed (see below) |
| Wrong/blank content | unknown slug or empty playlist | verify the slug matches the admin UI and the display has slides; the player shows an idle/error screen otherwise |

### Live debugging on the Pi (no visible DevTools)
Run a one-off foreground Chromium with console logging to the terminal, then Ctrl-C:

```shell
systemctl --user stop kiosk.service
export XDG_RUNTIME_DIR="/run/user/$(id -u)" WAYLAND_DISPLAY="wayland-0"
chromium --ozone-platform=wayland --kiosk --enable-logging=stderr --v=0 \
  "https://signage.giangmichaelphan.com/player/?display=signage-slug-one&debug=1" \
  2>&1 | grep -E "\[trace\]|ShowPlaylist|Error"
# Ctrl-C after watching a rotation or two, then bring the kiosk back:
systemctl --user start kiosk.service
```
`&debug=1` turns on the player's `[trace]` logs (`playPlaylist`, `REVEAL`, `ADVANCE`, …), which
show exactly what the rotation engine is doing.

### ⚠️ Stale `player.js` is the usual "fix didn't work" cause
When you rebuild the server but the Pi still runs old JS, it is almost always a **cache**, not a
broken build:
1. **On the Pi** — Chromium's HTTP cache lives in **`~/.cache/chromium`** (not only
   `~/.config/chromium`). Clear it and restart:
   ```shell
   rm -rf ~/.cache/chromium ~/.config/chromium/Default/Cache ~/.config/chromium/Default/"Code Cache"
   systemctl --user restart kiosk.service
   ```
2. **At Cloudflare** — the edge may serve an old copy for up to 4 h. Purge the URL:
   Caching → Purge by URL → `https://signage.giangmichaelphan.com/player/player.js`.
3. **Confirm the served file is current** from the Pi:
   ```shell
   curl -s "https://signage.giangmichaelphan.com/player/player.js" | grep -n "ShowPlaylist\] received"
   curl -s "http://192.168.68.130:8080/player/player.js"        | grep -n "ShowPlaylist\] received"
   # both should print the SAME line number (the current build's)
   ```
   The server sends `Cache-Control: no-cache` for `/player|/admin|/lib` so this shouldn't recur —
   but an *already-cached* copy persists until the Pi cache is cleared / Cloudflare is purged once.

### Updating a Pi
- **Slides/content:** nothing to do on the Pi — changes push live over SignalR (or the Pi picks them
  up on its next reconnect).
- **After a server rebuild** (new `player.js` etc.): purge Cloudflare for the asset if needed and, if
  the Pi shows stale behavior, clear `~/.cache/chromium` + `systemctl --user restart kiosk.service`.
- **Changing the slug/orientation:** edit `kiosk.sh` (`SLUG`, `ROTATE`) → `systemctl --user restart kiosk.service`.
- **Renaming the display's *name*** is safe; changing its **slug** requires updating `SLUG` in `kiosk.sh`
  (a live `SlugChanged` rejoin fixes an open tab, but not a future boot).

---

## Common Commands

```shell
# Build / run
dotnet build src/src.slnx
dotnet run --project src/GpSignageWebapp

# EF migrations
cd src/GpSignageWebapp
dotnet ef migrations add <Name>
dotnet ef database update          # uses ConnectionStrings__DefaultConnection
dotnet ef database remove          # undo last migration (if not applied)

# Query local DB
psql -h localhost -U ocbuumaster -d gp_signage -c "\dt"
psql -h localhost -U ocbuumaster -d gp_signage \
  -c "SELECT d.\"Slug\", s.\"Title\", ds.\"SortOrder\" FROM display_slides ds JOIN displays d ON d.\"Id\"=ds.\"DisplayId\" JOIN slides s ON s.\"Id\"=ds.\"SlideId\" ORDER BY d.\"Slug\", ds.\"SortOrder\";"
```

> EF design-time needs a connection string. If the env var isn't set, pass it inline:
> ```shell
> ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=gp_signage;Username=ocbuumaster;Password=" \
>   dotnet ef database update
> ```

---

## Using the app

### Admin (`/admin/`)
1. **Displays panel** — `+ New` to add a TV (name → slug auto-derived, orientation). ✎ edit / 🗑 delete.
2. **Slide Library** — upload images/videos; set each **image's duration** (seconds) and **Fit** (`Fit (bars)` = whole image, `Fill (crop)` = full-bleed). Videos show `▶ auto` (always contain).
3. Select a display, then **`+ Add`** slides to build its playlist (right panel).
4. **Reorder** with ↑/↓, **remove** with ✕, **Clear** to empty. All changes push live to the TV.

### Player (`/player/?display=<slug>`)
- Open it on the TV (or *any* browser) to watch that display's slideshow. Updates live — no reload needed.
- Misconfigured/unknown slug shows an error idle screen; deleted display shows "This display was deleted."

---

## Roadmap / Status

- [x] Project skeleton, DI injector pattern, EF Core (Npgsql), Docker, compose, scripts
- [x] `Slide` entity + config + repository + migration
- [x] `Display` entity + config + repository + migrations (incl. seed + orientation)
- [x] Deployment: DNS on Cloudflare, Tunnel `gp-signage`, public HTTPS URL live
- [x] SignalR `SignageHub` (`JoinDisplay`, `Heartbeat`; `ShowPlaylist` / `ShowSlide` push)
- [x] Minimal API: slides (upload/list/update/delete), displays (CRUD), playlist (get/set/append/remove), show
- [x] Player page — SignalR + **playlist rotation engine** (crossfade, per-slide durations, video-to-end, loop)
- [x] Admin page — displays CRUD, slide upload/durations, **per-display playlist** builder (reorder/remove/clear)
- [x] **Display CRUD** (create/rename/re-slug/delete) with live player handling (`SlugChanged`, `DisplayDeleted`)
- [x] **Per-slide durations** (editable in admin; respected by the player)
- [x] **Admin authentication** — cookie login, PBKDF2 password hashing, first-run admin bootstrap
- [x] **User management** — add / rename / reset-password / delete admin users (admin-only)
- [x] **Player robustness** — preload upcoming slides; one-advance guards (fixes double-`load` skip/stall); video safety timeout
- [x] **Upload image optimization** — downscale to 1920×1080 + JPEG/PNG re-encode (SkiaSharp) so heavy PNGs don't stall the TVs
- [x] **EXIF orientation on upload** — reads `SKCodec.EncodedOrigin` and rotates/flips upright (all 8 orientations) so phone photos aren't stored rotated
- [x] **Per-slide Fit** — `Contain` (whole image, bars, default) or `Cover` (fill screen, crop), set per image in the admin (`slides.Fit` column)
- [x] **Static asset cache busting** — `Cache-Control: no-cache` for `/player|/admin|/lib` so a rebuild is never shadowed by Cloudflare's 4h edge cache
- [x] **Pi kiosk setup** — Chromium kiosk (Wayland), portrait rotation (`wlr-randr`), systemd **user** service + `enable-linger`, auto-restart (see [Raspberry Pi (kiosk display)](#raspberry-pi-kiosk-display))
- [ ] Auto-apply migrations on startup (config-guarded) for production deploys
- [ ] Optional: Cloudflare Access / app auth; drag-and-drop playlist reorder; display reordering

---

## Notes / Gotchas

- **NuGet warnings** `NU1902` / `NU1903`: `System.Security.Cryptography.Pkcs 9.0.0` (via EF Core Design)
  has known vulnerabilities. Pin a patched version to clear:
  `<PackageReference Include="System.Security.Cryptography.Pkcs" Version="10.0.0" />`.
- **Local Postgres is v14**, production is **v16**. Fine for these tables; don't copy data dirs across.
- **Two apps, one Postgres server** — keep databases separate (`gp_signage` vs `giang_michael_phan`).
- **Slug is the stable URL key** the Pi loads. Renaming the **name** is safe anytime; changing the
  **slug** means updating that TV's kiosk URL (the live `SlugChanged` rejoin fixes an open tab, not a
  future boot). Set the slug you want **before** provisioning each Pi.
- **Deleting a display** keeps its slides in the library (only the playlist assignment is removed).
- **Keep slide images small.** New uploads are auto-optimized to ≤1920×1080 JPEG, but very large
  source images (e.g. full‑res PNG screenshots) still cost upload time. Aim ≤1080p JPG/WebP; the
  Pi decodes those instantly. Big images uploaded *before* the optimizer existed should be re-uploaded.
- **After a rebuild, if the browser runs old JS**, it's usually Cloudflare's edge cache (not the image).
  Purge `/player/player.js` and confirm `cf-cache-status`. The app now sends `no-cache` for app assets,
  but an existing cached copy persists until purged once. Tools: `?debug=1` on the player URL turns on
  `[trace]` logging.
- **Pi kiosk services are systemd *user* services** — they need `sudo loginctl enable-linger pi` to start
  without an interactive login, and `~/.cache/chromium` is where Chromium's HTTP cache actually lives
  (clearing only `~/.config/chromium/...` often misses it). See [Raspberry Pi (kiosk display)](#raspberry-pi-kiosk-display).
- **SSH is by IP, not the domain** — the domain resolves to Cloudflare, which only proxies 80/443.
- **Cloudflare Tunnel token is a credential** — it lives in the **standalone tunnel project**, env-only, never commit.
- `.gitignore` ignores `*.pfx`, `variables.*.env`, `bin/`, `obj/`.
- `.dockerignore` excludes `https/`, secrets, and deploy files from the build context.
