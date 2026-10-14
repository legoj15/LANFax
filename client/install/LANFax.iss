; Inno Setup 6 script. Build with: ISCC.exe client\install\LANFax.iss  (build.ps1 does this after publishing to dist\app).
; Output: client\dist\LANFax-Setup.exe. The installer is unsigned.

#define AppName "LANFax"
; Fallback only: build.ps1 passes /DAppVersion=<Version from Directory.Build.props>, and a test keeps this equal to it.
#ifndef AppVersion
  #define AppVersion "1.1.1"
#endif
#define AppExe "LANFax.exe"
; The published app folder. The test suite compiles this script with /DAppSource=<stub dir>, so no publish is needed there.
#ifndef AppSource
  #define AppSource "..\dist\app"
#endif

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
; Deliberate shared-machine decision: any local user may edit config.json, and the tray app of whoever is signed in
; writes the spool (jobs\, rejected\, sent\). The spooler no longer writes here: jobs arrive through \\.\pipe\LANFax.
; uninsneveruninstall: config.json, unsent documents and kept copies survive an uninstall (and a reinstall finds them).
Name: "{commonappdata}\LANFax"; Permissions: users-modify; Flags: uninsneveruninstall
Name: "{commonappdata}\LANFax\spool"; Permissions: users-modify; Flags: uninsneveruninstall
Name: "{commonappdata}\LANFax\spool\jobs"; Permissions: users-modify; Flags: uninsneveruninstall

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The publish output already carries install\*.ps1; listed again so the installer never ships without them.
Source: "*.ps1"; DestDir: "{app}\install"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LANFax"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue

[Run]
; Start the tray app as the logged-in user, not elevated. (The printer script runs from [Code] so its exit code is checked.)
Filename: "{app}\{#AppExe}"; Description: "Start LANFax"; Flags: nowait postinstall skipifsilent runasoriginaluser
; The in-app updater runs this installer silently: start the tray app again for the user who was running it.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install\Uninstall-LANFaxPrinter.ps1"""; RunOnceId: "RemoveLANFaxPrinter"; Flags: runhidden waituntilterminated

[Code]
procedure InstallPrinter;
var
  ResultCode: Integer;
  Params: String;
begin
  WizardForm.StatusLabel.Caption := 'Installing the LAN Fax printer...';
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\install\Install-LANFaxPrinter.ps1') +
    '" -SpoolDir "' + ExpandConstant('{commonappdata}\LANFax\spool') + '"';
  // Non-fatal: the app is installed either way and its tray menu offers "Install printer" again.
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    SuppressibleMsgBox('The LAN Fax printer could not be installed: PowerShell did not start (error ' + IntToStr(ResultCode) + ').' + #13#10 +
      'LANFax itself is installed; use "Install printer" in its tray menu to try again.', mbError, MB_OK, IDOK)
  else if ResultCode <> 0 then
    SuppressibleMsgBox('The LAN Fax printer could not be installed (the setup script exited with code ' + IntToStr(ResultCode) + ').' + #13#10 +
      'LANFax itself is installed; use "Install printer" in its tray menu to try again.', mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallPrinter;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  // Only once the user has confirmed the uninstall: stop every running tray instance (all sessions) so its files
  // can be removed. Failure (none running) is fine.
  if CurUninstallStep = usUninstall then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
