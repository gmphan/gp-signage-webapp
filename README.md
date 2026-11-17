# GP Signage Webapp

A digital signage web app for a nail salon. Admins pick which slide each TV shows; the TV just runs a browser.

- **Backend:** ASP.NET Core `.NET 10` minimal API + **SignalR**
- **Database:** PostgreSQL + Entity Framework Core 10
- **Server host:** Dell OptiPlex 7070 running Ubuntu (Docker host)
- **Display client:** Raspberry Pi running Chromium in **kiosk mode** — browser only, no app runtime

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
│  - cloudflared (tunnel)      │                    │  - no app / no DB     │
│  - SignalR server            │                    │                       │
│  - TLS terminated at CF edge │                    │                       │
└──────────────────────────────┘                    └───────────────────────┘
```

- **Everything server-side runs on the OptiPlex; the app itself is HTTP-only** (TLS is terminated by Cloudflare at the edge).
- **The Pi is a dumb display:** it only opens `https://signage.giangmichaelphan.com/player?display=<slug>` in a kiosk browser (works from any network). It never runs Docker, the app, or the database — it only holds a *client* SignalR connection.

---

## Core Requirements (decisions made)

| # | Requirement | Decision |
|---|---|---|
| 1 | Who chooses the slide? | **Admin/remote UI** picks which slide each screen shows (option 1a). |
| 2 | Playback model | **Single-slide-on-demand** — no autoplay loop, no playlist engine. |
| 3 | Which slides? | Any slide from the **global library** can go to any display. |
| 4 | State persistence | A display **returns to what it was showing** after reload/reboot → state kept in the **DB**. |
| 5 | Number of displays | Small: **2–4 TVs**. SignalR groups by display slug. |

### Resulting data model
- **No** `Playlist` / `PlaylistItem` tables. Just:
  - `Slide` — the media library.
  - `Display` — one row per TV, with `CurrentSlideId` (persisted "what it's showing now").

### Flow
```
Admin UI ──POST /api/displays/{slug}/show {slideId}──► server
                                                        │ 1. save Display.CurrentSlideId
                                                        │ 2. hub: Clients.Group(slug).SendAsync("ShowSlide", slideId)
                                                        ▼
Player (browser) ◄──── SignalR "ShowSlide" ────────────┘  (on connect: GET /api/displays/{slug})
```

---

## Project Layout

```
.
├── .vscode/
│   ├── launch.json              # F5 debug (.NET Core Launch (web))
│   └── tasks.json               # build / publish / watch / clean
├── Ubuntu/
│   └── app/
│       ├── docker-compose.prd.yml   # production compose: webapp + cloudflared (OptiPlex)
│       └── variables.prd.env        # prod env incl. TUNNEL_TOKEN (git-ignored)
├── src/
│   ├── src.slnx                 # solution
│   └── GpSignageWebapp/
│       ├── Program.cs           # app entry; runs all IInjector implementations
│       ├── Injectors/
│       │   ├── IInjector.cs
│       │   ├── Injector.cs               # InjectServicesFromAssemblies() extension
│       │   └── DbInjector.cs             # DbContext + repositories
│       ├── Models/
│       │   ├── Slide.cs                  # + MediaType enum (Image/Video)
│       │   └── Display.cs
│       ├── Data/
│       │   ├── AppDbContext.cs           # DbSet<Slide>, DbSet<Display>
│       │   ├── Configurations/
│       │   │   ├── SlideConfiguration.cs
│       │   │   └── DisplayConfiguration.cs
│       │   └── Repositories/
│       │       ├── IGenericRepository.cs / GenericRepository.cs
│       │       ├── ISlideRepository.cs   / SlideRepository.cs
│       │       └── IDisplayRepository.cs / DisplayRepository.cs
│       └── Migrations/
├── .dockerignore
├── .gitignore
├── build-and-publish.sh
├── Dockerfile
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
| `DurationSeconds` | int | default 10 (ignored for video) |
| `SortOrder` | int | |
| `IsActive` | bool | default true |
| `CreatedAtUtc` / `UpdatedAtUtc` | timestamptz | |

Index: `(IsActive, SortOrder)`.

### `Display` (table `displays`)
| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | PK |
| `Name` | varchar(200) | e.g. "Reception" |
| `Slug` | varchar(100) | **unique**; URL key + SignalR group name, e.g. `reception` |
| `CurrentSlideId` | uuid? | FK → `slides.Id`, **ON DELETE SET NULL** |
| `LastSeenUtc` | timestamptz? | heartbeat |
| `CreatedAtUtc` / `UpdatedAtUtc` | timestamptz | |

### Migrations applied
- `20261116154513_InitialCreate` → `slides`
- `20261116161131_AddDisplay` → `displays`

---

## DI / Injector Pattern

`Program.cs` does **not** register services directly. It discovers all `IInjector`
implementations and runs them:

```csharp
builder.Services.InjectServicesFromAssemblies(builder.Configuration);
```

`Injector.InjectServicesFromAssemblies` scans the assembly for classes implementing
`IInjector` and calls `InjectServices(services, configuration)` on each. **To add services,
create a new `IInjector` implementation** (e.g. a future `SignalRInjector`).

> Note: the Unit-of-Work was intentionally **removed**. Repositories are injected directly
> (`ISlideRepository`, `IDisplayRepository`); use the repository's `SaveChangesAsync()` (or
> inject `AppDbContext` for multi-repo transactions).

---

## Database / Postgres

### Sharing the DB server (important)
The OptiPlex runs **another app** (the portfolio site) that owns the `local_postgres` container.
The signage app **reuses that Postgres server** — it does **NOT** start its own.

- `local_postgres` **publishes host port `5432`**, so the signage stack (on its **own** network)
  connects via the host: **`Host=<optiplex-lan-ip>` (e.g. `192.168.68.130`), Port 5432**.
  It does **not** join the portfolio's `stack-prd_default` network (that coupling caused containers
  from one stack to be reconciled by the other — avoided now).
- **Use a separate database name to avoid colliding with the portfolio app's `giang_michael_phan`.**
  Signage uses **`gp_signage`**.

> ⚠️ Because signage is on its own network, use the **host LAN IP** for the DB (`192.168.68.130`),
> **not** `Host=postgres` and **not** `localhost` (a container's `localhost` is itself). If you ever
> want them to share a network again, create a dedicated external network and attach **both** stacks.

### Create the signage database (one-time)
On the OptiPlex (shared server):
```shell
docker exec -it local_postgres psql -U ocbuumaster -d postgres -c "CREATE DATABASE gp_signage OWNER ocbuumaster;"
```
> If `ocbuumaster` lacks `CREATEDB`, create it as the stack's superuser instead.

### Local dev database (Mac)
- Local Postgres runs via **Homebrew `postgresql@14`** on `localhost:5432`.
- Superuser/session role is the macOS user (**`giangphan`**); `trust` auth (no password).
- Dedicated signage DB created and migrated: **`gp_signage`**.
- The portfolio app's DB is `giang_michael_phan` — **do not** point signage at it.

Local connection string:
```dotenv
ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=gp_signage;Username=ocbuumaster;Password=
```

---

## Environment Variables

Env files (`variables.*.env`) hold secrets and are **git-ignored**. `.vscode/launch.json`
loads `variables.<Environment>.env` at the repo root (`Development` / `QA` / `Production`).

Key variables:
```dotenv
ConnectionStrings__DefaultConnection=Host=<optiplex-lan-ip>;Port=5432;Database=gp_signage;Username=ocbuumaster;Password=<secret>

# App is HTTP-only; Cloudflare terminates TLS at the edge.
ASPNETCORE_URLS=http://*:80

# Cloudflare Tunnel token (compose passes ${TUNNEL_TOKEN}). KEEP SECRET.
TUNNEL_TOKEN=<cloudflare-tunnel-token>
```

> ⚠️ **Postgres host:** since this stack now uses its **own** Docker network (not the portfolio's
> `stack-prd_default`), connect to the shared Postgres via its **published host port**, i.e.
> `Host=<optiplex-lan-ip>` (e.g. `192.168.68.130`), **not** `Host=postgres`. (Inside a container,
> `localhost` won't reach the host; use the host's LAN IP, or `host.docker.internal` on Docker Desktop.)
>
> Note: only host keys like `ASPNETCORE_URLS` / `ASPNETCORE_ENVIRONMENT` take the `ASPNETCORE_`
> prefix. We no longer set any `Kestrel__Certificates__*` keys — there is no Kestrel cert.

---

## HTTPS / TLS

- **TLS is terminated by Cloudflare** at its edge. Chain: browser → Cloudflare (real cert) → tunnel
  → `http://` origin on the OptiPlex.
- The app therefore runs **HTTP-only**; there is **no Kestrel certificate** and **no `/https` mount**.
- **No self-signed cert to trust on the Pis** — Cloudflare serves a public cert that browsers already
  trust. (The earlier self-signed/`dotnetcerts.pfx` approach was dropped when we moved to Cloudflare.)
- If we ever bypass Cloudflare and serve the app directly over HTTPS again, we'd restore a Kestrel
  cert + `/https` mount and a Pi trust-store step — see git history, not needed with the tunnel.

---

## Docker

### Dockerfile (summary)
- Base: `mcr.microsoft.com/dotnet/aspnet:10.0`
- Installs `tzdata`, sets **`TZ=America/New_York`**, full globalization.
- `COPY ./src/GpSignageWebapp/bin/Release/net10.0/publish /app` — relies on the **pre-built publish output**.
- Entry point: `dotnet GpSignageWebapp.dll`
- **No cert copy** (certs mount at runtime).

### Build & publish
`build-and-publish.sh`:
```sh
dotnet build -c Release src/GpSignageWebapp
dotnet publish -c Release src/GpSignageWebapp
docker buildx build --platform linux/amd64 -t ghcr.io/gmphan/gp-signage-webapp:latest --push .
```
- **linux/amd64 only** (server is x86_64 OptiPlex). The Pi is a browser, not a server → **no arm64 image**.
- Because the Dockerfile COPYs the pre-built publish output, `dotnet publish` MUST run before `docker build`
  (ideally `dotnet publish -r linux-x64` to avoid cross-OS publish artifacts). A multi-stage Dockerfile
  would remove this dependency — a future improvement.

### Production compose (`Ubuntu/app/docker-compose.prd.yml`)
- `name: gpsignage` — **own compose project**, independent of the portfolio's `stack-prd` project.
- Services: **`webapp`** (HTTP-only, `8080:80`, own network) and **`cloudflared`** (tunnel, `restart: always`).
- **No postgres service** — reuses shared `local_postgres` via its **published host port** (`Host=<optiplex-lan-ip>`).
- **No ports to the internet**, no cert mount. Cloudflare reaches the app through the tunnel.

Deploy on the OptiPlex:
```shell
cd Ubuntu/app
docker compose -p gpsignage --env-file variables.prd.env -f docker-compose.prd.yml up -d
```
(Ensure `variables.prd.env` exists there — with `ConnectionStrings__DefaultConnection`, `ASPNETCORE_URLS=http://*:80`,
and `TUNNEL_TOKEN=...` — and the `gp_signage` DB exists on the shared Postgres.)

---

## VS Code

- **`.vscode/launch.json`** — "`.NET Core Launch (web)`", prompts to pick environment,
  loads `variables.<env>.env`, launches a browser on "Now listening on".
- **`.vscode/tasks.json`** — `build`, `publish`, `watch`, `clean` against `src/src.slnx`.
  (`watch` may prefer the `.csproj` if the solution path errors.)
- `preLaunchTask: "build"` in launch.json depends on the `build` task existing.

---

## Deployment & Networking (read first when returning later)

This explains the whole ecosystem: how browsers reach the app, how DNS works, how the Cloudflare
Tunnel fits in, and **which IP (LAN vs public) to use for what**. Kept here because these pieces
are easy to forget months later.

### The big picture

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
   ├─ cloudflared             ──► http://192.168.68.130:8080
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
| `https://giangmichaelphan.com` | **Portfolio app** | Separate stack; served by `gmphan_webapp_prd` on host 443 (port-forward) — **not** this tunnel. |
| `http://192.168.68.130:8080` (LAN) | Signage directly | LAN-only bypass for testing. |
| `http://104.8.78.220:8080` (public) | Signage directly | Only if a router forward for 8080 exists (not recommended; use the tunnel). |

### Cloudflare (what exists)

- **DNS moved to Cloudflare.** `giangmichaelphan.com` nameservers = `adi.ns.cloudflare.com` /
  `tadeo.ns.cloudflare.com`. Domain still **registered at Namecheap**; DNS now managed at Cloudflare.
- **Tunnel `gp-signage`** = an **outbound** connection from the OptiPlex (`cloudflared`) to Cloudflare.
  → **No router ports opened for signage**, and it survives **dynamic public IP / CGNAT** (it dials out).
- **Route:** `signage.giangmichaelphan.com → http://192.168.68.130:8080` (service **HTTP**).
  Cloudflare auto-created the DNS **CNAME** `signage → <tunnel-id>.cfargotunnel.com`.
- **TLS terminates at Cloudflare** → app is **HTTP-only** (`ASPNETCORE_URLS=http://*:80`), with
  **no Kestrel cert and no `/https` mount**. This is why the old
  `FileNotFoundException /https/dotnetcerts.pfx` crash is gone.
- **Token safety:** the tunnel token (`TUNNEL_TOKEN`) grants account access. It lives **only** in
  `variables.prd.env` (git-ignored) and is passed as `${TUNNEL_TOKEN}` in compose. **Never commit
  it.** If exposed → Cloudflare → Networks → Tunnels → gp-signage → Refresh token.
- `cloudflared` runs as a **compose service** with `restart: always` (survives reboots). A bare
  foreground `docker run` dies when the terminal closes — a past gotcha.

### ⚠️ Cloudflare forwards HTTP/HTTPS only

This setup proxies **only 80/443**. It does **not** forward other ports such as **SSH (2222)**.
So `giangmichaelphan.com:2222` can **never** reach SSH — the domain resolves to Cloudflare, which
never handled port 2222. Non-HTTP access must use the **LAN IP** (at home) or **public IP** (away).

### SSH access (port 2222, user `gphan`) — use IP, NOT the domain

| Where you are | Command |
|---|---|
| **At home (LAN)** | `ssh -p 2222 gphan@192.168.68.130` |
| **Away** | `ssh -p 2222 gphan@104.8.78.220` ← public IP; needs router forward `2222 → 192.168.68.130:2222` (exists) |
| **Wrong / times out** | `ssh ubuntu-server` or `…@giangmichaelphan.com` → resolves to Cloudflare → **timeout** |

Mac `~/.ssh/config` should point at the **public IP**:
```sshconfig ~/.ssh/config
Host ubuntu-server
    HostName 104.8.78.220     # public IP, NOT giangmichaelphan.com
    User gphan
    Port 2222
```

### Which IP to use — quick rule

- **LAN IP `192.168.68.130`** → only **inside the home network** (testing, SSH at home). Never from outside.
- **Public IP `104.8.78.220`** → works **anywhere**, but only for **router-forwarded ports**
  (currently `2222` SSH; historically `443/80` portfolio). Breaks if the IP changes.
- **Domain `signage.giangmichaelphan.com`** → works **anywhere over HTTPS only**, via the tunnel —
  **no port-forward, independent of the home IP**. Intended path for the Pis.

### Networking TODO / known issues

- [ ] **`ddns_updater` is stale** — DNS is at Cloudflare now, so the Namecheap-based updater does
      nothing. If the ATT public IP rotates, the portfolio root A record (static on Cloudflare) and
      SSH-by-public-IP go stale. Fix by pointing DDNS at **Cloudflare's API**, or moving the
      portfolio behind a tunnel too. (Signage CNAME is unaffected — it points at Cloudflare.)
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
psql -h localhost -U ocbuumaster -d gp_signage -c "SELECT * FROM displays;"
```

> EF design-time needs a connection string. If the env var isn't set, pass it inline:
> ```shell
> ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=gp_signage;Username=ocbuumaster;Password=" \
>   dotnet ef database update
> ```

---

## Roadmap / Status

- [x] Project skeleton, DI injector pattern, EF Core (Npgsql), Docker, compose, scripts
- [x] `Slide` entity + config + repository + `InitialCreate` migration
- [x] `Display` entity + config + repository + `AddDisplay` migration
- [x] Local `gp_signage` DB created and migrated
- [x] Deployment: DNS on Cloudflare, Cloudflare Tunnel `gp-signage`, public URL
      `https://signage.giangmichaelphan.com` live, app HTTP-only behind the tunnel
- [ ] **SignalR**: `SignageHub` (`JoinDisplay(slug)` → group; `ShowSlide` push) + `SignalRInjector`
- [ ] Minimal API endpoints: `GET /api/displays`, `GET /api/displays/{slug}`,
      `POST /api/displays/{slug}/show`, `GET /api/slides`
- [ ] Player page (`/player?display=<slug>`) — initial load + SignalR listener
- [ ] Admin page — pick a slide per display
- [ ] Seed default displays (Reception / Window / …); editable later
- [ ] File upload for slide media (`wwwroot/uploads`)
- [ ] Pi kiosk setup (Chromium kiosk + systemd watchdog + cert trust)
- [ ] Auto-apply migrations on startup (config-guarded) for production deploys

---

## Notes / Gotchas

- **NuGet warnings** `NU1902` / `NU1903`: `System.Security.Cryptography.Pkcs 9.0.0` (pulled in by
  EF Core Design) has known vulnerabilities. Pin a patched version in the `.csproj` to clear:
  `<PackageReference Include="System.Security.Cryptography.Pkcs" Version="10.0.0" />`.
- **Local Postgres is v14**, production is **v16**. Fine for these tables; don't copy data dirs across.
- **Two apps, one Postgres server** on the OptiPlex — keep databases separate (`gp_signage` vs
  `giang_michael_phan`) so migration histories don't mix.
- **SSH is by IP, not the domain** — `giangmichaelphan.com` resolves to Cloudflare, which only
  proxies 80/443. Use LAN `192.168.68.130:2222` (home) or public `104.8.78.220:2222` (away).
- **Cloudflare Tunnel token is a credential** (`TUNNEL_TOKEN`) — env-only, never commit.
- `.gitignore` ignores `*.pfx`, `variables.*.env`, `bin/`, `obj/`.
- `.dockerignore` excludes `https/`, secrets, and deploy files from the build context.
