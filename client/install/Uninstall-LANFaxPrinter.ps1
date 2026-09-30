<#
.SYNOPSIS
  Removes the "LAN Fax" printer and its pipe port \\.\pipe\LANFax (and the file port spool\print.pdf of installs
  before 2026-09-30, when no printer uses it). Self-elevates (UAC prompt). Idempotent.
  Leaves %ProgramData%\LANFax alone (config.json, unsent documents in spool\jobs, kept copies in spool\sent,
  rejected print jobs in spool\rejected); the uninstaller keeps that folder too.
#>
[CmdletBinding()]
param(
    [string]$SpoolDir = (Join-Path $env:ProgramData 'LANFax\spool'),
    [string]$PrinterName = 'LAN Fax',
    [string]$PipeName = 'LANFax'
)

$ErrorActionPreference = 'Stop'

# Every value is quoted into ONE elevated command line: a quote would end its argument early, so it is refused;
# backslashes before the closing quote are doubled so they cannot escape it (argv parsing halves them again).
function ConvertTo-QuotedArg([string]$Value) {
    if ($Value.Contains('"')) { throw "A quote is not allowed here: $Value" }
    '"' + ($Value -replace '(\\+)$', '$1$1') + '"'
}
foreach ($value in @($SpoolDir, $PrinterName, $PipeName)) { $null = ConvertTo-QuotedArg $value } # refuse before anything runs

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $exe = (Get-Process -Id $PID).Path
    # One command-line string: Windows PowerShell 5.1 re-quotes array entries that contain spaces and splits them.
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File $(ConvertTo-QuotedArg $PSCommandPath) -SpoolDir $(ConvertTo-QuotedArg $SpoolDir) -PrinterName $(ConvertTo-QuotedArg $PrinterName) -PipeName $(ConvertTo-QuotedArg $PipeName)"
    $p = Start-Process -FilePath $exe -ArgumentList $argLine -Verb RunAs -Wait -PassThru
    exit $p.ExitCode
}

$portName = '\\.\pipe\' + $PipeName
$oldPort = Join-Path $SpoolDir 'print.pdf'

$printerRemoved = $false
if (Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue) {
    # Documents still waiting in the queue (none unless LANFax was not running) are removed first: with nobody on the
    # pipe they never finish, and a printer that still has jobs is only marked "pending deletion", keeping its port.
    $jobs = @(Get-PrintJob -PrinterName $PrinterName -ErrorAction SilentlyContinue)
    if ($jobs.Count -gt 0) {
        "discarding $($jobs.Count) document(s) still waiting in the '$PrinterName' queue"
        $jobs | Remove-PrintJob -ErrorAction Continue
    }
    Remove-Printer -Name $PrinterName
    for ($i = 0; $i -lt 20 -and (Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    if (Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue) {
        "warning: '$PrinterName' is still there (pending deletion while a document is in its queue); run this script again once its queue is empty to remove it and its port"
    }
    else { $printerRemoved = $true }
}

$portRemoved = $false
if ((Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue) -and
    -not (Get-Printer | Where-Object PortName -eq $portName)) {
    Remove-PrinterPort -Name $portName
    $portRemoved = $true
}

$oldPortRemoved = $false
if ((Get-PrinterPort -Name $oldPort -ErrorAction SilentlyContinue) -and
    -not (Get-Printer | Where-Object PortName -eq $oldPort)) {
    Remove-PrinterPort -Name $oldPort
    $oldPortRemoved = $true
}

"LANFax printer '$PrinterName' removed: $printerRemoved, port removed: $portRemoved, old file port removed: $oldPortRemoved"
