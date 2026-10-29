; Inno Setup 6 installer script for Historia
#define AppName "Historia"
#define AppPublisher "Kevin Chen"
#define AppURL "https://github.com/KChenSquared/Historia"
#define AppExeName "Historia.exe"
#define AppCmdrExeName "historia-cmdr.exe"

#ifndef AppVersion
#define AppVersion "0.0.0"
#endif

#ifndef SourceDir
#define SourceDir "..\..\bin\Historia.App\Release\net10.0\win-x64\publish"
#endif

#ifndef OutputDir
#define OutputDir "..\..\dist"
#endif

[Setup]
AppId={{B42E7A91-2E97-4DA3-8A76-8A6133B26F2D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Historia-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\Historia.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "addtopath"; Description: "Add Historia and historia-cmdr to PATH"; GroupDescription: "Additional tasks:"
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
#if Defined(RuntimeInstaller) && RuntimeInstaller != ""
Source: "{#RuntimeInstaller}"; DestDir: "{tmp}"; DestName: "dotnet-desktop-runtime-win-x64.exe"; Flags: dontcopy noencryption
#endif
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\{#AppName}"; ValueType: string; ValueName: "SetupAddedPath"; ValueData: "{app}"; Tasks: addtopath; Check: SetupWillAddPath; Flags: uninsdeletevalue uninsdeletekeyifempty

[Code]
const
  EnvironmentKeyUser = 'Environment';
  EnvironmentKeySystem = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  DesktopRuntimeSubKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  SetupAddedPathKey = 'Software\Historia';
  SetupAddedPathValue = 'SetupAddedPath';

var
  AddedPathAtUninstall: String;

function HasDesktopRuntime10(RootKey: Integer): Boolean;
var
  VersionNames: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(RootKey, DesktopRuntimeSubKey, VersionNames) then
  begin
    for I := 0 to GetArrayLength(VersionNames) - 1 do
    begin
      if Pos('10.', VersionNames[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function IsDesktopRuntime10Installed: Boolean;
begin
  Result := HasDesktopRuntime10(HKLM32) or HasDesktopRuntime10(HKLM64);
end;

function NormalizePathEntry(P: String): String;
begin
  Result := Trim(P);
  while (Length(Result) > 0) and (Result[Length(Result)] = '\') do
    Delete(Result, Length(Result), 1);
end;

function GetEnvironmentRootKey: Integer;
begin
  if IsAdminInstallMode then
    Result := HKLM
  else
    Result := HKCU;
end;

function GetEnvironmentSubKey: String;
begin
  if IsAdminInstallMode then
    Result := EnvironmentKeySystem
  else
    Result := EnvironmentKeyUser;
end;

function NeedsAddPath(AppPath: String): Boolean;
var
  OrigPath: String;
  NormalizedAppPath: String;
  P, NextP: Integer;
  Entry: String;
begin
  Result := True;
  NormalizedAppPath := NormalizePathEntry(AppPath);
  if not RegQueryStringValue(GetEnvironmentRootKey, GetEnvironmentSubKey, 'Path', OrigPath) then
    Exit;

  P := 1;
  while P <= Length(OrigPath) do
  begin
    NextP := Pos(';', Copy(OrigPath, P, Length(OrigPath) - P + 1));
    if NextP = 0 then
      NextP := Length(OrigPath) - P + 2;
    Entry := Copy(OrigPath, P, NextP - 1);
    if CompareText(NormalizePathEntry(Entry), NormalizedAppPath) = 0 then
    begin
      Result := False;
      Exit;
    end;
    P := P + NextP;
  end;
end;

// Runs before ssPostInstall appends the entry, so it sees Path as it was before this install.
function SetupWillAddPath: Boolean;
begin
  Result := NeedsAddPath(ExpandConstant('{app}'));
end;

procedure AddPathToEnvironment(AppPath: String);
var
  OrigPath, NewPath: String;
begin
  if not NeedsAddPath(AppPath) then
    Exit;

  if RegQueryStringValue(GetEnvironmentRootKey, GetEnvironmentSubKey, 'Path', OrigPath) then
  begin
    if (Length(OrigPath) > 0) and (OrigPath[Length(OrigPath)] <> ';') then
      NewPath := OrigPath + ';' + AppPath
    else
      NewPath := OrigPath + AppPath;
  end
  else
    NewPath := AppPath;

  RegWriteExpandStringValue(GetEnvironmentRootKey, GetEnvironmentSubKey, 'Path', NewPath);
end;

procedure RemovePathFromEnvironment(AppPath: String);
var
  OrigPath, NewPath, Entry: String;
  NormalizedAppPath: String;
  P, NextP: Integer;
  RootKey: Integer;
  SubKey: String;
  Removed: Boolean;
begin
  NormalizedAppPath := NormalizePathEntry(AppPath);
  RootKey := GetEnvironmentRootKey;
  SubKey := GetEnvironmentSubKey;

  if not RegQueryStringValue(RootKey, SubKey, 'Path', OrigPath) then
    Exit;

  Removed := False;
  NewPath := '';
  P := 1;
  while P <= Length(OrigPath) do
  begin
    NextP := Pos(';', Copy(OrigPath, P, Length(OrigPath) - P + 1));
    if NextP = 0 then
      NextP := Length(OrigPath) - P + 2;
    Entry := Copy(OrigPath, P, NextP - 1);
    if CompareText(NormalizePathEntry(Entry), NormalizedAppPath) <> 0 then
    begin
      if Length(NewPath) > 0 then
        NewPath := NewPath + ';' + Entry
      else
        NewPath := Entry;
    end
    else
      Removed := True;
    P := P + NextP;
  end;

  if Removed then
    RegWriteExpandStringValue(RootKey, SubKey, 'Path', NewPath);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
#if Defined(RuntimeInstaller) && RuntimeInstaller != ""
var
  RuntimePath: String;
  ResultCode: Integer;
  RuntimeInstalled: Boolean;
#endif
begin
  Result := '';
  #if Defined(RuntimeInstaller) && RuntimeInstaller != ""
  if not IsDesktopRuntime10Installed then
  begin
    RuntimeInstalled := False;
    try
      ExtractTemporaryFile('dotnet-desktop-runtime-win-x64.exe');
      RuntimePath := ExpandConstant('{tmp}\dotnet-desktop-runtime-win-x64.exe');
      // 1641 and 3010 report success with a restart pending.
      if ShellExec('runas', RuntimePath, '/install /passive /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
        RuntimeInstalled := (ResultCode = 0) or (ResultCode = 1641) or (ResultCode = 3010);
    except
      RuntimeInstalled := False;
    end;

    if not RuntimeInstalled then
    begin
      SuppressibleMsgBox('The Microsoft .NET Desktop Runtime 10.x (x64) is required by Historia but was not installed.' + #13#10 +
        'Setup will continue, but Historia requires the runtime before it can launch.', mbInformation, MB_OK, IDOK);
    end;
  end;
  #endif
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('addtopath') then
      AddPathToEnvironment(ExpandConstant('{app}'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // The recorded value is read before the uninstaller deletes its registry entries.
  if CurUninstallStep = usUninstall then
  begin
    if not RegQueryStringValue(HKA, SetupAddedPathKey, SetupAddedPathValue, AddedPathAtUninstall) then
      AddedPathAtUninstall := '';
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    if AddedPathAtUninstall <> '' then
      RemovePathFromEnvironment(AddedPathAtUninstall);
  end;
end;
