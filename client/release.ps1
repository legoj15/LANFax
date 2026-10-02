<#
.SYNOPSIS
  Publish the current version as a GitHub release: build + test, then upload dist\LANFax-Setup.exe.
  The in-app updater looks for exactly this asset on the latest non-draft, non-prerelease release.
.PARAMETER Notes
  Release notes shown in the update check. Defaults to a one-line message.
#>
param([string]$Notes = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$tag = "v$version"

if (git -C $root status --porcelain) { throw 'Working tree is not clean: commit first so the tag matches the build.' }
if (git -C $root tag --list $tag) { throw "Tag $tag already exists: bump <Version> in Directory.Build.props first." }

& (Join-Path $root 'build.ps1')
$setup = Join-Path $root 'dist\LANFax-Setup.exe'
if (-not (Test-Path $setup)) { throw 'dist\LANFax-Setup.exe was not built (is Inno Setup 6 installed?).' }
if (-not $Notes) { $Notes = "LANFax $version" }

gh release create $tag $setup --title "LANFax $version" --notes $Notes --target (git -C $root rev-parse HEAD)
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
Write-Host "published $tag"
