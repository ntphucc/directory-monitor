#define MyAppName "Directory Monitor"
#ifndef MyAppVersion
  #error MyAppVersion must be supplied by scripts/build-release.ps1
#endif
#define MyAppExeName "Directory Monitor.exe"

[Setup]
AppId={{5B94FC80-7C62-462B-9791-CB0946AA4C91}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\Directory Monitor
DefaultGroupName=Directory Monitor
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=DirectoryMonitor-Setup-{#MyAppVersion}-x64
SetupIconFile=..\src\DirectoryMonitor.Presentation\Assets\directory-monitor.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "*.pdb,appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall; Permissions: users-modify

[Dirs]
Name: "{app}\Data"; Permissions: users-modify
Name: "{app}\Logs"; Permissions: users-modify

[Icons]
Name: "{group}\Directory Monitor"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Directory Monitor"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\Directory Monitor"; ValueType: string; ValueName: "EventMessageFile"; ValueData: "{app}\System.Diagnostics.EventLog.Messages.dll"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\Directory Monitor"; ValueType: dword; ValueName: "TypesSupported"; ValueData: "7"
Root: HKLM; Subkey: "Software\Directory Monitor"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Directory Monitor"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: IsNormalInstall
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser skipifdoesntexist; Check: IsUpdateRelaunch

[Code]
function IsUpdateRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function IsNormalInstall: Boolean;
begin
  Result := not IsUpdateRelaunch;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ImportDatabase: String;
  TargetDatabase: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  ImportDatabase := ExpandConstant('{param:IMPORTDB|}');
  TargetDatabase := ExpandConstant('{app}\Data\directory-monitor.db');
  if (ImportDatabase <> '') and FileExists(ImportDatabase) and (not FileExists(TargetDatabase)) then
  begin
    Log('Importing portable Directory Monitor database from ' + ImportDatabase);
    if not CopyFile(ImportDatabase, TargetDatabase, False) then
      RaiseException('Directory Monitor could not import the existing portable database. The original backup remains at: ' + ImportDatabase);
  end;
end;
