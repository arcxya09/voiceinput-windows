; Build with scripts/BuildInstaller.ps1. Startup registration belongs to the
; application's Settings page; installing never opts the user into startup.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
AppId={{D2D24A36-3D20-4EAE-9A0B-82C57CF0C671}
AppName=VoiceInput
AppVersion={#AppVersion}
AppVerName=VoiceInput {#AppVersion}
AppPublisher=VoiceInput
AppPublisherURL=https://github.com/arcxya09/voiceinput-windows
AppSupportURL=https://github.com/arcxya09/voiceinput-windows/issues
AppUpdatesURL=https://github.com/arcxya09/voiceinput-windows/releases
DefaultDirName={localappdata}\Programs\VoiceInput
DefaultGroupName=VoiceInput
; Keep /GROUP functional for managed installs and isolated verification.
; Inno ignores that command-line override when this directive is yes.
DisableProgramGroupPage=auto
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=VoiceInput-Setup-{#AppVersion}
SetupIconFile={#PackageDir}\app\Assets\AppIcon.ico
UninstallDisplayIcon={app}\VoiceInput.exe
UninstallDisplayName=VoiceInput
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=VoiceInput
VersionInfoProductVersion={#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UsePreviousAppDir=yes
UsePreviousTasks=yes
; A hidden tray application treats WM_CLOSE as hide. Never force close it:
; the preflight checks below stop install/uninstall until the user chooses Exit.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PackageDir}\VoiceInput.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\README.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\VoiceInput"; Filename: "{app}\VoiceInput.exe"; WorkingDir: "{app}"; AppUserModelID: "VoiceInput.Desktop"
Name: "{autodesktop}\VoiceInput"; Filename: "{app}\VoiceInput.exe"; WorkingDir: "{app}"; AppUserModelID: "VoiceInput.Desktop"; Tasks: desktopicon

[Run]
Filename: "{app}\VoiceInput.exe"; Description: "{cm:LaunchProgram,VoiceInput}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
chinesesimplified.CloseVoiceInput=请先在 VoiceInput 托盘菜单中选择“退出”，再继续安装或卸载。请先保存正在识别的内容。
english.CloseVoiceInput=Choose Exit from the VoiceInput tray menu before installing or uninstalling. Save any dictation in progress first.

[Code]
const
  InvalidFileHandle = $FFFFFFFF;
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupMetadataKey = 'Software\VoiceInput\Startup';

function CreateFileW(FileName: String; DesiredAccess, ShareMode, SecurityAttributes,
  CreationDisposition, FlagsAndAttributes, TemplateFile: LongWord): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function OpenProcess(Access: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function GetLastError: LongWord;
  external 'GetLastError@kernel32.dll stdcall';

function InitializeSetup: Boolean;
var
  ParentId: Integer;
  Argument: String;
  Handle: THandle;
begin
  Result := True;
  Argument := ExpandConstant('{param:UPDATEWAIT|}');
  if Argument = '' then Exit;
  ParentId := StrToIntDef(Argument, 0);
  Result := ParentId > 0;
  if Result then
  begin
    Handle := OpenProcess($00100000, False, ParentId);
    if Handle = 0 then
      Result := GetLastError = 87 { The parent already exited. }
    else
    begin
      Log('Waiting for VoiceInput update parent to exit.');
      Result := WaitForSingleObject(Handle, 120000) = 0;
      CloseHandle(Handle);
      if Result then Log('VoiceInput update parent exited.');
    end;
  end;
  if not Result then
    SuppressibleMsgBox('VoiceInput update could not wait for the application to exit. Please exit VoiceInput and open the installer again.', mbError, MB_OK, IDOK);
end;

function FileIsInUse(const FileName: String): Boolean;
var
  Handle: THandle;
begin
  Result := False;
  if not FileExists(FileName) then Exit;
  { Loaded executable images deny write access. Sharing read/write/delete here
    avoids treating an ordinary read handle as a running application. A denied
    write also catches an unwritable destination before any files are changed. }
  Handle := CreateFileW(FileName, $40000000, $00000007, 0, 3, $80, 0);
  Result := Handle = InvalidFileHandle;
  if not Result then CloseHandle(Handle);
end;

function ApplicationIsInUse: Boolean;
begin
  Result := FileIsInUse(ExpandConstant('{app}\app\RealtimeTranscription.exe')) or
    FileIsInUse(ExpandConstant('{app}\VoiceInput.exe'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ApplicationIsInUse then Result := CustomMessage('CloseVoiceInput');
end;

function InitializeUninstall: Boolean;
begin
  Result := not ApplicationIsInUse;
  if not Result then
    SuppressibleMsgBox(CustomMessage('CloseVoiceInput'), mbError, MB_OK, IDOK);
end;

function IsOwnedStartupCommand(const Command: String): Boolean;
begin
  Result := (CompareText(Command, '"' + ExpandConstant('{app}\VoiceInput.exe') + '" --startup') = 0) or
    (CompareText(Command, '"' + ExpandConstant('{app}\app\RealtimeTranscription.exe') + '" --startup') = 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { A separate portable copy may own the same named Run value. Delete only
      the command for the installation being removed, never a sibling copy. }
    if RegQueryStringValue(HKCU, RunKey, 'VoiceInput', Command) and IsOwnedStartupCommand(Command) then
      RegDeleteValue(HKCU, RunKey, 'VoiceInput');
    if RegQueryStringValue(HKCU, StartupMetadataKey, 'RegisteredCommand', Command) and IsOwnedStartupCommand(Command) then
    begin
      RegDeleteValue(HKCU, StartupMetadataKey, 'RegisteredCommand');
      RegDeleteKeyIfEmpty(HKCU, StartupMetadataKey);
    end;
    { No action targets LocalAppData\RealtimeTranscription: configuration,
      credentials, vocabulary and history survive an uninstall. }
  end;
end;
