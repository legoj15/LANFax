; Inno Setup 6 script. Build with: ISCC.exe client\install\LANFax.iss  (build.ps1 does this after publishing to dist\app).
; Output: client\dist\LANFax-Setup.exe. The installer is unsigned.

#define AppName "LANFax"
#define AppVersion "0.2.0"
#define AppExe "LANFax.exe"

[Setup]
AppId={{7D3B9C52-4E0A-4B8E-9F1D-2A6C5E8B4F13}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=LANFax
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\dist
OutputBaseFilename=LANFax-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes
RestartApplications=no

[Dirs]
Name: "{commonappdata}\LANFax"; Permissions: users-modify
Name: "{commonappdata}\LANFax\spool"; Permissions: users-modify
Name: "{commonappdata}\LANFax\spool\jobs"; Permissions: users-modify

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "*.ps1"; DestDir: "{app}\install"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LANFax"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install\Install-LANFaxPrinter.ps1"" -SpoolDir ""{commonappdata}\LANFax\spool"""; StatusMsg: "Installing the LAN Fax printer..."; Flags: runhidden waituntilterminated
; Start the tray app as the logged-in user, not elevated.
Filename: "{app}\{#AppExe}"; Description: "Start LANFax"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install\Uninstall-LANFaxPrinter.ps1"""; RunOnceId: "RemoveLANFaxPrinter"; Flags: runhidden waituntilterminated
