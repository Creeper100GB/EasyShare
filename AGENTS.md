# EasyShare - Projektanweisungen

Native Windows-Dateiübertragung (LocalSend-kompatibel), .NET, WPF (Wpf.Ui), Kestrel-Server,
Multicast-Discovery. Branch `master`, Remote `Creeper100GB/EasyShare`, Veröffentlichung als
selbst-contained `win-x64`-Zip plus `.sha256` via GitHub-Actions-Tag-Release.

## Build, Test, Lint

Alle Targets über `just` (Auflistung: `just` / `just help`):

| Aufgabe | Befehl |
|---------|--------|
| Bauen | `just build` (`dotnet build src/EasyShare.App/EasyShare.App.csproj -c Release`) |
| Unit-Tests | `just test` (xunit, `tests/EasyShare.Core.Tests`) |
| Integrations-Tests | `just integration` (`dotnet run --project tests/EasyShare.IntegrationTests`) |
| Lint/Format | `just lint` (`dotnet format EasyShare.slnx --verify-no-changes`) |
| Alles | `just check` = build + test + integration + lint |
| Publish | `just publish` (selbst-contained win-x64 nach `publish/`) |
| Install | `just install` (`install.ps1`) |

Vor jedem Commit: `just check` grün. Vor dem Taggen zusätzlich die End-to-End-Integrations-Tests (laufen in CI).

## Release-Prozess

1. Version in `src/EasyShare.App/EasyShare.App.csproj` (`<Version>`) anheben, commit "Bump version to X.Y.Z".
2. `git push origin master` (triggert Nightly + `dotnet test` in CI).
3. `git tag vX.Y.Z` und Push. Der Build laggt Release `EasyShare-X.Y.Z-win-x64.zip` + `.sha256` automatisch.
4. CI-Run per `gh run watch <id> --repo Creeper100GB/EasyShare --exit-status` pollen, bis success.
5. Release-Assets prüfen (`gh release view vX.Y.Z`) und SHA256-End-to-End verifizieren (der In-App-Updater
   lädt `<zip>.sha256` und validiert die Summe).

## Architektur

- `src/EasyShare.Core` – Modelle, Discovery (Multicast), Sessions, Config, UpdateService (ohne UI/Win32).
- `src/EasyShare.Transport` – `LocalSendServer` (Kestrel, HTTPS, TLS-Fingerprint), `FileSender` (HttpClient, direkte LAN-IP).
- `src/EasyShare.App` – WPF-GUI (Wpf.Ui), Lokalisierung (`Resources/Lang/de.json` + `en.json`), Theme-Switching (Light/Dark/Auto).
- `src/EasyShare.Shell` – Shell-Integration (Explorer-Kontextmenü).
- `src/EasyShare.Cli` – CLI (share/install/uninstall).

Wichtige Fix-Regeln (wiederkehrende Fehler): Tunnel/VPN-Adapter (tun/tap/vpn/tailscale/...) dürfen für
Discovery/Transfer nicht genutzt werden – Übertragungen laufen direkt über lokale Subnet-IPs
(announced `Ip` wird nur vertraut, wenn sie auf einem lokalen Subnetz liegt). `ProgressStream` namens-
`CanSeek` am `FileStream`, damit `Content-Length` statt Chunked-Encoding gesendet wird.