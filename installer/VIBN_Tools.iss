[Setup]
AppId={{6798D3CD-9220-4F7F-A95A-17BBCE971E32}
AppName=VIBN Tools
AppVersion=1.0.0
DefaultDirName={autopf}\VIBN Tools
DefaultGroupName=VIBN Tools
OutputDir=..\artifacts\installer
OutputBaseFilename=VIBN_Tools_Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayIcon={app}\VIBN_Tools.exe

[Files]
Source: "..\artifacts\publish\VIBN_Tools-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\VIBN Tools"; Filename: "{app}\VIBN_Tools.exe"
Name: "{autodesktop}\VIBN Tools"; Filename: "{app}\VIBN_Tools.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Desktopverknüpfung erstellen"; GroupDescription: "Zusätzliche Symbole:"

[Run]
Filename: "{app}\VIBN_Tools.exe"; Description: "VIBN Tools starten"; Flags: nowait postinstall skipifsilent
