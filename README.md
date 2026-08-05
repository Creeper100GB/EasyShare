# EasyShare

Native Windows file sharing app with LocalSend protocol compatibility.

## Features

- Drag & drop file sharing between Windows devices
- LocalSend v2.1 protocol (compatible with LocalSend on Mac/Linux/Android/iOS)
- Explorer right-click context menu integration
- System tray with quick-share
- QR code / browser mode for zero-install receiving (any device)
- BLE device discovery + pairing
- TLS encrypted transfers
- Transfer history with resume support
- Fluent / Mica UI (Windows 11 style)
- Dark / Light / Auto theme
- i18n: German + English

## Transports

| Transport | Status |
|---|---|
| Wi-Fi (local) | Core |
| Ethernet / Thunderbolt Bridge | Automatic (any IP interface) |
| BLE Discovery + Pairing | Phase 3 |
| Browser / QR Mode | Phase 3 |

## Tech Stack

- .NET 8 (LTS)
- WPF + Wpf.Ui (Fluent/Mica)
- Kestrel (ASP.NET Core) HTTP server
- Makaretu.Dns.Multicast (mDNS)
- Windows.Devices.Bluetooth.Advertisement (BLE, WinRT native)
- H.NotifyIcon.Wpf (tray)
- Microsoft.Data.Sqlite (config + history)

## Build

```powershell
dotnet restore
dotnet build src/EasyShare.App
```

## Run

```powershell
dotnet run --project src/EasyShare.App
```

## Project Structure

```
src/
  EasyShare.Core/        Protocol models, config, crypto
  EasyShare.Transport/   HTTP server, mDNS, BLE, sessions
  EasyShare.App/         WPF GUI (Wpf.Ui Fluent/Mica)
  EasyShare.Shell/       Explorer context menu
  EasyShare.Cli/         CLI entry for context menu
tests/
  EasyShare.Core.Tests/
```

## License

Private use. All rights reserved.
