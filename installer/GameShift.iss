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
; One file per DPI step; Setup picks the best match. Used for both light and dark.
#define WizImages "wizard\wizard-large-202x386.png,wizard\wizard-large-269x515.png,wizard\wizard-large-336x643.png,wizard\wizard-large-403x772.png,wizard\wizard-large-430x824.png,wizard\wizard-large-498x953.png,wizard\wizard-large-534x1022.png"
#define WizSmallImages "wizard\wizard-small-58x58.png,wizard\wizard-small-77x77.png,wizard\wizard-small-97x97.png,wizard\wizard-small-116x116.png,wizard\wizard-small-124x124.png,wizard\wizard-small-143x143.png,wizard\wizard-small-159x159.png"

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
; Naming windows11 and excludelightcontrols spells out what WizardBackColor switches on anyway.
WizardStyle=modern dynamic windows11 excludelightcontrols
WizardBackColor=#EEF1F4
WizardBackColorDynamicDark=#0B0D10
; Set for dark too: Inno does not say whether dynamic dark falls back to the light images or its own.
WizardImageFile={#WizImages}
WizardImageFileDynamicDark={#WizImages}
WizardSmallImageFile={#WizSmallImages}
WizardSmallImageFileDynamicDark={#WizSmallImages}
WizardSmallImageBackColor=none
WizardSmallImageBackColorDynamicDark=none
DisableWelcomePage=no
; Ready lists nothing to change here. CurPageChanged gives the page before the install an Install button.
DisableReadyPage=yes

[LangOptions]
WelcomeFontName=Bahnschrift

[Messages]
SetupWindowTitle=%1 Setup
WelcomeLabel1=Install [name]
WelcomeLabel2=This installs [name/ver]. A new install goes to Program Files, where only administrators can change it.%n%nIf [name] is already installed, Setup updates it in its current folder. Your settings stay as they are.%n%nSetup copies the app and adds a Start menu shortcut. It does not change any Windows settings.
SetupAppRunningError=%1 is running.%n%nRight-click the %1 icon next to the clock (it may be behind the ^ arrow) and choose Exit. That ends any active session first.%n%nIf someone else is signed in to this PC, they need to exit %1 in their account too.%n%nThen click OK to continue, or Cancel to stop Setup.
WizardPreparing=Close programs
PreparingDesc=Setup checks for programs that use [name]'s files.
ApplicationsFound=These programs are using files that Setup needs to replace:
ApplicationsFound2=These programs are using files that Setup needs to replace:
CloseApplications=&Close them for me
DontCloseApplications=&Leave them open
ErrorCloseApplications=Setup could not close every program. Close the programs listed on the Close programs page yourself, then click Try again.
WizardInstalling=Installing
InstallingLabel=Copying the [name] files.
FinishedHeadingLabel=[name] is installed
FinishedLabel=Find [name] in the Start menu.
FinishedLabelNoIcons=[name] is ready.
ClickFinish=Click Finish to close Setup.
ExitSetupMessage=Setup is not finished. If you exit now, {#AppName} is not installed or updated.%n%nExit Setup?
ConfirmUninstall=Remove %1?%n%nUninstalling removes the %1 startup task and offers to undo the lasting changes %1 recorded for your Windows account. Changes that need your own choice are listed, not undone. For changes recorded in another Windows account, use Restore All on the Lasting changes page of %1 in that account first.%n%nYour settings stay in your user folder, so installing %1 again picks them up.
UninstallAppRunningError=%1 is running.%n%nRight-click the %1 icon next to the clock (it may be behind the ^ arrow) and choose Exit. That ends any active session first.%n%nIf someone else is signed in to this PC, they need to exit %1 in their account too.%n%nThen click OK to continue, or Cancel to keep %1 installed.
UninstallStatusLabel=Removing %1.
UninstalledAll=%1 is removed. Your settings are still in your user folder.
UninstalledMost=%1 is removed, but some files could not be deleted. You can delete them yourself.

[CustomMessages]
LaunchProgram=Start %1 now

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

  // Nested, so a silent run never shows the box whatever the evaluation order of "and".
  if not WizardSilent then
    if not WaitForGameShiftToClose(SetupMessage(msgSetupAppRunningError)) then
    begin
      Result := False;
      Exit;
    end;

  Result := True;
end;

// The Ready page is off, so the page before the install says Install.
// Preparing keeps Inno's button when a restart is needed (Finish ends Setup) or the install was refused.
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpWelcome) or
    ((CurPageID = wpPreparing) and WizardForm.NextButton.Enabled and
     (WizardForm.NextButton.Caption = SetupMessage(msgButtonNext))) then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
end;

function RelaunchRequested(): Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

// A fresh install must land in the admin-only Program Files folder: everything in it runs elevated, and the
// uninstaller deletes {app}\updates by path. An upgrade stays in the folder it already has, or moves there.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExpectedDir: String;
  ChosenDir: String;
  PrevDir: String;
begin
  Result := '';
  if WizardForm.PrevAppDir <> '' then
  begin
    // /DIR takes precedence over the existing folder, so without this check an upgrade could move anywhere.
    ExpectedDir := ExpandConstant('{autopf}\{#AppName}');
    ChosenDir := RemoveBackslashUnlessRoot(ExpandFileName(WizardDirValue));
    PrevDir := RemoveBackslashUnlessRoot(ExpandFileName(WizardForm.PrevAppDir));
    if (CompareText(ChosenDir, PrevDir) <> 0) and (CompareText(ChosenDir, ExpectedDir) <> 0) then
    begin
      Log('Refusing to move the install from ' + PrevDir + ' to ' + ChosenDir);
      Result := '{#AppName} is already installed in ' + PrevDir + '.' + #13#10#13#10 +
        'Run Setup again without /DIR to update it there. {#AppName} installs only there or in ' + ExpectedDir + '.';
    end;
    Exit;
  end;

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
  ResultCode := RunGameShiftCommand('--remove-tasks');
  if ResultCode = 3 then
    Exit;

  if ResultCode <> 0 then
    if not UninstallSilent then
      SuppressibleMsgBox('{#AppName} could not remove its startup task, so Task Scheduler may still list it.' + #13#10#13#10 +
        'To remove it, open Task Scheduler, open the GameShift folder and delete the Startup task, and BootRecovery if it is there. ' +
        'Or run this in a command prompt opened as administrator:' + #13#10 + 'schtasks /Delete /TN "GameShift\Startup" /F',
        mbInformation, MB_OK, IDOK);

  if not UninstallSilent then
    if SuppressibleMsgBox('Undo the lasting changes {#AppName} recorded for your Windows account?',
      mbConfirmation, MB_YESNO, IDYES) <> IDYES then
    begin
      Log('The user chose not to undo the recorded lasting changes');
      Exit;
    end;

  ResultCode := RunGameShiftCommand('--restore-tweaks');
  if (ResultCode <> 0) and (ResultCode <> 3) and not UninstallSilent then
    SuppressibleMsgBox('Some lasting changes {#AppName} recorded are not undone. ' +
      ExpandConstant('{commonappdata}\{#AppName}\logs\restore-tweaks.txt') + ' lists them when that file exists.' + #13#10#13#10 +
      'To undo them, start {#AppName} again (installed or portable) and use Restore All on the Lasting changes page.' + #13#10#13#10 +
      'If the list names a settings.unreadable file, move it out of your {#AppName} settings folder after reading it, or every undo reports it again.',
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
