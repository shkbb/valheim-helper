param([string]$GameDir)
$ErrorActionPreference = 'Stop'

function Find-Valheim {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        $libraries += [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($library in $libraries) {
        $candidate = Join-Path $library 'steamapps\common\Valheim'
        if (Test-Path (Join-Path $candidate 'valheim.exe')) { return $candidate }
    }
    return $null
}

try {
    if (-not $GameDir) { $GameDir = Find-Valheim }
    if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'valheim.exe'))) {
        $GameDir = Read-Host 'Не знайшов Valheim. Встав шлях до папки гри (де лежить valheim.exe)'
    }
    if (-not (Test-Path (Join-Path $GameDir 'valheim.exe'))) { throw "У папці '$GameDir' немає valheim.exe" }
    if (Get-Process valheim -ErrorAction SilentlyContinue) { throw 'Спершу закрий Valheim.' }

    if (Test-Path (Join-Path $GameDir 'BepInEx')) {
        Write-Warning 'У грі встановлено BepInEx. Valheim Helper замінить його winhttp.dll і doorstop_config.ini — моди BepInEx перестануть завантажуватись (а сам BepInEx вмикає позначку isModded, яка блокує досягнення).'
        if ((Read-Host 'Продовжити? (y/n)') -ne 'y') { exit 1 }
    }

    Copy-Item (Join-Path $PSScriptRoot 'game\*') $GameDir -Recurse -Force
    Write-Host ''
    Write-Host "Встановлено в: $GameDir" -ForegroundColor Green
    Write-Host 'Запускай гру як завжди через Steam. Меню: F7 або Insert, закрити також Esc.'
}
catch {
    Write-Host $_ -ForegroundColor Red
    exit 1
}
