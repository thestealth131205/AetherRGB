<img src="src/AetherRGB.App/Assets/AetherRGB-512.png" alt="AetherRGB-Logo" width="128" align="right" />

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
  Long-Lived-Access-Token, kein Nabu-Casa-Cloud-Umweg. HA-Lichter bekommen höchstens alle 3 s
  (einstellbar, nicht weniger) einen Befehl mit dem Mittelwert der Effektfarben, optional mit weichem
  Übergang (`transition`).
- **WLED**: Effekte per [UDP-Echtzeit](https://kno.wled.ge/interfaces/udp-realtime/) (DRGB, ab 491 LEDs
  DNRGB), Geräteinfos über die [WLED-JSON-API](https://kno.wled.ge/interfaces/json-api/). In WLED muss
  „Receive UDP realtime“ aktiv sein (Standard); alternativ in den Einstellungen auf JSON-API umstellen.
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
    nach `%APPDATA%\AetherRGB` oder gib den Pfad in den Einstellungen an. ARGB-Anschlüsse
    (PCH, JRAINBOW bis 100 LEDs) werden über „Direct Lighting Control“ LED für LED gesteuert,
    12-V-RGB (JRGB) mit einer Farbe. Die Bildrate ist begrenzt (Standard 10 Updates/s, einstellbar bis 60, bei Problemen automatisch niedriger),
    weil zu schnelle Aufrufe den Board-Controller zum Absturz bringen. Ein Aufruf dauert je Anschluss ~30 ms;
    die Farben werden erst unmittelbar vor jedem Aufruf berechnet, damit die Bewegung trotz niedriger Rate
    gleichmäßig bleibt. Belegt die LED-Anordnung nur einen Teil eines JRAINBOW-Headers, werden nur diese LEDs gesendet.
  - **Eigene Variante (direkt per USB/HID)**: ohne MSI Center und ohne DLL, experimentell
    (vorher MSI Center beenden und den sicheren Einzel-LED-Test machen).

- **Eigene Effekte** (im Aufbau): `.aeffect`-Dateien in `%APPDATA%\AetherRGB\effects` erscheinen
  automatisch in der Effekt-Auswahl. Ein Effekt besteht aus Ebenen (gemalt, auch als Daumenkino, oder
  Formen wie Kreis/Ring/Rechteck), deren Position, Größe, Drehung, Deckkraft und Farbton über Keyframes
  animiert werden.
- **Effekt-Editor** (Knopf „🎨 Effekt-Editor“ auf der Effekte-Seite, eigenes Fenster): auf das Raster
  des Effekt-Fensters (160 × 80 Zellen) malen — Pinsel (Größe 1–50 Zellen, Härte, Deckkraft, rund/eckig,
  Spiegeln), Radierer, Füllen, Linie, Rechteck, Ellipse, Pipette, Rückgängig/Wiederholen. Die Geräte des
  gewählten Effekt-Fensters liegen zur Orientierung darüber, „Live auf LEDs“ zeigt das Gemalte sofort an
  den echten LEDs. Speichern, Kopie, Löschen, Import/Export als `.aeffect`. Durchsichtig = LED aus.
  Tastatur: B/E/G/L/R/O/I = Werkzeuge, +/− = Pinselgröße, Strg+Z/Y, Strg+S, Alt+Klick = Pipette,
  Rechtsklick = radieren, Shift = gerade Linie/Quadrat/Kreis, Esc = Strich abbrechen.
  **Frames:** „➕ Nächster Frame“ (N, mit Shift als Kopie) legt hinter dem aktuellen Frame einen neuen an.
  Die Zeitleiste unter der Fläche zeigt alle Frames als Miniaturen — Klick lädt einen Frame in die Fläche,
  ✕ löscht ihn. Dazu Duplizieren (Strg+D), Verschieben, Dauer pro Frame (ms), weiches Überblenden,
  Zwiebelhaut (voriger Frame scheint durch), Abspielen im Editor und ←/→ zum Blättern.
  Rückgängig gilt pro Frame.
  Animations-Vorlagen (Wandern, ausdehnender Kreis …) folgen in der nächsten Ausbaustufe.
- **Synchrone Effekt-Fenster**: Im Dropdown „⇄ Synchron“ über den Tabs lassen sich Effekt-Fenster
  anhaken, die im Gleichschritt laufen sollen — sie übernehmen beim Anhaken, Fortsetzen und App-Start
  die Effektzeit der anderen markierten Fenster („Markierte Effekte gemeinsam neu starten“ setzt alle
  gleichzeitig auf Anfang). Mit gleichem Tempo bleiben sie Bild für Bild gleich.
- **Autostart**: AetherRGB startet beim Anmelden minimiert im Infobereich (abschaltbar unter
  Einstellungen). Normal über `HKCU\...\Run`; mit dem Mystic Light SDK (braucht Adminrechte) über eine
  Aufgabe „AetherRGB“ in der Aufgabenplanung mit höchsten Privilegien, damit keine UAC-Abfrage kommt.
  Es läuft immer nur eine Instanz; ein zweiter Start holt das vorhandene Fenster nach vorn.

## Architektur

```
AetherRGB.Core                 Geräte-Abstraktion (IDeviceProvider), Effekt-Engine, Konfiguration
AetherRGB.Providers.OpenRGB     Motherboard-ARGB via OpenRGB-SDK-Protokoll
AetherRGB.Providers.Wled        WLED-Geräte via UDP-Echtzeit + JSON-API
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
- "Nach Updates suchen" fragt das neueste GitHub-Release ab und lädt bei Bedarf die passende Datei
  (geprüft per Größe und SHA-256 aus der Releases-API): Bei installierter App den Installer
  (startet danach, AetherRGB beendet sich), bei der portablen EXE die neue portable EXE, die die
  alte ersetzt und neu startet.

## Lizenz

MIT, siehe [LICENSE](LICENSE).
