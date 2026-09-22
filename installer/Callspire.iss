; Windows installer for self-contained Callspire publish output.
; CI passes version: ISCC /DMyAppVersion=1.1.46 installer\Callspire.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{A7B4E2C1-9F3D-4E8A-B2C1-CallspireSoftphone}
AppName=Callspire
AppVersion={#MyAppVersion}
AppVerName=Callspire {#MyAppVersion}
DefaultDirName={autopf}\Callspire
DefaultGroupName=Callspire
DisableProgramGroupPage=yes
OutputDir=..
OutputBaseFilename=Callspire-{#MyAppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Callspire.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Callspire"; Filename: "{app}\Callspire.exe"
Name: "{autodesktop}\Callspire"; Filename: "{app}\Callspire.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Callspire.exe"; Description: "{cm:LaunchProgram,Callspire}"; Flags: nowait postinstall skipifsilent
