<#
.SYNOPSIS
  Installs the "LAN Fax" printer: a Microsoft Print To PDF queue whose port is a file path the LANFax app watches.
  Self-elevates (UAC prompt). Idempotent.
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:ProgramData 'LANFax'),
    [string]$SpoolDir = (Join-Path $env:ProgramData 'LANFax\spool'),
    [string]$PrinterName = 'LAN Fax'
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $exe = (Get-Process -Id $PID).Path
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath),
                 '-DataDir', ('"{0}"' -f $DataDir), '-SpoolDir', ('"{0}"' -f $SpoolDir),
                 '-PrinterName', ('"{0}"' -f $PrinterName))
    $p = Start-Process -FilePath $exe -ArgumentList $argList -Verb RunAs -Wait -PassThru
    exit $p.ExitCode
}

$portName = Join-Path $SpoolDir 'print.pdf'
$driver = 'Microsoft Print To PDF'

function Grant-UsersModify([string]$Path) {
    # Inherited by everything below, including files created later (config.json saved by another user).
    $acl = Get-Acl -Path $Path
    $rule = New-Object Security.AccessControl.FileSystemAccessRule(
        'BUILTIN\Users', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.SetAccessRule($rule)
    Set-Acl -Path $Path -AclObject $acl
}

# Data dir (config.json) and spool dir, writable by any logged-in user: any Windows user may save
# Settings, and the spooler writes print.pdf as the printing user.
$null = New-Item -ItemType Directory -Force -Path $DataDir
$null = New-Item -ItemType Directory -Force -Path $SpoolDir
$null = New-Item -ItemType Directory -Force -Path (Join-Path $SpoolDir 'jobs')
Grant-UsersModify -Path $DataDir
$dataFull = [IO.Path]::GetFullPath($DataDir).TrimEnd('\') + '\'
$spoolFull = [IO.Path]::GetFullPath($SpoolDir).TrimEnd('\') + '\'
if (-not $spoolFull.StartsWith($dataFull, [StringComparison]::OrdinalIgnoreCase)) {
    Grant-UsersModify -Path $SpoolDir  # custom spool location outside the data dir
}

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
