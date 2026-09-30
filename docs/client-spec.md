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
    SpoolWatcher.cs                 watches the port path; claims print.pdf with an exclusive handle and holds it through the %PDF / %%EOF checks and the rename (by handle) to jobs/<guid>.pdf (or rejected/<guid>.pdf); raises Claimed / NewPrintJob / Error
    ClaimedFile.cs                  CreateFileW(GENERIC_READ|DELETE, share 0) + SetFileInformationByHandle(FileRenameInfo); no delete-on-close
    PrintQueue.cs                   IPrintQueueControl + SpoolerPrintQueue (winspool SetPrinter pause/resume, IsPaused; JobCount is probe-only); IPrintJobSource + SpoolerJobSource (FindFirstPrinterChangeNotification, job STATUS field, "finished" = JOB_STATUS_PRINTED, IsRunning false once the watch dies: 10 failed reads in a row with 100 ms x n backoff)
    PrintLossMonitor.cs             finished spooler jobs vs claimed files; a pair is trusted (Matched) once no job reported within 15 s after it is lost; an unmatched job after 15 s raises Lost, and the pairs made shortly before it Doubtful; claims no followed job took raise ClaimUnpaired; IgnoreJob skips jobs LANFax deleted itself
    KeptJobs.cs                     KeptJob + IKeptJobStore + SpoolerKeptJobs (EnumJobs level 1 printed jobs, SetJob delete/restart), KeptJobLedger (spool\kept-jobs.json), KeptJobRecovery (reprint at start / on loss or doubt, delete when trusted, sweep and exit settle by claim count)
    Numbers.cs                      normalize_number / check_number mirror (shared vectors: LANFax.Tests/numbers-vectors.json)
    Models.cs                       Job, ArmState, ServerStatus records mirroring faxd JSON
  (PrinterSetup, which detects the printer and launches the elevated install/uninstall script, lives in LANFax.App)
  LANFax.App/                       net10.0-windows WinForms, single-file self-contained win-x64
    Program.cs                      machine-wide single-instance mutex (Global\LANFax.Tray, see "One active session"); tray icon; no console
    TrayContext.cs                  NotifyIcon + menu: Open, Send a test..., Receive a test (arm 20 min), Settings, Install printer, Exit
    SendDialog.cs                   non-modal; number, subject, page count, Send / Cancel; "Send test page instead" checkbox
    Logic/                          testable plain classes: SendSession, SendExecutor, JobTracker, DialogQueue, NotificationQueue, PdfInfo, PrinterSetup, InboundAnnouncer + InboundCatchUp, InstanceGuard, PrintedBy, QueueLifecycle, PrintGuard (LossCheckHost + StartGate + PrinterHealth + KeepPrintedJobsGuard), ...
    MainForm.cs                     (phase 3, NOT YET WRITTEN) status header, arm card with countdown, jobs grid, inbox grid, open PDF
    SettingsDialog.cs               server host/ip, port, token, "Clear token", "Test connection"
  LANFax.Tests/                     xunit: Core (config, FaxApi, EventStream, SpoolWatcher, numbers) + install/Inno script checks
  LANFax.App.Tests/                 xunit: App Logic classes (no UI; the WinForms files are not tested)
  install/
    Install-LANFaxPrinter.ps1       elevated: Add-PrinterPort -Name <spool path>; Add-Printer -Name "LAN Fax" -DriverName "Microsoft Print To PDF" -PortName <spool path>, or Set-Printer -PortName <spool path> when the printer exists on another port; adds an allow ACE for BUILTIN\Users (S-1-5-32-545, READ_CONTROL|PRINTER_ACCESS_ADMINISTER|PRINTER_ACCESS_USE, no inheritance) to this printer's PermissionSDDL; sets KeepPrintedJobs (Win32_Printer, Set-CimInstance); pauses the queue (Win32_Printer Pause) for the app to resume, unless a LANFax.exe runs in any session (then it is left printing); idempotent
    Uninstall-LANFaxPrinter.ps1     removes printer and port (documents still held in the queue are discarded, and the count of unprinted ones is printed; kept printed jobs are not counted); if Remove-Printer fails the queue is resumed, never left paused; leaves %ProgramData%\LANFax
    (both scripts self-elevate by relaunching with ONE -ArgumentList command-line string, never a pre-quoted array)
    LANFax.iss                      Inno Setup: installs app to Program Files, runs Install-LANFaxPrinter.ps1 from [Code] (non-zero exit = non-fatal message), adds Run key for tray autostart; uninstall taskkills LANFax.exe in CurUninstallStepChanged(usUninstall) (after the user confirms), then removes the printer; /DAppSource=<dir> overrides the published app folder (the test suite compiles against a stub)
  build.ps1                         dotnet test, dotnet publish, iscc -> dist/LANFax-Setup.exe
  tools/Probe-PrinterBurst.ps1      live burst probe on the real printer (not in the sln or the build): builds tools/BurstProbe, runs only the watcher + loss check + kept-job deletes, prints -Count jobs of 1..Count pages at once; -Runs, -PausedFirst, -NoWatch, -Crash (nobody claims while they print, then the start-up recovery), -ShowStatuses; tray app must be closed; deletes its kept jobs; leaves the queue resumed
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
- Watcher (claim and hold): `FileSystemWatcher` on the spool dir plus a 2 s
  polling fallback start one loop, which runs while `print.pdf` exists and
  tries to claim it every `RetryEvery` (10 ms). The claim is
  `ClaimedFile.TryOpen`: `CreateFileW(GENERIC_READ | DELETE, share 0)`. It
  fails (sharing violation) while the spooler still has the file open, so a
  successful claim means that job is finished; from then on the spooler's
  next job gets a sharing violation on `print.pdf` and retries by itself
  (measured: completes 3-6 s after the handle closes, nothing lost). Under the
  claim the file is checked: not `%PDF` (or 0 bytes) goes to
  `spool\rejected\<guid>.pdf` with `Error("print.pdf was not a PDF ...; kept
  as ...")`; one `%%EOF` goes to `spool\jobs\<guid>.pdf`; two or more goes to
  `rejected\` with `Error("print.pdf contained N documents; the LAN Fax
  printer received two jobs at once. Print them one at a time.")` (a guard; it
  never fires for this printer); no `%%EOF` goes to `rejected\` as not a PDF
  at once, still under the claim (the exclusive open proves the spooler closed
  it, so it is truncated; releasing it would reopen the overwrite window).
  Access denied on the claim (a file pending deletion, briefly; or an ACL that
  keeps this user out, for good) is reported through `Error("cannot take
  print.pdf off the printer port: access denied ...")` once it lasts
  `AccessDeniedReportAfter` (5 s), rate-limited like other errors. Every move is a rename through the held handle
  (`SetFileInformationByHandle(FileRenameInfo)`, `\??\` path, no replace), so
  the file is never unlocked at the port path. Nothing is deleted and there is
  no delete-on-close: a crash while holding just closes the handle and leaves
  `print.pdf` in place. After each move `Claimed(path)` fires (the loss check
  counts these), then `NewPrintJob(guid, path)` for `jobs\`. Stopped after the
  move: the file stays in `jobs\` without a record and the next start's
  `JobTracker.Scan` offers it.
- Timings are instance properties (`RetryEvery` 10 ms, `PollEvery` 2 s,
  `AccessDeniedReportAfter` 5 s). `WaitIdle(timeout)` starts a check and
  blocks until nothing is at the port and no check runs (start-up uses it). The old settle wait (1 s age + 500 ms size recheck) is gone:
  holding the exclusive handle is the proof the spooler is done.
- A failed move (e.g. `jobs\` unwritable) raises `Error(reason, exception)`
  at most once per distinct reason per 60 s; the claim is kept and only the
  rename is retried (every 250 ms, after 5 failures every `PollEvery`), so
  the spooler's next job keeps getting a sharing violation instead of
  replacing the file. A check loop that ends re-checks for `print.pdf` once it
  is marked idle (a Created event in that gap used to be dropped).
  `Stop()` cancels a check in progress before it moves or raises. A file held
  open elsewhere for 2 min is logged (not an error: a long job looks the same).
- Queue pause lifecycle (`QueueLifecycle` over `IPrintQueueControl`,
  `SpoolerPrintQueue`): the queue is paused whenever no watcher claims
  `print.pdf`, so jobs wait in the queue instead of overwriting each other at
  the port. The install script pauses it (unless LANFax.exe is running). The
  tray app at start: pauses it (a crash leaves it printing), notes whether a
  `print.pdf` is left from the last run, starts the watcher and waits up to
  2 s (`WaitIdle`) for that leftover to be claimed. `StartGate` then runs the
  hand-over once: the loss check starts (so the leftover claim is not
  counted), kept jobs are recovered (below), and the queue is resumed (held
  and reprinted jobs drain into a claiming watcher). If the leftover is still
  at the port after 2 s (antivirus scanning it), the queue stays held and the
  10 s health tick releases the gate once `SpoolWatcher.IsIdle`; resuming
  early would let the first held job overwrite the leftover while its own
  claim was counted for it. A hold lasting 60 s (the leftover open in a PDF
  viewer: the claim's share-mode-0 open fails with a sharing violation, which
  the watcher only logs) shows one balloon: "Documents printed to LAN Fax are
  waiting: <spool>\print.pdf is open in another program. Close it and LANFax
  will pick them up." It
  pauses it if the watcher cannot start, and on tray Exit, on
  `SystemEvents.SessionEnded` (logoff, shutdown), on `Dispose` and, best
  effort, on an unhandled exception. After "Install printer" succeeds (with
  the start hand-over done) the app resumes it and restarts the loss check.
  Every 10 s, on a thread-pool timer (a hung spooler must not freeze the tray;
  a tick still running skips the next), `PrinterHealth.Tick` re-reads the
  printer (registry, cheap): a move to
  another port is announced once per change with the Misconfigured balloon;
  Missing/Misconfigured -> Installed restarts the loss check (the printer may
  be new or re-created); while the watcher runs, a dead loss check is started
  again and `QueueLifecycle.Reconcile` resumes a queue found paused (the
  install script, a person; one LANFax per machine, so never another user's
  LANFax). It also checks the printer's "Keep printed documents"
  (`KeepPrintedJobsGuard` over `IKeepPrintedJobs`: `PRINTER_INFO_5`
  Attributes `0x100`): anyone with the Users ACE can clear it on the Advanced
  tab, which would silently turn off kept-job recovery, so it is set back
  (SetPrinter level 5 on a handle with ADMINISTER|USE; ADMINISTER alone is
  refused, measured; logged), and if that is
  refused one balloon per change offers the repair (the install script sets
  it). Measured with `Probe-PrinterBurst.ps1 -KeepCheck` (clear, then set
  back through this path). A resume that keeps failing is logged once until it changes. A pause/resume
  failure never stops the app: it is logged, and access denied (Win32 5) shows
  one balloon per run: "LANFax cannot pause or resume the "LAN Fax" printer,
  so documents printed while LANFax is closed may be lost. Click here to
  repair it (an administrator must approve)." The click runs the install
  script, and the tray menu then shows "Repair "LAN Fax" printer
  permissions..." even though the printer is installed. A denied pause on
  Exit is not shown (the tray is going away); the next start's pause fails the
  same way and shows it. A missing printer is logged only (it has its own
  balloon). Permission: by default only the installing user and
  Administrators may pause (measured `PermissionSDDL` 2026-09-30: the
  installing user's SID has `LCSWSDRCWDWO`, Everyone only `SWRC`), hence the
  installer's Users ACE. That ACE is wider than pause/resume: Windows has no
  narrower right, and `PRINTER_ACCESS_ADMINISTER` also lets any user change
  this printer's port, driver, separator page and print processor (not its
  permissions, not deleting it). The 10 s health check limits a moved port to
  at most ~10 s of misrouted jobs before the Fix balloon. Open decision in
  handoff.md. A `taskkill /F` (the uninstaller), a crash that skips the
  handlers, or a power loss leaves the queue running with nobody claiming
  until the next start; the kept-job recovery below gets those documents back.
- Kept-job recovery (`KeptJobRecovery` over `IKeptJobStore`,
  `SpoolerKeptJobs`): the installer sets `KeepPrintedJobs`, so a printed job
  stays in the queue with status Printed (observed statuses with it on:
  `0x8`, `0x0`, `0x10`, `0x2010` while printing, `0x0`, then `0x80`; our
  delete then gives `0x84`, `0x184`) and `SetJob(JOB_CONTROL_RESTART)` prints
  it to `print.pdf` again (measured: same size, a new file 200 ms later).
  At start (queue held, leftover claimed): every kept printed job not in the
  ledger and not followed by the loss check was printed while nobody claimed;
  the one whose page count (`JOB_INFO_1.TotalPages`) matches the claimed
  leftover owns it (deleted), the others are restarted and wait in the held
  queue, then drain through the watcher. Queue position is not print order
  (measured, `-Crash` x3: jobs spooled together printed out of position
  order; picking the last by position deleted a 1-page job whose file was
  gone while restarting the leftover's 2-page job). When no single job has
  the leftover's page count, every job is restarted (one duplicate send
  dialog, never a loss). Claims made before the loss check starts (the leftover, and jobs
  a refused pause let print meanwhile) are counted: as many claims as those
  jobs and all are deleted instead (nothing was replaced). While running: a
  job the loss check trusts as `Matched` is deleted (queued, deleted on a
  pool thread by `Drain`); a `Lost` job and its `Doubtful` companions are
  restarted (at most 2 times each) instead of the "print it again" balloon,
  which shows only when the lost job is not kept or cannot be restarted (or a
  doubtful one is kept but refused). Claims no followed job took
  (`ClaimUnpaired`: no job reported within 15 s, dropped by a reset, in
  flight in a replaced monitor, or made while no monitor runs) are recorded;
  the health tick's `Sweep` waits until every kept printed job the loss check
  does not follow has been seen for 60 s, then deletes them all when there
  are as many claims, otherwise prints them all again (which one was replaced
  cannot be told: duplicates, never a silent loss) and reports the ones it
  cannot restart; unneeded claims are forgotten after 60 s. Nothing is
  deleted that no claim accounts for. On Exit: the watcher is stopped and
  waited for (2 s, so a claim past its cancellation check is reported), pairs
  at least 2 s old that no pending job doubts are trusted
  (`SettleForExit`), and `SettleAtExit` deletes unfollowed jobs only when
  the claims add up; everything else stays kept and the next start prints
  it again. A job this user may not restart or
  delete (another user's: SetJob needs the job owner or an admin) is reported
  (start: "... printed to LAN Fax while LANFax was not running and replaced
  before it could be sent; print it again.") and written to
  `spool\kept-jobs.json` (id + submit time) so no later start touches it;
  entries for jobs gone from the queue are pruned. Deleting a kept job the
  current job source never saw print reads as a finish (PRINTED|DELETING);
  `DeletedHere` feeds `PrintLossMonitor.IgnoreJob` so it is not counted
  (found live by the `-Crash` probe). Residual: a crash between a claim and
  its delete prints that document again at the next start (a duplicate send
  dialog, never a silent loss). An install without `KeepPrintedJobs` (older
  script) keeps the old detect-only behaviour.
- Loss check (`PrintLossMonitor` over `IPrintJobSource`,
  `SpoolerJobSource`): spooler change notifications for the queue's jobs
  (`JOB_NOTIFY_FIELD_STATUS`, needs only `PRINTER_ACCESS_USE`, so every
  user's jobs); a job is finished the first time its status has
  `JOB_STATUS_PRINTED` (observed: `0x84` PRINTED|DELETING then `0x184`,
  about 5-15 ms before the watcher's claim). Matching is by count: each claim
  pays the newest unmatched finished job (the port keeps only the last one
  written), a claim may also wait 15 s for its job. A finished job still
  unmatched after 15 s while no file sits at the port raises `Lost` (checked
  every 5 s). A claim can be the file of a job whose "finished" is still on
  its way (claim-before-finish), so the newest pending job may be the wrong
  owner: a pair is only trusted (`Matched`, which deletes the kept job) once
  15 s have passed and no job reported after it (within 15 s) is still
  pending; when such a job is `Lost`, the pairs made before it within 15 s
  are raised `Doubtful` and printed again too. A pair made after the lost job
  finished is not doubted (the claimed file was written after the lost one). The balloon "A document printed to LAN Fax may have been
  replaced; print it again." shows only when the printer is Installed and the
  watcher runs (`NotificationText.ForLostJob`); otherwise printing again
  cannot help, the port or watcher balloon names the real cause, and the
  loss is logged. `LossCheckHost` owns the one monitor, restarts it on
  demand, and forwards the watcher's claims to the current one without
  blocking (with no monitor, a claim goes to the kept-job sweep). Discarded
  notifications (`PRINTER_NOTIFY_INFO_DISCARDED`) reset the monitor: pending
  and paired jobs stop being followed, their claims are handed to the sweep,
  and for 15 s nothing is paired (a claim may be a job the discarded batch
  reported); the sweep's claim count then decides. The refresh read that
  follows reports every kept job's status: those ids are recorded but not
  counted as finishes (they may have printed long ago, e.g. another user's
  ledgered job). Jobs finished while the app is not running are not counted
  by the loss check; the kept-job recovery covers that time. `FinishedJobs` dedups statuses per printing: a job leaves the set
  on DELETED or on a status without PRINTED (restarted; measured: a
  restarted kept job reports `0x800`, `0x0`, `0x10`, `0x2010`, then `0x80`
  again, so it re-arms), a repeated DELETED is
  remembered (256 ids), and kept jobs are capped at 4096, oldest ids first
  (the old set was cleared at 4096, which fired the next `0x184` twice).
  Winspool offsets are hand-computed for x64; `SpoolerJobSource.Start` and
  `JobCount` throw `PlatformNotSupportedException` in a 32-bit process.
- Phase-2 probe, result 2026-09-30 (the real `LAN Fax` printer, tray app not
  running, `Out-Printer` twice back to back): the second job **silently
  overwrites** the first. A 3-page job followed at once by a 1-page job left
  one `print.pdf` with `/Count 1` and one `%%EOF`; the 3-page job alone gives
  `/Count 3`. No spooler error, no append. The spooler runs the jobs one after
  the other, so the first file is complete and then replaced.
- Burst probe after the claim-and-hold fix, 2026-09-30
  (`client/tools/Probe-PrinterBurst.ps1`, watcher only, jobs spooled
  concurrently with `PrintDocument` so the spooler runs them back to back):
  5 jobs x 3 runs, 5 jobs spooled into a paused queue then resumed, and 10
  jobs x 1 run: every run claimed exactly N files with `/Count` {1..N}, one
  `%%EOF` each, spooler finished = claimed, loss check 0. Timing: the spooler
  marks job N printed and starts job N+1 about 2 ms later, but job N+1
  creates `print.pdf` about 190 ms after that; the watcher claimed 5-17 ms
  after "printed", so the margin is roughly 175 ms. With `-NoWatch` (3 jobs,
  no watcher) the overwrite reproduces (one file, `/Count 3`) and the loss
  check reported exactly the 2 lost jobs. Residual risk: a claim delayed past
  that margin (the machine stalled ~200 ms) still loses the earlier job;
  the loss check then says so. `Out-Printer` was not used for the burst
  (text pagination makes page counts inexact, and sequential `Print()` calls
  never overlapped: each blocks about one job's rendering).
- Kept-job probes, 2026-09-30 (KeepPrintedJobs set on this PC's printer by
  its owner, non-elevated `Set-CimInstance`): `-Count 5` PASS with every
  matched job deleted (probe jobs still kept: 0); `-Count 5 -Crash` (5 jobs
  printed with nobody claiming, then the start-up hand-over) claimed the
  leftover `/Count 5` plus 4 restarted jobs, page counts {1..5}, loss check 0.
  Its first run showed the false loss fixed by `IgnoreJob` (the leftover
  owner's delete read as a finish).
- Round-4 probes, 2026-09-30: `-Count 3 -Crash -Runs 3` PASS with the
  page-count owner (in run 3 the last job by position had 1 page, the
  leftover 2); an earlier `-Crash` run under the position rule gave page
  counts {2,2,3} (a silent loss). `-Count 5` PASS with trusted matches
  deleted after the 15 s wait (probe jobs still kept: 0). `-KeepCheck` PASS
  (cleared, then set back through `SetKeepPrintedJobs`). Restart statuses
  (`-ShowStatuses`): `0x800` twice, then `0x0`, `0x10`, `0x2010`, `0x0`,
  `0x80`, `0x84`, `0x184`.

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
