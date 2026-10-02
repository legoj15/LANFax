<#
.SYNOPSIS
  One command: dotnet test, publish the app (single-file, self-contained win-x64), then Inno Setup if available.
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Invoke-Step([string]$What, [scriptblock]$Cmd) {
    Write-Host "== $What"
    & $Cmd
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
}

Invoke-Step 'dotnet test' { dotnet test (Join-Path $root 'LANFax.sln') }

$appProj = Join-Path $root 'LANFax.App'
if (Test-Path $appProj) {
    Invoke-Step 'dotnet publish' {
        dotnet publish $appProj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o (Join-Path $root 'dist\app')
    }
} else {
    Write-Host 'App project not present, skipping publish'
}

$iss = Join-Path $root 'install\LANFax.iss'
$iscc = $null
$onPath = Get-Command iscc -ErrorAction SilentlyContinue
if ($onPath) { $iscc = $onPath.Source }
foreach ($c in @('E:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe')) {
    if (-not $iscc -and (Test-Path $c)) { $iscc = $c }
}
# One version, in Directory.Build.props: the app, the installer and the updater all read it.
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
if ($iscc -and (Test-Path $iss)) {
    Invoke-Step 'inno setup' { & $iscc "/DAppVersion=$version" $iss }
} else {
    Write-Host 'Inno Setup or LANFax.iss not found, skipping installer'
}
Write-Host "build ok (version $version)"
