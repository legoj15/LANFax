# LANFax Windows client spec (phases 2 and 3)

Read `design.md` for intent and `faxd-spec.md` for the API the client talks
to. Phase 1 (the `faxd` daemon) is live; this document is what the client is
built against.

## Facts about the target machines (surveyed 2026-09-29 on the dev laptop)

- Windows 11 (10.0.26200). .NET SDK 10.0.401 and runtimes 8/9/10 present;
  many older SDKs too, so the build must pin `TargetFramework` and ship a
  `global.json`.
- Printer driver `Microsoft Print To PDF` (v4, inbox driver) is installed and
  bound to the `PORTPROMPT:` port on the stock printer. The `local` parameter
  set of `Add-PrinterPort` takes only `-Name`, so a file-path port needs no
  host address. No existing port on the box is path-named, so **the silent
  write-to-path behaviour is unverified and is the first thing phase 2 must
  prove** with a disposable printer (needs elevation: a UAC prompt the user
  must accept).
- A printer named `Fax` (Microsoft Shared Fax Driver) and a shared
  `\\server1\Fax` already exist. Ours is named **`LAN Fax`**.
- Inno Setup 6 is at `E:\Program Files (x86)\Inno Setup 6\ISCC.exe` (not on C:).
  No code-signing certificate; the installer will be unsigned (SmartScreen
  warning on first run; acceptable for a LAN tool, note it in the README).
- The dev shell is not elevated. Printer install/uninstall must run elevated.

## Layout

```
client/
  global.json                       pins SDK 10.0.x
  LANFax.sln
  LANFax.Core/                      net10.0 class library: no UI
    ClientConfig.cs                 %ProgramData%\LANFax\config.json (server, port, printer name, spool dir) + per-user %LocalAppData%\LANFax\token.bin; Validate(); Load never throws, sets LoadError
    Dpapi.cs                        DPAPI via crypt32 P/Invoke (no NuGet package); Protect takes a DpapiScope (CurrentUser for the token)
    FaxApi.cs                       ISendApi (post job, test send, both to a SendEndpoint snapshot, PDF as byte[]) + HttpClient wrapper for faxd: status, jobs, inbox, post job (Idempotency-Key), test send, arm/disarm, pdf download; FaxApiConfigException
    EventStream.cs                  SSE client: GET /events?token=, Last-Event-ID, stall detection, reconnect with backoff, raises typed events
    SpoolWatcher.cs                 watches the port path; stability wait; %PDF and %%EOF checks; moves to jobs/<guid>.pdf (or rejected/<guid>.pdf); raises NewPrintJob / Error
    Numbers.cs                      normalize_number / check_number mirror (shared vectors: LANFax.Tests/numbers-vectors.json)
    Models.cs                       Job, ArmState, ServerStatus records mirroring faxd JSON
  (PrinterSetup, which detects the printer and launches the elevated install/uninstall script, lives in LANFax.App)
  LANFax.App/                       net10.0-windows WinForms, single-file self-contained win-x64
    Program.cs                      machine-wide single-instance mutex (Global\LANFax.Tray, see "One active session"); tray icon; no console
    TrayContext.cs                  NotifyIcon + menu: Open, Send a test..., Receive a test (arm 20 min), Settings, Install printer, Exit
    SendDialog.cs                   non-modal; number, subject, page count, Send / Cancel; "Send test page instead" checkbox
    Logic/                          testable plain classes: SendSession, SendExecutor, JobTracker, DialogQueue, NotificationQueue, PdfInfo, PrinterSetup, InboundAnnouncer + InboundCatchUp, InstanceGuard, PrintedBy, ...
    MainForm.cs                     (phase 3, NOT YET WRITTEN) status header, arm card with countdown, jobs grid, inbox grid, open PDF
    SettingsDialog.cs               server host/ip, port, token, "Clear token", "Test connection"
  LANFax.Tests/                     xunit: Core (config, FaxApi, EventStream, SpoolWatcher, numbers) + install/Inno script checks
  LANFax.App.Tests/                 xunit: App Logic classes (no UI; the WinForms files are not tested)
  install/
    Install-LANFaxPrinter.ps1       elevated: Add-PrinterPort -Name <spool path>; Add-Printer -Name "LAN Fax" -DriverName "Microsoft Print To PDF" -PortName <spool path>, or Set-Printer -PortName <spool path> when the printer exists on another port; idempotent
    Uninstall-LANFaxPrinter.ps1     removes printer and port; leaves %ProgramData%\LANFax
    (both scripts self-elevate by relaunching with ONE -ArgumentList command-line string, never a pre-quoted array)
    LANFax.iss                      Inno Setup: installs app to Program Files, runs Install-LANFaxPrinter.ps1 from [Code] (non-zero exit = non-fatal message), adds Run key for tray autostart; uninstall taskkills LANFax.exe in CurUninstallStepChanged(usUninstall) (after the user confirms), then removes the printer; /DAppSource=<dir> overrides the published app folder (the test suite compiles against a stub)
  build.ps1                         dotnet test, dotnet publish, iscc -> dist/LANFax-Setup.exe
```

## Spool path and the printer

- Port name and file: `C:\ProgramData\LANFax\spool\print.pdf`. The installer
  grants BUILTIN\Users Modify, inherited, on `C:\ProgramData\LANFax` itself
  (not only `spool\`): any logged-in user's print job can write the spool (the
  spooler writes as the user for Local Ports) and any Windows user can save
  `config.json`. The Inno Setup `[Dirs]` entry for `{commonappdata}\LANFax`
  must grant `users-modify` too. All three `[Dirs]` entries carry
  `Flags: uninsneveruninstall`: uninstalling keeps `config.json`, unsent
  documents (`spool\jobs`), kept copies (`spool\sent`) and rejected print
  files; a reinstall picks them up.
- The app checks the binding, not only the name: `PrinterSetup.Status` reads
  `HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers\<name>` and its
  `Port` value and returns Installed (port is `<spool dir>\print.pdf`,
  case-insensitive), Misconfigured (another port) or Missing. Misconfigured
  shows "The "LAN Fax" printer points at X, not the LANFax spool. Click here
  to fix it." and the tray item becomes "Fix ... printer"; both re-run the
  install script, which moves the queue with `Set-Printer`.
- Watcher: `FileSystemWatcher` on the spool dir (Created/Changed) plus a 2 s
  polling fallback. A job is complete when the file has existed for at least
  1 s, its size is unchanged across two checks, and it can be opened with
  `FileShare.None`. A complete file that is 0 bytes or does not start with
  `%PDF` is deleted and reported via `Error("... not a PDF ...")`. The
  `%%EOF` markers in the whole file are counted: 0 means not finished yet, so
  the watcher keeps checking while the size is unchanged for up to `EofWait`
  (10 s), then deletes it as not a PDF (a truncated document is never
  faxed); 2 or more means two jobs landed in one file, so it is moved to
  `spool\rejected\<guid>.pdf`, never sent, and reported via `Error("print.pdf
  contained N documents; the LAN Fax printer received two jobs at once. Print
  them one at a time.")`. Right before the move the size and first 1024 bytes
  are compared with what was checked (in-place truncation or rewrite guard);
  if they differ the file is checked again from the start. Otherwise move it
  to `spool\jobs\<guid>.pdf` immediately (frees the port path for the next
  job) and raise `NewPrintJob(guid, path)`.
- The timings (`MinAge` 1 s, `SizeRecheck` 500 ms, `RetryEvery` 250 ms,
  `PollEvery` 2 s, `EofWait` 10 s) are instance properties so the tests run the
  same checks about five times faster.
- A failed move (e.g. `jobs\` unwritable) raises `Error(reason, exception)`
  at most once per distinct reason per 60 s; the file stays at `print.pdf`.
  After 5 consecutive failures the watcher retries only on the 2 s poll.
  `Stop()` cancels a check in progress before it moves or raises.
- Phase-2 probe (do first, record the result here): print two jobs back to
  back to the disposable printer and record whether the second overwrites,
  appends, or fails while the first is still present. If it fails, the app
  must move the file within ~1 s (it does) and the second job will retry via
  the spooler's own retry; if it appends, the app must detect `%%EOF` count
  and split, or reject and notify. **Result still pending** (needs the
  elevated disposable printer). Until it is recorded the app defends
  anyway: a file with two or more `%%EOF` markers is rejected (moved to
  `spool\rejected\`, error balloon, never sent), and a file without one is not
  sent. Caveat: a PDF with incremental updates or linearization also has
  several `%%EOF`; Microsoft Print To PDF writes neither, so this is safe for
  the LAN Fax printer's own output.

## Send flow (phase 2)

1. `NewPrintJob` -> queued in a `DialogQueue`: one non-modal, topmost
   `SendDialog` at a time; the title shows "Send fax (N more waiting)" while
   N > 0 print jobs wait. Retries and unsent documents use the same queue.
2. Number field: accepts anything, normalized client-side the same way as
   faxd (`normalize_number` rules in `faxd-spec.md`; block 911/933/988 and
   900/976 area codes client-side too, with a clear message). The document
   line shows "N page(s), X KB" (`PdfInfo`: largest `/Count` inside an object
   with `/Type /Pages`, so outline counts are ignored, else the number of
   `/Type /Page` objects), computed on the thread pool after the dialog opens.
   Shared PC: when the spooled file's NTFS owner is another Windows user (not
   this user, a group this user is in, or SYSTEM/LOCAL/NETWORK SERVICE) the
   line ends ". Printed by <DOMAIN\user>" (`PrintedBy`). A file owned by
   BUILTIN\Administrators (what an elevated process's files often get) is
   inherently ambiguous: it says nothing about which user printed it, so it
   may be labelled either way depending on whether this user is an
   administrator.
3. Subject optional. "Send test page instead of this document" checkbox
   turns the request into `POST /test/send` and discards the PDF.
4. `POST /jobs` with `Content-Type: application/pdf`, `X-Fax-To` (the
   normalized digits; `FaxApi` throws `ArgumentException` before sending if
   the number has non-ASCII characters or no digits), `X-Fax-Subject`,
   `Idempotency-Key: <guid>`. 3 attempts in total with the same key, backoff
   2 s then 4 s, only on timeout, connection failure, 502 or 503 (never on
   500, 401 or other 4xx). 201 or 200 both mean accepted. A 2xx body that is
   not the expected JSON is a `FaxApiException` with that 2xx status (not
   retried).
5. `SendSession` (one per dialog) owns the key, a fresh GUID that is not the
   spool file id. One attempt, in order: validate the number (`SendPlan`),
   refuse a PDF over faxd's `MAX_BODY` (25 MB, `FaxApi.MaxBodyBytes`), read
   it, snapshot the endpoint (`SendEndpoint.From(cfg)`: base URI + token; a
   server/port that cannot form a URI is refused here), persist `sending`
   (step 6), `Started`, send. Every attempt of that send, FaxApi's retries
   included, goes to the snapshot, so Settings saved mid-retry cannot replay
   the key to another server; Settings also refuse Save while a send is in
   flight ("Wait for the current fax to finish"). A session locked by an
   uncertain attempt pins that server (`LockedServer`), and the record keeps
   it (`server`, the host:port authority, written by `BeginSend` and by
   `RecordFailure`), so the pin survives a restart: `SendSession.Resume`
   restores it and a later Send while Settings point elsewhere is "not sent"
   with a message naming both servers, and no request (a new token for the
   same server is fine). A record from an older version has no `server`: it
   resumes unpinned and the dialog adds "(It was sent from an earlier version;
   server not recorded.)". The snapshot also refuses a token that is not a
   valid HTTP Bearer parameter (`ClientConfig.TokenError`: visible ASCII, no
   space, quote or backslash) with `FaxApiConfigException`. A failure before
   `Started` is "not sent" and changes nothing; a `FaxApiConfigException` is
   "not sent" even after `Started` (defensive: the snapshot throws it first);
   anything thrown after it is classified by the pure `SendOutcome.Classify`
   (4xx except 409 = rejected; everything else including 409, 3xx, 5xx,
   status 0, an unreadable 2xx and non-API exceptions = uncertain; faxd sends
   409 when the key already holds a job for another number, so a job exists):
   - uncertain: the plan is frozen and the number, subject and test-page box
     become read-only with "The server may already have this job; fields are
     locked. Send will not create a duplicate. Cancel keeps the document (see
     "Unsent documents" in the LANFax menu)." Only Send (same key, same
     request) or Cancel remain. The dialog stays locked until a definite
     rejection.
   - rejected (a 4xx): fields stay (or become) editable and the key rotates.
     faxd checks the Idempotency-Key before it validates or reads the body,
     so any 4xx except 401 (and 409, which is uncertain) proves no job exists under the key: a LOCKED
     session (in this run or resumed from disk) unlocks, rotates the key and
     the record becomes `draft`. A 401 fails before the key lookup, so a
     locked session stays locked with the same key.
   `SendExecutor` runs one attempt (read PDF, write `sending`, `Started`,
   request through `ISendApi`, classify, record the failure); the dialog only
   validates and shows the result.
   `Failed(rejected|uncertain)` or `Succeeded` without a `Started` attempt
   throws `InvalidOperationException`.
6. Every spooled document's state is persisted by `JobTracker` in
   `spool\jobs\<guid>.json` beside the PDF (spool id, server job id,
   Idempotency-Key, number, subject, test-page flag, keepLocalCopies, state,
   server authority),
   written to a temp file, `Flush(true)`, then renamed over the record:
   - `sending`: written before the first byte of every attempt, with the key
     and full plan. If it cannot be written, nothing is sent.
   - `uncertain`: after an uncertain failure (same key, same plan).
   - `draft`: after a definite rejection (plan editable, no key), including
     one that unlocks a locked session.
   - `accepted`: the server took it; `serverId` set. Only these are polled
     (`TrackedJob.IsPollable` is `State == accepted` with a server id; an older
     record's `queued`/`dialing` with a server id loads as `accepted`). The PDF stays until a
     terminal state arrives by `JobChanged` or by the fallback poll
     (`GET /jobs/<id>` at startup, on every stream (re)connect and every
     60 s): `sent`/`cancelled` delete it (or move it to `spool\sent\` with
     "keep local copies") and remove the json; `failed` keeps both, shows a
     retry balloon and adds a "Retry failed fax (N)..." tray item (fresh key,
     prefilled).
   - `unknown`: the poll got 404 for a tracked job (server database reset).
     Polling stops and the PDF is offered with the "may already have been
     sent" warning. Saving Settings with another server has the same effect
     for every accepted document still pending: the new server answers 404,
     so each is re-offered with that warning.
   On accept the balloon says "Fax to <number> queued" using the server's
   `to_number`, or "Server queued this fax to X (you entered Y)". The tray's
   own test page has no PDF and is not persisted; a print job sent as a test
   page is persisted until accepted, then its PDF is deleted.
7. Cancel (button or close box): delete the PDF and its json, except while
   the dialog is locked (resumed from disk, or after an uncertain failure in
   this run), where Cancel keeps both and the document is listed under
   "Unsent documents (N)..." in the tray menu (the record says `uncertain`,
   so the next start reopens it locked). A Suspect document ("may already
   have been sent") is kept on Cancel too. A locked dialog that a definite 4xx
   unlocked is a draft again: Cancel deletes. The tray's test page has no
   document, so its Cancel keeps nothing. App exit or shutdown with a dialog
   open keeps the PDF. Re-offers from the tray menu ("Unsent documents",
   "Retry failed fax") and from a failed-fax balloon are rebuilt from the
   record on disk when opened (`JobTracker.Reoffer`), never from the request
   the dialog first opened with: a document locked in this run reopens locked
   with the same key, plan and server pin (no new key, no editable fields),
   one pinned during this run stays pinned, and a draft reopens prefilled.
   With no readable record the dialog's own lock is used. A Suspect document
   stays Suspect unless its record is locked (a rejection of a new key proves
   nothing about the job the server lost).
8. Startup, before the watcher starts, `JobTracker.Scan` returns a
   `SendRequest` per document that needs the user, and each opens straight
   into the dialog queue (no balloon to miss):
   - `sending`/`uncertain` with a key: opens LOCKED with the stored key and
     plan and "This document may already have been sent; Send will not create
     a duplicate. Cancel keeps the document."
   - `draft`: opens unlocked with number and subject prefilled.
   - `failed`: opens as a retry, prefilled, fresh key.
   - `unknown`, a corrupt json (renamed to `<guid>.json.bad`), a `sending`
     record without a key, an accepted record without `serverId`, or any
     state this version does not know: opens
     with the "This document may already have been sent" warning; Send stays
     disabled until the user ticks "Send it anyway", then uses a fresh key.
   - a guid-named PDF with no json: offered as unsent, as before. So is one
     whose json was listed but is gone when read (FileNotFound after the
     enumeration: a Discard or finished job raced the scan).
   `accepted` records are polled again; terminal leftovers are finished; a
   record whose PDF is gone is removed. A json that cannot be opened (held by
   antivirus or backup) is read again 3 times `ReadRetryDelay` (200 ms) apart; if it stays
   unavailable it is skipped for this scan (logged, not renamed, its PDF not
   offered) and handled at the next start. Only content that is not a record
   becomes `.bad`. A `<guid>.json.tmp` with no `<guid>.json` (crash between the
   flushed temp write and the rename) is adopted when it parses as a record;
   a torn temp file, or one beside an existing json, is ignored.
9. Exit: if a send is on the wire, the tray asks "A fax is being sent. Wait
   for it?". Yes disables the dialog (no second attempt can start) and waits
   while it is sending, up to 30 s in all, for the result (an accepted job is
   still recorded), No exits at once; the `sending` record already prevents a
   duplicate. On Windows shutdown during a send the dialog refuses the
   first close and registers a shutdown block reason ("A fax is being
   sent."), so Windows shows it and the user can still force the shutdown.
10. Balloons all go through `NotificationQueue`: one at a time, each ends on
   click, close or an 8 s timer (stale timers and the Closed event of the
   balloon being replaced are ignored), so a later balloon never takes over a
   pending click action. A click stops the timer before its action runs (the
   action may open a modal dialog that pumps messages), timeouts and closes
   are ignored while it runs, and the next balloon shows once it returns. Startup balloons: settings problem
   (`ClientConfig.LoadError`, click opens Settings), printer missing or on
   the wrong port (click installs/fixes), token missing. A 401 on the event stream shows "The server rejected the LANFax
   token" once per app run (again after Settings are saved); the click opens
   Settings. `SpoolWatcher.Error` shows a balloon (Core rate-limits it).
   `FaxApi`, `EventStream` and `SpoolWatcher` `Log` go to the app log.
11. The dialog queue opens the next dialog even if one fails to open (that
   document goes to "Unsent documents (N)...") or its close handler throws.
## State vocabularies (where each transition is decided)

| Vocabulary | Values | Decided in |
|---|---|---|
| `SendState` (record `state`, `JobTracker.cs`) | `sending`, `uncertain`, `draft`, `accepted`, `unknown`, plus the server's terminal `sent`/`failed`/`cancelled` | `BeginSend` (sending), `RecordFailure` (uncertain if the session is locked, else draft), `Track` (accepted), `MarkUnknown` (404), `OnJobUpdate` (terminal) |
| `SendKind` (why a dialog opens, `DialogQueue.cs`) | `New`, `Retry`, `Draft`, `Resume`, `Suspect` | `JobTracker.Scan` at startup (by record state), `RequestFor` (failed -> Retry, unknown -> Suspect), a fresh print job (New) |
| `AttemptOutcome` (`SendSession.cs`) | `NotSent`, `Rejected`, `Uncertain` | `SendOutcome.Classify` via `SendSession.Failed`: NotSent before `Started` or on `FaxApiConfigException`; 4xx except 409 = Rejected; the rest Uncertain. A locked session unlocks only on Rejected with a status other than 401 |
| `DialogOutcome` (`SendDialog.cs`) | `Accepted`, `Cancelled`, `Kept`, `Abandoned` | `SendDialog.OnClosing`: user close keeps the document (Kept) when `SendSession.CancelKeepsDocumentFor` says so (locked or Suspect), else Cancelled; app exit/shutdown = Abandoned. `TrayContext.OnSendDialogClosed` acts on it |
| `OfferCategory` (tray items, `DialogQueue.cs`) | `Unsent`, `Retry` | `TrayContext`: Kept dialogs and dialogs that failed to open -> Unsent; `OnJobChanged` failed -> Retry |

## Inbound catch-up (phase 2, tray)

- `InboundAnnouncer` decides which received faxes get "Fax received from X":
  a live `received` event and a catch-up both go through it, so each fax is
  announced once. It keeps the last 200 announced ids and the newest inbound
  `updated_at` seen in `%LocalAppData%\LANFax\announced.json` (a restart
  does not re-announce them).
- On every stream `Connected` (first connect and each reconnect) and on a
  `resync`, the tray calls `GET /jobs?direction=in&state=received&limit=100
  &updated_since=<resync since, else newest seen>` and announces the ids not
  announced yet, oldest first (more than 3 give one summary balloon, "N faxes
  were received while LANFax was not connected"). The first completed
  catch-up is a silent baseline: `announced.json` keeps an explicit
  `baselineTaken` flag and `baselineAt` (this machine's UTC time), so an empty
  inbox on the first run still ends it; an older file with ids or a time
  counts as taken. A live `received` event that is announced also ends the
  baseline (its balloon showed and the newest seen moved to it, so a later
  catch-up, even after a restart, is news). `InboundCatchUp` reads "baseline or not" BEFORE the request
  goes out, so a live `received` event during the first request cannot turn
  the whole inbox into a summary balloon. faxd lists newest first with no
  cursor, so a full page (100) is asked again with `limit=1000` (faxd's cap)
  rather than paged; beyond 1000 only the newest are checked and the skip is
  logged. A catch-up requested while one runs queues exactly one more run
  (with the earliest requested `since`; a queued reconnect with no fax seen
  yet asks for everything and so wins over a queued resync time), never dropped. Failures, a
  `FaxApiConfigException` included, are logged; the baseline stays pending.

## One active session

- The spool file and `spool\jobs` journal are machine-wide, so only one
  LANFax runs per machine: the mutex is `Global\LANFax.Tray`. A second start
  in the same session asks the running tray for its "already running"
  balloon (`Local\LANFax.Tray.ShowRunning`); a start in another Windows
  session (fast user switching, RDP) shows "LANFax is already running in
  another session" and exits. `InstanceGuard.Acquire` creates the show event
  the moment it owns the mutex (before the tray is built), and a second start
  retries the signal for up to 2 s before concluding "another session", so a
  second launch during startup is not misreported. Within the same 2 s it also
  retries the mutex, so a start during the owner's shutdown becomes the new
  owner instead of exiting. Print jobs from the other session still land in
  the spool and are offered on the active instance's desktop: on a shared PC
  only one user at a time should be signed in with LANFax.

## Monitor (phase 3, pending)

- **Not implemented yet.** There is no `MainForm`; "Open LANFax" opens the web
  page. Core's `GetStatusAsync`, `ServerStatus.Merge`, `ArmState`,
  `GetInboxAsync`, `DownloadPdfAsync`, `ArmAsync`/`DisarmAsync` and the
  `EventStream.StatusChanged`/`ArmChanged` events exist for it and have no
  production caller until then (only the inbound catch-up uses `GetJobsAsync`).
  The arm-window-expiring balloon is part of this phase.
- `MainForm` mirrors the web page: status pill (registrations, daemon,
  receiving), arm card with countdown and buttons (1 h, 4 h, 8 h, 20 min test
  window, Disarm), jobs grid (time, to, state badge, pages, error), inbox
  grid (time, from, pages), double-click opens the PDF (download to
  `%LocalAppData%\LANFax\cache\<id>.pdf`, then `ShellExecute`).
- One `EventStream` for the app's lifetime (it reads the config at each
  connect). Saving Settings cancels the run, waits for it to end and calls
  `StartAsync` again with a new token, so `LastEventId` survives; events a
  cancelled run raises are dropped.
- Live updates from `EventStream`; on reconnect reload both grids
  (`GET /jobs?updated_since=`, formatted culture-invariant as
  `yyyy-MM-ddTHH:mm:ssZ` in UTC). If no line (event or the 15 s keepalive)
  arrives for `StallTimeout` (45 s) the stream is aborted, `Disconnected`
  gets a `TimeoutException`, and it reconnects with `Last-Event-ID`.
- A `resync` event (`{"reason","since"}`, sent when the replay gap is too large)
  raises `EventStream.Resync(since)`; the tray polls tracked jobs and runs the inbound catch-up, and the
  grids reload with `GET /jobs?updated_since=<since>` instead of expecting replay.
- `FaxApi`, `EventStream` and `SpoolWatcher` each take an optional
  `Log` (`Action<string>`) for diagnostics (skipped list rows, malformed
  events, stalls, spool errors); none of them ever passes the token to it.
- Balloons: fax received (from), job sent, job failed, arm window expiring in
  5 min.
- "Send a test..." from the tray: number prompt -> `POST /test/send`.
- "Receive a test": confirm -> `POST /test/receive {"minutes": 20}` -> the
  arm card shows the countdown; balloon when a fax arrives.

## Config

`%ProgramData%\LANFax\config.json` (shared by every Windows user, no secret):
```json
{ "server": "192.0.2.10", "port": 8080, "token": null, "keepLocalCopies": false, "printerName": "LAN Fax", "spoolDir": "..." }
```
The token is per Windows user: `%LocalAppData%\LANFax\token.bin`, the raw
DPAPI blob protected with `DpapiScope.CurrentUser`, so a standard user on a
shared PC cannot read another user's token (the shared directory grants
Users Modify). Clearing the token deletes the file. Migration: a `token`
still in the shared file (machine-scope, written by older versions) is
decrypted, re-protected into the user's `token.bin` and removed from the
shared file on first load; a `token.bin` that does not decrypt for this user
falls back to that migration. The shared token is removed only once a token
decrypted here AND is in this user's `token.bin` (never because the file
merely exists). Each other user enters the token once. The token is trimmed
on load, on save and in Settings; one that is not a valid Bearer parameter
("Token must be plain ASCII without spaces") is refused by Settings, dropped
on load with `LoadError`, and refused by the send snapshot. `config.json` and
`token.bin` are written to a temp file, flushed to disk, then renamed.
Default server is the IP, not the hostname, so VPN name resolution never
matters. Settings dialog has "Test connection" (`GET /status`).
`ClientConfig.Validate()` checks port 1..65535, that the server is a bare
host name or IP (no port, scheme or path) that forms a URI
(`EndpointError()`, which the send snapshot uses), and that the printer name
and spool dir contain no `"` (they are quoted into the elevated install
command line; `Load` falls back to the default with `LoadError`, and
`PrinterSetup.BuildArguments` and both scripts' relaunch refuse a quote and
double trailing backslashes). Settings refuse to save (or test) anything it
rejects.
`ClientConfig.Load(path, tokenPath)` never throws: a blank, corrupt or
unreadable file gives defaults; an unusable port or server falls back to the
default for that value; a token that is not decryptable for this user is
dropped; all set `LoadError` (never contains the token) for the App to
show.

## Build and test rules

- Tests first in `LANFax.Tests` for Core: number normalization, spool
  stability/move logic (temp dir), FaxApi request shapes and retry (against
  an in-process `HttpListener` fake), EventStream parsing including `id:`
  and `Last-Event-ID`, config round trip.
- `build.ps1` is the one command: `dotnet test`, `dotnet publish -c Release
  -r win-x64 --self-contained -p:PublishSingleFile=true`, then Inno Setup.
- No third-party NuGet packages in Core or App beyond the BCL. DPAPI is a
  crypt32 P/Invoke in `Dpapi.cs` (`System.Security.Cryptography.ProtectedData`
  is a NuGet package, not part of the shared framework). Tests may use xunit.
- Number rules are pinned by one vector file used on both sides:
  `client/LANFax.Tests/numbers-vectors.json` and its identical copy
  `server/tests/numbers-vectors.json` (`tests.test_numbers_vectors` checks
  both copies match). Edit both together.
- The app never stores the token in plain text, never in the shared config,
  and never logs it.
