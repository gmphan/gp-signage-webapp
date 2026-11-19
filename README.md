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
│       ├── Storage/LocalStorageService.cs   # saves uploads under wwwroot/uploads (or mounted path)
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

---

## HTTP API

### Slides — `/api/slides`
| Method | Path | Purpose |
|---|---|---|
| `GET` | `/` | list the (active) slide library |
| `POST` | `/` | upload media + create slide (`multipart/form-data`: `file`, optional `title`, `durationSeconds`) |
| `PATCH` | `/{id}` | update `title` / `durationSeconds` / `sortOrder` / `isActive` (all optional) |
| `DELETE` | `/{id}` | delete slide (removes its file + all playlist references) |

Uploads are validated: **≤ 50 MB**, extensions `.jpg .jpeg .png .gif .webp .mp4 .webm .mov`.
Stored under `wwwroot/uploads/` (or `Storage:UploadsPath` if configured to a mounted volume).

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
  loops, preloads next image, survives brief disconnects via auto-reconnect. No reload needed for
  live updates.
- **`/login.html`** — sign-in page (sets the auth cookie, then redirects to `/admin/`).
- **`/admin/`** — the control UI (`admin.js`), **auth-gated**. Header shows the signed-in user with
  **Users** (manage admins) and **Log out**. Three panels:
  - **Displays** — create (+ New), edit (✎), delete (🗑), select.
  - **Slide Library** — upload, delete, **per-slide duration** editor, `+ Add` to the selected display.
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
2. **Slide Library** — upload images/videos; set each **image's duration** (seconds). Videos show `▶ auto`.
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
- [ ] Pi kiosk setup (Chromium kiosk + portrait rotation + systemd watchdog)  ← **next**
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
- **SSH is by IP, not the domain** — the domain resolves to Cloudflare, which only proxies 80/443.
- **Cloudflare Tunnel token is a credential** — it lives in the **standalone tunnel project**, env-only, never commit.
- `.gitignore` ignores `*.pfx`, `variables.*.env`, `bin/`, `obj/`.
- `.dockerignore` excludes `https/`, secrets, and deploy files from the build context.
