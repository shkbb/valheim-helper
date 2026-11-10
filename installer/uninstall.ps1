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
        $GameDir = Read-Host 'Встав шлях до папки гри (де лежить valheim.exe)'
    }
    if (Get-Process valheim -ErrorAction SilentlyContinue) { throw 'Спершу закрий Valheim.' }

    $config = Join-Path $GameDir 'doorstop_config.ini'
    # Only remove the loader if it is ours, never someone else's BepInEx setup
    if ((Test-Path $config) -and ((Get-Content $config -Raw) -match 'ValheimHelper\\ValheimHelper\.dll')) {
        Remove-Item $config -Force
        Remove-Item (Join-Path $GameDir 'winhttp.dll') -Force -ErrorAction SilentlyContinue
    }
    $modDir = Join-Path $GameDir 'ValheimHelper'
    if (Test-Path $modDir) { Remove-Item $modDir -Recurse -Force }
    Write-Host "Valheim Helper видалено з: $GameDir" -ForegroundColor Green
}
catch {
    Write-Host $_ -ForegroundColor Red
    exit 1
}
