; GameShift installer (Inno Setup 6.7 or later).
;
; Build from the repository root:
;   dotnet publish src/GameShift.App/GameShift.App.csproj -c Release -r win-x64 -p:IncludeNativeLibrariesForSelfExtract=false -o artifacts/installer-publish
;   iscc installer\GameShift.iss
; Output: artifacts\installer\GameShift-Setup.exe. Pass /DPublishDir=<folder> to package another publish folder.
;
; The installed build must not self-extract. With IncludeNativeLibrariesForSelfExtract=false its native
; libraries sit next to the exe in Program Files instead of being unpacked into the user's %TEMP%, which
; the user can write to and the elevated app would load them from.

#if Ver < EncodeVer(6, 7, 0)
  #error Inno Setup 6.7 or later is required to build the GameShift installer
#endif

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
SetupAppRunningError=%1 is running.%n%nRight-click the %1 icon next to the clock (it may be behind the ^ arrow) and choose Exit. That ends any active session first.%n%nThen click OK to continue, or Cancel to stop Setup.
ConfirmUninstall=Remove %1?%n%nUninstalling removes the %1 startup task and offers to undo the lasting changes %1 recorded for your Windows account. Changes that need your own choice are listed, not undone. For changes recorded in another Windows account, use Restore All in %1 in that account first.%n%nYour settings stay in your user folder, so installing %1 again picks them up.
UninstallAppRunningError=%1 is running.%n%nRight-click the %1 icon next to the clock (it may be behind the ^ arrow) and choose Exit. That ends any active session first.%n%nThen click OK to continue, or Cancel to keep %1 installed.

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

// Restart Manager does not cover the uninstaller, and replacing or removing the files of a running,
// elevated GameShift would leave it running without them. Returns False if the user cancels.
function WaitForGameShiftToClose(const RunningMessage: String): Boolean;
begin
  Result := True;
  while IsGameShiftRunning() do
  begin
    if SuppressibleMsgBox(FmtMessage(RunningMessage, ['{#AppName}']),
      mbInformation, MB_OKCANCEL, IDCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;
  end;
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

  if not WizardSilent and not WaitForGameShiftToClose(SetupMessage(msgSetupAppRunningError)) then
  begin
    Result := False;
    Exit;
  end;

  Result := True;
end;

function RelaunchRequested(): Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

// A fresh install must land in the admin-only Program Files folder: everything in it runs elevated, and the
// uninstaller deletes {app}\updates by path. An upgrade keeps the folder it already has.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExpectedDir: String;
begin
  Result := '';
  if WizardForm.PrevAppDir <> '' then
    Exit;

  ExpectedDir := ExpandConstant('{autopf}\{#AppName}');
  if CompareText(RemoveBackslashUnlessRoot(WizardDirValue), ExpectedDir) <> 0 then
  begin
    Log('Refusing a fresh install in ' + WizardDirValue + ': GameShift installs only in ' + ExpectedDir);
    Result := '{#AppName} installs only in ' + ExpectedDir + ', a folder only administrators can change.' + #13#10#13#10 +
      'Run Setup again without /DIR, or with /DIR set to that folder.';
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := WaitForGameShiftToClose(SetupMessage(msgUninstallAppRunningError));
end;

// Runs {app}\GameShift.App.exe with one of its windowless commands and returns its exit code, or -1 when it
// could not run. Exit code 3 means GameShift is running, possibly in another signed-in account.
function RunGameShiftCommand(const Argument: String): Integer;
var
  ExePath: String;
begin
  Result := -1;
  ExePath := ExpandConstant('{app}\{#AppExeName}');
  if not FileExists(ExePath) then
  begin
    Log('Skipping ' + Argument + ': ' + ExePath + ' is missing');
    Exit;
  end;

  while True do
  begin
    if not Exec(ExePath, Argument, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    begin
      Log('Could not run ' + Argument + ': ' + SysErrorMessage(Result));
      Result := -1;
      Exit;
    end;
    Log(Argument + ' exited with ' + IntToStr(Result));

    if (Result <> 3) or UninstallSilent then
      Exit;
    if SuppressibleMsgBox('{#AppName} is running in another signed-in account, so it cannot remove its startup task or undo its changes now.' + #13#10#13#10 +
      'Close {#AppName} in every signed-in account, then click Retry. Click Cancel to remove {#AppName} without that.',
      mbInformation, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
      Exit;
  end;
end;

// Both commands need {app}, so they run before its files are deleted. The tasks go first: removing them
// is quick and needs no answer, so no elevated task is left pointing at the removed exe whatever the
// user answers next.
procedure RunUninstallCommands();
var
  ResultCode: Integer;
begin
  if RunGameShiftCommand('--remove-tasks') = 3 then
    Exit;

  if not UninstallSilent and (SuppressibleMsgBox('Undo the lasting changes {#AppName} recorded for your Windows account?',
    mbConfirmation, MB_YESNO, IDYES) <> IDYES) then
  begin
    Log('The user chose not to undo the recorded lasting changes');
    Exit;
  end;

  ResultCode := RunGameShiftCommand('--restore-tweaks');
  if (ResultCode <> 0) and (ResultCode <> 3) and not UninstallSilent then
    SuppressibleMsgBox('Some lasting changes {#AppName} recorded are not undone. ' +
      ExpandConstant('{commonappdata}\{#AppName}\logs\restore-tweaks.txt') + ' lists them when that file exists.' + #13#10#13#10 +
      'To undo them, start {#AppName} again (installed or portable) and use Restore All in Settings.',
      mbInformation, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Checked again after the confirmation box, which the user may answer after starting GameShift.
  if (CurUninstallStep = usAppMutexCheck) and not WaitForGameShiftToClose(SetupMessage(msgUninstallAppRunningError)) then
    Abort;

  if CurUninstallStep = usUninstall then
    RunUninstallCommands();
end;
