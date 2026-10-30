<p align="center">
  <img src="docs/brand/noted-x-poster.png" alt="noted-x — your notes. your network." width="620">
</p>

<h1 align="center">noted-x</h1>

<p align="center">
  A private, Notion-like notebook for your Windows desktop and the phones on your Wi-Fi.
</p>

<p align="center">
  <strong>No cloud. No telemetry. No accounts.</strong>
</p>

---

noted-x runs an embedded web server on your PC and stores everything in a local SQLite
database. The desktop app is a full notes workspace; a phone on the same network can pair
by scanning a QR code and use the installable web app. Nothing leaves your LAN.

## Highlights

- **Nested pages and a block editor** — headings, paragraphs, lists, to-dos, quotes, code,
  tables, dividers, and images.
- **Fast local search** — SQLite FTS5 with filters for tags and favorites, plus a cached
  fallback on the phone when the desktop is unreachable.
- **Files up to 2 GB** — content-addressed SHA-256 storage, 1 MB resumable chunks, duplicate
  chunk recovery, HTTP range downloads, and image previews.
- **Realtime updates** — SignalR keeps the desktop and every paired phone in step.
- **QR pairing with per-device tokens** — single-use pairing links, an optional four-digit
  PIN, and per-device revocation.
- **Works offline on the phone** — the PWA shell, page list, recent pages, and images are
  cached; edits are made against the desktop when it is reachable.
- **LAN discovery and firewall help** — mDNS and UDP discovery, with a one-click Windows
  Firewall rule helper in Settings.
- **Lives in the tray** — close the window and the notebook stays available to paired
  devices until you quit.
- **Optional HTTPS** — a self-signed certificate is generated locally when enabled.

## Requirements

- Windows 10 or Windows 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build and run from source
- A modern browser on the phone (Chrome, Edge, Safari, or Firefox)
- The PC and phone on the same private network

No account, internet connection, or external service is required at runtime. Internet access
is only needed to restore NuGet packages when building.

## Quick start

From the repository root:

```powershell
dotnet restore
dotnet build noted-x.sln
dotnet run --project src/NotedX.Desktop
```

Or open `noted-x.sln` in Visual Studio 2022 and run the `NotedX.Desktop` project.

The desktop opens with an empty workspace. The local server starts with the app and picks
the first free port starting at **5177**. The actual address and port are shown in
**Settings** and in the pairing sheet.

### Pair a phone

1. On the desktop, select the phone button in the title bar (or open **Settings → Pair a
   device**).
2. Keep **Require PIN** enabled if you want the four-digit PIN shown with the QR code.
3. On the phone, join the same Wi-Fi network and scan the QR code, or type the pairing URL.
4. Enter the PIN if prompted. The phone receives its own device token.
5. Use the browser's **Add to Home Screen** / **Install app** action for a full-screen app.

Pairing links are single-use and expire after **2 minutes**. Pairing attempts are limited to
**5 per minute per address**. Each paired phone appears under **Settings → Devices**, where it
can be revoked at any time.

> **Install and offline caching need a secure context.** Browsers only register service
> workers and offer installation on HTTPS origins (or `localhost`). To use the PWA offline
> on a phone, enable **HTTPS** in Settings, restart the app, and trust the self-signed
> certificate on the phone. Over plain HTTP the phone can still use noted-x in the browser,
> but without the offline shell.

## How it works

```
┌────────────────────────────── Windows PC ──────────────────────────────┐
│                                                                        │
│  WPF desktop (noted-x.exe)                                             │
│    ├─ pages, editor, search, files, trash, settings                    │
│    └─ in-process Kestrel server                                        │
│          ├─ REST API        /api/*                                     │
│          ├─ SignalR hub     /hub                                       │
│          ├─ pairing         /pair                                      │
│          ├─ PWA shell       / (embedded wwwroot)                       │
│          ├─ mDNS + UDP discovery                                       │
│          └─ SQLite + SHA-256 blob store                                │
│                                                                        │
└───────────────────────────────┬────────────────────────────────────────┘
                                │ same Wi-Fi / LAN
                     ┌──────────┴──────────┐
                     │  Phone PWA          │
                     │  notes · search     │
                     │  files · offline    │
                     └─────────────────────┘
```

- The PWA is embedded inside the server assembly; there is no second web server and no
  external hosting.
- The desktop does not depend on a browser, but both surfaces share the same REST API,
  database, and blob store.
- Device tokens are sent as `Authorization: Bearer …` headers (or a SignalR access token)
  and compared against SHA-256 hashes stored in SQLite.

## Project layout

| Path                   | Purpose                                                                                                      |
| ---------------------- | ------------------------------------------------------------------------------------------------------------ |
| `src/NotedX.Core`      | Domain model, EF Core `DbContext`, page/revision services, Markdown codec, search, blob store, token hashing |
| `src/NotedX.Server`    | Kestrel host, REST endpoints, SignalR hub, pairing and device auth, discovery, HTTPS certificate             |
| `src/NotedX.MobileWeb` | Vanilla ES-module PWA embedded as static assets (`wwwroot`)                                                  |
| `src/NotedX.Desktop`   | WPF desktop app (WPF-UI, CommunityToolkit.Mvvm, `noted-x.exe`)                                               |
| `tests/NotedX.Tests`   | xUnit tests for storage, APIs, discovery, search, and embedding                                              |
| `docs/brand`           | Logo and network poster used by the app and this README                                                      |
| `build-icon.ps1`       | Generates the desktop `.ico`/PNG and all PWA icons from the logo                                             |

## Data and backups

Everything is under your Windows profile:

```
%LOCALAPPDATA%\noted-x\
  notedx.db          SQLite database (pages, blocks, tags, devices, sessions)
  blobs\             SHA-256 content-addressed attachments
  uploads\           resumable upload staging
  certs\             self-signed HTTPS certificate and key
  logs\              rolling Serilog logs
```

To back up, quit noted-x (including the tray icon) and copy the whole `noted-x` folder. To
move to another PC, copy that folder into the same location on the new machine.

> **Coming from an earlier `noted-xBUT` build?** This release renamed the app and its data
> folder. While the app is closed, rename `%LOCALAPPDATA%\noted-xBUT` to
> `%LOCALAPPDATA%\noted-x` **and rename `notedxbut.db` to `notedx.db` inside it** to keep
> your existing notes. The HTTPS certificate is regenerated on demand if the old one is
> left behind.

## Security model

- **LAN only.** The server binds to local interfaces and never publishes to the internet.
  If a public address is detected while the port is reachable, the app logs a warning.
- **Every API and hub request is authenticated** except `POST /pair`, which is the pairing
  entry point.
- **Pairing is short-lived.** Links are single-use, expire after 2 minutes, and are limited
  to 5 attempts per minute per address. The optional four-digit PIN is hashed and compared
  in constant time.
- **Tokens are hashed at rest.** Only SHA-256 hashes are stored; raw device and pairing
  tokens are never written to the database or logs.
- **Revocation is immediate.** Removing a device in Settings invalidates its token.
- **Local data is not encrypted at rest.** Use BitLocker or another full-disk encryption
  solution if physical disk access is in your threat model.
- **Anyone with a paired token has full access** to the notebook. Pair only trusted devices
  and revoke phones you no longer use.

See [`NotedX.Server/Security`](src/NotedX.Server/Security) and
[`NotedX.Core/Security`](src/NotedX.Core/Security) for the implementation.

## Configuration

| Setting                | Default                                                       |
| ---------------------- | ------------------------------------------------------------- |
| HTTP port              | `5177`, trying up to 20 consecutive ports if busy             |
| Bind address           | All LAN interfaces (`0.0.0.0`)                                |
| mDNS service           | `_noted-x._tcp`, enabled                                      |
| UDP discovery port     | `50000`, enabled                                              |
| Pairing PIN            | Required (four digits)                                        |
| Pairing token lifetime | 2 minutes                                                     |
| Pairing rate limit     | 5 attempts per minute per address                             |
| Maximum upload         | 2 GB                                                          |
| Upload chunk size      | 1 MB                                                          |
| HTTPS                  | Off; enable in Settings to generate a self-signed certificate |

Theme, PIN requirement, HTTPS, firewall rules, paired devices, and server status are
available in the desktop **Settings** view.

## API surface

All routes below require a device token, except `POST /pair`:

```
POST   /pair
GET    /api/pages
POST   /api/pages
GET    /api/pages/{id}
PUT    /api/pages/{id}
PUT    /api/pages/{id}/move
DELETE /api/pages/{id}
POST   /api/pages/{id}/restore
GET    /api/pages/{id}/revisions
GET    /api/search?q=...
GET    /api/workspaces
GET    /api/tags

POST   /api/files                 begin a resumable upload
PUT    /api/files/{uploadId}      upload a chunk (Content-Range, 1 MB)
GET    /api/uploads/{uploadId}    resume status
POST   /api/uploads/{uploadId}/complete
DELETE /api/uploads/{uploadId}    cancel
GET    /api/files                 list attachments
GET    /api/files/{id}            download (Range/ETag supported)
GET    /api/files/{id}/meta
DELETE /api/files/{id}

GET    /api/devices
DELETE /api/devices/{id}

WS     /hub                      SignalR realtime events
GET    /                          embedded PWA (SPA fallback)
```

## Build, test, and brand assets

```powershell
# Build and test
dotnet build noted-x.sln -c Debug
dotnet test noted-x.sln

# Run the desktop app
dotnet run --project src/NotedX.Desktop

# Produce a framework-dependent Windows build
dotnet publish src/NotedX.Desktop -c Release -r win-x64 --self-contained false -o artifacts\win-x64
```

Regenerate every icon from `docs/brand/noted-x-logo.png` after changing the artwork:

```powershell
powershell -ExecutionPolicy Bypass -File build-icon.ps1
```

The script crops the badge mark, centers it on the brand's black tile, and writes:

- `src/NotedX.Desktop/Assets/appicon.png` and `app.ico` (16–256 px)
- `src/NotedX.MobileWeb/wwwroot/icons/icon-180.png`, `icon-192.png`, `icon-512.png`
- `icon-maskable-512.png` and `favicon.ico`

## Troubleshooting

| Symptom                                | What to try                                                                                                                                                            |
| -------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Phone cannot open the pairing URL      | Confirm both devices are on the same Wi-Fi, the network is not using client isolation, and Windows Firewall allows noted-x. Use **Settings → Allow through firewall**. |
| QR code works but the page never loads | Check the address and port shown in Settings; another program may own the preferred port. The app tries the next 20 ports automatically.                               |
| No install prompt / no offline mode    | Enable HTTPS, restart, and trust the certificate on the phone; service workers require a secure context.                                                               |
| Certificate warning on the phone       | Expected for a self-signed certificate. Trust it on the phone, or use HTTP when offline caching is not needed.                                                         |
| mDNS name does not resolve             | mDNS is best effort. Use the pairing QR code or the numeric LAN address instead.                                                                                       |
| Port is already in use                 | noted-x logs the fallback and shows the active port in Settings. Close the other program or restart noted-x.                                                           |
| Lost or replaced a phone               | Revoke its device in **Settings → Devices**; it must pair again.                                                                                                       |
| Notes from the old name are missing    | Rename `%LOCALAPPDATA%\noted-xBUT` to `%LOCALAPPDATA%\noted-x` and `notedxbut.db` to `notedx.db` while the app is closed.                                              |

## Status

Version `0.1.0` — pre-release. The desktop app, embedded server, LAN discovery, realtime
updates, and installable PWA are implemented. Storage, API, discovery, search, and embedding
are covered by xUnit tests; the suite is expanded before release.

---

<p align="center">
  Built by MicroEG with .NET 8, WPF-UI, EF Core, SignalR, and Makaretu.DNS.
</p>
