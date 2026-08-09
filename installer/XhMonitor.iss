; XhMonitor Installer Script for Inno Setup
; 星核监视器安装程序
; 使用 Inno Setup 6.x 编译
;
;   - BuildType=Lite           : Desktop/bridge 依赖系统 .NET 8（最精简，不内置运行时）
;   - BuildType=LiteNet8       : 同 Lite，并内置 .NET Desktop + ASP.NET Core Runtime 8 安装包
;   - BuildType=Full           : Desktop/bridge self-contained（最大，无需系统 .NET 运行时）
;
; 使用示例：
;   ISCC.exe /DMyAppVersion=1.0.0 /DBuildType=Lite XhMonitor.iss
;   ISCC.exe /DMyAppVersion=1.0.0 /DBuildType=LiteNet8 XhMonitor.iss
;   ISCC.exe /DMyAppVersion=1.0.0 /DBuildType=Full XhMonitor.iss

#define MyAppName "星核监视器"
#define MyAppNameEn "XhMonitor"
; 版本号通过命令行参数 /DMyAppVersion=x.x.x 传递
#ifndef MyAppVersion
  #define MyAppVersion "0.2.1"
#endif
; 构建类型通过命令行参数 /DBuildType=xxx 传递
#ifndef BuildType
  #define BuildType "LiteNet8"
#endif
#define MyAppPublisher "Xinghe"
#define MyAppURL "https://github.com/zhao-wuyan/xhMonitor"
#define MyAppExeName "XhMonitor.Desktop.exe"
#define MyAppServiceName "xhm-service.exe"
; C# WPF Desktop 需要 .NET Desktop Runtime（WPF/WinForms）与 ASP.NET Core Runtime（内嵌 Kestrel/YARP）。
; 两者的运行时安装包均自带 Microsoft.NETCore.App 基础运行时，可覆盖 lhm-bridge 的需求，无需单独打包 dotnet-runtime。
#ifndef AspNetCoreRuntimeInstallerFileName
  #define AspNetCoreRuntimeInstallerFileName "aspnetcore-runtime-8.0.27-win-x64.exe"
#endif
#ifndef DotNetDesktopRuntimeInstallerFileName
  #define DotNetDesktopRuntimeInstallerFileName "windowsdesktop-runtime-8.0.27-win-x64.exe"
#endif
#ifndef PawnIOInstallerFileName
  #define PawnIOInstallerFileName "PawnIO_setup.exe"
#endif
#define DotNetRuntimeDownloadUrl "https://dotnet.microsoft.com/download/dotnet/8.0"

; 根据构建类型设置输出文件名
#if BuildType == "Lite"
  #define OutputSuffix "Lite"
#elif BuildType == "LiteNet8"
  #define OutputSuffix "Lite-Net8"
#elif BuildType == "Full"
  #define OutputSuffix "Full"
#else
  #define OutputSuffix "Lite"
#endif

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
OutputBaseFilename=XhMonitor-v{#MyAppVersion}-{#OutputSuffix}-Setup
SetupIconFile=..\XhMonitor.Desktop\Assets\icon.ico
UninstallDisplayIcon={app}\Desktop\Assets\icon.ico

; 压缩设置
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; 权限设置
PrivilegesRequired=admin
; XhMonitor installs under Program Files and can manage hardware-access drivers, so setup stays elevated.

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
english.AutoInstallDotNetRuntime=Auto install .NET 8 runtimes required by XhMonitor
english.AutoInstallDotNetRuntimeHint=Silently install .NET Desktop + ASP.NET Core Runtime 8 when missing
english.AutoInstallPawnIO=Auto install PawnIO hardware driver
english.AutoInstallPawnIOHint=Install PawnIO for LibreHardwareMonitor builds that support safer CPU sensor access
; 中文消息（启用中文语言后生效）
; chinesesimplified.CreateDesktopIcon=创建桌面快捷方式(&D)
; chinesesimplified.CreateQuickLaunchIcon=创建快速启动栏快捷方式(&Q)
; chinesesimplified.LaunchProgram=运行 %1
; chinesesimplified.AssocFileExtension=将 %1 与 %2 文件扩展名关联
chinesesimplified.StartupTask=开机自动启动
; chinesesimplified.SystemSettings=系统设置:
chinesesimplified.SystemSettings=系统设置：
chinesesimplified.AutoInstallDotNetRuntime=自动安装 XhMonitor 所需的 .NET 8 运行环境
chinesesimplified.AutoInstallDotNetRuntimeHint=缺少运行环境时，静默安装 .NET Desktop + ASP.NET Core Runtime 8
chinesesimplified.AutoInstallPawnIO=自动安装 PawnIO 硬件驱动
chinesesimplified.AutoInstallPawnIOHint=为支持 PawnIO 的 LibreHardwareMonitor 版本安装更安全的 CPU 传感器访问驱动

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
; Name: "startupicon"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:SystemSettings}"; Flags: unchecked
#if BuildType != "Lite"
Name: "autoinstallpawnio"; Description: "{cm:AutoInstallPawnIO}"; GroupDescription: "{cm:SystemSettings}"
#endif
; 仅 LiteNet8 版本显示自动安装 .NET Runtime 8 的勾选框
#if BuildType == "LiteNet8"
Name: "autoinstalldotnetruntime"; Description: "{cm:AutoInstallDotNetRuntime}"; GroupDescription: "{cm:SystemSettings}"
#endif

[Files]
; 桌面应用程序
Source: "..\release\XhMonitor-v{#MyAppVersion}\Desktop\*"; DestDir: "{app}\Desktop"; Flags: ignoreversion recursesubdirs createallsubdirs

; 后端服务
Source: "..\release\XhMonitor-v{#MyAppVersion}\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs

; 启动脚本
Source: "..\release\XhMonitor-v{#MyAppVersion}\启动服务.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\release\XhMonitor-v{#MyAppVersion}\停止服务.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\release\XhMonitor-v{#MyAppVersion}\README.txt"; DestDir: "{app}"; Flags: ignoreversion

; Keep the plain Lite setup lean for upgrade installs. Driver/runtime dependencies belong in heavier installers.
#if BuildType != "Lite"
Source: "..\tools\PawnIO\{#PawnIOInstallerFileName}"; DestDir: "{tmp}"; Flags: dontcopy; Tasks: autoinstallpawnio
#endif

#if BuildType == "LiteNet8"
; LiteNet8 版本：将 .NET 8 运行时安装包打入安装器（不落盘到应用目录，仅用于安装阶段自动静默安装）
; ASP.NET Core Runtime + .NET Desktop Runtime 均自带 Microsoft.NETCore.App 基础运行时。
Source: "..\tools\RuntimePkg\{#AspNetCoreRuntimeInstallerFileName}"; DestDir: "{tmp}"; Flags: dontcopy
Source: "..\tools\RuntimePkg\{#DotNetDesktopRuntimeInstallerFileName}"; DestDir: "{tmp}"; Flags: dontcopy
#endif

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
Filename: "{app}\Desktop\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser; Check: CanLaunchDesktopApp

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
const
  DotNetRuntimeSilentArgs = '/install /quiet /norestart';
  PawnIOInstallerSilentArgs = '-install -silent';
  DotNetCoreSharedFrameworkName = 'Microsoft.NETCore.App';
  AspNetCoreSharedFrameworkName = 'Microsoft.AspNetCore.App';
  WindowsDesktopSharedFrameworkName = 'Microsoft.WindowsDesktop.App';

var
  RuntimePromptShown: Boolean;

// 强制终止进程
procedure KillProcess(ProcessName: String);
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM ' + ProcessName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure StopWinRingDriver();
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'stop WinRing0_1_2_0', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2000);
end;

function IsChineseSystemLanguage(): Boolean;
var
  LangId: Integer;
begin
  try
    LangId := GetUILanguage;
  except
    LangId := 0;
  end;

  // PRIMARYLANGID(LANGID) = LANGID & 0x3FF，中文主语言 ID = 0x04
  Result := (LangId and $3FF) = $04;
end;

function TryGetDotNetInstallLocationX64(var InstallLocation: String): Boolean;
begin
  // 优先读取 64 位 .NET 安装路径（支持自定义安装目录）
  InstallLocation := '';
  Result := IsWin64 and RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', InstallLocation);
end;

function HasSharedFramework8AtBase(const BasePath: String; const SharedFrameworkName: String): Boolean;
var
  SharedFrameworkBase: String;
  SearchPattern: String;
  FindRec: TFindRec;
begin
  // 如果连 dotnet.exe 都不存在，则直接判定未安装（避免误扫）
  if not FileExists(AddBackslash(BasePath) + 'dotnet.exe') then
  begin
    Result := False;
    exit;
  end;

  SharedFrameworkBase := AddBackslash(AddBackslash(BasePath) + 'shared') + SharedFrameworkName;
  if not DirExists(SharedFrameworkBase) then
  begin
    Result := False;
    exit;
  end;

  SearchPattern := AddBackslash(SharedFrameworkBase) + '8.*';

  // 通过共享框架目录判断是否已安装 .NET Runtime 8.x
  if FindFirst(SearchPattern, FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end
  else
  begin
    Result := False;
  end;
end;

function IsSharedFramework8Installed(const SharedFrameworkName: String): Boolean;
var
  InstallLocation: String;
begin
  // 先用注册表的 x64 安装路径（支持自定义目录）
  if TryGetDotNetInstallLocationX64(InstallLocation) then
  begin
    if HasSharedFramework8AtBase(InstallLocation, SharedFrameworkName) then
    begin
      Result := True;
      exit;
    end;
  end;

  // 再检查常见默认路径。注意：安装器可能为 32 位，此时 {pf} 会指向 Program Files (x86)，因此需要显式检查 {pf64}。
  if IsWin64 then
  begin
    if HasSharedFramework8AtBase(ExpandConstant('{pf64}\dotnet'), SharedFrameworkName) then
    begin
      Result := True;
      exit;
    end;

    if HasSharedFramework8AtBase(ExpandConstant('{pf32}\dotnet'), SharedFrameworkName) then
    begin
      Result := True;
      exit;
    end;
  end
  else
  begin
    if HasSharedFramework8AtBase(ExpandConstant('{pf}\dotnet'), SharedFrameworkName) then
    begin
      Result := True;
      exit;
    end;
  end;

  // 兜底：有些环境可能为“用户级 dotnet”安装
  if HasSharedFramework8AtBase(ExpandConstant('{localappdata}\Microsoft\dotnet'), SharedFrameworkName) then
  begin
    Result := True;
    exit;
  end;

  Result := False;
end;

function AreRequiredDotNetRuntimesInstalled(): Boolean;
begin
  // C# WPF Desktop 需要 Desktop + ASP.NET Core 共享框架；两者均自带 NETCore.App 基础运行时。
  Result :=
    IsSharedFramework8Installed(WindowsDesktopSharedFrameworkName) and
    IsSharedFramework8Installed(AspNetCoreSharedFrameworkName);
end;

function IsSelfContainedPackageInstalled(): Boolean;
begin
  // Full（self-contained）发布会随 Desktop 一起落地 hostfxr.dll，无需系统安装的 .NET 运行时。
  Result := FileExists(ExpandConstant('{app}\Desktop\hostfxr.dll'));
end;

procedure ShowRuntimeMissingPrompt(); forward;

function CanLaunchDesktopApp(): Boolean;
begin
  if IsSelfContainedPackageInstalled() then
  begin
    Result := True;
    exit;
  end;

  if AreRequiredDotNetRuntimesInstalled() then
  begin
    Result := True;
    exit;
  end;

  // 运行时缺失时不自动拉起 Desktop，改为提示，避免弹出 .NET host 默认错误框。
  ShowRuntimeMissingPrompt();
  Result := False;
end;

function ShouldAutoInstallDotNetRuntime(): Boolean;
begin
#if BuildType == "LiteNet8"
  Result := WizardIsTaskSelected('autoinstalldotnetruntime');
#else
  Result := False;
#endif
end;

function IsPawnIOInstalled(): Boolean;
var
  ResultCode: Integer;
  DisplayName: String;
begin
  if Exec('sc.exe', 'query PawnIO', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
  begin
    Result := True;
    exit;
  end;

  if RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PawnIO') then
  begin
    Result := True;
    exit;
  end;

  if RegValueExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO', 'DisplayName') then
  begin
    Result := True;
    exit;
  end;

  if RegValueExists(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO', 'DisplayName') then
  begin
    Result := True;
    exit;
  end;

  if RegValueExists(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO', 'DisplayName') then
  begin
    Result := True;
    exit;
  end;

  if RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO_is1', 'DisplayName', DisplayName) and
     (Pos('PawnIO', DisplayName) > 0) then
  begin
    Result := True;
    exit;
  end;

  if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO_is1', 'DisplayName', DisplayName) and
     (Pos('PawnIO', DisplayName) > 0) then
  begin
    Result := True;
    exit;
  end;

  if RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO_is1', 'DisplayName', DisplayName) and
     (Pos('PawnIO', DisplayName) > 0) then
  begin
    Result := True;
    exit;
  end;

  if RegKeyExists(HKLM64, 'SOFTWARE\PawnIO') or
     RegKeyExists(HKLM32, 'SOFTWARE\PawnIO') or
     RegKeyExists(HKCU, 'SOFTWARE\PawnIO') or
     RegKeyExists(HKLM64, 'SOFTWARE\namazso\PawnIO') or
     RegKeyExists(HKLM32, 'SOFTWARE\namazso\PawnIO') or
     RegKeyExists(HKCU, 'SOFTWARE\namazso\PawnIO') then
  begin
    Result := True;
    exit;
  end;

  Result := False;
end;

function ShouldAutoInstallPawnIO(): Boolean;
begin
#if BuildType == "Lite"
  Result := False;
#else
  Result := WizardIsTaskSelected('autoinstallpawnio') and (not IsPawnIOInstalled());
#endif
end;

function InstallBundledPawnIOIfNeeded(var NeedsRestart: Boolean): String;
var
  InstallerPath: String;
  ResultCode: Integer;
begin
  Result := '';

  if not ShouldAutoInstallPawnIO() then
    exit;

  Log('Auto-install PawnIO requested and PawnIO service is not installed.');

  try
    ExtractTemporaryFile('{#PawnIOInstallerFileName}');
  except
    Result := 'Failed to extract bundled PawnIO installer.';
    Log(Result);
    exit;
  end;

  InstallerPath := ExpandConstant('{tmp}\{#PawnIOInstallerFileName}');
  if not FileExists(InstallerPath) then
  begin
    Result := 'Bundled PawnIO installer not found: ' + InstallerPath;
    Log(Result);
    exit;
  end;

  if not Exec(InstallerPath, PawnIOInstallerSilentArgs, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Failed to execute bundled PawnIO installer.';
    Log(Result);
    exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1641) then
  begin
    Result := 'Bundled PawnIO installer exit code: ' + IntToStr(ResultCode);
    Log(Result);
    exit;
  end;

  if (ResultCode = 3010) or (ResultCode = 1641) then
    NeedsRestart := True;

  Sleep(1500);
  Log('PawnIO silent installation completed.');
end;

function InstallBundledRuntime(const InstallerFileName: String; const RuntimeDisplayName: String; var NeedsRestart: Boolean): Boolean;
var
  InstallerPath: String;
  ResultCode: Integer;
begin
  Result := False;

  try
    ExtractTemporaryFile(InstallerFileName);
  except
    Log('Failed to extract bundled installer for ' + RuntimeDisplayName + '.');
    exit;
  end;

  InstallerPath := ExpandConstant('{tmp}\' + InstallerFileName);
  if not FileExists(InstallerPath) then
  begin
    Log('Bundled installer not found for ' + RuntimeDisplayName + ': ' + InstallerPath);
    exit;
  end;

  if not Exec(InstallerPath, DotNetRuntimeSilentArgs, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('Failed to execute bundled installer for ' + RuntimeDisplayName + '.');
    exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1641) then
  begin
    Log('Bundled installer exit code for ' + RuntimeDisplayName + ': ' + IntToStr(ResultCode));
    exit;
  end;

  if (ResultCode = 3010) or (ResultCode = 1641) then
    NeedsRestart := True;

  Sleep(1500);
  Result := True;
end;

function InstallRequiredDotNetRuntimeIfNeeded(var NeedsRestart: Boolean): String;
begin
  Result := '';

#if BuildType == "LiteNet8"
  if not ShouldAutoInstallDotNetRuntime() then
    exit;

  if AreRequiredDotNetRuntimesInstalled() then
    exit;

  Log('Auto-install .NET 8 runtimes requested because a required shared framework is missing.');

  if not IsSharedFramework8Installed(AspNetCoreSharedFrameworkName) then
  begin
    Log('Microsoft.AspNetCore.App 8.x is missing, start silent install.');
    if not InstallBundledRuntime('{#AspNetCoreRuntimeInstallerFileName}', 'ASP.NET Core Runtime 8', NeedsRestart) then
    begin
      ShowRuntimeMissingPrompt();
      exit;
    end;
  end;

  if not IsSharedFramework8Installed(WindowsDesktopSharedFrameworkName) then
  begin
    Log('Microsoft.WindowsDesktop.App 8.x is missing, start silent install.');
    if not InstallBundledRuntime('{#DotNetDesktopRuntimeInstallerFileName}', '.NET Desktop Runtime 8', NeedsRestart) then
    begin
      ShowRuntimeMissingPrompt();
      exit;
    end;
  end;

  if not AreRequiredDotNetRuntimesInstalled() then
  begin
    Log('A required .NET 8 runtime is still missing after silent install, fallback to download prompt.');
    ShowRuntimeMissingPrompt();
    exit;
  end;

  Log('.NET 8 runtimes silent installation completed.');
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := InstallRequiredDotNetRuntimeIfNeeded(NeedsRestart);
  if Result <> '' then
    exit;

  Result := InstallBundledPawnIOIfNeeded(NeedsRestart);
end;

procedure OpenUrl(const Url: String);
var
  ResultCode: Integer;
begin
  ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

procedure ShowRuntimeMissingPrompt();
var
  TitleText: String;
  MessageText: String;
  DotNetUrl: String;
  ResultId: Integer;
begin
  if RuntimePromptShown then
    exit;

  RuntimePromptShown := True;
  DotNetUrl := '{#DotNetRuntimeDownloadUrl}';

  if IsChineseSystemLanguage() then
  begin
    TitleText := '缺少 .NET 8 运行环境';
    MessageText :=
      'XhMonitor 桌面应用需要 .NET 8 运行环境才能启动。' + #13#10 + #13#10 +
      '当前系统缺少以下共享框架中的至少一个：' + #13#10 +
      '- Microsoft.WindowsDesktop.App 8.x（WPF 桌面界面）' + #13#10 +
      '- Microsoft.AspNetCore.App 8.x（内嵌 Web 服务 / lhm-bridge）' + #13#10 + #13#10 +
      '请安装 ".NET Desktop Runtime 8" 与 "ASP.NET Core Runtime 8"（均含基础运行时），' + #13#10 +
      '或改用包含 self-contained 组件的 Full 安装包。' + #13#10 + #13#10 +
      '运行环境下载：' + #13#10 + DotNetUrl + #13#10 + #13#10 +
      '是否立即打开运行环境下载页面？';
  end
  else
  begin
    TitleText := 'Missing .NET 8 runtime';
    MessageText :=
      'The XhMonitor desktop application requires the .NET 8 runtime to start.' + #13#10 + #13#10 +
      'Your system is missing at least one of these shared frameworks:' + #13#10 +
      '- Microsoft.WindowsDesktop.App 8.x (WPF desktop UI)' + #13#10 +
      '- Microsoft.AspNetCore.App 8.x (embedded web server / lhm-bridge)' + #13#10 + #13#10 +
      'Install ".NET Desktop Runtime 8" and "ASP.NET Core Runtime 8" (both include the base runtime),' + #13#10 +
      'or use the Full installer with self-contained components.' + #13#10 + #13#10 +
      'Runtime download:' + #13#10 + DotNetUrl + #13#10 + #13#10 +
      'Open the runtime download page now?';
  end;

  ResultId := MsgBox(MessageText, mbInformation, MB_YESNO);
  if ResultId = IDYES then
  begin
    OpenUrl(DotNetUrl);
  end;
end;


// 安装升级前停止新旧组件（在安装目录确定后）
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
    // 强制终止可能残留的新旧进程（{#MyAppExeName}=XhMonitor.Desktop.exe，另补旧 Rust 进程名）
    KillProcess('{#MyAppServiceName}');
    KillProcess('{#MyAppExeName}');
    KillProcess('lhm-bridge.exe');
    KillProcess('xhm-desktop.exe');
    KillProcess('XhMonitor.Service.exe');
    StopWinRingDriver();
    Sleep(1000);
  end;

#if BuildType != "Full"
  // framework-dependent 版本：缺少运行时会导致 C# Desktop 无法启动，安装后立即提示。
  if CurStep = ssPostInstall then
  begin
    if not IsSelfContainedPackageInstalled() and not AreRequiredDotNetRuntimesInstalled() then
      ShowRuntimeMissingPrompt();
  end;
#endif
end;

// 卸载前停止新旧组件
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
  // 强制终止新旧进程（{#MyAppExeName}=XhMonitor.Desktop.exe，另补旧 Rust 进程名）
  KillProcess('{#MyAppServiceName}');
  KillProcess('{#MyAppExeName}');
  KillProcess('lhm-bridge.exe');
  KillProcess('xhm-desktop.exe');
  KillProcess('XhMonitor.Service.exe');
  StopWinRingDriver();
  Sleep(1000);
end;
