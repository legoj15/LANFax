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
    # the token is read and used on the server; it never touches this machine
    $remote = 'curl -fsS -H "Authorization: Bearer $(sudo -n grep ^FAXD_TOKEN= /etc/faxserver/settings.env | cut -d= -f2)" http://127.0.0.1:' + $Port + '/status'
    Invoke-Step "GET /status" { ssh $HostName $remote }
    Write-Host ""
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
