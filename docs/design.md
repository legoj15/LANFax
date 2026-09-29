# LANFax design assessment (2026-09-29)

Status: proposal, not started. Written after surveying the live server.

## What exists today (server `faxserver`, 192.0.2.10)

- Ubuntu 24.04 Hyper-V VM, **1 vCPU, 805 MB RAM** (54 MB free at survey time;
  two Claude remote sessions were holding ~330 MB, Asterisk ~45 MB).
- Asterisk 20.6 + `res_fax_spandsp`, G.711 audio-mode fax against the SIP provider
  (192.0.2.20). The provider refuses T.38; do not enable it. Two SIP registrations:
  300 inbound, 301 outbound (301 -> 300 is the real end-to-end loop test).
- Everything is bash + systemd + Samba:
  - `fax-outbox.path` fires `fax-send-queue.sh` on any change to `/srv/fax/outbox`.
    It parses the phone number from the filename, Ghostscripts PDF -> TIFF G4,
    writes an Asterisk call file, moves the PDF to `sending/`.
  - Dialplan calls `fax-result.sh` / `fax-received.sh`, which move files to
    `sent/` `failed/` `inbox/` and write `.txt` receipts.
  - `fax-done.timer` (every minute) sweeps never-answered calls and re-runs the queue.
  - Arming = AstDB key `fax/armed_until`, set by `fax-arm.sh`, driven by
    `ARM-<n>H.txt` control files dropped in the outbox. `ARM-STATUS.txt` is the
    only status surface.
  - Samba share `[fax]` = `/srv/fax`, user `fax`, `map to guest = never`,
    SMB2+ only, everything 0777/0666.
- AMI is enabled on 127.0.0.1:5038 (unused). Python 3.12 present. No Node, no Go.
- Server-side memory for the box lives in
  `faxserver:~/.claude/projects/-home-admin/memory/` (architecture + deferred hardening).

## What went wrong on 2026-09-29 over VPN

- `faxserver` did not resolve: NetBIOS/mDNS do not cross the VPN. Only the
  IP worked.
- `\192.0.2.10` listed but `\192.0.2.10\fax` failed for a while, then
  "started working out of the blue". Server logs show a clean session from the
  VPN client (198.51.100.4, 11:42) with no errors at log level 0, so the server
  cannot prove the cause. Best fits: Windows credential cache keyed on server
  *name* (creds saved for `faxserver` do not apply to the IP) and/or SMB3 over
  VPN MTU blackholing that later self-corrected. Both are SMB/Windows-layer
  problems that a single-port HTTP transport sidesteps entirely.
- At 12:13 the laptop tried to open a share literally named
  `home - provider.pdf`: a PDF was dropped at the server root instead of in
  `fax\outbox`. That is the UX failure the client is meant to remove.

## Prior art (has this been built before, better?)

| System | Verdict |
|---|---|
| Windows Server Fax role + Windows Fax and Scan | Exactly the UX wanted (shared fax printer, status, inbox, sent). Needs a modem or fax board on the server; the client protocol is MS-FAX RPC over named pipes plus point-and-print driver download. Not implementable from Linux in any sane budget. |
| HylaFAX(+) + iaxmodem -> Asterisk, AvantFAX web UI, Winprint HylaFAX Reloaded | The mature open-source version of the whole idea. Windows print client prompts for a number. Costs: softmodem hop, hfaxd, PHP/MySQL, heavy for this VM, and the box was deliberately built without HylaFAX. Rejected. |
| FreePBX fax module / Fax Pro, ICTFax (FreeSWITCH) | Web upload + email, T.38-centric, no print client, heavy. Rejected. |
| Implementing the hfaxd protocol so old HylaFAX Windows clients work | Considered. Clients are abandoned-era, no arming concept, poor status. Rejected. |

Conclusion: the server core (Asterisk res_fax over Talk, arming for a shared
line) is not off the shelf and is worth keeping. The missing layer is a
proper job model, an API, and a thin Windows client. This is an addition, not
a rewrite.

## Proposed architecture

### Server: one daemon, `faxd` (Python 3.12 stdlib only, systemd service)

Owns the state that today is scattered across filenames, AstDB and `.txt`
receipts.

- **Job registry**: sqlite at `/var/lib/faxd/faxd.db`. States:
  `queued -> converting -> dialing -> sending(page n/m) -> sent | failed`.
  Inbound faxes are rows too (`received`).
- **Ingress**: HTTP API and the existing outbox folder both call the same
  enqueue function. The SMB drop-a-file path stays as the zero-client fallback.
- **Live progress**: AMI on 127.0.0.1:5038 (already enabled): `FAXStatus`,
  `OriginateResponse`, `Hangup`, `UserEvent` from the dialplan. Replaces
  polling receipts and gives page-by-page progress.
- **Arming**: same AstDB key, exposed as arm/disarm/extend with a countdown.
- **HTTP API** (single port, e.g. 8080, LAN + VPN, bearer token from
  `/etc/faxserver/settings.env`):
  - `GET /status` registrations, arm state, queue depth, daemon health
  - `POST /jobs` multipart: pdf, to, subject, cover(bool) -> job id
  - `GET /jobs`, `GET /jobs/{id}`, `GET /jobs/{id}/pdf`
  - `GET /inbox`, `GET /inbox/{id}/pdf`
  - `POST /arm {minutes}`, `POST /disarm`
  - `POST /test/send {to}` canned test page to a number
  - `POST /test/receive {minutes}` arm and return a watch token
  - `GET /events` server-sent events: job state changes, fax received, arm changes
- **Web page** served by the same daemon: status, arm buttons, inbox, send
  test. Works from a phone over VPN, which the SMB share never could. Likely
  higher value than the Windows client for the *monitoring* half.
- **Subject / header**: goes into the job row and into the fax header line
  via `FAXOPT(headerinfo)`. Cover page generation is v2 (needs a renderer).
- **Footprint target**: under 30 MB RSS. No Node, no Docker, no pip.

### Windows client: one tray app, `LANFax` (.NET 8, single self-contained exe)

- **Printer**: installs a printer named "LAN Fax" using the built-in
  *Microsoft Print To PDF* driver bound to a Local Port whose name is a file
  path under `%ProgramData%\LANFax\spool\`. That driver writes silently to a
  path port. Install is two PowerShell cmdlets (`Add-PrinterPort`,
  `Add-Printer`); no driver signing, no port monitor DLL.
- **Print flow**: tray app watches the spool path; new file -> dialog for
  number, subject, "send test page instead" -> `POST /jobs` -> toast on
  sent/failed.
- **Monitor**: window with status, arm countdown + arm/disarm buttons, jobs
  (sending/sent/failed) and inbox with open-in-viewer, "Send a test" and
  "Receive a test (arm 20 min)" buttons. Subscribes to `/events`.
- **Config**: server host or IP + token. Defaults to IP so VPN name
  resolution is irrelevant.
- Also: drag-drop a PDF onto the tray icon / Explorer "Send to" entry.

Rejected client options: IPP printer (Windows adds it natively but there is
no way to ask for a number at print time), real port monitor DLL (signing and
isolation pain), Python client (bundling), Windows Fax and Scan (see above).

### Security notes (ties into deferred hardening)

- The API is a "spend money" surface: token required, bind LAN only, log every
  send with caller IP.
- Once the client exists, the share can become read-only on `inbox/` `sent/`
  and the outbox watcher can be disabled, closing the 0777 hole.
- ufw is installed but inactive. Opening one more port is the moment to turn
  it on.
- Fix root cause of the name failure too: static DNS record for `faxserver`
  in the router controller so it resolves over VPN.

### Delivery order

1. `faxd` job registry + AMI progress + API + web page, running beside the
   existing scripts (they keep working unchanged).
2. Windows tray app: printer install + send dialog.
3. Monitor window + events.
4. Retire the outbox watcher and tighten the share.
