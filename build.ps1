# Builds ValheimHelper.dll in three editions and assembles the packages (each with an install zip and an update zip):
#   dist\ValheimHelper         (ValheimHelper-full.zip, -full-update.zip)      full: everything, cheats included (installed here)
#   dist\ValheimHelper-helper  (ValheimHelper-helper.zip, -helper-update.zip)  helper: map, guide, camera, statistics + base helper
#   dist\ValheimHelper-legal   (ValheimHelper-legal.zip, -legal-update.zip)    legal: map, guide, camera, statistics, achievement list
# The code of a higher edition is not compiled into a lower one (ValheimHelper.csproj, property Edition).
# ASCII-only on purpose: Windows PowerShell 5.1 misreads non-ASCII in scripts saved without BOM.
# -SkipTests: package without the sort/wiki tests (only while a wiki pack is being refreshed and its cache is ahead of it).
# -Editions full,helper,legal: which packages to make (full is always built first, it is the base of the others).
param([switch]$SkipTests, [string[]]$Editions = @('full', 'helper', 'legal'))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pack = Join-Path $root 'deps\BepInExPack\BepInExPack_Valheim'
$project = Join-Path $root 'ValheimHelper\ValheimHelper.csproj'

function Build-Edition([string]$edition) {
    $out = Join-Path $root ('ValheimHelper\bin\Release\' + $edition)
    # the build's output goes to the screen, not into what the function returns
    dotnet build $project -c Release -nologo "-p:Edition=$edition" -o $out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $edition" }
    return (Join-Path $out 'ValheimHelper.dll')
}

$fullDll = Build-Edition 'full'

# The boot DLL Doorstop loads first: it puts a downloaded update in place, then starts the mod (Boot\Entrypoint.cs)
$bootOut = Join-Path $root 'Boot\bin\Release\boot'
dotnet build (Join-Path $root 'Boot\Boot.csproj') -c Release -nologo -o $bootOut
if ($LASTEXITCODE -ne 0) { throw 'Build failed: boot' }
$bootDll = Join-Path $bootOut 'ValheimHelper.Boot.dll'

# Storage sorting rules are checked on scenarios before anything is packaged
if (-not $SkipTests) {
    dotnet run --project (Join-Path $root 'tools\SortTests\SortTests.csproj') -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Sort tests failed' }
}

$dist = Join-Path $root 'dist\ValheimHelper'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
$gameDir = Join-Path $dist 'game'
$modDir = Join-Path $gameDir 'ValheimHelper'
New-Item -ItemType Directory -Force $modDir | Out-Null

# Files that end up in the Valheim folder
Copy-Item (Join-Path $pack 'winhttp.dll') $gameDir
Copy-Item (Join-Path $root 'installer\doorstop_config.ini') $gameDir
Copy-Item $fullDll $modDir
Copy-Item $bootDll $modDir
# Menu languages other than Ukrainian and English: ValheimHelper\lang\<code>.json (tools\l10n\gen_langs.py)
$langSource = Join-Path $root 'ValheimHelper\lang'
if (Test-Path $langSource) {
    $langTarget = Join-Path $modDir 'lang'
    New-Item -ItemType Directory -Force $langTarget | Out-Null
    Copy-Item (Join-Path $langSource '*.json') $langTarget
}
foreach ($dep in '0Harmony.dll', 'MonoMod.RuntimeDetour.dll', 'MonoMod.Utils.dll', 'Mono.Cecil.dll') {
    Copy-Item (Join-Path $pack "BepInEx\core\$dep") $modDir
}

# The in-game wiki: every language pack (pages, redirects, meta) and the pictures they share; the HTML cache stays out
$wikiPack = Join-Path $root 'wiki-localizer\data\modpack'
if (Test-Path (Join-Path $wikiPack 'img\index.tsv')) {
    $wikiDir = Join-Path $modDir 'wiki'
    foreach ($langDir in Get-ChildItem $wikiPack -Directory) {
        if ($langDir.Name -eq 'img' -or -not (Test-Path (Join-Path $langDir.FullName 'pages.txt'))) { continue }
        $target = Join-Path $wikiDir $langDir.Name
        New-Item -ItemType Directory -Force $target | Out-Null
        foreach ($file in 'pages.txt', 'redirects.tsv', 'meta.txt', 'langlinks.tsv') {
            $source = Join-Path $langDir.FullName $file
            if (Test-Path $source) { Copy-Item $source $target }
        }
    }
    # only the pictures the index knows (a download still running may have files it has not listed yet)
    $imgTarget = Join-Path $wikiDir 'img'
    New-Item -ItemType Directory -Force $imgTarget | Out-Null
    $index = Join-Path $wikiPack 'img\index.tsv'
    Copy-Item $index $imgTarget
    $files = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($index, [System.Text.Encoding]::UTF8)) {
        $parts = $line.Split("`t")
        if ($parts.Length -ge 2) { $files[$parts[1]] = $true }
    }
    foreach ($file in $files.Keys) {
        $source = Join-Path $wikiPack ('img\' + $file)
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source $imgTarget }
    }
} else {
    Write-Host 'Wiki pack not built yet: dist goes without the wiki'
}

# Installer scripts: .ps1 get a UTF-8 BOM so PowerShell 5.1 reads Ukrainian text correctly
$utf8Bom = New-Object System.Text.UTF8Encoding $true
foreach ($name in 'install.ps1', 'uninstall.ps1') {
    $text = [System.IO.File]::ReadAllText((Join-Path $root "installer\$name"), [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText((Join-Path $dist $name), $text, $utf8Bom)
}
foreach ($name in 'install.bat', 'uninstall.bat', 'README.txt') {
    Copy-Item (Join-Path $root "installer\$name") $dist
}

function Zip-Folder([string]$folder, [string]$zip) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $folder -DestinationPath $zip
    Write-Host "OK: $zip"
}

# The update archive of an edition: what lies in game\ValheimHelper (DLLs, languages), at the root of the zip, without
# the guide's packs (the guide updates itself from the wiki). The mod's updater downloads it (Core\Updater.cs).
function Zip-Update([string]$editionDist, [string]$edition) {
    $zip = Join-Path $root ('dist\ValheimHelper-' + $edition + '-update.zip')
    if (Test-Path $zip) { Remove-Item $zip -Force }
    $items = Get-ChildItem (Join-Path $editionDist 'game\ValheimHelper') | Where-Object { $_.Name -ne 'wiki' }
    Compress-Archive -Path ($items | ForEach-Object { $_.FullName }) -DestinationPath $zip
    Write-Host "OK: $zip"
}

if ($Editions -contains 'full') {
    Zip-Folder $dist (Join-Path $root 'dist\ValheimHelper-full.zip')
    Zip-Update $dist 'full'
}
# The other editions: the same package with their own DLL
foreach ($edition in $Editions) {
    if ($edition -eq 'full') { continue }
    $dll = Build-Edition $edition
    $editionDist = Join-Path $root ('dist\ValheimHelper-' + $edition)
    if (Test-Path $editionDist) { Remove-Item $editionDist -Recurse -Force }
    Copy-Item $dist $editionDist -Recurse
    Copy-Item $dll (Join-Path $editionDist 'game\ValheimHelper\ValheimHelper.dll') -Force
    Zip-Folder $editionDist (Join-Path $root ('dist\ValheimHelper-' + $edition + '.zip'))
    Zip-Update $editionDist $edition
}
