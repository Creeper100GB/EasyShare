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

# Laufende Instanz beenden, sonst sind die DLLs gesperrt und Copy-Item schlägt fehl.
$wasRunning = $false
Get-Process EasyShare -ErrorAction SilentlyContinue | ForEach-Object {
    $wasRunning = $true
    Write-Host 'Beende laufende EasyShare-Instanz ...'
    Stop-Process -Id $_.Id -Force
}
if ($wasRunning) { Start-Sleep -Milliseconds 500 }

# Neuestes Release auflösen (ohne GitHub API → kein Rate-Limit)
# Hinweis: -MaximumRedirection 0 wirft in PowerShell 5.1 eine Exception,
# daher HttpWebRequest mit deaktiviertem AutoRedirect verwenden.
$req = [System.Net.HttpWebRequest]::Create("https://github.com/$repo/releases/latest")
$req.AllowAutoRedirect = $false
$req.UserAgent = 'EasyShare-Installer'
$resp = $req.GetResponse()
$tag = [System.Uri]::new($resp.Headers['Location']).Segments[-1].TrimEnd('/')
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

# Firewall-Regeln (benötigt Admin)
function Add-FirewallRules {
    param([string]$ExePath)
    $ruleName = 'EasyShare'
    $existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host 'Firewall-Regeln bereits vorhanden.' -ForegroundColor Green
        return
    }
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Program $ExePath -Protocol TCP -LocalPort 53317 -Profile Private,Domain -ErrorAction SilentlyContinue | Out-Null
    New-NetFirewallRule -DisplayName "$ruleName-UDP" -Direction Inbound -Action Allow -Program $ExePath -Protocol UDP -LocalPort 53317 -Profile Private,Domain -ErrorAction SilentlyContinue | Out-Null
    Write-Host 'Firewall-Regeln erstellt (TCP+UDP Port 53317, Privates/Domänen-Profil).' -ForegroundColor Green
}

if ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Add-FirewallRules -ExePath $exe
} else {
    try {
        $proc = Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -Command `"Add-FirewallRules -ExePath '$exe'`"" -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        if ($proc.ExitCode -ne 0) {
            Write-Host 'Firewall-Regeln konnten nicht erstellt werden (Admin-Rechte nötig).' -ForegroundColor Yellow
        }
    } catch {
        Write-Host 'Firewall-Regeln: UAC-Prompt abgelehnt oder fehlgeschlagen. Port 53317 muss manuell freigegeben werden.' -ForegroundColor Yellow
    }
}

Write-Host 'Starte EasyShare ...' -ForegroundColor Green
Start-Process $exe
