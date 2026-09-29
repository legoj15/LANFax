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
    Config.cs                       %ProgramData%\LANFax\config.json (server url, token via DPAPI machine scope, printer name)
    FaxApi.cs                       HttpClient wrapper for faxd: status, jobs, inbox, post job (Idempotency-Key), test send, arm/disarm, pdf download
    EventStream.cs                  SSE client: GET /events?token=, Last-Event-ID, reconnect with backoff, raises typed events
    Spool.cs                        watches the port path; stability wait; moves to jobs/<guid>.pdf; raises NewPrintJob
    PrinterSetup.cs                 detects the printer; launches the elevated install/uninstall script
    Models.cs                       Job, ArmState, Status records mirroring faxd JSON
  LANFax.App/                       net10.0-windows WinForms, single-file self-contained win-x64
    Program.cs                      single-instance mutex; tray icon; no console
    TrayContext.cs                  NotifyIcon + menu: Open, Send a test..., Receive a test (arm 20 min), Settings, Install printer, Exit
    SendDialog.cs                   number, subject, preview page count, Send / Cancel; "Send test page instead" checkbox
    MainForm.cs                     (phase 3) status header, arm card with countdown, jobs grid, inbox grid, open PDF
    SettingsDialog.cs               server host/ip, port, token, "Test connection"
  LANFax.Tests/                     xunit: Core only (no UI)
  install/
    Install-LANFaxPrinter.ps1       elevated: Add-PrinterPort -Name <spool path>; Add-Printer -Name "LAN Fax" -DriverName "Microsoft Print To PDF" -PortName <spool path>; idempotent
    Uninstall-LANFaxPrinter.ps1
    LANFax.iss                      Inno Setup: installs app to Program Files, runs Install-LANFaxPrinter.ps1, adds Run key for tray autostart, uninstall removes printer
  build.ps1                         dotnet test, dotnet publish, iscc -> dist/LANFax-Setup.exe
```

## Spool path and the printer

- Port name and file: `C:\ProgramData\LANFax\spool\print.pdf`. The directory
  is created by the installer with Users:Modify so any logged-in user's print
  job can write it (the spooler writes as the user for Local Ports).
- Watcher: `FileSystemWatcher` on the spool dir (Created/Changed) plus a 2 s
  polling fallback. A job is complete when the file has existed for at least
  1 s, its size is unchanged across two checks, and it can be opened with
  `FileShare.None`. Then move it to `spool\jobs\<guid>.pdf` immediately
  (frees the port path for the next job) and raise `NewPrintJob(guid, path)`.
- Phase-2 probe (do first, record the result here): print two jobs back to
  back to the disposable printer and record whether the second overwrites,
  appends, or fails while the first is still present. If it fails, the app
  must move the file within ~1 s (it does) and the second job will retry via
  the spooler's own retry; if it appends, the app must detect `%%EOF` count
  and split, or reject and notify.

## Send flow (phase 2)

1. `NewPrintJob` -> `SendDialog` on the UI thread, focused, topmost.
2. Number field: accepts anything, normalized client-side the same way as
   faxd (`normalize_number` rules in `faxd-spec.md`; block 911/933/988 and
   900/976 area codes client-side too, with a clear message).
3. Subject optional. "Send test page instead of this document" checkbox
   turns the request into `POST /test/send` and discards the PDF.
4. `POST /jobs` with `Content-Type: application/pdf`, `X-Fax-To`,
   `X-Fax-Subject`, `Idempotency-Key: <guid>`. Retry on timeout/5xx with the
   same key up to 3 times, backoff 2/4/8 s. 201 or 200 both mean accepted.
5. On accept: balloon "Fax to <number> queued", keep `spool\jobs\<guid>.pdf`
   until the job is terminal, then delete (or move to `spool\sent\` if the
   "keep local copies" setting is on). On terminal `failed`: balloon with the
   error and a "Retry" action that reopens the dialog with the same PDF.
6. Cancel: delete the PDF.

## Monitor (phase 3)

- `MainForm` mirrors the web page: status pill (registrations, daemon,
  receiving), arm card with countdown and buttons (1 h, 4 h, 8 h, 20 min test
  window, Disarm), jobs grid (time, to, state badge, pages, error), inbox
  grid (time, from, pages), double-click opens the PDF (download to
  `%LocalAppData%\LANFax\cache\<id>.pdf`, then `ShellExecute`).
- Live updates from `EventStream`; on reconnect reload both grids
  (`GET /jobs?updated_since=`).
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

## Build and test rules

- Tests first in `LANFax.Tests` for Core: number normalization, spool
  stability/move logic (temp dir), FaxApi request shapes and retry (against
  an in-process `HttpListener` fake), EventStream parsing including `id:`
  and `Last-Event-ID`, config round trip.
- `build.ps1` is the one command: `dotnet test`, `dotnet publish -c Release
  -r win-x64 --self-contained -p:PublishSingleFile=true`, then Inno Setup.
- No third-party NuGet packages in Core or App beyond the BCL
  (`System.Security.Cryptography.ProtectedData` is part of .NET on Windows).
  Tests may use xunit.
- The app never stores the token in plain text and never logs it.
