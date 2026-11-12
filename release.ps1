# Publishes a release of Valheim Helper on GitHub (shkbb/valheim-helper) with gh:
#   tag v<Version> (the <Version> of ValheimHelper\ValheimHelper.csproj, raise it before a new release),
#   assets ValheimHelper-full.zip, -helper.zip, -legal.zip (to install) and -full-update.zip, -helper-update.zip,
#   -legal-update.zip (what the mod's updater downloads by itself).
#   .\release.ps1 -Notes "What changed"        build everything, then publish
#   .\release.ps1 -SkipBuild -Notes "..."      publish what dist\ already holds
# ASCII-only on purpose: Windows PowerShell 5.1 misreads non-ASCII in scripts saved without BOM.
param([switch]$SkipBuild, [string]$Notes = '', [switch]$Draft)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = 'shkbb/valheim-helper'

[xml]$project = Get-Content (Join-Path $root 'ValheimHelper\ValheimHelper.csproj') -Raw
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in ValheimHelper.csproj' }
$tag = 'v' + $version

& gh release view $tag -R $repo *> $null
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists: raise <Version> in ValheimHelper.csproj first" }

if (-not $SkipBuild) {
    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) { throw 'build.ps1 failed' }
}

$assets = @()
foreach ($edition in 'full', 'helper', 'legal') {
    foreach ($suffix in '', '-update') {
        $file = Join-Path $root ('dist\ValheimHelper-' + $edition + $suffix + '.zip')
        if (-not (Test-Path $file)) { throw "Missing $file" }
        $assets += $file
    }
}

if (-not $Notes) { $Notes = "Valheim Helper $version" }
$text = $Notes + "`n`n" +
    "Install: pick ONE edition, unpack it and run install.bat (Valheim closed).`n" +
    "- ValheimHelper-legal.zip: map, guide, camera, statistics, achievement list, no advantage in the game`n" +
    "- ValheimHelper-helper.zip: legal + chests, smelters, torches, crafting from chests`n" +
    "- ValheimHelper-full.zip: everything, cheats included`n" +
    "The *-update.zip files are for the mod's own updater; you don't need them."
$args = @('release', 'create', $tag, '-R', $repo, '--title', "Valheim Helper $version", '--notes', $text) + $assets
if ($Draft) { $args += '--draft' }
& gh @args
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
Write-Host "Published $tag"
