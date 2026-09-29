<#
.SYNOPSIS
  Installs the "LAN Fax" printer: a Microsoft Print To PDF queue whose port is a file path the LANFax app watches.
  Self-elevates (UAC prompt). Idempotent.
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
$driver = 'Microsoft Print To PDF'

# Spool directory, writable by any logged-in user (the spooler writes as the printing user).
$null = New-Item -ItemType Directory -Force -Path $SpoolDir
$null = New-Item -ItemType Directory -Force -Path (Join-Path $SpoolDir 'jobs')
$acl = Get-Acl -Path $SpoolDir
$rule = New-Object Security.AccessControl.FileSystemAccessRule(
    'BUILTIN\Users', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.SetAccessRule($rule)
Set-Acl -Path $SpoolDir -AclObject $acl

$portAdded = $false
if (-not (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue)) {
    Add-PrinterPort -Name $portName
    $portAdded = $true
}

$printerAdded = $false
if (-not (Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue)) {
    Add-Printer -Name $PrinterName -DriverName $driver -PortName $portName
    $printerAdded = $true
}

"LANFax printer '$PrinterName' -> $portName (port added: $portAdded, printer added: $printerAdded)"
