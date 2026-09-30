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

# Every value is quoted into ONE elevated command line: a quote would end its argument early, so it is refused;
# backslashes before the closing quote are doubled so they cannot escape it (argv parsing halves them again).
function ConvertTo-QuotedArg([string]$Value) {
    if ($Value.Contains('"')) { throw "A quote is not allowed here: $Value" }
    '"' + ($Value -replace '(\\+)$', '$1$1') + '"'
}
foreach ($value in @($DataDir, $SpoolDir, $PrinterName)) { $null = ConvertTo-QuotedArg $value } # refuse before anything runs

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $exe = (Get-Process -Id $PID).Path
    # One command-line string: Windows PowerShell 5.1 re-quotes array entries that contain spaces and splits them.
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File $(ConvertTo-QuotedArg $PSCommandPath) -DataDir $(ConvertTo-QuotedArg $DataDir) -SpoolDir $(ConvertTo-QuotedArg $SpoolDir) -PrinterName $(ConvertTo-QuotedArg $PrinterName)"
    $p = Start-Process -FilePath $exe -ArgumentList $argLine -Verb RunAs -Wait -PassThru
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

# Adds one allow ACE for BUILTIN\Users (by SID, so it is not localized) to a printer's security descriptor:
# READ_CONTROL | PRINTER_ACCESS_ADMINISTER | PRINTER_ACCESS_USE, no inheritance flags. Windows has no narrower right
# that allows pause/resume, and PRINTER_ACCESS_ADMINISTER also lets any user change THIS printer's settings (its port,
# driver, separator page, print processor). It does not cover its jobs, deleting it, or its permissions. The tray app
# re-reads the port every 10 s and offers a fix when it moved. Every existing ACE is kept as it is; the new one goes
# after any deny ACEs. Returns the input unchanged when the ACE is already there.
function Add-UsersPauseAce([string]$Sddl) {
    $sd = New-Object Security.AccessControl.RawSecurityDescriptor($Sddl)
    if ($null -eq $sd.DiscretionaryAcl) { throw "The printer's security descriptor has no DACL: $Sddl" }
    $users = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
    $mask = 0x0002000C
    foreach ($ace in $sd.DiscretionaryAcl) {
        if ($ace -is [Security.AccessControl.CommonAce] -and $ace.SecurityIdentifier -eq $users -and
            $ace.AceQualifier -eq 'AccessAllowed' -and $ace.AceFlags -eq 'None' -and (($ace.AccessMask -band $mask) -eq $mask)) {
            return $Sddl
        }
    }
    $at = 0
    while ($at -lt $sd.DiscretionaryAcl.Count -and $sd.DiscretionaryAcl[$at].AceQualifier -eq 'AccessDenied') { $at++ }
    $allow = New-Object Security.AccessControl.CommonAce('None', 'AccessAllowed', $mask, $users, $false, $null)
    $sd.DiscretionaryAcl.InsertAce($at, $allow)
    $sd.GetSddlForm('All')
}

$portAdded = $false
if (-not (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue)) {
    Add-PrinterPort -Name $portName
    $portAdded = $true
}

$printerAdded = $false
$printerMoved = $false
$existing = Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue
if (-not $existing) {
    Add-Printer -Name $PrinterName -DriverName $driver -PortName $portName
    $printerAdded = $true
}
elseif ($existing.PortName -ne $portName) {
    # A queue left on another port (older install, different spool dir, made by hand) prints where nobody watches.
    "moving printer '$PrinterName' from port '$($existing.PortName)' to '$portName'"
    Set-Printer -Name $PrinterName -PortName $portName
    $printerMoved = $true
}

# The LANFax tray app (a standard user) resumes the queue when it starts and pauses it when it exits, so documents
# printed while it is not running wait in the queue instead of overwriting each other at the port.
$sddl = (Get-Printer -Name $PrinterName -Full).PermissionSDDL
$withUsers = Add-UsersPauseAce $sddl
$permissionAdded = $withUsers -ne $sddl
if ($permissionAdded) { Set-Printer -Name $PrinterName -PermissionSDDL $withUsers }

# Printed jobs stay in the queue (as "Printed") until the app deletes them. A document whose print.pdf was replaced
# before anyone claimed it (the app killed or crashed while the queue printed) can then be printed again: the app
# restarts every kept job it has not dealt with when it starts.
$wp = Get-CimInstance -ClassName Win32_Printer | Where-Object Name -eq $PrinterName
$keepAdded = $false
if (-not $wp.KeepPrintedJobs) {
    $wp.KeepPrintedJobs = $true
    Set-CimInstance -InputObject $wp
    $keepAdded = $true
}

# Held until the app runs: it resumes the queue once it is watching print.pdf. A LANFax already running (any
# session: a repair, an upgrade it was kept open for) watches the port now, so the queue is left printing for it.
$lanfaxRunning = [bool](Get-Process -Name LANFax -ErrorAction SilentlyContinue)
if ($lanfaxRunning) {
    $queueState = 'left printing: LANFax is running'
}
else {
    $paused = (Get-CimInstance -ClassName Win32_Printer | Where-Object Name -eq $PrinterName | Invoke-CimMethod -MethodName Pause).ReturnValue
    if ($paused -ne 0) { "warning: pausing '$PrinterName' returned $paused" }
    $queueState = 'paused until LANFax starts'
}

"LANFax printer '$PrinterName' -> $portName (port added: $portAdded, printer added: $printerAdded, printer moved: $printerMoved, users may pause: added $permissionAdded, keeps printed jobs: set $keepAdded, $queueState)"
