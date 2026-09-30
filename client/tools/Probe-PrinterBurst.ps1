<#
.SYNOPSIS
  Live burst probe for the real "LAN Fax" printer. No elevation, no server, tray app must be closed.
  Starts only the SpoolWatcher (plus the spooler job count used by the tray app's loss check), prints -Count jobs
  back to back with 1..Count pages, and checks that exactly Count files were claimed with page counts {1..Count}.
  Probe files end in -Out (never left in spool\jobs); the probe's kept printed jobs are deleted; the queue is left resumed.
.EXAMPLE
  .\Probe-PrinterBurst.ps1 -Count 5 -Runs 3
  .\Probe-PrinterBurst.ps1 -Count 5 -PausedFirst     # held in a paused queue, then resumed: must drain without loss
  .\Probe-PrinterBurst.ps1 -Count 3 -NoWatch         # no watcher: the loss check must report Count-1 lost
  .\Probe-PrinterBurst.ps1 -Count 3 -Crash           # app dead while printing, then start-up recovery: all Count come back
  .\Probe-PrinterBurst.ps1 -KeepCheck                # clear "Keep printed documents" and set it back (health check path)
#>
[CmdletBinding()]
param(
    [int]$Count = 5,
    [int]$Runs = 1,
    [switch]$PausedFirst,
    [switch]$NoWatch,
    [switch]$Crash,
    [switch]$ShowStatuses,
    [switch]$KeepCheck,
    [string]$PrinterName = 'LAN Fax',
    [string]$SpoolDir = (Join-Path $env:ProgramData 'LANFax\spool'),
    [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) ('lanfax-burst-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'BurstProbe\BurstProbe.csproj'
dotnet build $proj -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "building the probe failed (exit $LASTEXITCODE)" }
$exe = Join-Path $PSScriptRoot 'BurstProbe\bin\Release\net10.0-windows\BurstProbe.exe'

$failed = 0
for ($r = 1; $r -le $Runs; $r++) {
    "==== run $r of $Runs ===="
    $a = @('--count', $Count, '--printer', $PrinterName, '--spool', $SpoolDir, '--out', (Join-Path $Out "run$r"))
    if ($PausedFirst) { $a += '--paused-first' }
    if ($NoWatch) { $a += '--no-watch' }
    if ($Crash) { $a += '--crash' }
    if ($ShowStatuses) { $a += '--verbose' }
    if ($KeepCheck) { $a += '--keep-check' }
    & $exe @a
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
"==== $($Runs - $failed) of $Runs run(s) passed; files in $Out ===="
exit $failed
