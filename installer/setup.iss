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
DisableProgramGroupPage=yes
WizardStyle=modern

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\installer\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\AetherRGB"; Filename: "{app}\AetherRGB.exe"
Name: "{autodesktop}\AetherRGB"; Filename: "{app}\AetherRGB.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung erstellen"; GroupDescription: "Zusätzliche Symbole:"

[Run]
Filename: "{app}\AetherRGB.exe"; Description: "AetherRGB jetzt starten"; Flags: nowait postinstall skipifsilent
