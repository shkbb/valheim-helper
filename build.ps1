# Builds ValheimHelper.dll and assembles dist\ValheimHelper (+ zip for the friend).
# ASCII-only on purpose: Windows PowerShell 5.1 misreads non-ASCII in scripts saved without BOM.
# -SkipTests: package without the sort/wiki tests (only while a wiki pack is being refreshed and its cache is ahead of it).
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pack = Join-Path $root 'deps\BepInExPack\BepInExPack_Valheim'

dotnet build (Join-Path $root 'ValheimHelper\ValheimHelper.csproj') -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

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
Copy-Item (Join-Path $root 'ValheimHelper\bin\Release\netstandard2.1\ValheimHelper.dll') $modDir
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

$zip = Join-Path $root 'dist\ValheimHelper.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $dist -DestinationPath $zip
Write-Host "OK: $dist"
Write-Host "OK: $zip"
