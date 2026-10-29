; Per-user installer for AgentReclaim (Inno Setup 6). Built by publish.ps1:
;   ISCC /DAppVersion=0.6.0 /DSourceDir=<folder with AgentReclaim.exe and README.md> /O<output> installer.iss
; Installs to %LOCALAPPDATA%\Programs\AgentReclaim without admin rights, so the app's own updater can
; replace its exe in place.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<publish folder>
#endif

[Setup]
AppId={{C651E385-0118-4F7B-B868-0CF9F11D502B}
AppName=AgentReclaim
AppVersion={#AppVersion}
AppVerName=AgentReclaim {#AppVersion}
AppPublisher=AgentReclaim
AppPublisherURL=https://github.com/baslawson/AgentReclaim-releases
AppSupportURL=https://github.com/baslawson/AgentReclaim-releases/issues
AppUpdatesURL=https://github.com/baslawson/AgentReclaim-releases/releases
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\AgentReclaim
DisableDirPage=auto
DefaultGroupName=AgentReclaim
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupIconFile=..\src\AgentReclaim.App\Assets\AgentReclaim.ico
UninstallDisplayIcon={app}\AgentReclaim.exe
UninstallDisplayName=AgentReclaim
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputBaseFilename=AgentReclaim-{#AppVersion}-win-x64-setup
; Close a running copy of the installed exe (and only that copy) before replacing it.
CloseApplications=force
RestartApplications=no
; Start-with-Windows can be changed in the app later, so never re-apply an old installer choice on upgrade.
UsePreviousTasks=no

[Tasks]
Name: "startup"; Description: "Start AgentReclaim with Windows (in the tray)"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\AgentReclaim.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\AgentReclaim"; Filename: "{app}\AgentReclaim.exe"
Name: "{autodesktop}\AgentReclaim"; Filename: "{app}\AgentReclaim.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AgentReclaim.exe"; Description: "Start AgentReclaim"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stops this copy and removes start-with-Windows and the Claude Code hook only if they point at it.
Filename: "{app}\AgentReclaim.exe"; Parameters: "uninstall-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "Cleanup"

[UninstallDelete]
; Left behind by the in-app updater.
Type: files; Name: "{app}\AgentReclaim.exe.old"

[Code]
// Start with Windows: written only when ticked in this run (never re-applied on upgrade), as the same
// value the app's own setting writes. Unticking never removes an existing entry: the app manages that.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('startup') then
    RegWriteStringValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AgentReclaim',
      '"' + ExpandConstant('{app}\AgentReclaim.exe') + '" --tray');
end;

// Firewall blocks made in the Firewall tab outlive the app unless removed. Removing needs admin, so ask first;
// silent uninstalls leave them (Windows Defender Firewall lists them under the group "AgentReclaim").
// `firewall has-rules` exits 0 if there are AgentReclaim rules, 5 if none, 6 if the firewall couldn't be read.
function FirewallRulesLeft(Exe: String): Integer;
var
  ResultCode: Integer;
begin
  if Exec(Exe, 'firewall has-rules', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode
  else
    Result := 6;
end;

procedure RemoveFirewallBlocks();
var
  Exe, Manual: String;
  ResultCode, Left: Integer;
begin
  Exe := ExpandConstant('{app}\AgentReclaim.exe');
  if not FileExists(Exe) then Exit;
  Left := FirewallRulesLeft(Exe);
  if Left = 5 then Exit;
  Manual := 'To remove them later, open PowerShell as administrator and run:' + #13#10 +
    '  Remove-NetFirewallRule -Group "AgentReclaim"' + #13#10 +
    'or delete the rules named "AgentReclaim: block ..." in Windows Defender Firewall.';
  if MsgBox('AgentReclaim may have added firewall rules that block ports. Remove them too?' + #13#10#13#10 +
       'Windows will ask for administrator permission. If you keep them, they stay in Windows Defender Firewall under the group "AgentReclaim".',
       mbConfirmation, MB_YESNO) <> IDYES then Exit;
  repeat
    if not ShellExec('runas', Exe, 'firewall remove-all', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Left := 6
    else
      Left := FirewallRulesLeft(Exe);
    if Left = 5 then Exit;
  until MsgBox('AgentReclaim''s firewall rules weren''t all removed (the administrator prompt was declined, or Windows refused).' + #13#10#13#10 +
       Manual, mbError, MB_RETRYCANCEL) <> IDRETRY;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if (CurUninstallStep = usUninstall) and not UninstallSilent() then RemoveFirewallBlocks();
  if CurUninstallStep <> usPostUninstall then Exit;
  // Silent uninstalls (with or without /SUPPRESSMSGBOXES) never ask and always keep the data.
  if UninstallSilent() then Exit;
  DataDir := ExpandConstant('{localappdata}\AgentReclaim');
  if not DirExists(DataDir) then Exit;
  if MsgBox('Also delete AgentReclaim''s settings, logs and activity history?' + #13#10#13#10 + DataDir,
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
