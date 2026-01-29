; XhMonitor Installer Script for Inno Setup
; 星核监视器安装程序
; 使用 Inno Setup 6.x 编译

#define MyAppName "星核监视器"
#define MyAppNameEn "XhMonitor"
; 版本号通过命令行参数 /DMyAppVersion=x.x.x 传递
#ifndef MyAppVersion
  #define MyAppVersion "0.2.1"
#endif
#define MyAppPublisher "Xinghe"
#define MyAppURL "https://github.com/zhao-wuyan/xhMonitor"
#define MyAppExeName "XhMonitor.Desktop.exe"
#define MyAppServiceName "XhMonitor.Service.exe"

[Setup]
; 应用程序信息
AppId={{8A7B9C0D-1E2F-3A4B-5C6D-7E8F9A0B1C2D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; 安装目录
DefaultDirName={autopf}\{#MyAppNameEn}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; 输出设置
OutputDir=..\release
OutputBaseFilename=XhMonitor-v{#MyAppVersion}-Setup
SetupIconFile=..\XhMonitor.Desktop\Assets\icon.ico
UninstallDisplayIcon={app}\Desktop\Assets\icon.ico

; 压缩设置
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; 权限设置
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; 界面设置
WizardStyle=modern
WizardSizePercent=100
DisableWelcomePage=no
ShowLanguageDialog=auto

; 版本信息
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoCopyright=Copyright (C) 2024-2026 {#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; 如需中文界面，请从 https://github.com/jrsoftware/issrc/tree/main/Files/Languages/Unofficial 下载 ChineseSimplified.isl
; 放置到 Inno Setup 安装目录的 Languages 文件夹，然后取消下行注释
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.CreateDesktopIcon=Create a &desktop icon
english.CreateQuickLaunchIcon=Create a &Quick Launch icon
english.LaunchProgram=Launch %1
english.AssocFileExtension=&Associate %1 with the %2 file extension
english.StartupTask=Start automatically with Windows
english.SystemSettings=System Settings:
; 中文消息（启用中文语言后生效）
; chinesesimplified.CreateDesktopIcon=创建桌面快捷方式(&D)
; chinesesimplified.CreateQuickLaunchIcon=创建快速启动栏快捷方式(&Q)
; chinesesimplified.LaunchProgram=运行 %1
; chinesesimplified.AssocFileExtension=将 %1 与 %2 文件扩展名关联
; chinesesimplified.StartupTask=开机自动启动
; chinesesimplified.SystemSettings=系统设置:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
; Name: "startupicon"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:SystemSettings}"; Flags: unchecked

[Files]
; 桌面应用程序
Source: "..\release\XhMonitor-v{#MyAppVersion}\Desktop\*"; DestDir: "{app}\Desktop"; Flags: ignoreversion recursesubdirs createallsubdirs

; 后端服务
Source: "..\release\XhMonitor-v{#MyAppVersion}\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs

; 启动脚本
Source: "..\release\XhMonitor-v{#MyAppVersion}\启动服务.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\release\XhMonitor-v{#MyAppVersion}\停止服务.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\release\XhMonitor-v{#MyAppVersion}\README.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; 开始菜单快捷方式
Name: "{group}\{#MyAppName}"; Filename: "{app}\Desktop\{#MyAppExeName}"; IconFilename: "{app}\Desktop\Assets\icon.ico"
Name: "{group}\Start Service"; Filename: "{app}\启动服务.bat"; IconFilename: "{app}\Desktop\Assets\icon.ico"
Name: "{group}\Stop Service"; Filename: "{app}\停止服务.bat"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"

; 桌面快捷方式
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\Desktop\{#MyAppExeName}"; IconFilename: "{app}\Desktop\Assets\icon.ico"; Tasks: desktopicon

; 开机启动
; Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\启动服务.bat"; Tasks: startupicon

[Run]
; 安装完成后运行
Filename: "{app}\Desktop\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; 卸载前停止服务
Filename: "{app}\停止服务.bat"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"

[UninstallDelete]
; 卸载时删除生成的文件
Type: filesandordirs; Name: "{app}\Service\logs"
Type: filesandordirs; Name: "{app}\Service\*.db"
Type: filesandordirs; Name: "{app}\Service\*.db-shm"
Type: filesandordirs; Name: "{app}\Service\*.db-wal"

[Code]
// 强制终止进程
procedure KillProcess(ProcessName: String);
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM ' + ProcessName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// 安装前停止旧服务（在安装目录确定后）
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  StopBat: String;
begin
  if CurStep = ssInstall then
  begin
    // 先尝试执行停止脚本
    StopBat := ExpandConstant('{app}\停止服务.bat');
    if FileExists(StopBat) then
    begin
      Exec(StopBat, '', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(2000);
    end;
    // 强制终止可能残留的进程
    KillProcess('XhMonitor.Service.exe');
    KillProcess('XhMonitor.Desktop.exe');
    Sleep(1000);
  end;
end;

// 卸载前停止服务
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  StopBat: String;
begin
  Result := True;
  StopBat := ExpandConstant('{app}\停止服务.bat');
  if FileExists(StopBat) then
  begin
    Exec(StopBat, '', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
  end;
  // 强制终止进程
  KillProcess('XhMonitor.Service.exe');
  KillProcess('XhMonitor.Desktop.exe');
  Sleep(1000);
end;
