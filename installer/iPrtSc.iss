; Inno Setup script for iPrtSc — per-machine installer (one UAC prompt at install)
; so it appears in Windows 11 Settings > Installed apps.
; The published app is self-contained: the .NET 8 runtime is bundled, so the
; target machine does NOT need .NET installed.
;
; Build:  iscc installer\iPrtSc.iss   (run from the repo root)
; Output: installer\Output\iPrtSc-Setup-<version>.exe

#define MyAppName "iPrtSc"
; Version is normally passed in by build-installer.ps1 (read from the .csproj),
; e.g. iscc /DMyAppVersion=0.2.0 ... — this is just the fallback for a bare run.
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "Ihor Kaliuzhnyi"
#define MyAppExeName "iPrtSc.exe"
; Path to the self-contained publish output, relative to this .iss file.
#define MyAppSource "..\publish\win-x64"

[Setup]
; A stable, unique GUID identifies the app across upgrades/uninstalls. Do not change it.
AppId={{8B4A7C2E-1F3D-4A6B-9C8E-2D5F7A0B1C3E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/1tsok/iPrtSc
AppSupportURL=https://github.com/1tsok/iPrtSc/issues
AppUpdatesURL=https://github.com/1tsok/iPrtSc/releases
VersionInfoVersion={#MyAppVersion}

; Self-contained .NET 8 supports Windows 10 1607+; refuse older systems upfront.
MinVersion=10.0.14393

; A running instance is closed by our own taskkill in [Code] (the app lives in
; the tray with no closable window, so Restart Manager's graceful close never
; works) — skip the confusing "files in use" wizard page entirely.
CloseApplications=no

; Per-machine install (writes the uninstall entry to HKLM) so it reliably shows
; in Windows 11 Settings > Installed apps. Costs a single UAC prompt at install.
; The app's per-user autostart (HKCU\...\Run) keeps working unchanged.
PrivilegesRequired=admin
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}

; Branding / icons.
SetupIconFile=..\src\iPrtSc\Assets\app.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern

; Output.
OutputDir=Output
OutputBaseFilename=iPrtSc-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes

; 64-bit only.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Wipe the previous version's files before installing so DLLs/assets that were
; dropped between releases don't accumulate in {app}. Guarded by a check that
; iPrtSc.exe is already there, so a custom folder with unrelated files is safe.
Type: filesandordirs; Name: "{app}\*"; Check: PreviousInstallExists

[Files]
; Whole self-contained publish folder, recursively.
Source: "{#MyAppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Offer to launch right after install. runasoriginaluser: setup is elevated,
; but the app must run as the logged-in user (per-user autostart, clipboard,
; drag&drop all break under an admin token).
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[CustomMessages]
en.DeleteSettings=Do you also want to delete your iPrtSc settings and screenshot history?
uk.DeleteSettings=Видалити також налаштування та історію знімків iPrtSc?

[Code]
const
  // Must match the single-instance mutex created in App.xaml.cs.
  AppMutexName = 'iPrtSc.SingleInstance';

var
  // Set in PrepareToInstall; drives the post-upgrade relaunch.
  AppWasRunning: Boolean;

// True when the chosen install folder already contains a previous iPrtSc —
// gates [InstallDelete] so we never wipe an unrelated directory.
function PreviousInstallExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\{#MyAppExeName}'));
end;

function AppIsRunning: Boolean;
begin
  Result := CheckForMutexes(AppMutexName);
end;

// Kill a running iPrtSc so its files aren't locked during install/uninstall.
// The app lives in the tray with no visible window, so a graceful WM_CLOSE
// wouldn't reach it — force-terminate, then wait for the single-instance
// mutex to vanish (process really gone) plus a grace period for file handles.
procedure KillRunningApp;
var
  ResultCode, I: Integer;
begin
  if not AppIsRunning then Exit;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for I := 1 to 10 do
  begin
    if not AppIsRunning then Break;
    Sleep(300);
  end;
  Sleep(300);
end;

// Runs after the user commits to installing, right before file operations.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  AppWasRunning := AppIsRunning;
  KillRunningApp;
  Result := '';
end;

// After an upgrade, restore the pre-install state: if the app was running
// when setup started, bring it back — as the logged-in user, not admin.
// ExecAsOriginalUser needs the un-elevated setup stub (normal UAC flow); when
// setup was launched already elevated it can fail silently, so fall back to
// a plain Exec rather than leave the app closed.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and AppWasRunning then
    if not ExecAsOriginalUser(ExpandConstant('{app}\{#MyAppExeName}'), '', '',
                              SW_SHOWNORMAL, ewNoWait, ResultCode) then
      Exec(ExpandConstant('{app}\{#MyAppExeName}'), '', '',
           SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir: String;
  I: Integer;
begin
  // Close the app only AFTER the user confirms the uninstall (an
  // InitializeUninstall kill would fire even if they answer "No").
  if CurUninstallStep = usUninstall then
    KillRunningApp;

  if CurUninstallStep = usPostUninstall then
  begin
    // Drop the per-user autostart value so the next sign-in doesn't try to
    // launch a deleted exe. Written by the app (AutoStart.cs), not setup,
    // so the uninstaller has to remove it explicitly.
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', '{#MyAppName}');

    // Sweep away {app} if it survived — after taskkill /F the freed file
    // handles can lag (NTFS delayed delete, antivirus scan), so the built-in
    // directory removal races and loses. Retry for a few seconds.
    AppDir := ExpandConstant('{app}');
    for I := 1 to 10 do
    begin
      if not DirExists(AppDir) then Break;
      DelTree(AppDir, True, True, True);
      if not DirExists(AppDir) then Break;
      Sleep(300);
    end;

    // Ask before removing %APPDATA%\iPrtSc. Defaults to "No" (keep settings);
    // silent uninstalls never prompt and keep the settings.
    if not UninstallSilent then
      if MsgBox(CustomMessage('DeleteSettings'), mbConfirmation,
                MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{userappdata}\iPrtSc'), True, True, True);
  end;
end;
