; Inno Setup script for A'Tipik. Builds a no-admin, per-user installer that lays
; down the app folder (small exe + runtime + brand assets), a Start Menu shortcut
; and an uninstaller. Pass the version with:  iscc /DMyAppVersion=0.1.0 atipik.iss
; Source folder (publish-installer\) is produced by the release workflow.

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppName "A'Tipik"
#define MyAppExeName "Atypik.exe"
#define MyAppPublisher "Hope 'n Mind SASU"
#define MyAppURL "https://hopenmind.com"

[Setup]
AppId={{6F3B1E2A-9C4D-4A87-B15E-2D7A9F0C3E64}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
; Per-user install: no administrator rights required.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Atypik
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
SourceDir={#SourcePath}\..
OutputDir=dist
OutputBaseFilename=Atypik-Setup-win-x64
SetupIconFile=src\Resources\atypik.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish-installer\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
