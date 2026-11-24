# AetherRGB

Lokale, Cloud-freie RGB-Sync-Software für Windows — ähnlich wie SignalRGB oder OpenRGB, aber mit
offener API, Home-Assistant-Anbindung und WLED-Unterstützung, komplett ohne Account, Lizenzserver
oder Telemetrie.

## Kernprinzipien

- **Keine Cloud.** Keine Server-Kommunikation nach außen — alles läuft lokal auf dem PC. Die
  einzige externe Verbindung ist die optionale, manuell ausgelöste Update-Prüfung gegen die
  öffentliche GitHub-Releases-API (kein eigener Update-Server, kein Tracking).
- **Offene lokale REST-API** (`http://127.0.0.1:<Port>/api/...`), bindet ausschließlich an
  `127.0.0.1` — für eigene Skripte, Spiele-Mods oder Dashboards.
- **Home Assistant**: eigene Lichter aus HA als Sync-Quelle einbinden, und/oder HA-Lichter direkt
  wie ein RGB-Gerät mitsteuern — alles über die lokale HA-REST-API mit einem selbst erzeugten
  Long-Lived-Access-Token, kein Nabu-Casa-Cloud-Umweg.
- **WLED**: direkte Steuerung über die offizielle [WLED-JSON-API](https://kno.wled.ge/interfaces/json-api/).
- **Motherboard-ARGB** (ASUS Aura Sync, ASRock Polychrome, Gigabyte RGB Fusion, MSI Mystic Light):
  über das [OpenRGB](https://openrgb.org/)-SDK-Protokoll (TCP, Port 6742). AetherRGB implementiert
  bewusst **keine eigenen** SMBus/I2C-Low-Level-Treiber — OpenRGB deckt diese Boards bereits
  geprüft ab, ein Nachbau ohne echte Testhardware birgt ein unnötiges Risiko für die Hardware.
  Voraussetzung: OpenRGB ist installiert und läuft (mit aktiviertem SDK-Server).
- **MSI Mystic Light**: bei erkanntem MSI-Mainboard fragt AetherRGB beim Start, welcher Weg genutzt
  werden soll (jederzeit änderbar unter Einstellungen → MSI Mystic Light):
  - **Offizielles MSI Mystic Light SDK**: AetherRGB meldet sich als Drittanbieter-Client beim
    MSI-Center-Dienst (LEDKeeper2) an, also kein Konflikt mit MSI Center. Voraussetzungen:
    MSI Center mit Mystic-Light-Modul läuft, Drittanbieter-Steuerung ist in Mystic Light erlaubt,
    AetherRGB läuft als Administrator. Die `MysticLight_SDK_x64.dll` wird wegen der MSI-Lizenz
    **nicht mitgeliefert**: Lade das SDK bei MSI herunter und lege die DLL in den Programmordner,
    nach `%APPDATA%\AetherRGB` oder gib den Pfad in den Einstellungen an. Das SDK steuert ganze
    LED-Bereiche, keine einzelnen Pixel, und ist bei schnellen Effekten in der Bildrate begrenzt.
  - **Eigene Variante (direkt per USB/HID)**: ohne MSI Center und ohne DLL, experimentell
    (vorher MSI Center beenden und den sicheren Einzel-LED-Test machen).

## Architektur

```
AetherRGB.Core                 Geräte-Abstraktion (IDeviceProvider), Effekt-Engine, Konfiguration
AetherRGB.Providers.OpenRGB     Motherboard-ARGB via OpenRGB-SDK-Protokoll
AetherRGB.Providers.Wled        WLED-Geräte via JSON-API
AetherRGB.Providers.HomeAssistant  Home-Assistant-Lichter als Ziel UND als Quelle
AetherRGB.Providers.MsiMysticLight MSI-RGB via offiziellem Mystic Light SDK oder direkt per USB/HID
AetherRGB.Api                   Lokale REST-API (nur 127.0.0.1), ASP.NET Core Minimal API
AetherRGB.App                   WPF-Hauptanwendung
```

## Build

Build läuft ausschließlich über GitHub Actions (Windows-Runner, `.github/workflows/build-release.yml`):
- **Portable EXE** (`AetherRGB-Portable-x64.exe`) — self-contained Single-File, kein Installer nötig.
- **Installer** (`AetherRGB-Setup-x64.exe`, via Inno Setup) — Programme-und-Features-Eintrag,
  Start-Menü-/Desktop-Verknüpfung, Deinstallation.
- Signierung über ein selbst erzeugtes Codesigning-Zertifikat (GitHub-Secrets
  `AETHERRGB_PFX_BASE64`/`AETHERRGB_PFX_PASSWORD`) — ohne kommerzielles EV-Zertifikat wird Windows
  SmartScreen trotzdem zunächst warnen, bis genug Reputation für diese Signatur aufgebaut ist.
- "Reparieren" und "Nach Updates suchen" sind App-interne Funktionen (kein natives
  MSI-Repair-Feature), siehe `AetherRGB.Core/Update/`.

## Lizenz

MIT, siehe [LICENSE](LICENSE).
