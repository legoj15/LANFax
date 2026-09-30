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
    ClientConfig.cs                 %ProgramData%\LANFax\config.json (server, port, DPAPI token, printer name, spool dir); Load never throws, sets LoadError
    Dpapi.cs                        machine-scope DPAPI via crypt32 P/Invoke (no NuGet package)
    FaxApi.cs                       ISendApi (post job, test send) + HttpClient wrapper for faxd: status, jobs, inbox, post job (Idempotency-Key), test send, arm/disarm, pdf download
    EventStream.cs                  SSE client: GET /events?token=, Last-Event-ID, stall detection, reconnect with backoff, raises typed events
    SpoolWatcher.cs                 watches the port path; stability wait; %PDF and %%EOF checks; moves to jobs/<guid>.pdf (or rejected/<guid>.pdf); raises NewPrintJob / Error
    Numbers.cs                      normalize_number / check_number mirror (shared vectors: LANFax.Tests/numbers-vectors.json)
    Models.cs                       Job, ArmState, ServerStatus records mirroring faxd JSON
  (PrinterSetup, which detects the printer and launches the elevated install/uninstall script, lives in LANFax.App)
  LANFax.App/                       net10.0-windows WinForms, single-file self-contained win-x64
    Program.cs                      single-instance mutex; tray icon; no console
    TrayContext.cs                  NotifyIcon + menu: Open, Send a test..., Receive a test (arm 20 min), Settings, Install printer, Exit
    SendDialog.cs                   non-modal; number, subject, page count, Send / Cancel; "Send test page instead" checkbox
    Logic/                          testable plain classes: SendSession, SendExecutor, JobTracker, DialogQueue, NotificationQueue, PdfInfo, PrinterSetup, ...
    MainForm.cs                     (phase 3) status header, arm card with countdown, jobs grid, inbox grid, open PDF
    SettingsDialog.cs               server host/ip, port, token, "Clear token", "Test connection"
  LANFax.Tests/                     xunit: Core only (no UI)
  install/
    Install-LANFaxPrinter.ps1       elevated: Add-PrinterPort -Name <spool path>; Add-Printer -Name "LAN Fax" -DriverName "Microsoft Print To PDF" -PortName <spool path>; idempotent
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
   read the PDF, persist `sending` (step 6), `Started`, send. A failure before
   `Started` is "not sent" and changes nothing; anything thrown after it is
   classified by the pure `SendOutcome.Classify` (4xx = rejected, everything
   else including 3xx, 5xx, status 0, an unreadable 2xx and non-API
   exceptions = uncertain):
   - uncertain: the plan is frozen and the number, subject and test-page box
     become read-only with "The server may already have this job; fields are
     locked. Send will not create a duplicate. Cancel keeps the document (see
     "Unsent documents" in the LANFax menu)." Only Send (same key, same
     request) or Cancel remain. The dialog stays locked until a definite
     rejection.
   - rejected (a 4xx): fields stay (or become) editable and the key rotates.
     faxd checks the Idempotency-Key before it validates or reads the body,
     so any 4xx except 401 proves no job exists under the key: a LOCKED
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
   Idempotency-Key, number, subject, test-page flag, keepLocalCopies, state),
   written to a temp file, `Flush(true)`, then renamed over the record:
   - `sending`: written before the first byte of every attempt, with the key
     and full plan. If it cannot be written, nothing is sent.
   - `uncertain`: after an uncertain failure (same key, same plan).
   - `draft`: after a definite rejection (plan editable, no key), including
     one that unlocks a locked session.
   - `accepted`: the server took it; `serverId` set. The PDF stays until a
     terminal state arrives by `JobChanged` or by the fallback poll
     (`GET /jobs/<id>` at startup, on every stream (re)connect and every
     60 s): `sent`/`cancelled` delete it (or move it to `spool\sent\` with
     "keep local copies") and remove the json; `failed` keeps both, shows a
     retry balloon and adds a "Retry failed fax (N)..." tray item (fresh key,
     prefilled).
   - `unknown`: the poll got 404 for a tracked job (server database reset).
     Polling stops and the PDF is offered with the "may already have been
     sent" warning.
   On accept the balloon says "Fax to <number> queued" using the server's
   `to_number`, or "Server queued this fax to X (you entered Y)". The tray's
   own test page has no PDF and is not persisted; a print job sent as a test
   page is persisted until accepted, then its PDF is deleted.
7. Cancel (button or close box): delete the PDF and its json, except while
   the dialog is locked (resumed from disk, or after an uncertain failure in
   this run), where Cancel keeps both and the document is listed under
   "Unsent documents (N)..." in the tray menu (the record says `uncertain`,
   so the next start reopens it locked). A locked dialog that a definite 4xx
   unlocked is a draft again: Cancel deletes. The tray's test page has no
   document, so its Cancel keeps nothing. App exit or shutdown with a dialog
   open keeps the PDF.
8. Startup, before the watcher starts, `JobTracker.Scan` returns a
   `SendRequest` per document that needs the user, and each opens straight
   into the dialog queue (no balloon to miss):
   - `sending`/`uncertain` with a key: opens LOCKED with the stored key and
     plan and "This document may already have been sent; Send will not create
     a duplicate. Cancel keeps the document."
   - `draft`: opens unlocked with number and subject prefilled.
   - `failed`: opens as a retry, prefilled, fresh key.
   - `unknown`, a corrupt json (renamed to `<guid>.json.bad`), a `sending`
     record without a key, or an accepted record without `serverId`: opens
     with the "This document may already have been sent" warning; Send stays
     disabled until the user ticks "Send it anyway", then uses a fresh key.
   - a guid-named PDF with no json: offered as unsent, as before.
   `accepted` records are polled again; terminal leftovers are finished; a
   record whose PDF is gone is removed. A json that cannot be opened (held by
   antivirus or backup) is read again 3 times 200 ms apart; if it stays
   unavailable it is skipped for this scan (logged, not renamed, its PDF not
   offered) and handled at the next start. Only content that is not a record
   becomes `.bad`. A `<guid>.json.tmp` with no `<guid>.json` (crash between the
   flushed temp write and the rename) is adopted when it parses as a record;
   a torn temp file, or one beside an existing json, is ignored.
9. Exit: if a send is on the wire, the tray asks "A fax is being sent. Wait
   for it?". Yes waits up to 30 s for the result (an accepted job is still
   recorded), No exits at once; the `sending` record already prevents a
   duplicate. On Windows shutdown during a send the dialog refuses the
   first close and registers a shutdown block reason ("A fax is being
   sent."), so Windows shows it and the user can still force the shutdown.
10. Balloons all go through `NotificationQueue`: one at a time, each ends on
   click, close or an 8 s timer (stale timers and the Closed event of the
   balloon being replaced are ignored), so a later balloon never takes over a
   pending click action. A click stops the timer before its action runs (the
   action may open a modal dialog that pumps messages), timeouts and closes
   are ignored while it runs, and the next balloon shows once it returns. Startup balloons: settings problem
   (`ClientConfig.LoadError`, click opens Settings), printer missing, token
   missing. A 401 on the event stream shows "The server rejected the LANFax
   token" once per app run (again after Settings are saved); the click opens
   Settings. `SpoolWatcher.Error` shows a balloon (Core rate-limits it).
   `FaxApi`, `EventStream` and `SpoolWatcher` `Log` go to the app log.
11. The dialog queue opens the next dialog even if one fails to open (that
   document goes to "Unsent documents (N)...") or its close handler throws.
## Monitor (phase 3)

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
  raises `EventStream.Resync(since)`; the tray polls tracked jobs, and the
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

`%ProgramData%\LANFax\config.json`:
```json
{ "server": "192.0.2.10", "port": 8080, "token": "<DPAPI machine-scope, base64>", "keepLocalCopies": false }
```
Default server is the IP, not the hostname, so VPN name resolution never
matters. Settings dialog has "Test connection" (`GET /status`).
`ClientConfig.Load` never throws: a blank, corrupt or unreadable file gives
defaults, and a token that is not base64 or not decryptable on this machine
is dropped; both set `LoadError` (never contains the token) for the App to
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
- The app never stores the token in plain text and never logs it.
