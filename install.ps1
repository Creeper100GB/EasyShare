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

# Neuestes Release auflösen (ohne GitHub API → kein Rate-Limit)
$redirect = Invoke-WebRequest "https://github.com/$repo/releases/latest" -MaximumRedirection 0 -ErrorAction Stop
$tag = $redirect.Headers.Location -replace '.*/tag/', ''
$version = $tag.TrimStart('v')
$zipName = "EasyShare-$version-win-x64.zip"
$downloadUrl = "https://github.com/$repo/releases/download/$tag/$zipName"

Write-Host "Hole $zipName (Version $tag) ..."
$zip = Join-Path $env:TEMP $zipName
Invoke-WebRequest $downloadUrl -OutFile $zip
$extract = Join-Path $env:TEMP ('EasyShare-install-' + [guid]::NewGuid().ToString('N'))
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