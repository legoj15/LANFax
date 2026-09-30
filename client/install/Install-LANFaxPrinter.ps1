<#
.SYNOPSIS
  Installs the "LAN Fax" printer: a Microsoft Print To PDF queue whose port is the local named pipe
  \\.\pipe\LANFax, served by the LANFax tray app. Every print job is its own connection to the pipe.
  Self-elevates (UAC prompt). Idempotent.
  Also migrates an install from before 2026-09-30 (file port spool\print.pdf): moves the printer to the pipe port,
  removes the old file port when no printer uses it, turns "Keep printed documents" off, resumes a paused queue and
  takes back the Users "manage this printer" permission that the superseded install added, lists documents it kept
  after printing (left in the queue: harmless, the user may delete them), and hands a document left in the old
  print.pdf to the tray app as an unsent job when it is one complete PDF (otherwise to spool\rejected\).
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:ProgramData 'LANFax'),
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
foreach ($value in @($DataDir, $SpoolDir, $PrinterName, $PipeName)) { $null = ConvertTo-QuotedArg $value } # refuse before anything runs

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $exe = (Get-Process -Id $PID).Path
    # One command-line string: Windows PowerShell 5.1 re-quotes array entries that contain spaces and splits them.
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File $(ConvertTo-QuotedArg $PSCommandPath) -DataDir $(ConvertTo-QuotedArg $DataDir) -SpoolDir $(ConvertTo-QuotedArg $SpoolDir) -PrinterName $(ConvertTo-QuotedArg $PrinterName) -PipeName $(ConvertTo-QuotedArg $PipeName)"
    $p = Start-Process -FilePath $exe -ArgumentList $argLine -Verb RunAs -Wait -PassThru
    exit $p.ExitCode
}

$portName = '\\.\pipe\' + $PipeName
$oldPort = Join-Path $SpoolDir 'print.pdf'   # the file port of installs before 2026-09-30
$driver = 'Microsoft Print To PDF'

function Grant-UsersModify([string]$Path) {
    # Inherited by everything below, including files created later (config.json saved by another user).
    $acl = Get-Acl -Path $Path
    $rule = New-Object Security.AccessControl.FileSystemAccessRule(
        'BUILTIN\Users', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.SetAccessRule($rule)
    Set-Acl -Path $Path -AclObject $acl
}

# Takes back the one ACE the superseded install (commit c677f1a) added to the printer: BUILTIN\Users (by SID),
# allow READ_CONTROL | PRINTER_ACCESS_ADMINISTER | PRINTER_ACCESS_USE (0x0002000C), no inheritance flags. Any other
# ACE, including other Users ACEs, is kept as it is. Returns the input unchanged when that ACE is not there.
function Remove-UsersPauseAce([string]$Sddl) {
    $sd = New-Object Security.AccessControl.RawSecurityDescriptor($Sddl)
    if ($null -eq $sd.DiscretionaryAcl) { return $Sddl }
    $users = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
    $removed = $false
    for ($i = $sd.DiscretionaryAcl.Count - 1; $i -ge 0; $i--) {
        $ace = $sd.DiscretionaryAcl[$i]
        if ($ace -is [Security.AccessControl.CommonAce] -and $ace.SecurityIdentifier -eq $users -and
            $ace.AceQualifier -eq 'AccessAllowed' -and $ace.AceFlags -eq 'None' -and $ace.AccessMask -eq 0x0002000C) {
            $sd.DiscretionaryAcl.RemoveAce($i)
            $removed = $true
        }
    }
    if (-not $removed) { return $Sddl }
    $sd.GetSddlForm('All')
}

# The print pipe listener's acceptance rule (docs/client-spec.md "Print job acceptance"; the same test vectors pin
# both): starts with %PDF, exactly one %%EOF, nothing but PDF whitespace after it, at most $MaxBytes (the listener's
# limit). Two jobs written over each other in the old print.pdf hold two (a cut-off one none) and are not offered.
function Test-SinglePdf([string]$Path, [long]$MaxBytes = 200MB) {
    $length = (Get-Item -LiteralPath $Path).Length
    if ($length -lt 4 -or $length -gt $MaxBytes) { return $false }
    $text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($Path)) # Latin-1: one char per byte
    $text.StartsWith('%PDF', [StringComparison]::Ordinal) -and ([regex]::Matches($text, '%%EOF')).Count -eq 1 -and
        [regex]::IsMatch($text, '%%EOF[ \r\n\t\f\x00]*\z')
}

# The message listing documents the superseded install kept in the queue after printing (JobStatus has Printed, not
# Error), or nothing when there are none. They are left alone: they never print again unless someone restarts one.
function Format-KeptPrintedJobs([object[]]$Jobs) {
    $kept = @($Jobs | Where-Object { "$($_.JobStatus)" -match 'Printed' -and "$($_.JobStatus)" -notmatch 'Error' })
    if ($kept.Count -eq 0) { return }
    $names = ($kept | ForEach-Object { "'$($_.DocumentName)'" }) -join ', '
    "$($kept.Count) printed document(s) kept in the queue by an earlier LANFax install, left there: $names. They are harmless; delete them from the printer queue if you like (restarting one would fax it again)."
}

# Data dir (config.json) and spool dir, writable by any logged-in user: any Windows user may save Settings, and the
# tray app (running as whoever is signed in) writes spool\jobs, spool\rejected and spool\sent. The spooler itself no
# longer writes here: it hands every job to the tray app through the pipe.
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
$printerMoved = $false
$existing = Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue
if (-not $existing) {
    Add-Printer -Name $PrinterName -DriverName $driver -PortName $portName
    $printerAdded = $true
}
elseif ($existing.PortName -ne $portName) {
    # The file port of an older install (or any other port) prints where nobody listens now.
    "moving printer '$PrinterName' from port '$($existing.PortName)' to '$portName'"
    Set-Printer -Name $PrinterName -PortName $portName
    $printerMoved = $true
}

# Jobs the superseded install kept after they printed stay in the queue. They are listed, never removed: the user
# decides. Listed first: once "Keep printed documents" is off the spooler may drop them, and the list would be lost.
# A job in Error was never delivered and is not listed (restart it once LANFax runs).
try { Format-KeptPrintedJobs (Get-PrintJob -PrinterName $PrinterName) }
catch { "warning: listing the printed documents kept in '$PrinterName' failed: $($_.Exception.Message)" }

# Printed jobs leave the queue: every job reaches the app whole through its own connection, nothing to reprint.
$wp = Get-CimInstance -ClassName Win32_Printer | Where-Object Name -eq $PrinterName
$keepCleared = $false
if ($wp.KeepPrintedJobs) {
    $wp.KeepPrintedJobs = $false
    Set-CimInstance -InputObject $wp
    $keepCleared = $true
}

# A queue the superseded install left paused prints again. With no listener the spooler holds and retries jobs
# itself ("Error - Printing") and delivers them once the app runs.
$resumed = $false
if ("$((Get-Printer -Name $PrinterName).PrinterStatus)" -match 'Paused') {
    $r = (Get-CimInstance -ClassName Win32_Printer | Where-Object Name -eq $PrinterName | Invoke-CimMethod -MethodName Resume).ReturnValue
    if ($r -ne 0) { "warning: resuming '$PrinterName' returned $r" } else { $resumed = $true }
}

# Migration cleanups last, as warnings: neither may leave the printer paused or keeping printed jobs.
$aceRemoved = $false
try {
    $sddl = (Get-Printer -Name $PrinterName -Full).PermissionSDDL
    $withoutAce = Remove-UsersPauseAce $sddl
    if ($withoutAce -ne $sddl) { Set-Printer -Name $PrinterName -PermissionSDDL $withoutAce; $aceRemoved = $true }
}
catch { "warning: taking back the Users manage right on '$PrinterName' failed: $($_.Exception.Message)" }

# A document printed to the old file port while the tray was not running is still in print.pdf, and nothing reads that
# file any more. One complete PDF moves to spool\jobs\<guid>.pdf, which the tray offers as unsent at its next start;
# anything else (two jobs written over each other, a cut-off one) goes to spool\rejected\ and is never offered.
$leftover = 'none'
$rejectedDir = Join-Path $SpoolDir 'rejected'
try {
    if ((Test-Path -LiteralPath $oldPort -PathType Leaf) -and (Get-Item -LiteralPath $oldPort).Length -gt 0) {
        if (Test-SinglePdf $oldPort) {
            Move-Item -LiteralPath $oldPort -Destination (Join-Path $SpoolDir "jobs\$([guid]::NewGuid()).pdf"); $leftover = 'offered as unsent'
        }
        else {
            $null = New-Item -ItemType Directory -Force -Path $rejectedDir
            $dest = Join-Path $rejectedDir "print-$([guid]::NewGuid()).pdf"
            Move-Item -LiteralPath $oldPort -Destination $dest
            "the document left in '$oldPort' is not one complete PDF; moved to '$dest' (not offered for sending)"
            $leftover = 'rejected'
        }
    }
}
catch { "warning: moving the document left in '$oldPort' out of the way failed (move it into the jobs or rejected folder by hand): $($_.Exception.Message)" }

# The old file port goes once no printer uses it (never another printer's port).
$oldPortRemoved = $false
try {
    if ((Get-PrinterPort -Name $oldPort -ErrorAction SilentlyContinue) -and -not (Get-Printer | Where-Object PortName -eq $oldPort)) {
        Remove-PrinterPort -Name $oldPort; $oldPortRemoved = $true
    }
}
catch { "warning: removing the old port '$oldPort' failed (run this script again later): $($_.Exception.Message)" }

"LANFax printer '$PrinterName' -> $portName (port added: $portAdded, printer added: $printerAdded, printer moved: $printerMoved, keep printed jobs cleared: $keepCleared, queue resumed: $resumed, users manage right removed: $aceRemoved, document left at the old port: $leftover, old file port removed: $oldPortRemoved)"
