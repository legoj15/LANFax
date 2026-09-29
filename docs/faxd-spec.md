# faxd module spec (phase 1)

`faxd` is the server daemon described in `design.md`. Phase 1 rule: **the
existing bash pipeline keeps doing the work**. faxd is an ingress (API writes
files into the outbox) and an observer (it derives job state from the spool,
the share folders, receipts and Asterisk Manager Interface events) and serves
an HTTP API plus a web page. Nothing in `/usr/local/bin/*.sh`, the systemd
units, or the Asterisk templates is modified in phase 1.

## Constraints

- Python 3.12 on the server, 3.14 on the dev machine. **Stdlib only.** No
  pip, no `cgi` module (removed in 3.13), no `asyncio` (threads are fine and
  simpler to test), no third-party test runner: `python -m unittest discover`.
- Target under 30 MB RSS. Poll, do not busy-loop. Default poll interval 2 s.
- Every path and port comes from `Config` so tests run against temp dirs on
  Windows. No hard-coded `/srv/fax` outside `config.py` defaults.
- Tests first. Each module ships with `tests/test_<module>.py` that fails
  before the implementation exists and passes after.
- Layout:

```
server/
  faxd/            python package (importable as `faxd`)
    __init__.py  __main__.py  main.py  config.py  db.py  events.py
    spool.py  ami.py  arm.py  enqueue.py  testpage.py  api.py
    web/index.html
  tests/           unittest modules, run from server/ with
                   `python -m unittest discover -s tests -t .`
  faxd.service     systemd unit
  install.sh       idempotent server-side installer (run as root)
  deploy.ps1       from Windows: copies server/ to faxserver and runs install.sh
```

## config.py

```python
@dataclass
class Config:
    share_root: str = "/srv/fax"          # outbox/ sending/ sent/ failed/ inbox/ queue-meta/ ARM-STATUS.txt
    outqueue: str = "/var/spool/asterisk/outqueue"
    outgoing_done: str = "/var/spool/asterisk/outgoing_done"
    db_path: str = "/var/lib/faxd/faxd.db"
    http_bind: str = "0.0.0.0"
    http_port: int = 8080
    token: str = ""                        # FAXD_TOKEN; empty = refuse to start
    ami_host: str = "127.0.0.1"
    ami_port: int = 5038
    ami_user: str = "faxd"
    ami_secret: str = ""
    arm_script: str = "/usr/local/bin/fax-arm.sh"
    poll_interval: float = 2.0
    station_id: str = "LANFax"             # printed on test pages
    faxin_ext: str = "300"
    log_path: str = "/var/log/faxserver.log"

def load(env_file="/etc/faxserver/settings.env", environ=os.environ) -> Config
```

`load` reads `KEY=value` lines from the env file (ignore blanks/comments,
strip optional quotes), then overlays `os.environ`. Keys: `FAXD_SHARE_ROOT`,
`FAXD_OUTQUEUE`, `FAXD_OUTGOING_DONE`, `FAXD_DB_PATH`, `FAXD_HTTP_BIND`,
`FAXD_HTTP_PORT`, `FAXD_TOKEN`, `FAXD_AMI_HOST`, `FAXD_AMI_PORT`,
`FAXD_AMI_USER`, `FAXD_AMI_SECRET`, `FAXD_ARM_SCRIPT`, `FAXD_POLL_INTERVAL`,
`FAXD_STATION_ID`, `FAXIN_EXT`, `FAXD_LOG_PATH`. Helper properties:
`outbox`, `sending`, `sent`, `failed`, `inbox`, `queue_meta`, `arm_status_file`.

## events.py

```python
class EventBus:
    def subscribe(self) -> "queue.Queue[dict]"      # each subscriber gets its own queue
    def unsubscribe(self, q) -> None
    def publish(self, type: str, **data) -> None    # puts {"type": type, "ts": iso8601, **data}
```

Thread-safe. Event types used across modules: `job` (data: the job dict),
`arm` (data: arm state dict), `status` (data: status dict), `received`
(data: the inbound job dict).

## db.py

sqlite3, WAL mode, one connection per thread (use `threading.local`).

```python
JOB_STATES = ("queued", "converting", "dialing", "sending", "sent", "failed", "received")

class Registry:
    def __init__(self, path: str)               # creates schema if missing
    def upsert_job(self, job: dict) -> dict     # insert or update by id; sets updated_at; returns row
    def set_state(self, job_id, state, *, pages=None, error=None, detail=None) -> dict | None
                                                 # also appends to job_events; sets finished_at for sent/failed/received
    def get_job(self, job_id) -> dict | None
    def find_by_filename(self, filename) -> dict | None       # filename = basename in the share
    def find_by_ast_jobid(self, ast_jobid) -> dict | None
    def list_jobs(self, *, direction=None, states=None, limit=100, since=None) -> list[dict]
    def job_events(self, job_id) -> list[dict]
    def close(self)
```

Job dict columns: `id` (text pk, faxd's own: `lf`+12 hex for api-originated,
`sh`+12 hex for share-dropped, `in`+12 hex for inbound), `direction`
(`out`|`in`), `state`, `filename` (basename in share), `to_number`,
`from_number`, `subject`, `origin` (`api`|`share`|`test`|`inbound`),
`client` (ip or hostname), `ast_jobid` (the `jYYYYMMDD-HHMMSS-...` id from the
bash pipeline, nullable), `pages` (int, nullable), `error` (text, nullable),
`pdf_path` (current absolute path of the pdf, nullable), `created_at`,
`updated_at`, `finished_at` (iso8601 UTC). `job_events`: id, job_id, ts,
state, detail.

## spool.py

```python
class SpoolObserver:
    def __init__(self, cfg: Config, reg: Registry, bus: EventBus)
    def scan_once(self) -> None        # one reconciliation pass; idempotent; safe to call from tests
    def run(self, stop: threading.Event) -> None   # loop scan_once every cfg.poll_interval
    def arm_state(self) -> dict        # parsed from ARM-STATUS.txt, see arm.py format
```

Reconciliation rules for `scan_once`, in order:

1. `outbox/*.pdf|*.tif|*.tiff` (case-insensitive, skip names starting
   `ARM-`/`DISARM`, skip dotfiles): if no row `find_by_filename`, create
   `sh…` row, direction out, state `queued`, origin `share`, `to_number` =
   digits before first `_` (apply the same 10-digit -> `1`-prefix rule as the
   bash script), `pdf_path` = that file.
2. `outqueue/*.env`: parse `KEY=value` lines (values may be `%q`-quoted by
   bash: strip surrounding quotes and `\ ` escapes). `SRCFILE` basename is the
   share filename. Match row by filename; if none, create `sh…` row. Set
   `ast_jobid=JOBID`, `to_number=TO`. If `outqueue/<JOBID>.result` does not
   exist and state is `queued`/`converting`, set `dialing`. `SELFTEST.env`
   maps to `to_number="000"`.
3. `sending/<filename>` exists and state is `queued` -> `dialing`.
4. `sent/<ast_jobid>.txt` / `failed/<ast_jobid>.txt` receipts: parse lines
   `Status:`, `To:`, `Pages:`, `Error:`, `Date:`. Set `sent` (pages) or
   `failed` (error). `pdf_path` = `sent/<filename>` or `failed/<filename>` if
   present. Receipt names are `<ast_jobid>.txt`; match via
   `find_by_ast_jobid`, else by scanning rows whose `filename` sits beside it.
5. `failed/invalid-number-<filename>`, `failed/convert-failed-<filename>`:
   match by the suffix filename; state `failed`, error `invalid number` /
   `conversion failed`, `pdf_path` = that file.
6. `inbox/fax_<YYYYMMDD-HHMMSS>_from_<id>.pdf`: if no row with this filename,
   create `in…` row, direction in, state `received`, origin `inbound`,
   `from_number` = `<id>`, `created_at` from the timestamp in the name (local
   time -> UTC), `pdf_path`. Publish `received`.
7. `failed/recv_<ts>_from_<id>.txt`: inbound failed row (state `failed`,
   direction in, error from the `Error:` line).
8. Re-read `ARM-STATUS.txt` when its mtime changes; publish `arm`.

Every state change publishes `job` with the full job dict. Never delete or
move files; the observer only reads.

## ami.py

Blocking socket client on its own thread, reconnects with backoff (1 s
doubling to 30 s).

```python
class AmiClient:
    def __init__(self, host, port, user, secret, on_event: Callable[[dict], None], logger=None)
    def start(self) -> None; def stop(self) -> None
    def connected(self) -> bool
    def action(self, name: str, timeout=5.0, **fields) -> dict   # sends ActionID, returns the Response packet
                                                                # (and for list responses, key "list": [packets])
    def db_get(self, family, key) -> str | None    # via DBGet action; None if not found
    def db_put(self, family, key, value) -> bool
    def db_del(self, family, key) -> bool
    def get_var(self, channel, var) -> str | None  # via Getvar
```

Protocol: on connect read the banner line, send
`Action: Login\r\nUsername: …\r\nSecret: …\r\nEvents: on\r\n\r\n`, then
packets are `Key: Value` lines terminated by a blank line. `on_event`
receives every packet that has an `Event` key. Tests use a fake AMI server on
localhost (a thread with a listening socket) that replays scripted packets.

```python
class AmiProgress:
    """Maps AMI events to job state. Constructed with (reg, bus, ami)."""
    def handle(self, ev: dict) -> None
```

Rules (field names confirmed on the server 2026-09-29, Asterisk 20.6,
`res_fax.conf` has `statusevents=yes`):
- The call file's `Set: JOBID=…` / `Set: FAXTO=…` lines produce `VarSet`
  events (`Channel`, `Uniqueid`, `Variable`, `Value`) on the outbound
  channel. On `VarSet` with `Variable: JOBID`, remember
  `Uniqueid -> ast_jobid` and look the row up via `find_by_ast_jobid`
  (retry on the next event if the row does not exist yet). State `dialing`.
  No `Getvar` round-trip is needed.
- `FAXStatus` (fields: common channel header, `Operation` send|receive|gateway,
  `Status` free text, `LocalStationID`, `FileName`) on a known `Uniqueid`
  -> state `sending`, `detail` = Status text. It carries **no page number**.
  Optional: while any job is `sending`, poll the `FAXSessions` action every
  2 s and use `FAXSessionsEntry`/`FAXSession` (`PageNumber`,
  `PagesTransmitted`) to set `pages` for live progress.
- `SendFAX` (completion; fields `LocalStationID`, `RemoteStationID`,
  `PagesTransferred`, `Resolution`, `TransferRate`, `FileName`) -> record
  `pages` and `detail`, but do **not** set sent/failed: the receipt in the
  share (spool.py rule 4) is the source of truth for terminal states because
  it carries `FAXOPT(status)`/`error` which AMI events do not.
- `Hangup` on a known `Uniqueid` -> forget the mapping; no state change.
- `ReceiveFAX`/`FAXStatus` with `Operation: receive` -> publish `status`
  with `{"receiving": true, "from": CallerIDNum}`; terminal state comes from
  the inbox file. `OriginateResponse` with `Response: Failure` on a known
  channel -> `detail` "dial attempt failed" (pbx_spool retries up to 2 more
  times, 300 s apart; the job stays `dialing`).
- `FAXStatus`, `SendFAX`, `ReceiveFAX` exist only as events; the FAX actions
  are `FAXSession`, `FAXSessions`, `FAXStats`. `DBGet`/`DBPut`/`DBDel`,
  `Getvar`, `Setvar`, `Originate`, `UserEvent`, `PJSIPShowRegistrationsOutbound`
  are available. The AMI user needs `read=system,call,user,reporting,dialplan`
  and `write=system,call,reporting`.

## arm.py

```python
class ArmController:
    def __init__(self, cfg: Config, bus: EventBus, runner=subprocess.run)
    def state(self) -> dict     # {"armed": bool, "until": iso8601|None, "remaining_min": int, "ext": str, "changed": iso8601|None, "raw": str}
    def arm(self, minutes: int, origin: str) -> dict     # runs: sudo -n <arm_script> <minutes>M "<origin>"
    def disarm(self, origin: str) -> dict                # runs: sudo -n <arm_script> off "<origin>"
```

`state()` parses `ARM-STATUS.txt`, whose two formats are:

```
Fax receiver: ARMED - receiving faxes on ext 300
Window ends: 2026-09-29 12:35 EDT (~1h 00m remaining)
Last change: Tue Sep 29 11:35:12 EDT 2026
```
```
Fax receiver: DISARMED - send-only; inbound calls ring the phones and go to the SIP provider voicemail
Last change: ...
```

`remaining_min` is computed from `Window ends` vs now (server local time),
not from the `~…` text. After `arm`/`disarm` re-read and publish `arm`.

## enqueue.py

```python
def safe_slug(s: str, maxlen=24) -> str            # [A-Za-z0-9-] only
def normalize_number(s: str) -> str                # digits only; 10-digit NANP -> "1"+digits; raise ValueError if < 2 digits
def enqueue_pdf(cfg, reg, bus, pdf_bytes: bytes, to: str, *, subject="", origin="api", client="") -> dict
```

Writes `<number>_<slug>-<id>.pdf` where `<id>` is the new `lf…` row id and
slug is `safe_slug(subject) or "fax"`. Write to `outbox/.<name>.part` then
`os.replace` to the final name (atomic; the bash script's size-stability
check then sees a complete file). Write `queue-meta/<name>.json` with
`{id, to, subject, origin, client, created_at}`. Create the row (state
`queued`, pdf_path) and publish `job`. Reject bodies that do not start with
`%PDF` (ValueError). Number `000` is the offline self-test and is allowed.

## testpage.py

```python
def make_test_pdf(*, to: str, station: str, when: datetime, note: str = "") -> bytes
```

Hand-built single-page Letter PDF (612x792), Helvetica (a base-14 font, no
embedding), text lines: "LANFax test page", "To: <to>", "From: <station>",
"Sent: <when local>", the note, and a block of increasing-size text plus a
row of `|||||` at several sizes so a received copy shows resolution. Must
have correct xref offsets. Test: bytes start `%PDF-1.4`, end with
`%%EOF`, contain `/Type /Page`, contain the number, and if `gs` is on PATH,
`gs -dNOPAUSE -dBATCH -sDEVICE=nullpage` exits 0.

## api.py

`ThreadingHTTPServer` + `BaseHTTPRequestHandler`, `daemon_threads=True`.

```python
class Api:
    def __init__(self, cfg, reg, bus, arm: ArmController, status_fn: Callable[[], dict],
                 enqueue_fn, testpage_fn)
    def serve_forever(self); def shutdown(self)
```

Auth: every route except `GET /` and `GET /healthz` requires header
`Authorization: Bearer <cfg.token>`; otherwise 401 JSON `{"error":"unauthorized"}`.
Compare with `hmac.compare_digest`. All JSON responses `application/json; charset=utf-8`.
Errors: `{"error": "<message>"}` with 400/404/405/500.

Routes:

| Method, path | Behaviour |
|---|---|
| `GET /` | serves `web/index.html` |
| `GET /healthz` | `{"ok":true}` |
| `GET /status` | `status_fn()` merged with `{"arm": arm.state()}` |
| `GET /jobs?direction=out&state=sent,failed&limit=50` | `{"jobs":[…]}` newest first |
| `GET /jobs/<id>` | job dict + `"events": [...]` |
| `GET /jobs/<id>/pdf` | streams `pdf_path` as `application/pdf`, `Content-Disposition: inline; filename="<filename>"`; 404 if missing |
| `POST /jobs` | body is the PDF (`Content-Type: application/pdf`), headers `X-Fax-To` (required), `X-Fax-Subject` (optional). Returns 201 with the job dict. Max body 50 MB. |
| `GET /inbox?limit=50` | inbound jobs newest first |
| `POST /arm` | JSON `{"minutes": 60}` -> arm state |
| `POST /disarm` | -> arm state |
| `POST /test/send` | JSON `{"to": "…", "note": ""}` -> builds a test page, enqueues with origin `test`, subject `test page`; returns job |
| `POST /test/receive` | JSON `{"minutes": 20}` -> arms and returns arm state (alias of /arm with default 20) |
| `GET /events` | Server-Sent Events: `event: <type>\ndata: <json>\n\n`; `: keepalive\n\n` every 15 s; on connect sends one `status` and one `arm` event |

Origin string passed to `arm` is `"faxd api from <client ip>"`. `client` on
jobs is the client ip.

## web/index.html

One file, no build step, no external CDN (must work offline over VPN).
Sections: header with status pill (registrations, daemon ok), arm card
(state + countdown, buttons 1h / 4h / 8h / 20 min test window / Disarm),
send card (file picker + number + subject + Send; "Send test page to number"
+ Send test), jobs table (time, to, state, pages, error, open pdf), inbox
table (time, from, open pdf). Token: prompt once, keep in `localStorage`,
send as Bearer header on every fetch; open PDFs via fetch -> blob ->
`window.open`. Subscribes to `/events` with `EventSource` — note EventSource
cannot set headers, so `/events` additionally accepts `?token=` **only for
this route**. Refresh tables on `job`/`received` events; update arm card on
`arm`. Phone-width friendly (single column under 700 px).

## main.py

```python
def build_status_fn(cfg, reg, ami) -> Callable[[], dict]
    # {"daemon": {"version", "started_at", "ami_connected"}, "queue": {"queued","dialing","sending"} counts,
    #  "registrations": [{"name","status"}] from AMI action PJSIPShowRegistrationsOutbound if connected else [],
    #  "last_sent": job|None, "last_received": job|None}
def main(argv=None) -> int
```

Starts: Registry, EventBus, SpoolObserver thread, AmiClient (+AmiProgress),
ArmController, Api. Logs to stdout via `logging` (journald captures it).
Refuses to start with an empty token (exit 2 with a clear message). Handles
SIGTERM/SIGINT for clean shutdown. `python -m faxd` runs `main()`.

## faxd.service

`Type=simple`, `User=faxd`, `Group=faxd`, `SupplementaryGroups=asterisk fax`,
`ExecStart=/usr/bin/python3 -m faxd`, `WorkingDirectory=/opt/faxd`,
`Environment=PYTHONPATH=/opt/faxd`, `Restart=on-failure`, `RestartSec=5`,
`MemoryMax=96M`, `After=network-online.target asterisk.service`,
`WantedBy=multi-user.target`.

## install.sh (root, idempotent)

0. Swap: the VM has 805 MB RAM, ~32 MB available at survey time and no
   swap. If `swapon --show` is empty, create `/swapfile` (512 MB, `chmod 600`,
   `mkswap`, `swapon`, add to `/etc/fstab`). Print what was done.
1. `useradd --system --home /var/lib/faxd faxd` if missing; add to groups
   `asterisk` (needed: `/var/spool/asterisk` and `outqueue` are 750
   `asterisk:asterisk`, files inside are 666) and `fax`; `mkdir -p
   /var/lib/faxd` owned by faxd.
2. `rsync -a --delete` the package dir to `/opt/faxd/faxd` and web files.
3. Sudoers `/etc/sudoers.d/faxd`: `faxd ALL=(root) NOPASSWD: /usr/local/bin/fax-arm.sh` (mode 0440, validate with `visudo -c`).
4. `/etc/asterisk/manager.d/faxd.conf`: `[faxd]` with `secret` (generate once,
   keep if present), `deny=0.0.0.0/0.0.0.0`, `permit=127.0.0.1/255.255.255.255`,
   `read=system,call,user,reporting,dialplan`, `write=system,call,reporting`. The file must be readable by the `asterisk` user (`chown root:asterisk`, mode 0640).
   Then `asterisk -rx "manager reload"`. Write the same secret into
   `/etc/faxserver/settings.env` as `FAXD_AMI_SECRET=` (add or replace line).
5. `FAXD_TOKEN=` in `settings.env`: generate 32 hex if absent. Print it once at the end.
6. Ensure `/srv/fax/queue-meta` exists and is writable by faxd.
7. Install unit, `systemctl daemon-reload`, `enable --now faxd`, then
   `curl -fsS http://127.0.0.1:8080/healthz`.
8. If `ufw` is installed and active, `ufw allow from 192.168.0.0/16 to any port 8080 proto tcp`. Do not enable ufw here.

## deploy.ps1

`scp -r server/ faxserver:~/lanfax-deploy/` then
`ssh faxserver 'sudo -n bash ~/lanfax-deploy/install.sh'`, then prints
`/status` via curl using the token read back from the server. Re-runnable.

## Running on the server

- Service `faxd` (systemd, user `faxd`). Logs: `journalctl -u faxd -f`. Restart: `sudo systemctl restart faxd`.
- Config lives in `/etc/faxserver/settings.env` (root:faxd 0640): `FAXD_TOKEN`, `FAXD_AMI_SECRET` and the other `FAXD_*` keys above. The AMI user is defined in `/etc/asterisk/manager.d/faxd.conf` (owned by the installer). Database: `/var/lib/faxd/faxd.db` (safe to delete; history is rebuilt from the share on start).
- Redeploy from Windows: `cd server; .\deploy.ps1` (idempotent; copies, runs `install.sh`, prints `/status`).
- Listens on `0.0.0.0:8080`; `GET /healthz` needs no token.
- Facts learned against the real server (first deployment):
  - The offline self-test (number `000`) writes TWO env files, `<jobid>.env` and `SELFTEST.env` (JOBID=SELFTEST, same SRCFILE), and its receipt is `sent/SELFTEST.txt` (`To: SELFTEST`), not `<jobid>.txt`. The `<jobid>.env` job never gets a `.result`. The observer keeps the pipeline's own id, matches `SELFTEST.txt` through `SELFTEST.env`'s SRCFILE and ignores a receipt older than `SELFTEST.env`.
  - The self-test emits no `sending` state (no FAXStatus mapping on the Local channel): queued -> dialing -> sent in about 25 s.
  - Historic receipts (`sent|failed/j<stamp>-<slug>.txt`) with no row are adopted as finished rows, dated from the job id and the receipt `Date:`; the pdf is matched by the alphanumeric prefix of the file name.
  - `settings.env` is root-only (0600) before install; the installer changes the group to `faxd` and mode to 0640.
  - AMI registrations are named `reg-in` and `reg-out`.
