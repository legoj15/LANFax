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
  set of `Add-PrinterPort` takes only `-Name`, so a file-path or named-pipe
  port needs no host address. The client prints to the pipe port
  `\\.\pipe\LANFax` (see "Spool path and the printer" for why and what was
  measured). Adding a port or printer needs elevation (a UAC prompt).
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
    FaxApi.cs                       ISendApi (post job, test send, both to a SendEndpoint snapshot, PDF as byte[]) + IMonitorApi (jobs, inbox, pdf download, arm/disarm/test receive; the runtime's loader loads through it, MainForm and the tray call its arm methods) + HttpClient wrapper for faxd: status, jobs, inbox, post job (Idempotency-Key), test send, arm/disarm, pdf download (body read failures and stalls -> FaxApiException 0, per-read idle limit = HttpClient.Timeout); FaxApiConfigException
    EventStream.cs                  SSE client: GET /events?token=, Last-Event-ID, stall detection, reconnect with backoff, raises typed events
    IPrintListener.cs               what ClientRuntime needs from the listener (start, stop, running/receiving, events); PrintPipeListener implements it, tests fake it
    PrintPipeListener.cs            serves the printer's pipe port \\.\pipe\LANFax (ACL: deny NETWORK, allow SYSTEM + current user, job-writing rights for Authenticated Users; first instance only, retried while taken; 2 instances); streams each connection to jobs/<guid>.pdf.part, flushes, renames to jobs/<guid>.pdf (or moves to rejected/); raises NewPrintJob / Error; EofCounter
    OldPortLeftover.cs              sorts a document left in the old file port spool\print.pdf into jobs/ (one complete PDF) or rejected/; the tray runs it at start and after an install
    Numbers.cs                      normalize_number / check_number mirror (shared vectors: LANFax.Tests/numbers-vectors.json)
    Models.cs                       Job, ArmState, ServerStatus records mirroring faxd JSON
  (PrinterSetup, which detects the printer and launches the elevated install/uninstall script, lives in LANFax.App)
  LANFax.App/                       net10.0-windows WinForms, single-file self-contained win-x64
    Program.cs                      machine-wide single-instance mutex (Global\LANFax.Tray, see "One active session"); loads the config; installs the UI thread's WindowsFormsSynchronizationContext, builds the one ClientRuntime on that context and hands it to TrayContext; no console
    Logic/ClientRuntime.cs          everything non-UI, one instance per process: FaxApi + HttpClients, the one EventStream and its restart, connection/status/arm snapshot, JobTracker + 60 s poll, InboundAnnouncer + InboundCatchUp, print listener + ListenerWatch + session-end stop, PrinterWatch + stuck-print-job checks (System.Threading timers); takes the owner (UI) thread's SynchronizationContext, raises plain events on it, exposes it as `Owner`; implements IMonitorSource (Logic/IMonitorSource.cs) for MainForm
    TrayContext.cs                  UI only, consumes ClientRuntime: NotifyIcon + menu (Open LANFax = the MainForm, also on icon double-click and on a second start's show signal; Open web page; Unsent documents (N); Retry failed fax (N); Send a test page...; Receive a test (arm 20 min); Install printer; Settings; Exit), balloons (incl. arm window ending, via ArmExpiryWatch), send dialogs one at a time, the one MainForm, ShutdownBlocker, Exit; gets back to the UI thread only through `ClientRuntime.Owner`
    SendDialog.cs                   non-modal; number, subject, page count, Send / Cancel; "Send test page instead" checkbox
    Logic/                          testable plain classes: ClientRuntime, MonitorModel + ArmExpiryWatch, MonitorLoader, PdfOpener, SendSession, SendExecutor, JobTracker, DialogQueue, NotificationQueue, PdfInfo, PrinterSetup, InboundAnnouncer + InboundCatchUp, InstanceGuard, ...
    MainForm.cs                     monitor window, a view of IMonitorSource (the runtime's MonitorModel rows, loads and arm state) + PdfOpener: status pill, arm card with countdown and buttons, jobs grid, inbox grid, double-click opens the PDF; one instance, close hides it
    SettingsDialog.cs               server host/ip, port, token, "Clear token", "Test connection"
  LANFax.Tests/                     xunit: Core (config, FaxApi, EventStream, PrintPipeListener against real pipes, numbers) + install/Inno script checks
  LANFax.App.Tests/                 xunit: App Logic classes; MonitorModelTests; MonitorLoaderTests; PdfOpenerTests; MainFormSmokeTests (the one WinForms file built in a test: STA thread, fake IMonitorSource; [InteractiveFact] reports it skipped without an interactive desktop); ClientRuntimeTests run the runtime on a dedicated owner thread against FakeServer (linked from LANFax.Tests) and a fake IPrintListener
  install/
    Install-LANFaxPrinter.ps1       elevated: Add-PrinterPort -Name \\.\pipe\<PipeName>; Add-Printer -Name "LAN Fax" -DriverName "Microsoft Print To PDF" -PortName <pipe port>, or Set-Printer -PortName <pipe port> when the printer exists on another port; removes the old file port spool\print.pdf when unused; KeepPrintedJobs off (lists jobs it kept, removes none); resumes a paused queue; removes the superseded Users manage ACE if present; idempotent
    Uninstall-LANFaxPrinter.ps1     removes printer, pipe port and an unused old file port; leaves %ProgramData%\LANFax
    (both scripts self-elevate by relaunching with ONE -ArgumentList command-line string, never a pre-quoted array)
    LANFax.iss                      Inno Setup: installs app to Program Files, runs Install-LANFaxPrinter.ps1 from [Code] (non-zero exit = non-fatal message), adds Run key for tray autostart; uninstall taskkills LANFax.exe in CurUninstallStepChanged(usUninstall) (after the user confirms), then removes the printer; /DAppSource=<dir> overrides the published app folder (the test suite compiles against a stub)
  build.ps1                         dotnet test, dotnet publish, iscc -> dist/LANFax-Setup.exe
  tools/Probe-PipePrinter.ps1       live probe on the disposable "LANFax Pipe Probe" printer (not in the sln or the build): builds tools/PipeProbe, runs only PrintPipeListener on pipe LANFaxProbe; -Count/-Runs (N jobs of 1..N pages), -NoListenerSeconds, -StopMidJobSeconds, -WhoIs, -Drain; files go to a temp folder
```

## Spool path and the printer

- Port: the local named pipe `\\.\pipe\LANFax` (a Local Port, Local Monitor,
  created with `Add-PrinterPort -Name '\\.\pipe\LANFax'`, elevated). The
  spooler opens one connection per print job, writes the PDF the driver
  renders, and closes it. Every job is its own connection, so no job can
  overwrite another (measured, see "Why a pipe port" below). The tray app
  serves the pipe with `LANFax.Core.PrintPipeListener` (pipe name
  configurable, default `LANFax`; the live probe uses `LANFaxProbe`).
- Spool dir `C:\ProgramData\LANFax\spool` keeps `jobs\`, `rejected\` and
  `sent\`; there is no `print.pdf` any more. The installer grants
  BUILTIN\Users Modify, inherited, on `C:\ProgramData\LANFax` itself (not
  only `spool\`): the tray app runs as whoever is signed in and writes
  `jobs\` (PDFs and tracker records), `rejected\` and `sent\`, and any Windows
  user can save `config.json`. The spooler no longer writes the spool. The
  Inno Setup `[Dirs]` entries grant `users-modify` too and carry
  `Flags: uninsneveruninstall`: uninstalling keeps `config.json`, unsent
  documents (`spool\jobs`), kept copies (`spool\sent`) and rejected print
  jobs; a reinstall picks them up.
- The app checks the binding, not only the name: `PrinterSetup.Status`
  reads `HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers\<name>` and
  its `Port` value and returns Installed (port is `\\.\pipe\LANFax`,
  case-insensitive), Misconfigured (any other port, including the file port
  `spool\print.pdf` of installs before 2026-09-30) or Missing. Misconfigured
  shows "The "LAN Fax" printer prints to X, not to LANFax (\\.\pipe\LANFax).
  Click here to fix it." and the tray item becomes "Fix ... printer"; both
  re-run the install script, which moves the queue with `Set-Printer`.
  The tray checks at start, when its menu opens, and every 30 s
  (`PrinterCheckInterval`, a registry read). `PrinterWatch` (Logic,
  unit-tested) remembers the last status: a change of state or port to
  Missing or Misconfigured gets the same balloon as at start once per
  change, never on every tick; a change back to Installed is silent; the
  start-up status is the baseline; ticks are skipped while an install runs.
  The same 30 s tick checks the listener (`PrintPipeListener.IsRunning`,
  true while it serves or retries a taken name): one that ended on an
  unexpected fault (reported once as "print pipe listener stopped: ...") is
  started again, which also restarts the 10-minute stuck-job window (jobs
  printed meanwhile wait in the queue). `ListenerWatch` (Logic,
  unit-tested): the first restart of a session gets a balloon ("Receiving
  from the "LAN Fax" printer had stopped; LANFax started it again ..."),
  later ones are only logged. After a restart the tray offers any document
  in `jobs\` not offered yet (a job the stopped loop saved but never
  raised). A deliberate stop (WM_ENDSESSION, Exit) tells `ListenerWatch`
  (`StoppedOnPurpose`), which then never restarts it: the session-end stop
  does not end the tray, so without that the next tick started it again.
- The tray runs the install script elevated (UAC, `runas`) through
  `powershell -EncodedCommand`, a wrapper that appends every line the script
  writes (all streams) to a temp file and adds "error: ..." with exit code 1
  when it throws; it exits 0 otherwise, unless the script ended with its own
  non-zero `exit N` (then N; `$?` tells that apart from a native command the
  script ran and tolerated, which leaves `$LASTEXITCODE` non-zero); an elevated ShellExecute process cannot have its output
  redirected. `PrinterSetup.InstallAsync` returns an `InstallResult`: a
  non-zero exit code is a failure even when the port already reads right;
  every line goes to the log (warning/error lines at error level), and the
  success balloon names the first "warning:" line. After every install the
  tray retries the leftover `print.pdf` (below) and offers any new document
  in `jobs\` (`OfferRescuedDocuments`, `JobTracker.NewDocuments`).
- Install script (idempotent, PowerShell 5.1): creates the pipe port if
  missing, adds the printer on it or moves an existing one onto it, removes
  the old file port `<spool dir>\print.pdf` when no printer uses it, lists
  (never removes) jobs `KeepPrintedJobs` kept after they printed and only
  then turns that setting off (off first could let the spooler drop them
  before they are listed) (`Format-KeptPrintedJobs`: status Printed, not Error; count
  and document names in the script output; harmless unless someone
  restarts one, which would fax it again, so the user deletes them), moves a
  non-empty document left in the old `print.pdf` to `spool\jobs\<guid>.pdf`
  (the tray offers it as unsent at its start, and right after a printer
  install from the tray) only when `Test-SinglePdf`
  accepts it by the listener's rule ("Print job acceptance" below); anything
  else (two jobs written over each other, a cut-off one) goes to
  `spool\rejected\print-<guid>.pdf` and is never offered. That step only
  warns when it fails (the file locked by a viewer or a scanner), so the tray
  does the same sorting itself (`OldPortLeftover`, Core, tested) at every
  start before its unsent scan and after every install, whenever
  `print.pdf` exists and the printer no longer prints to it; a file it
  cannot move gets a balloon and is retried next time, one it rejects gets a
  balloon naming where it went. It resumes a
  paused queue, and removes
  exactly one ACE if present: the
  BUILTIN\Users allow `0x0002000C` (manage printer) that the superseded
  install added (a one-off revert of c677f1a, accepted as such). It adds no
  permissions. The queue settings come first; the ACE revert and the old
  port removal come last and only warn on failure, so neither can leave the
  queue paused. Uninstall removes the jobs still in the queue (with no
  listener they never finish, and a printer with jobs is only marked "pending
  deletion"), then the printer (it warns if the printer is still there), the
  pipe port and an unused old file port.
- Pipe security: DACL = deny NT AUTHORITY\NETWORK (named pipes are reachable
  over SMB), allow SYSTEM and the user running the listener full access, and
  allow Authenticated Users `0x13019b` (GENERIC_READ|GENERIC_WRITE|DELETE
  without FILE_CREATE_PIPE_INSTANCE). The spooler opens the port as the job's
  owner, so that last rule is what lets another signed-in user's job in; it
  is what printing to the printer already allows. Measured 2026-09-30 with
  `Probe-PipePrinter.ps1 -WhoIs -OtherUser` (the listener's DACL minus the
  printing user's own rule, i.e. what another user's job faces): SYSTEM only
  -> refused; `0x120196`, `0x12019b` and `0x12019f` (GENERIC_READ|WRITE) ->
  refused; `0x13019f` and `0x13019b` -> delivered. Every refused job waited
  as "Error, Printing" and was not retried within 45 s. A print from a real
  second account has not been run; the probe stands in for it.
- The first instance is created with `PipeOptions.FirstPipeInstance`
  (FILE_FLAG_FIRST_PIPE_INSTANCE): if the name exists (a second LANFax,
  another signed-in user's LANFax, a squatter) `Start` raises
  `Error("another program is already listening on \\.\pipe\LANFax ...")`,
  `IsListening` stays false, and the listener keeps trying every
  `NameRetry` (5 s), reports it again every `NameTakenRepeat` (10 min) while
  the name stays taken, and serves the pipe once it is free. A fixed pipe
  name cannot be reserved: any local program that creates it first receives
  every user's print jobs. This detects that, it does not prevent it (the
  only real fix is a SYSTEM service that holds the name from boot; open item
  in handoff.md). When every instance is gone for a moment (a client that
  came and went before a spare existed, or a spare that could not be
  created) the name is taken again with FILE_FLAG_FIRST_PIPE_INSTANCE, so a
  program that grabbed it in that moment is reported, never joined. Max 2 instances: the one handling a job and a
  spare waiting, so the spooler's next connection always finds an instance.
  Jobs are handled one at a time in arrival order.
- Per connection: stream to `jobs\<guid>.pdf.part` (64 KB buffer, never the
  whole job in memory), `Flush(true)`, rename to `jobs\<guid>.pdf`, raise
  `NewPrintJob(guid, path)`. A rename that still fails after its ~1 s of
  retries (a scanner holding the file without FILE_SHARE_DELETE) falls back
  to copying the job to `jobs\<guid>.pdf` (raised as usual; the `.part` is
  deleted, or set aside at the next start when that fails too); if the copy
  fails as well, the `.part` stays and is reported (moved to `rejected\` at
  the next start: print it again). End of stream (the client closed) is the end of
  the job. Rejected, moved to `rejected\<guid>.pdf` and reported through
  `Error` (never deleted): a job that fails "Print job acceptance" below
  (over `MaxJobBytes`: the first 200 MB are kept, the rest is read and
  dropped so the spooler's write succeeds and the job leaves the queue); an
  IOException while reading (broken connection). A connection that delivers no bytes is only
  logged (its empty file is deleted); the spooler was seen making one.
  A connection silent for `IdleTimeout` (2 min) is dropped: with no bytes
  at all it is reported (rate-limited, "a program connected ... and sent
  nothing"), with some it is rejected like a broken connection. Jobs are read
  one at a time and any signed-in user may open the pipe, so without it a
  client that connects and sends nothing would stall the printer. It bounds
  the stall, it does not prevent a program that reconnects in a loop.
  A failure on our side while writing (disk full, `jobs\` not writable)
  drops the connection, moves the `.part` to `rejected\` and reports it.
- Start sweeps `jobs\*.part`, unless a loop an earlier `Stop` gave up on is
  still running (its `.part` is not stale; the sweep waits for the next
  start). Every `.part` goes to `rejected\`, never back into `jobs\`: one
  beside its `<guid>.pdf` is the copy a failed rename left (logged); one that
  passes "Print job acceptance" with no `<guid>.pdf` beside it is either
  such a copy whose job was since sent or cancelled (the tracker moved the
  `.pdf` away) or a job whose rename and copy both failed. The two cannot be
  told apart and offering a sent job again would fax it twice, so it goes to
  `rejected\<guid>.pdf` and is reported through `Error` ("... may already
  have been faxed ... check before faxing it"); anything else (a crash
  mid-job) is logged. `*.pdf.part` never matches the tracker's `jobs\*.pdf`
  scan.
- `Stop()`: no new connection, nothing raised any more (a job rejected or
  failing during the grace is only logged); a job in progress
  gets `StopGrace` (5 s) to finish and is then kept in `jobs\` (offered as
  unsent at the next start); after that it is cut off and its `.part` moved
  to `rejected\`. A job the spooler already started on the spare instance is
  read the same way (an instance accepts clients from creation until it is
  closed); no new spare is opened once stopping. A connection that lands in
  the instant between the last check and closing the pipe is still dropped
  (only pausing the printer first would close that). `Stop` blocks at most
  `StopWait` (7 s); a loop still busy then (slow disk) is logged and finishes
  in the background, and the next `Start` waits for it before sweeping
  `.part` files; while that loop still holds the name, `Start` reports "the
  previous listener ... is still finishing a job" instead of the
  "another program is already listening" squatter message. Idle stop is
  immediate. A `Stop` called while another is in progress waits for the
  same loop until the first one's `StopWait` runs out (the tray's Exit runs
  `Stop` off the UI thread, and a WM_ENDSESSION meanwhile calls it again;
  without that the second call returned at once and Windows could end the
  process mid-job). Tray Exit sets the tray tooltip while a document is
  still arriving.
- Print job acceptance (one rule, applied by `PrintPipeListener` to every
  job, by `PrintPipeListener.IsOneCompletePdf` to a file (the start sweep and
  the tray's leftover rescue) and by the install script's `Test-SinglePdf` to
  a document left in the old `print.pdf`; `PdfAcceptanceVectors` in
  LANFax.Tests pins all three): the
  bytes start with `%PDF`, hold exactly one `%%EOF`, have nothing but PDF
  whitespace (space, CR, LF, tab, FF, NUL) after it, and are at most 200 MB.
  Print To PDF writes exactly one `%%EOF` (every probe run), so this refuses
  a cut-off job, one that ended after an earlier section, and two jobs
  written over each other, and costs no real document.
- In flight: `IsReceiving` is true from the moment a client is connected
  until its file is saved (renamed and raised, or set aside);
  `ReceivingChanged(bool)` fires true then false once per job, on a
  thread-pool thread, also while stopping. The tray's `ShutdownBlocker` (a
  hidden top-level `NativeWindow`; WM_QUERYENDSESSION only reaches top-level
  windows) calls `ShutdownBlockReasonCreate` with "Receiving a document from
  the LAN Fax printer" on true and destroys it on false, and answers
  WM_QUERYENDSESSION with FALSE while `IsReceiving`, so sign-out or shutdown
  waits for the read; the user can still force it from the Windows screen,
  and then WM_ENDSESSION (wParam != 0, receiving or not, since IsReceiving lags
  a job already connected) calls the listener's `Stop` synchronously before returning (`SessionEnd.OnEndSession`), so the
  job gets `StopGrace` to finish into `jobs\`; one cut off after that leaves
  a `.part` the next start moves to `rejected\`. `SendDialog` uses the same
  P/Invokes for a send on the wire. Not exercised with a real sign-out.
- Rate limiting: a pipe name already taken is reported at start and again
  every `NameTakenRepeat` (10 min); "cannot save a print job" and "a program
  connected and sent nothing" and a failed move of a stale `.part` raise
  `Error` at most once per 60 s per distinct reason text (the exception
  message is part of it); a spare-instance creation failure is only logged;
  rejected jobs, a failed rename and "listener stopped" every time.
  A program that creates the pipe while the listener is already serving
  joins it undetected (the spare instance is created by joining); accepted,
  as the client is for single-user PCs only (see the handoff). The tray shows `NotificationText.PrintPipeProblem` (printer
  name + reason, cut to 255 characters).
- Who connects (measured with `Probe-PipePrinter.ps1 -WhoIs`): the client
  process is `spoolsv`; after the first read `GetImpersonationUserName` and
  the impersonation token say NT AUTHORITY\LOCAL SERVICE at Identification
  level, with no job-owner SID in its groups (pipe clients use dynamic
  security tracking, so the reported identity is the one at the last write).
  The open is done as the job owner (see "Pipe security": a DACL without the
  printing user refuses it). The owner cannot be read from the pipe, and the
  job file is always written by the tray user, so the old "Printed by
  <owner>" line (NTFS owner of the spooled file) was removed with
  `PrintedBy`; the owner would have to come from the spooler (open item in
  handoff.md).

### Why a pipe port (measured 2026-09-30, this PC, not elevated)

- File port (`spool\print.pdf`, the design before 2026-09-30): the second of
  two back-to-back jobs **silently overwrites** the first. A 3-page job
  followed at once by a 1-page job left one `print.pdf` with `/Count 1` and
  one `%%EOF`; no spooler error, no append. Documents printed while the tray
  app is not running are lost except the last, and a job finishing inside
  the watcher's settle window replaces the one before it.
- The file-claim design that tried to close that hole (claim-and-hold with an
  exclusive handle, queue paused whenever no watcher ran, a loss check over
  spooler job notifications, kept-job recovery with KeepPrintedJobs, a Users
  "manage printer" ACE so a standard user could pause and resume) is in
  commit c677f1a and was dropped: four review rounds, still with timing
  windows, a permission widening on the printer, and a lot of state to keep
  right. A pipe port removes the shared file, so none of it is needed.
- Pipe port, disposable printer `LANFax Pipe Probe` on `\\.\pipe\LANFaxProbe`
  (`client/tools/Probe-PipePrinter.ps1`, only the listener runs; jobs printed
  with `PrintDocument`, explicit page counts):
  - 5 jobs back to back, 3 runs, and 10 jobs once: every run N files,
    `/Count` exactly 1..N in print order, one `%%EOF` each, no listener
    error, queue empty afterwards.
  - No listener: the first job shows "Error, Printing" and the spooler
    retries it (4+ minutes seen, no event-log error); the rest wait "Normal".
    5 jobs printed, 30 s without a listener, then listening: all 5 delivered
    whole, in order. How long the spooler keeps retrying is NOT measured
    beyond those 4+ minutes: a job printed hours before LANFax starts may
    already sit in a non-retried "Error" (as a cut-off job does, below) and
    need `Restart-PrintJob`. The tray checks the queue on every 30 s
    printer check from 60 s to 10 min after the listener starts
    (`PrintQueueJobs`, EnumJobs level 1, no elevation): each job submitted
    before the start and still in Error gets one balloon, once per job id
    ("N documents printed to "LAN Fax" before LANFax started are stuck ...")
    that opens the queue (`printui.dll,PrintUIEntry /o`). It does not
    restart them: a restart can deliver a job twice (below), so the user
    decides.
  - A job refused at open (the DACL probe above) and then restarted with
    `Restart-PrintJob` once a listener ran was delivered TWICE in one run
    (two whole copies of the same queue job, 0.5 s apart). Any automatic
    restart of errored jobs must expect duplicates.
  - Duplicates cannot be dropped by content: the same document printed
    twice back to back within one second is byte-identical
    (`Probe-PipePrinter.ps1 -Same -Count 4`, 2026-09-30: 4 prints, 3
    distinct SHA-256; the only differing bytes are `/CreationDate` and
    `/ModDate`, which have 1 s resolution). Suppressing a repeat hash would
    swallow an intentional reprint, so there is none; the stuck-jobs balloon
    says a restart can deliver a document twice and to send only one copy.
    The `-Same` probe passes when every print arrived and the queue is empty,
    and prints identical files as a NOTE, not a failure.
  - Listener stopped mid-job (40 pages, the spooler writes while the driver
    renders, so the job takes 15-20 s): the job goes to "Error, Printing" and
    is **not** retried by the spooler (30 s, 60 s and 10+ minutes seen) even
    after the listener is back. `Restart-PrintJob` (the job owner may, no
    elevation) delivers it whole. Hence `StopGrace`, and the rule that the
    listener never drops a connection for a bad job (it reads to the end).
  - `Add-PrinterPort -Name '\\.\pipe\...'` and `Add-Printer` need elevation.

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
   The dialog does not say which Windows user printed the document (the pipe
   does not tell; open item in handoff.md).
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
   Settings. `PrintPipeListener.Error` shows a balloon (Core rate-limits the repeating ones).
   `FaxApi`, `EventStream` and `PrintPipeListener` `Log` go to the app log.
11. The dialog queue opens the next dialog even if one fails to open (that
   document goes to "Unsent documents (N)...") or its close handler throws.
## State vocabularies (where each transition is decided)

| Vocabulary | Values | Decided in |
|---|---|---|
| `SendState` (record `state`, `JobTracker.cs`) | `sending`, `uncertain`, `draft`, `accepted`, `unknown`, plus the server's terminal `sent`/`failed`/`cancelled` | `BeginSend` (sending), `RecordFailure` (uncertain if the session is locked, else draft), `Track` (accepted), `MarkUnknown` (404), `OnJobUpdate` (terminal) |
| `SendKind` (why a dialog opens, `DialogQueue.cs`) | `New`, `Retry`, `Draft`, `Resume`, `Suspect` | `JobTracker.Scan` at startup (by record state), `RequestFor` (failed -> Retry, unknown -> Suspect), a fresh print job (New) |
| `AttemptOutcome` (`SendSession.cs`) | `NotSent`, `Rejected`, `Uncertain` | `SendOutcome.Classify` via `SendSession.Failed`: NotSent before `Started` or on `FaxApiConfigException`; 4xx except 409 = Rejected; the rest Uncertain. A locked session unlocks only on Rejected with a status other than 401 |
| `DialogOutcome` (`SendDialog.cs`) | `Accepted`, `Cancelled`, `Kept`, `Abandoned` | `SendDialog.OnClosing`: user close keeps the document (Kept) when `SendSession.CancelKeepsDocumentFor` says so (locked or Suspect), else Cancelled; app exit/shutdown = Abandoned. `TrayContext.OnSendDialogClosed` acts on it |
| `OfferCategory` (tray items, `DialogQueue.cs`) | `Unsent`, `Retry` | `TrayContext`: Kept dialogs and dialogs that failed to open -> Unsent; `OnJobFinished` (`ClientRuntime.JobFinished`, kept for retry) -> Retry |

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
  in the same session signals the running tray to show its monitor window
  (`Local\LANFax.Tray.ShowRunning`; as "Open LANFax"; while Exit waits for a
  send in flight it gets the balloon "LANFax is exiting, waiting for a fax to
  finish" instead, and once stopping nothing: `InstanceGuard.OnShowSignal`); a start in another Windows
  session (fast user switching, RDP) shows "LANFax is already running in
  another session" and exits. `InstanceGuard.Acquire` creates the show event
  the moment it owns the mutex (before the tray is built), and a second start
  retries the signal for up to 2 s before concluding "another session", so a
  second launch during startup is not misreported. Within the same 2 s it also
  retries the mutex, so a start during the owner's shutdown becomes the new
  owner instead of exiting. Print jobs from the other session reach the
  active instance through the pipe (the pipe lets every signed-in user's job
  in; measured by DACL probe, not with a real second account) and are offered
  on its desktop, with nothing saying who printed them: on a shared PC only
  one user at a time should be signed in with LANFax. With no LANFax running,
  jobs wait in the queue (see "Why a pipe port" for how long that is known
  to hold).

## Monitor (phase 3)

Built 2026-10-01: `MainForm` (pure WinForms view, no state of its own),
`Logic/MonitorModel.cs` (view-model, no UI, tested), `Logic/MonitorLoader.cs`
(the loads, owned by `ClientRuntime`, tested in `MonitorLoaderTests` and
through the runtime in `ClientRuntimeTests`), `Logic/PdfOpener.cs`
(double-click to PDF, tested in `PdfOpenerTests` against FakeServer and the
real `FaxApi`) and `Logic/IMonitorSource.cs` (what the form needs from
`ClientRuntime`; the smoke test fakes it).

- **Runtime underneath.** `ClientRuntime` owns the non-UI state the tray and
  the form share. It merges `status` and `arm` events into a `Status` / `Arm`
  snapshot (plus `Connected`, `EverConnected`) and raises `StatusChanged`,
  `ArmChanged` (only when the arm state differs) and `ConnectionChanged`; a
  window opened later reads the snapshot. No `GET /status` at connect: faxd's
  stream opens with a full `status` and an `arm` event. While disconnected the
  snapshot is the last one known; `RestartStream` (settings save) resets it to
  `ServerStatus.Empty`. The arm state lives only in `Status.Arm`: an arm,
  test-window or disarm call's HTTP answer (window buttons, the tray's
  "Receive a test") goes in through `ClientRuntime.NoteArm(answer)`, which
  raises `StatusChanged` and `ArmChanged` when it differs, as the stream's
  `arm` event does; a caller drops an answer asked before a restart
  (`ServerGeneration`, bumped by every `RestartStream`).
  The runtime also owns the monitor's rows (`Monitor`, a `MonitorModel`, kept
  whether or not a window is open) and their `MonitorLoader`: it loads on
  every connect and resync (see Loads), feeds every live job update (stream,
  poll, a send just accepted) and `received` event into it, and raises
  `MonitorChanged` whenever rows may have changed (a row moved, a load
  landed, a restart cleared them) and `MonitorLoadFailed(e)` for a failed
  load (kept in `MonitorLoadError` until a load succeeds, for a window opened
  later; logged by the runtime, a 401 only as info). `JobChanged` (before the
  tracker, so before `JobFinished`), `InboxChanged` (before `FaxReceived`)
  and `Resynced(since)` stay as runtime events for the tray and tests; the
  window does not use them. Not applied to `Monitor` (nor raised as
  `JobChanged`/`InboxChanged`): a job or received event from a run a restart
  cancelled, or a poll answer to a request made before a restart (the save
  may have switched servers, and the restart just cleared the rows); those
  still reach the tracker, `JobFinished` and the announcer. A poll asked
  after a restart is live even when the restart started no run (unusable
  settings): poll liveness is a restart counter, not the stream run's token.
- **Threading.** The runtime takes the owner (UI) thread's
  `SynchronizationContext` (callbacks are posted through it; `Start` throws
  unless it runs under it) and exposes it as `Owner`; the tray posts through
  `Owner`. Every runtime member is called on, and every event raised on, the
  owner thread; nothing is raised once `StopAsync` (Exit: timers die, run
  cancelled, listener stopped on purpose, run awaited up to 5 s) or `Dispose`
  began. `Dispose` without `StopAsync` does the same stop synchronously. An
  injected `Listener` is stopped, not disposed.
- **One window.** "Open LANFax" in the tray menu (bold) and a double-click on
  the tray icon show the one `MainForm`, made on first use, and raise it if
  it is open. "Open web page" keeps the browser route. The user's close
  (`CloseReason.UserClosing`) hides it; sign-out and shutdown close it for
  real (so it never refuses `WM_QUERYENDSESSION`), and the tray's Exit hides
  then disposes it. Saving Settings calls `SettingsSaved`, whose
  `RestartStream` resets the status (the arm card back to Disarmed, even
  after a noted arm answer), clears the rows (`MonitorChanged`; the next
  load is full, a load in flight lands nowhere) and bumps
  `ServerGeneration` (an arm call's answer or error landing after it is the
  old server's and is neither noted nor shown). The window opens on the
  runtime's current rows, status and last load failure, and renders on
  `ConnectionChanged`, `StatusChanged`, `MonitorChanged` and
  `MonitorLoadFailed`; a load failure on the message line is replaced by the
  hint once a load succeeds.
- **Layout, mirroring the web page.** Header pill; "Receive faxes" card with
  ARMED/Disarmed, the countdown line and Arm 1 h / 4 h / 8 h, Test window
  (20 min, `POST /test/receive`), Disarm (no confirm, as on the page); "Sent
  and queued" grid (time, to, state badge, pages, error); "Inbox" grid (time,
  from, pages); a message line. Double-click any row: download to
  `%LocalAppData%\LANFax\cache\<id>.pdf` (fresh each time: the server says
  `no-store`), then ShellExecute. When the finished download cannot be
  moved over the copy there (a viewer holds it open: Windows answers
  `UnauthorizedAccessException`, measured 2026-10-01, or a sharing
  violation), `DownloadPdfAsync` raises `PdfFileInUseException` (an
  `IOException`) and that copy is opened, the message line saying so. Any
  other file failure (the `.part` cannot be created or written: disk full,
  read-only cache folder) is an error, as is a body that breaks off or
  stalls; never the stale copy: `FaxApi.DownloadPdfAsync` raises read failures as
  `FaxApiException` (StatusCode 0) and gives each body read the HttpClient's
  `Timeout` (30 s) again, since `ResponseHeadersRead` ends that timeout at
  the headers. A 404 says "The server has no PDF for this fax"; a 401 the
  token line (`NotificationText.WindowUnauthorized`, the window's one
  wording for a refused token: loads, arm buttons, PDF). A second double-click on a fax still downloading is ignored. The job id
  must match `^[A-Za-z0-9_-]{1,64}$` and the full path must stay in the cache
  dir (`MonitorModel.PdfCachePath`). The cache is never pruned.
- **Model rules (`MonitorModel`, tested in `MonitorModelTests`).**
  - Times: local zone, `MMM d HH:mm`, invariant culture. Party: to-number
    for outbound, from-number for inbound, `-` when empty.
  - Badges: queued/dialing/sending amber, sent green, failed red, received
    blue, cancelled gray; the client's `SendState`s too (accepted, uncertain
    amber; draft, unknown gray); anything else shows raw, gray.
  - Pill: not connected = "connecting" (neutral) or "reconnecting" (bad,
    once ever connected); connected before any status = "connected"; then as
    the page: no registrations and AMI down = "PBX link down"; any
    registration whose status does not match `registered` or matches
    `unregistered` = "N line(s) down"; AMI down = "degraded"; else "N
    line(s) registered" or "daemon ok". ", receiving a fax" is appended while
    `Receiving` is set and the last status event that carried it is under
    2 min old (the PBX sends no "finished"; `ReceivingTtl`).
  - Countdown: "Receiving until HH:mm (mm:ss left)", `h:mm:ss` from an hour,
    "(ending)" once past; "Receiving on ext N" without an end; "Send-only.
    Inbound calls go to the phones." when disarmed. A 1 s timer ticks it only
    while the window is visible. An arm button shows its answer at once
    (`NoteArm`); the stream's arm event follows and changes nothing more.
  - Rows merge by id into the grid their direction names, newest first by
    `created_at`, capped at 200; an update older (`updated_at`) than the row
    is ignored, so a slow reload never undoes a newer event; at an equal
    `updated_at` (faxd stamps to the second) a strictly less advanced state
    (queued < dialing < sending < terminal) is ignored too, so the tray's
    re-apply of the POST's `queued` row never undoes a same-second stream
    state; an equal state still applies (faxd's annotate event adds pages and
    detail, and `received` adds `finished_at`, with no state change), and an
    update identical to the row is a no-op. Rows an answer omits stay.
  - Loads (started by the runtime, never the window): the first connect's
    (and the first after a restart) is full: `GET
    /jobs?direction=out&limit=50` + `GET /inbox?limit=50`. On every
    reconnect: `GET /jobs?limit=500&updated_since=<last load start - 5 min>`
    (both directions; the 5 min `SkewMargin` absorbs a client clock ahead of
    the server's, the merge absorbs the overlap). On `resync`:
    `updated_since=<the event's since>`. `updated_since` is UTC
    `yyyy-MM-ddTHH:mm:ssZ`, invariant. While nothing is loaded yet (first
    load failed, or after a settings save) every load is full, a resync's
    too. One load at a time (`MonitorLoader`): requests meanwhile coalesce
    into one more pass after it, from the earliest resync `since`. A load in
    flight across a settings save lands nowhere (`SettingsSaved` ->
    `RestartStream` clears the model and bumps `ServerGeneration`; the
    loader's own `ServerChanged` is only that reset). When the saved settings
    are unusable no stream run starts, so `RestartStream` asks for one load
    itself: the config error reaches `MonitorLoadFailed` before anything is
    sent. A failed load still runs
    the pass queued behind it, once, from the queued `since` (the reconnect,
    resync or new server's connect that queued it will not come again; a
    failure of a load already dropped by a settings save is not shown). A
    failure is reported once per load request and server, however many of
    its passes fail. A 401 shows the token line. A `resync` still fetches twice: this
    since-load and the inbound catch-up (`GET
    /jobs?direction=in&state=received`, 100 then 1000); they are kept apart
    on purpose (the catch-up's baseline is decided before its request, it is
    paged to faxd's cap and must run even when the monitor has nothing
    loaded, which makes the monitor's load a full one that does not cover the
    gap).
  - Grids are rebuilt on every change; the selected fax stays selected. A
    view scrolled down keeps its top visible fax on top (by id), so a new
    fax arriving does not shift it; a view at the top stays at the top, so
    the new fax shows.
- **Balloons:** fax received (from), job sent, job failed (tray, as before),
  and "Fax receiving ends in N min (at HH:mm)" once per arm window when 5 min
  or less is left (`ArmExpiryWatch`; a window is its end time, so extending
  or re-arming warns again). Checked on every `ArmChanged` and by a 15 s tray
  timer; clicking it opens the window ("Click here to open LANFax and
  extend it."), where an Arm button extends the window.
- "Send a test page..." in the tray opens `SendDialog` in test-page mode:
  number prompt -> `POST /test/send`.
- "Receive a test" in the tray: confirm -> `POST /test/receive
  {"minutes": 20}` -> balloon with the end time; the answer is noted
  (`NoteArm`), so the window's arm card shows the countdown at once.
- One `EventStream` for the app's lifetime; a Settings save cancels the run,
  waits for it and starts again so `LastEventId` survives; a cancelled run's
  connected/disconnected/resync/status/arm events are dropped (job and
  received still reach the tracker and the announcer, not the monitor's rows). Stall: no line for 45 s aborts and reconnects with
  `Last-Event-ID`.
- `FaxApi`, `EventStream` and `PrintPipeListener` each take an optional
  `Log`; none ever passes the token to it.

## Config

`%ProgramData%\LANFax\config.json` (shared by every Windows user, no secret):
```json
{ "server": "192.168.168.168", "port": 8080, "token": null, "keepLocalCopies": false, "printerName": "LAN Fax", "spoolDir": "..." }
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
