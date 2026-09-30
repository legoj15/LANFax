<#
.SYNOPSIS
  Live probe of the pipe port design on the disposable "LANFax Pipe Probe" printer (port \\.\pipe\LANFaxProbe).
  No elevation, no server, no tray app: only the LANFax PrintPipeListener runs, on pipe name LANFaxProbe.
  Prints -Count jobs back to back with 1..Count pages and checks -Count files with /Count exactly {1..Count}, one
  %%EOF each, no listener errors, and an empty queue afterwards. Files land in a temp folder, never in the real spool.
  The probe printer is created once, elevated:
    Add-PrinterPort -Name '\\.\pipe\LANFaxProbe'; Add-Printer -Name 'LANFax Pipe Probe' -DriverName 'Microsoft Print To PDF' -PortName '\\.\pipe\LANFaxProbe'
.EXAMPLE
  .\Probe-PipePrinter.ps1 -Count 5 -Runs 3
  .\Probe-PipePrinter.ps1 -Count 10
  .\Probe-PipePrinter.ps1 -Count 3 -NoListenerSeconds 30   # print with nobody listening, wait, then listen: all must arrive
  .\Probe-PipePrinter.ps1 -StopMidJobSeconds 60              # listener stopped mid-job: does the spooler retry by itself?
  .\Probe-PipePrinter.ps1 -Same -Count 2                   # same content printed twice: all must arrive (identical bytes within one second are expected, noted)
  .\Probe-PipePrinter.ps1 -WhoIs                           # who the pipe client is (spooler identity, impersonation)
  .\Probe-PipePrinter.ps1 -WhoIs -OtherUser                # listener DACL minus this user's rule: does another user's job get through?
  .\Probe-PipePrinter.ps1 -Drain                           # take in leftovers of an aborted run until the queue is empty
#>
[CmdletBinding()]
param(
    [int]$Count = 5,
    [int]$Runs = 1,
    [int]$NoListenerSeconds = -1,
    [int]$StopMidJobSeconds = -1,
    [switch]$WhoIs,
    [switch]$OtherUser,
    [switch]$Drain,
    [switch]$Same,
    [string]$PrinterName = 'LANFax Pipe Probe',
    [string]$PipeName = 'LANFaxProbe',
    [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) ('lanfax-pipe-probe-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'PipeProbe\PipeProbe.csproj'
dotnet build $proj -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "building the probe failed (exit $LASTEXITCODE)" }
$exe = Join-Path $PSScriptRoot 'PipeProbe\bin\Release\net10.0-windows\PipeProbe.exe'

$failed = 0
for ($r = 1; $r -le $Runs; $r++) {
    "==== run $r of $Runs ===="
    $a = @('--count', $Count, '--printer', $PrinterName, '--pipe', $PipeName, '--out', (Join-Path $Out "run$r"))
    if ($NoListenerSeconds -ge 0) { $a += @('--no-listener', $NoListenerSeconds) }
    if ($StopMidJobSeconds -ge 0) { $a += @('--stop-mid-job', $StopMidJobSeconds) }
    if ($WhoIs) { $a += '--whois' }
    if ($OtherUser) { $a += '--other-user' }
    if ($Drain) { $a += '--drain' }
    if ($Same) { $a += '--same' }
    & $exe @a
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
"==== $($Runs - $failed) of $Runs run(s) passed; files in $Out ===="
exit $failed
