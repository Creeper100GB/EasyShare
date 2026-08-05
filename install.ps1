# EasyShare installer
# Usage:  irm https://raw.githubusercontent.com/Creeper100GB/EasyShare/master/install.ps1 | iex
$ErrorActionPreference = 'Stop'

$repo = 'Creeper100GB/EasyShare'
$installDir = Join-Path $env:LOCALAPPDATA 'EasyShare'
$exe = Join-Path $installDir 'EasyShare.exe'

Write-Host 'EasyShare – Installation' -ForegroundColor Cyan

if (Test-Path $exe) {
    Write-Host 'EasyShare ist bereits installiert. Überschreibe mit neuester Version ...'
}

# Neuestes Release auflösen (echte Versionierung, kein nightly)
$latest = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'EasyShare-installer' }
$asset = $latest.assets | Where-Object { $_.name -like '*win-x64.zip' } | Select-Object -First 1
if (-not $asset) { throw 'Kein win-x64-Asset im neuesten Release gefunden.' }

$zip = Join-Path $env:TEMP $asset.name
$extract = Join-Path $env:TEMP ('EasyShare-install-' + [guid]::NewGuid().ToString('N'))

Write-Host "Hole $($asset.name) (Version $($latest.tag_name)) ..."
Invoke-WebRequest $asset.browser_download_url -OutFile $zip
Expand-Archive $zip -DestinationPath $extract -Force

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item (Join-Path $extract '*') -Destination $installDir -Recurse -Force
Remove-Item $zip, $extract -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Installiert nach: $installDir" -ForegroundColor Green

# Desktop-Verknüpfung
try {
    $shell = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnk = $shell.CreateShortcut((Join-Path $desktop 'EasyShare.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $installDir
    $lnk.Save()
    Write-Host 'Desktop-Verknüpfung erstellt.' -ForegroundColor Green
} catch {
    Write-Host 'Desktop-Verknüpfung konnte nicht erstellt werden.' -ForegroundColor Yellow
}

Write-Host 'Starte EasyShare ...' -ForegroundColor Green
Start-Process $exe