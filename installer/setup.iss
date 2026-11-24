; Inno Setup Script für AetherRGB.
; Erwartet, dass die CI den self-contained Publish-Output vorher nach ..\publish\installer gelegt hat.
; AppVersion wird von der CI per /DMyAppVersion=x.y.z übergeben (ISCC-Kommandozeile).

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{B7E4C9A1-5F3D-4E8A-9B2C-AE7A4C1D3F90}}
AppName=AetherRGB
AppVersion={#MyAppVersion}
AppPublisher=thestealth131205
AppPublisherURL=https://github.com/thestealth131205/AetherRGB
DefaultDirName={autopf}\AetherRGB
DefaultGroupName=AetherRGB
OutputDir=..\dist
OutputBaseFilename=AetherRGB-Setup-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\AetherRGB.exe
SetupIconFile=..\src\AetherRGB.App\Assets\AetherRGB.ico
DisableProgramGroupPage=yes
WizardStyle=modern
; Bild links auf der Willkommensseite (Effekt-Editor); Inno wählt die zur DPI passende Größe.
; Die Abschlussseite bekommt in [Code] ein eigenes Bild. Erzeugt von tools/make_wizard_images.py.
WizardImageFile=wizard-editor-164.bmp,wizard-editor-246.bmp,wizard-editor-328.bmp

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\installer\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "wizard-finish-*.bmp"; Flags: dontcopy

[Icons]
Name: "{group}\AetherRGB"; Filename: "{app}\AetherRGB.exe"
Name: "{autodesktop}\AetherRGB"; Filename: "{app}\AetherRGB.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung erstellen"; GroupDescription: "Zusätzliche Symbole:"

[Run]
Filename: "{app}\AetherRGB.exe"; Description: "AetherRGB jetzt starten"; Flags: nowait postinstall skipifsilent

; Autostart wird von der App selbst angelegt (Einstellungen → „Mit Windows starten“) — beim Deinstallieren
; beides wieder entfernen: den Run-Eintrag und die Aufgabe mit Adminrechten (nur beim Mystic Light SDK).
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "AetherRGB"; Flags: uninsdeletevalue dontcreatekey

[UninstallRun]
; Chroma-Bridge (RzChromaSDK64.dll in System32) entfernen — die App löscht nur ihre eigene DLL, nie die von Razer.
Filename: "{app}\AetherRGB.exe"; Parameters: "--chroma-bridge uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveChromaBridge"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""AetherRGB"" /F"; Flags: runhidden; RunOnceId: "DeleteAutostartTask"

[Code]
// Abschlussseite: eigenes Bild (Effekte & Layout) statt des Willkommensbilds — kleinste Größe,
// die die Bildfläche bei der aktuellen DPI noch ausfüllt.
procedure InitializeWizard;
var
  FileName: String;
begin
  if WizardForm.WizardBitmapImage2.Width <= 164 then
    FileName := 'wizard-finish-164.bmp'
  else if WizardForm.WizardBitmapImage2.Width <= 246 then
    FileName := 'wizard-finish-246.bmp'
  else
    FileName := 'wizard-finish-328.bmp';
  ExtractTemporaryFile(FileName);
  WizardForm.WizardBitmapImage2.Bitmap.LoadFromFile(ExpandConstant('{tmp}\') + FileName);
end;
