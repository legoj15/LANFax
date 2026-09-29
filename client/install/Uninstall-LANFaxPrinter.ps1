<#
.SYNOPSIS
  Removes the "LAN Fax" printer and its file port. Self-elevates (UAC prompt). Idempotent. Leaves the spool folder.
#>
[CmdletBinding()]
param(
    [string]$SpoolDir = (Join-Path $env:ProgramData 'LANFax\spool'),
    [string]$PrinterName = 'LAN Fax'
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $exe = (Get-Process -Id $PID).Path
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath),
                 '-SpoolDir', ('"{0}"' -f $SpoolDir), '-PrinterName', ('"{0}"' -f $PrinterName))
    $p = Start-Process -FilePath $exe -ArgumentList $argList -Verb RunAs -Wait -PassThru
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
