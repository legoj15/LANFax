#requires -Version 7
<#
Copies server/ to the fax server, runs install.sh as root, then prints /status.
Re-runnable.
#>
param(
    [string]$HostName = "faxserver",
    [int]$Port = 8080
)
$ErrorActionPreference = "Stop"

function Invoke-Step([string]$What, [scriptblock]$Block) {
    & $Block
    if ($LASTEXITCODE -ne 0) { throw "deploy failed at: $What (exit $LASTEXITCODE)" }
}

try {
    Invoke-Step "clean remote tmp" { ssh $HostName "rm -rf ~/lanfax-deploy-tmp" }
    Invoke-Step "scp server dir" { scp -r "$PSScriptRoot" "${HostName}:~/lanfax-deploy-tmp" }
    Invoke-Step "install on server" {
        ssh $HostName "rm -rf ~/lanfax-deploy && mv ~/lanfax-deploy-tmp ~/lanfax-deploy && sudo -n bash ~/lanfax-deploy/install.sh"
    }
    $token = (ssh $HostName "grep ^FAXD_TOKEN= /etc/faxserver/settings.env | cut -d= -f2").Trim()
    if ($LASTEXITCODE -ne 0 -or -not $token) { throw "could not read FAXD_TOKEN from server" }
    Invoke-Step "GET /status" { curl.exe -fsS -H "Authorization: Bearer $token" "http://${HostName}:$Port/status" }
    Write-Host ""
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
