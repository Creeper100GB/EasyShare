# EasyShare

Native Windows file sharing app with LocalSend protocol compatibility. Sendet und empfängt Dateien verschlüsselt (TLS) zwischen Geräten im selben Netzwerk – ohne Cloud, ohne Konto.

## Installation

### Empfohlen – ein Befehl in PowerShell

```powershell
irm https://raw.githubusercontent.com/Creeper100GB/EasyShare/master/install.ps1 | iex
```

Das lädt die neueste stabile Version, installiert sie nach `%LOCALAPPDATA%\EasyShare`, erstellt eine
Desktop-Verknüpfung und startet die App. Kein .NET-Runtime nötig (self-contained Build).

### Manuell

Lade das neueste ZIP von der [Releases-Seite](https://github.com/Creeper100GB/EasyShare/releases/latest),
entpacke es und starte `EasyShare.exe`.

### Nightly-Build

Bei jedem Push auf `master` baut [GitHub Actions](./.github/workflows/build-release.yml) automatisch einen
`nightly`-Build und veröffentlicht ihn als Prerelease. Für die neuesten (evtl. instabilen) Änderungen.

## Build aus dem Quellcode

```powershell
dotnet restore
dotnet publish src/EasyShare.App/EasyShare.App.csproj -c Release -r win-x64 --self-contained -o publish
```

## Features

- Drag & drop Dateiübertragung zwischen Windows-Geräten (LocalSend v2.1 Protokoll)
- TLS-verschlüsselte Übertragungen (Fingerprint-Pinning)
- Discovery im lokalen Netzwerk (multicast)
- QR-Code / Browser-Modus zum Empfangen auf jedem Gerät (zero-install)
- System-Tray
- Explorer-Kontextmenü-Integration
- Fluent / Mica UI (Windows 11 Stil)
- Dark / Light / Auto Theme
- Eingebauter Auto-Updater über GitHub Releases

## Tech Stack

- .NET 10 (WPF, net10.0-windows)
- WPF + Wpf.Ui (Fluent/Mica)
- Kestrel (ASP.NET Core) HTTP-Server mit TLS
- H.NotifyIcon.Wpf (Tray)
- QRCoder
- SQLite (Config + Verlauf)

## Projektstruktur

```
src/
  EasyShare.Core/        Protokollmodelle, Config, Crypto, Trust
  EasyShare.Transport/   HTTP-Server (LocalSend v2), Discovery, FileSender
  EasyShare.App/         WPF GUI (Wpf.Ui Fluent/Mica)
  EasyShare.Shell/       Explorer-Kontextmenü
```

## CI

[`.github/workflows/build-release.yml`](./.github/workflows/build-release.yml) publiziert bei jedem `v*`-Tag
ein Release und bei jedem Push auf `master` einen Nightly-Prerelease.

## License

Private use. All rights reserved.