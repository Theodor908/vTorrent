; vTorrent Inno Setup Script
; Creates a traditional Windows installer (.exe)

#define MyAppName "vTorrent"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "vTorrent"
#define MyAppURL "https://github.com/vtorrent"
#define MyAppExeName "vTorrent.exe"

[Setup]
; Unique identifier for this application
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Installation directories
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Output settings
OutputDir=..\..\dist\windows\installer
OutputBaseFilename=vTorrent-{#MyAppVersion}-Setup
SetupIconFile=..\..\Assets\Images\logo256x256.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

; Compression
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Privileges - install for current user by default, but allow elevation
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Visual settings
WizardStyle=modern
WizardSizePercent=100

; Windows version requirement (Windows 10 1809+)
MinVersion=10.0.17763

; Allow user to select architecture if on ARM64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Notify shell of file association changes (refreshes icon cache)
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "fileassoc"; Description: "Associate .torrent files with vTorrent"; GroupDescription: "File associations:"; Flags: checkedonce
Name: "magnetassoc"; Description: "Handle magnet: links with vTorrent"; GroupDescription: "File associations:"; Flags: checkedonce

[Files]
; Main application files (from publish output)
Source: "..\..\dist\windows\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; Desktop (optional)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; .torrent file association
Root: HKA; Subkey: "Software\Classes\.torrent"; ValueType: string; ValueName: ""; ValueData: "vTorrent.TorrentFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\vTorrent.TorrentFile"; ValueType: string; ValueName: ""; ValueData: "BitTorrent File"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\vTorrent.TorrentFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\vTorrent.TorrentFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc

; magnet: protocol handler
Root: HKA; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: ""; ValueData: "URL:Magnet Protocol"; Flags: uninsdeletekey; Tasks: magnetassoc
Root: HKA; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Tasks: magnetassoc
Root: HKA; Subkey: "Software\Classes\magnet\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: magnetassoc
Root: HKA; Subkey: "Software\Classes\magnet\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: magnetassoc

[Run]
; Option to launch after install
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up app data on uninstall (optional - commented out to preserve user data)
; Type: filesandordirs; Name: "{localappdata}\vTorrent"

[Code]
// Check if app is running before install/uninstall
function IsAppRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  // Use cmd to pipe tasklist through findstr - findstr returns 0 only if process name is found
  if Exec('cmd.exe', '/c tasklist /FI "IMAGENAME eq vTorrent.exe" /NH 2>nul | findstr /I /C:"vTorrent.exe" >nul', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    // findstr returns 0 if match found, 1 if no match
    Result := (ResultCode = 0);
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if IsAppRunning() then
  begin
    MsgBox('vTorrent is currently running. Please close it before installing.', mbError, MB_OK);
    Result := False;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if IsAppRunning() then
  begin
    MsgBox('vTorrent is currently running. Please close it before uninstalling.', mbError, MB_OK);
    Result := False;
  end;
end;
