#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
[Setup]
AppId={{C8576336-ED17-4D14-A2D8-23C48D7DAF19}
AppName=开机自启管理
AppVersion={#AppVersion}
AppPublisher=StartupManager contributors
DefaultDirName={localappdata}\Programs\StartupManager
DefaultGroupName=开机自启管理
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\packages
OutputBaseFilename=StartupManager-{#AppVersion}-win-x64-Setup
SetupIconFile=..\src\StartupManager.App\Assets\app.ico
UninstallDisplayIcon={app}\StartupManager.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE
[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\开机自启管理"; Filename: "{app}\StartupManager.exe"
Name: "{autodesktop}\开机自启管理"; Filename: "{app}\StartupManager.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\StartupManager.exe"; Description: "打开开机自启管理"; Flags: nowait postinstall skipifsilent
[Code]
procedure InitializeUninstallProgressForm();
begin
  if not UninstallSilent then
    MsgBox('卸载将保留恢复备份，也不会自动恢复已关闭的启动项。如需恢复，请先取消卸载并在软件中恢复。', mbInformation, MB_OK);
end;
