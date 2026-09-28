# Web Portal (remote access)

**LibationWeb** is a small web server that runs Libation without a desktop. Open it in a phone's browser, from anywhere you can reach your home network, and:

- see your Audible library with Libation's statuses (not downloaded, downloaded, error, absent from Audible),
- scan Audible for new purchases, by hand or on a schedule,
- queue downloads - one title, a selection, or everything new - and watch them progress live,
- fetch a missing PDF, convert a title to MP3, or fix a status by hand,
- add an Audible account, or sign one in again, without a desktop,
- read the last lines of Libation's log when something goes wrong.

It uses Libation's own download and decrypt pipeline, the same one the desktop app and `libationcli liberate` use, and it writes to the same Books folder. It never deletes anything. Whatever plays your books today - [Nightstand, Audiobookshelf](../frequently-asked-questions.md#now-that-ive-downloaded-my-books-how-can-i-listen-to-them), a folder on your phone - keeps doing that; the portal only gets the files there.

What it deliberately does not do: play audio (that is your player's job) or edit Libation's settings (use the desktop app; the portal reads `Settings.json` and never writes it - its own scan schedule goes in a file of its own, `LibationWeb.json`). It *can* add an Audible account and sign one in again, using the same external-browser login as [`login-external`](../advanced/command-line-interface.md#log-in-with-an-external-browser-login-external) - your Audible password is only ever typed on Amazon's own page, never in the portal. See [Accounts](#accounts) below.

> [!WARNING] The portal can download to your disk and it talks to your Audible account.
> Protect it with a password (required) and put it behind a VPN such as Tailscale or an HTTPS reverse proxy before reaching it from outside your home. Never publish port 8791 to the internet as plain HTTP.

## How it fits with the desktop app and the CLI

The portal reads the same Libation files folder as the desktop app and the CLI: `Settings.json`, `AccountsSettings.json` and the `LibationContext.db` database. Everything it downloads is recorded there, so the desktop app shows the same statuses afterwards.

Because it is the same database, run downloads from **one place at a time**. Libation logs a warning when the desktop app and the portal are open against the same folder; browsing in both is fine, downloading the same title from both is not.

## Run it on the computer that already runs Libation

There is no separate installer yet. Build it from the repository with the .NET 10 SDK:

```console
dotnet publish Source/LibationWeb/LibationWeb.csproj -c Release -o C:\Tools\LibationWeb
```

Then start it with a password. On Windows (PowerShell):

```powershell
$env:LIBATION_WEB_PASSWORD = "a long password of your own"
C:\Tools\LibationWeb\LibationWeb.exe
```

On Linux or macOS:

```console
LIBATION_WEB_PASSWORD='a long password of your own' ./LibationWeb
```

The console prints where it is listening (`http://0.0.0.0:8791` by default), which Libation files folder it found and whether the Books folder exists. Open `http://127.0.0.1:8791` on the same machine, or `http://<that computer's address>:8791` from a phone on the same Wi-Fi (allow the port through the firewall when Windows asks). The first visit shows a sign-in screen; a sign-in lasts 30 days on that device.

If your Libation files are somewhere other than the default, point at them the way the CLI does: `--libationFiles "D:\path\to\LibationFiles"` or the `LIBATION_FILES_DIR` environment variable.

This mode is the quickest way to try the portal, but the computer has to stay on. For an always-on portal, run it on the NAS.

## Run it on a NAS in Docker

The image is built from this repository and contains both `LibationWeb` and `LibationCli`. It mounts two folders: a **config** folder (Libation's files) and the **books** folder (where downloads go, which should be the folder your player scans).

The container uses the mounted config folder **directly** - it is Libation's files folder, not a copy staged inside the container - so everything the portal writes stays there and survives the container being recreated: an account added or signed in again from the *Activity* tab and its refreshed Audible tokens, the library database, `LibationWeb.json` (the scan schedule), the cover-image cache, the search index and `Log*.log`. The user in `PUID`/`PGID` therefore needs **write** access to it. The only two settings the container changes are the Books and in-progress folders, and it changes them in memory for its own process, so the `Settings.json` you copied in is never rewritten.

### 1. Copy Libation's files from the desktop

Create a folder on the NAS, for example `/volume1/docker/libation/config`, and copy into it from your desktop's Libation files folder (on Windows usually `C:\Users\<you>\Libation`):

- `AccountsSettings.json` - your Audible account and its tokens
- `Settings.json` - your naming templates, audio format and other preferences. `Books` and `InProgress` in it are ignored inside the container.
- `LibationContext.db` - the library database. Copy it if you want the portal to know which books you already have; from then on treat the NAS copy as the real one.
- `libation-master.key` - **required if your tokens are stored encrypted (the default).** Export it on the desktop first: **Settings -> Important -> Export encryption key...**, or `libationcli export-master-key libation-master.key`. Without it the container logs `Failed to decrypt ExistingAccessToken` and every scan fails. Treat this file like a password. Details in [Docker - encrypted tokens](docker.md#configuration).

The container runs as an ordinary user (`PUID`/`PGID`, found with `id <username>` over SSH). That user needs read access to the config folder and **write** access to both the config folder (the database is written there) and the books folder.

### 2. Configure and start

Copy the repository to the NAS (outside the audiobook share), then:

```bash
cd /volume1/docker/libation/app
cp .env.web.example .env
vi .env            # PUID/PGID, LIBATION_CONFIG_PATH, LIBATION_BOOKS_PATH, LIBATION_WEB_PASSWORD
sudo docker compose -f docker-compose.web.yml up -d --build
sudo docker compose -f docker-compose.web.yml logs -f
```

The log ends with the same banner as the desktop run: Libation files, Books folder, whether the Books folder exists, the listening address. Open `http://nas:8791` and sign in with the password from `.env`.

Later, if the portal reports that the account needs a login (tokens expired, or you skipped the master key and want to sign in fresh instead), the quickest fix is **Sign in again** on the portal's *Activity* page - see [Accounts](#accounts). The same thing from a shell, inside the container against the mounted config so it persists:

```bash
sudo docker exec -it libation-web LibationCli login-external --account you@example.com --locale us
sudo docker compose -f docker-compose.web.yml restart
```

(No `--libationFiles` needed: the image sets `LIBATION_FILES_DIR=/config`, and that applies to anything started with `docker exec` too.)

Updating is `git pull` and `docker compose -f docker-compose.web.yml up -d --build` again.

### The books folder and your player

Point `LIBATION_BOOKS_PATH` at the folder your player already watches. Libation lays books out as one folder per title (`Title [ASIN]/Title.m4b`), so:

- **Nightstand**: that folder is its `SHELVES_PATH`, with `SINGLE_SHELF` set so the title folders are read as books, not shelves. Nightstand's quick scan (default every 10 minutes) picks new books up on its own.
- **Audiobookshelf**: use the library folder, or leave the folder wherever you like and turn on Libation's [Audiobookshelf auto-upload](../features/audiobookshelf.md) - the portal runs that step after each download, exactly as `liberate` does.

Colons in titles are a problem on NTFS shares; see [Set custom replacement characters](../advanced/command-line-interface.md#set-custom-replacement-characters).

## Deploying with Portainer

Portainer stacks cannot build an image from a compose file pasted into the web editor, so the portal is deployed as a **pre-built image** plus the stack file [Docker/portainer-stack.libation-web.yml](../../Docker/portainer-stack.libation-web.yml).

### 1. Get the image onto the NAS

Build it on any machine with Docker (from the repository root) and save it as a tar. Use `linux/amd64` for an Intel or AMD NAS, `linux/arm64` for an ARM one:

```bash
docker build --platform linux/amd64 -f Docker/LibationWeb.Dockerfile -t libation-web:14.2.1 .
docker save libation-web:14.2.1 -o libation-web-14.2.1-amd64.tar
```

Then load it on the NAS, either in Portainer (**Images -> Import**, upload the tar) or over SSH:

```bash
sudo docker load -i "/volume1/Shared/path/to/libation-web-14.2.1-amd64.tar"
```

`docker images` should then list `libation-web` with the `14.2.1` tag. A wrong architecture shows up as `exec format error` in the container log on first start.

### 2. Prepare the config folder

A folder on local NAS disk holding `AccountsSettings.json`, `Settings.json`, `libation-master.key` and (optionally) `LibationContext.db`, as described under [Run it on a NAS in Docker](#1-copy-libations-files-from-the-desktop). The user in `PUID`/`PGID` needs write access to it: the container mounts it as Libation's files folder and uses it directly, so the database, the log, `LibationWeb.json`, the image cache, the search index **and any account you add from the portal** are written there and are still there after the stack is redeployed.

If the folder is empty apart from the mount itself, the container creates a minimal `Settings.json` on first start and the portal comes up with an empty library; add the Audible account from the *Activity* tab and scan.

### 3. Create the stack

**Stacks -> Add stack**, name it `libation-web`, paste the stack file into the web editor, then fill in the **Environment variables** section. Portainer does not read a `.env` file, and relative paths resolve inside Portainer's own volume, so every path must be absolute:

| Variable | Example |
| --- | --- |
| `PUID` / `PGID` | `1026` / `100` (from `id <user>` over SSH) |
| `LIBATION_CONFIG_PATH` | `/volume1/docker/libation/config` |
| `LIBATION_BOOKS_PATH` | `/volume1/media/AudioBooks` - the folder your player scans |
| `LIBATION_WEB_PASSWORD` | the sign-in password, 8+ characters |
| `LIBATION_WEB_TOKEN` | optional, for Nightstand; `openssl rand -hex 32` |
| `LIBATION_WEB_PORT` | `8791` |
| `TZ` | `America/New_York` |

Deploy. The container log (**Containers -> libation-web -> Logs**) shows the entrypoint checking the config folder, then the banner with the Libation files folder, the Books folder and `Listening: http://0.0.0.0:8791`. Open `http://nas:8791` and sign in.

### Updating

Build and load the new tar, change the `image:` tag in the stack if the version changed, then **Update the stack** with "Re-pull image and redeploy" off (the image is local), or **Containers -> libation-web -> Recreate** when the tag is unchanged. The config folder is a bind mount and is used directly, so nothing is lost - including the accounts, the schedule and the database.

## Reaching it away from home

The portal itself speaks plain HTTP. Choose one of these before using it from outside the house:

**Tailscale (recommended - nothing is exposed).** Install Tailscale on the NAS (Synology has a package) and on your phone. Then the portal is `http://<NAS tailscale address>:8791` from anywhere, over an encrypted tunnel, and nobody else can reach it. `tailscale serve` can additionally give it HTTPS and a hostname if you want the app icon on the home screen (see below).

**A reverse proxy with HTTPS.** On a Synology NAS this is the same procedure Nightstand documents: a DDNS hostname, a Let's Encrypt certificate (Control Panel -> Security -> Certificate), and a reverse-proxy rule (Control Panel -> Login Portal -> Advanced -> Reverse Proxy) from `https://libation.yourname.synology.me:443` to `http://localhost:8791`. Then set `LIBATION_WEB_TRUST_PROXY` to the docker bridge gateway address (`sudo docker inspect libation-web -f '{{range .NetworkSettings.Networks}}{{.Gateway}}{{end}}'`) so the portal sees the real client address for its sign-in throttle and marks its cookie `Secure`. Only the proxy's port 443 is opened on the router; 8791 stays inside.

Either way, keep the password. Five wrong passwords from one address earn a growing delay, but a strong password is what actually protects your account.

**Home-screen app.** The page ships a web manifest, so on a phone "Add to Home Screen" gives it an icon and a full-screen window. Browsers only allow that over HTTPS or on `localhost`, which is one reason to use Tailscale Serve or the reverse proxy.

## Using it

- **Library** starts on *Not downloaded*: everything Libation knows about that is not on disk yet. Switch to *All*, *Downloaded*, *Errors*, *PDF missing* or *Not on Audible*. Search accepts the same [Lucene syntax](../features/lucene.md) as the desktop app (`author:sanderson`, `-liberated`, ...) and falls back to a plain text match when the query is not valid Lucene. Each row has **Download** (or **Re-download** for a title already on disk); tick several and use the bar that appears; **Download all new** queues everything not yet downloaded, leaving out titles Audible refused recently (the [refused-downloads wait](../features/retrying-refused-downloads.md)). **Scan Audible** asks Audible for new purchases. Tap a title for its description, file path and the other actions: PDF, convert to MP3, mark as downloaded or not downloaded.
- **Queue** shows every job with its step, percentage, bytes and time left, updated live. **Cancel** stops a download in flight the way the desktop app's queue does. Finished jobs stay listed with their result - *Done*, *Skipped* with the reason (already downloaded, daily limit reached, refused recently), *Failed* with Libation's explanation - until you clear them.
- **Activity** has the scan card (last result and the schedule below), the accounts card described below, and the tail of Libation's log.
- **About** shows the folders in use, the settings that matter here and a sign-out button.

### Scanning on a schedule

Out of the box the portal scans Audible only when **Scan now** is pressed. The scan card on **Activity** can put that on a timer instead: **Scan Audible** offers *Manually only* (the default) or every 1, 2, 3, 6 or 12 hours, or once a day, and the change takes effect immediately - there is no Save button. When automatic scanning is on, the card shows when the next scan is due.

- The first automatic scan never runs in the first five minutes after the portal starts, so a container that is restarted or recreated often does not scan every time it comes up.
- The interval is counted from the **end of the last scan**, whoever started it, so pressing **Scan now** pushes the next automatic scan out rather than having the two run back to back.
- A scan that falls due while one is already running, while an Audible sign-in is waiting to be finished, or while the queue is still downloading, simply waits and is tried again a minute later.
- **After an automatic scan, download the new titles** is off by default, and worth leaving off unless you know you want it: queuing many titles at once is what makes Audible throttle an account. With it on, a successful automatic scan queues exactly what **Download all new** would (titles Audible refused recently are left out).

The setting lives in `LibationWeb.json` in the Libation files folder - in Docker, in the mounted config folder - so it survives a restart. The desktop app neither reads nor writes it.

A download that fails is recorded exactly as it would be in the desktop app, including the wait after a refusal, so the two never disagree about a title. A title Libation is still waiting on can be forced anyway with **Try now, ignoring the recent refusal** on its detail sheet (or **Try now** on the skipped queue row), but during an Audible throttle forcing it repeatedly extends the wait, so use it once.

### Accounts

The accounts card on **Activity** lists each Audible account, its marketplace and whether its login is still valid, and lets you **Sign in again** when one says *Login needed* (tokens expire, and a config folder copied to a NAS may never have had any), **Add account** for a login Libation does not know yet, or **Remove** one. Removing an account keeps the books it has already downloaded, exactly as the desktop app does.

Signing in is the same two steps as [`login-external`](../advanced/command-line-interface.md#log-in-with-an-external-browser-login-external), with the portal standing in for the terminal:

1. the portal shows an Amazon sign-in link; open it in a new tab and sign in there;
2. the browser ends up on a page that may look broken or say it does not exist - that is expected. Copy its full address and paste it back into the portal, then **Finish sign-in**.

**Your Audible password is only ever typed on Amazon's own page.** The portal never sees it and it never passes through the API. Adding an account also offers the device registration Libation registers with (the same choice as Settings; leave it on *Default* unless Audible has been refusing download licenses), which only applies to a new sign-in.

One sign-in runs at a time. It is abandoned on its own after 20 minutes, and **Cancel** ends it immediately. An account cannot be removed while a scan or a download is running.

The portal downloads one title at a time by default. `LIBATION_WEB_CONCURRENCY` raises that to at most four; the desktop setting for parallel downloads is not used, because a NAS is usually the slower machine.

## Using it from Nightstand (or any other server)

Nothing has to change for Nightstand to *play* what the portal downloads: the files appear in the folder it scans. To *control* Libation from inside Nightstand, use the portal's API.

Set `LIBATION_WEB_TOKEN` to a long random secret (`openssl rand -hex 32`). A request carrying `Authorization: Bearer <that secret>` is accepted without a cookie, so a server can call the portal on the household's behalf while members never see the portal's own sign-in. The natural shape on the Nightstand side is one owner-only route that forwards `/api/libation/*` to `http://libation-web:8791/api/*` (both containers on one Docker network, or the NAS's LAN address) and adds the header - then a Settings panel that lists *Not downloaded* titles with a Download button and shows the queue. Nightstand's own sign-in, roles and HTTPS then cover the portal too.

```bash
curl -H "Authorization: Bearer $LIBATION_WEB_TOKEN" http://nas:8791/api/books?status=new
curl -H "Authorization: Bearer $LIBATION_WEB_TOKEN" -H "Content-Type: application/json" \
     -d '{"asins":["B0EXAMPLE1"]}' http://nas:8791/api/queue
```

Errors are returned as `{ "error": { "code": "...", "message": "..." } }`. The routes:

| Method and path | What it does |
| --- | --- |
| `GET /api/health` | Liveness (no sign-in needed). |
| `GET /api/status` | Version, folders, scan state, the scan schedule, queue counts, library counts, accounts, the settings that matter. |
| `GET /api/books?q=&status=&sort=&page=&pageSize=` | The library. `status`: `all`, `new`, `downloaded`, `error`, `partial`, `absent`, `pdf`. `sort`: `added`, `title`, `author`, `published`, `length`, `relevance`. |
| `GET /api/books/{asin}` | One title with description, categories and the path of its audio file when Libation knows it. |
| `GET /api/books/{asin}/cover?size=300` | Cover image (JPEG); `size` is 80, 300 or 500. |
| `POST /api/books/{asin}/status` `{ "status": "NotLiberated" \| "Liberated" \| "Error" }` | Set the download status by hand. |
| `GET /api/accounts` | Accounts, locale, whether each login is still valid. |
| `GET /api/accounts/locales` | The marketplaces a sign-in can use: `[{ "countryCode": "us", "name": "us" }, ...]`. |
| `GET /api/accounts/login` | `{ "pending": {...} \| null, "last": {...} \| null }` - the sign-in in progress and how the last one ended. |
| `POST /api/accounts/login` `{ "accountId": "you@example.com", "locale": "us", "deviceRegistration": null }` | Start a sign-in. `202` with `{ flowId, accountId, locale, loginUrl, startedAt }`; `200 { "alreadySignedIn": true }` when the account still has valid tokens; `409 login_pending` when one is already waiting. |
| `POST /api/accounts/login/complete` `{ "flowId": "...", "responseUrl": "https://..." }` | Finish it with the address the browser landed on. Returns `{ "status": "succeeded" \| "failed" \| "cancelled", "error": null }`. `404 no_such_flow`. |
| `POST /api/accounts/login/cancel` `{ "flowId": "..." }` | Abandon the sign-in in progress. |
| `DELETE /api/accounts/{accountId}?locale=us` | Forget an account; downloaded books are kept. `409 busy` during a scan or download. |
| `POST /api/scan` `{ "accounts": ["nickname"] }` (optional) · `GET /api/scan` | Start a library scan (`409` if one is running) · its state. |
| `GET /api/settings/portal` | `{ settings, schedule }` - the portal's own settings and what the scheduler has made of them (`nextScanAt`, `lastAutomaticScanAt`, `lastAutomaticScanError`). |
| `PUT /api/settings/portal` `{ "scanMode": "manual" \| "automatic", "scanEveryHours": 1 \| 2 \| 3 \| 6 \| 12 \| 24, "downloadAfterScan": false }` | Set the scan schedule. Saved to `LibationWeb.json`; answers the same `{ settings, schedule }`. `400 bad_request` for anything else. |
| `GET /api/queue` | Every job with progress. |
| `POST /api/queue` `{ "asins": [...], "kind": "download" \| "pdf" \| "convert", "force": false }` | Queue titles. `force` re-downloads a title already on disk and ignores a recent refusal. Returns what was added and what was skipped, with reasons. |
| `POST /api/queue/new` `{ "force": false }` | Queue everything not yet downloaded (the same selection as `libationcli liberate`). |
| `DELETE /api/queue/{id}` | Cancel a queued or running job. |
| `POST /api/queue/clear` | Remove finished jobs from the list. |
| `GET /api/events` | Server-Sent Events: `hello`, `queue`, `job`, `scan`, `schedule`, `library`, `accounts`. |
| `GET /api/logs?lines=200` | The tail of Libation's current log file. |
| `POST /api/auth/login` `{ "password": "..." }` · `POST /api/auth/logout` · `GET /api/auth/status` | Cookie sign-in for the page. |

## Environment variables

| Variable | Default | Meaning |
| --- | --- | --- |
| `LIBATION_WEB_PASSWORD` | | Sign-in password, 8+ characters. The portal refuses to start without it unless anonymous access is on. |
| `LIBATION_WEB_TOKEN` | | Bearer token for server-to-server calls. Optional. |
| `LIBATION_WEB_ALLOW_ANONYMOUS` | | `1` disables sign-in. Only on a network nobody else can reach (Tailscale, a LAN without port forwarding). |
| `LIBATION_WEB_SESSION_DAYS` | `30` | How long a sign-in lasts on a device. |
| `LIBATION_WEB_URLS` | `http://0.0.0.0:8791` | Listening address(es), `;`-separated. `--urls` and `ASPNETCORE_URLS` take precedence. |
| `LIBATION_WEB_CONCURRENCY` | `1` | Titles downloaded at once, 1 to 4. |
| `LIBATION_WEB_TRUST_PROXY` | | Reverse-proxy address(es) whose `X-Forwarded-For`/`X-Forwarded-Proto` headers are believed, or `any`. |
| `LIBATION_FILES_DIR` | the desktop app's folder | Libation files folder (same as the CLI; `--libationFiles` also works). |
| `LIBATION_BOOKS_DIR` / `LIBATION_INPROGRESS_DIR` | from `Settings.json` | Override the Books and in-progress folders for this process only; `Settings.json` is not changed. The Docker image sets them to `/data` and `/tmp`. |
| `LIBATION_MASTER_KEY_FILE` / `LIBATION_MASTER_KEY` | | Decrypt tokens exported from another machine when `libation-master.key` is not beside `AccountsSettings.json`. |

The Docker image sets `LIBATION_FILES_DIR=/config`, `LIBATION_BOOKS_DIR=/data` and `LIBATION_INPROGRESS_DIR=/tmp` for itself. `LIBATION_CONFIG_DIR` (default `/config`) names the folder the entrypoint checks and then hands to Libation as its files folder; unlike the scan/liberate image, this one stages no copy, so the scan/liberate image's `LIBATION_DB_DIR`, `LIBATION_DB_FILE` and `LIBATION_CREATE_DB` play no part here - the database is simply `LibationContext.db` in the mounted folder, created on first start if it is missing.

## Troubleshooting

- **"LibationWeb cannot start: No password is set"** - set `LIBATION_WEB_PASSWORD`, or `LIBATION_WEB_ALLOW_ANONYMOUS=1` if the portal is truly unreachable from outside a private network.
- **Books folder not found** (red banner on the Library page) - the `Books` setting, or `LIBATION_BOOKS_DIR`, names a folder this process cannot see. In Docker, check the `LIBATION_BOOKS_PATH` mount and that `PUID:PGID` may write there.
- **Scan fails with "Failed to decrypt ExistingAccessToken"** - the tokens were encrypted on another machine; copy the exported `libation-master.key` next to `AccountsSettings.json`, or set `LIBATION_MASTER_KEY_FILE`. See [Docker - encrypted tokens](docker.md#configuration).
- **Account shows "Login needed"** - use **Sign in again** on the *Activity* page (see [Accounts](#accounts)). The same thing from a shell is `LibationCli login-external` against the same Libation files folder (inside the container that is already `/config`, so no `--libationFiles` is needed), or sign in once in the desktop app and copy `AccountsSettings.json` again.
- **A title is skipped with "Audible refused this title recently"** - Libation is observing the wait described in [Retrying refused downloads](../features/retrying-refused-downloads.md). **Try now** on the title's detail sheet, or on the skipped queue row, forces an attempt regardless; repeating that during a throttle extends the wait.
- **Nothing updates live** - the *Live* chip in the header turns red when the event stream is down; the page then refreshes every 30 seconds. A reverse proxy that buffers responses breaks Server-Sent Events; DSM's proxy needs no change, nginx needs `proxy_buffering off` for `/api/events`.
- **Port already in use** - set `LIBATION_WEB_URLS` (or `LIBATION_WEB_PORT` in `.env` for Docker) to another port.
