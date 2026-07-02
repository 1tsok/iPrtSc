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
VersionInfoVersion={#MyAppVersion}

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
; Offer to launch right after install.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
en.DeleteSettings=Do you also want to delete your iPrtSc settings and screenshot history?
uk.DeleteSettings=Видалити також налаштування та історію знімків iPrtSc?

[Code]
// True when the chosen install folder already contains a previous iPrtSc —
// gates [InstallDelete] so we never wipe an unrelated directory.
function PreviousInstallExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\{#MyAppExeName}'));
end;

// On uninstall, ask before removing %APPDATA%\iPrtSc. Defaults to "No"
// (keep settings), which is also the auto-answer for silent uninstalls.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if MsgBox(CustomMessage('DeleteSettings'), mbConfirmation,
              MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\iPrtSc'), True, True, True);
end;
