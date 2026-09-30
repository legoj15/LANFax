<#
.SYNOPSIS
  Removes the "LAN Fax" printer and its file port. Self-elevates (UAC prompt). Idempotent.
  Leaves %ProgramData%\LANFax alone (config.json, unsent documents in spool\jobs, kept copies in spool\sent,
  rejected print files); the uninstaller keeps that folder too.
#>
[CmdletBinding()]
param(
    [string]$SpoolDir = (Join-Path $env:ProgramData 'LANFax\spool'),
    [string]$PrinterName = 'LAN Fax'
)

$ErrorActionPreference = 'Stop'

# Every value is quoted into ONE elevated command line: a quote would end its argument early, so it is refused;
# backslashes before the closing quote are doubled so they cannot escape it (argv parsing halves them again).
function ConvertTo-QuotedArg([string]$Value) {
    if ($Value.Contains('"')) { throw "A quote is not allowed here: $Value" }
    '"' + ($Value -replace '(\\+)$', '$1$1') + '"'
}
foreach ($value in @($SpoolDir, $PrinterName)) { $null = ConvertTo-QuotedArg $value } # refuse before anything runs

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $exe = (Get-Process -Id $PID).Path
    # One command-line string: Windows PowerShell 5.1 re-quotes array entries that contain spaces and splits them.
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File $(ConvertTo-QuotedArg $PSCommandPath) -SpoolDir $(ConvertTo-QuotedArg $SpoolDir) -PrinterName $(ConvertTo-QuotedArg $PrinterName)"
    $p = Start-Process -FilePath $exe -ArgumentList $argLine -Verb RunAs -Wait -PassThru
    exit $p.ExitCode
}

$portName = Join-Path $SpoolDir 'print.pdf'

$printerRemoved = $false
if (Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue) {
    Remove-Printer -Name $PrinterName
    $printerRemoved = $true
}

$portRemoved = $false
if (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue) {
    Remove-PrinterPort -Name $portName
    $portRemoved = $true
}

"LANFax printer '$PrinterName' removed: $printerRemoved, port removed: $portRemoved"
