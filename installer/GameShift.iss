; GameShift installer (Inno Setup 6.3 or later).
;
; Build from the repository root:
;   dotnet publish src/GameShift.App/GameShift.App.csproj -c Release -r win-x64 -p:IncludeNativeLibrariesForSelfExtract=false -o artifacts/installer-publish
;   iscc installer\GameShift.iss
; Output: artifacts\installer\GameShift-Setup.exe. Pass /DPublishDir=<folder> to package another publish folder.
;
; The installed build must not self-extract. With IncludeNativeLibrariesForSelfExtract=false its native
; libraries sit next to the exe in Program Files instead of being unpacked into the user's %TEMP%, which
; the user can write to and the elevated app would load them from.

#define AppName "GameShift"
#define AppExeName "GameShift.App.exe"
; Never change: it identifies the installation for upgrades and uninstall. GameShift reads InstallLocation
; from Uninstall\{AppGuid}_is1 to know it runs as the installed copy (InstallationDetector.InstallerAppId).
#define AppGuid "E56444A0-53F3-4443-86A2-D7B59168BC56"
; Single-instance mutex GameShift holds while it runs (App.OnStartup).
#define AppMutexName "Global\GameShift_SingleInstance"
#define RepoUrl "https://github.com/lhceist41/GameShift"

#ifndef PublishDir
  #define PublishDir AddBackslash(SourcePath) + "..\artifacts\installer-publish"
#endif
#define PublishFolder AddBackslash(PublishDir)

; The version comes from <Version> in the app's csproj. A plain #define inside #sub is local to the
; sub, so the version is defined public to reach the rest of the script.
#define Csproj AddBackslash(SourcePath) + "..\src\GameShift.App\GameShift.App.csproj"
#define CsprojHandle FileOpen(Csproj)
#if !CsprojHandle
  #error Cannot open src\GameShift.App\GameShift.App.csproj to read the version
#endif
#define AppVersion ""
#sub ReadVersionLine
  #define Line = FileRead(CsprojHandle)
  #if Pos("<Version>", Line) > 0
    #define public AppVersion = Copy(Line, Pos("<Version>", Line) + Len("<Version>"), Pos("</Version>", Line) - Pos("<Version>", Line) - Len("<Version>"))
  #endif
#endsub
#for {0; AppVersion == "" && !FileEof(CsprojHandle); 0} ReadVersionLine
#expr FileClose(CsprojHandle)
#if AppVersion == ""
  #error No <Version> element found in src\GameShift.App\GameShift.App.csproj
#endif

; Refuse to package a missing or stale publish: the exe's file version must match the csproj version.
#define ExeVersion GetVersionNumbersString(PublishFolder + AppExeName)
#if ExeVersion == ""
  #error GameShift.App.exe not found in the publish folder; publish the installer build first (see the top of this file)
#endif
#if Pos(AppVersion + ".", ExeVersion + ".") != 1
  #error The published GameShift.App.exe version does not match <Version> in the csproj; publish again
#endif

[Setup]
AppId={{{#AppGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
; GameShift runs elevated, so it must live in a folder only administrators can change. The directory
; page is hidden to keep Program Files; an upgrade reuses the existing install folder.
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
; GameShift does not register with Restart Manager; the [Run] section relaunches it instead.
RestartApplications=no
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\src\GameShift.App\Assets\Icons\app-icon.ico
OutputDir=..\artifacts\installer
OutputBaseFilename=GameShift-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Messages]
ConfirmUninstall=Before you remove %1, undo the lasting changes it made: open %1, uncheck Easy Mode in Settings, click "Restore All GameShift Tweaks", and revert any fixes you applied in DPC Doctor.%n%nAre you sure you want to completely remove %1 and all of its components?

[Files]
Source: "{#PublishFolder}{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Native libraries (WPF, Mono helpers, TraceEvent per architecture). Compilation fails if there are none,
; which means the publish folder holds a self-extracting build.
Source: "{#PublishFolder}*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"

[Run]
; GameShift needs administrator rights, so both entries start it with Setup's credentials.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser
; The in-app updater runs this installer silently with /RELAUNCH=1 and exits; start GameShift again
; afterwards. Other silent runs (for example a deployment tool running as SYSTEM) do not start it.
Filename: "{app}\{#AppExeName}"; Flags: nowait runascurrentuser; Check: RelaunchRequested

[UninstallDelete]
; Installers the in-app updater downloaded into the install folder.
Type: filesandordirs; Name: "{app}\updates"
; Written next to the exe by earlier portable releases started from this folder.
Type: files; Name: "{app}\gameshift-diag.log"

[Code]
function IsGameShiftRunning(): Boolean;
begin
  Result := CheckForMutexes('{#AppMutexName}');
end;

function InitializeSetup(): Boolean;
var
  Waited: Integer;
begin
  // The in-app updater starts Setup and then exits, reverting any active optimizations first. Wait for
  // it, so its files are free to replace; Restart Manager (CloseApplications) handles anything still
  // running after that.
  if WizardSilent then
  begin
    Waited := 0;
    while IsGameShiftRunning() and (Waited < 180000) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
  end;

  Result := True;
end;

function RelaunchRequested(): Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

// Restart Manager does not cover the uninstaller, and removing the files of a running, elevated
// GameShift would leave it running without them. Returns False if the user cancels.
function WaitForGameShiftToClose(): Boolean;
begin
  Result := True;
  while IsGameShiftRunning() do
  begin
    if SuppressibleMsgBox(FmtMessage(SetupMessage(msgUninstallAppRunningError), ['{#AppName}']),
      mbError, MB_OKCANCEL, IDCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := WaitForGameShiftToClose();
end;

// Checked again after the confirmation box, which asks the user to open GameShift first.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usAppMutexCheck) and not WaitForGameShiftToClose() then
    Abort;
end;
