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
#define DotNetDesktopRuntimeInstallerFileName "windowsdesktop-runtime-8.0.23-win-x64.exe"
#define DotNetDesktopRuntimeDownloadUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.23/windowsdesktop-runtime-8.0.23-win-x64.exe"

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
english.AutoInstallDotNetRuntime=Auto install .NET runtime
english.AutoInstallDotNetRuntimeHint=Install .NET Desktop Runtime 8 silently if missing
; 中文消息（启用中文语言后生效）
; chinesesimplified.CreateDesktopIcon=创建桌面快捷方式(&D)
; chinesesimplified.CreateQuickLaunchIcon=创建快速启动栏快捷方式(&Q)
; chinesesimplified.LaunchProgram=运行 %1
; chinesesimplified.AssocFileExtension=将 %1 与 %2 文件扩展名关联
; chinesesimplified.StartupTask=开机自动启动
; chinesesimplified.SystemSettings=系统设置:
chinesesimplified.SystemSettings=系统设置：
chinesesimplified.AutoInstallDotNetRuntime=自动安装 .NET 运行环境
chinesesimplified.AutoInstallDotNetRuntimeHint=若缺少 .NET Desktop Runtime 8，则静默安装后继续

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
; Name: "startupicon"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:SystemSettings}"; Flags: unchecked
#ifdef IsLiteBuild
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

#ifdef IsLiteBuild
; 轻量版：将 .NET Desktop Runtime 安装包打入安装器（不落盘到应用目录，仅用于安装阶段自动静默安装）
Source: "..\tools\RuntimePkg\{#DotNetDesktopRuntimeInstallerFileName}"; DestDir: "{tmp}"; Flags: dontcopy skipifsourcedoesntexist
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
  DotNetDesktopRuntimeSilentArgs = '/install /quiet /norestart';

var
  RuntimePromptShown: Boolean;

// 强制终止进程
procedure KillProcess(ProcessName: String);
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM ' + ProcessName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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

function HasDesktopRuntime8AtBase(const BasePath: String): Boolean;
var
  SearchPattern: String;
  FindRec: TFindRec;
begin
  // 如果连 dotnet.exe 都不存在，则直接判定未安装（避免误扫）
  if not FileExists(AddBackslash(BasePath) + 'dotnet.exe') then
  begin
    Result := False;
    exit;
  end;

  SearchPattern := AddBackslash(BasePath) + 'shared\Microsoft.WindowsDesktop.App\8.*';

  // 通过共享框架目录判断是否已安装 Desktop Runtime 8.x
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

function IsDotNetDesktopRuntime8Installed(): Boolean;
var
  InstallLocation: String;
begin
  // 先用注册表的 x64 安装路径（支持自定义目录）
  if TryGetDotNetInstallLocationX64(InstallLocation) then
  begin
    if HasDesktopRuntime8AtBase(InstallLocation) then
    begin
      Result := True;
      exit;
    end;
  end;

  // 再检查常见默认路径。注意：安装器可能为 32 位，此时 {pf} 会指向 Program Files (x86)，因此需要显式检查 {pf64}。
  if IsWin64 then
  begin
    if HasDesktopRuntime8AtBase(ExpandConstant('{pf64}\dotnet')) then
    begin
      Result := True;
      exit;
    end;

    if HasDesktopRuntime8AtBase(ExpandConstant('{pf32}\dotnet')) then
    begin
      Result := True;
      exit;
    end;
  end
  else
  begin
    if HasDesktopRuntime8AtBase(ExpandConstant('{pf}\dotnet')) then
    begin
      Result := True;
      exit;
    end;
  end;

  // 兜底：有些环境可能为“用户级 dotnet”安装
  if HasDesktopRuntime8AtBase(ExpandConstant('{localappdata}\Microsoft\dotnet')) then
  begin
    Result := True;
    exit;
  end;

  Result := False;
end;

function IsSelfContainedPackageInstalled(): Boolean;
begin
  // Self-Contained 版本会包含 hostfxr.dll（不依赖系统安装的 .NET Desktop Runtime）
  Result := FileExists(ExpandConstant('{app}\Desktop\hostfxr.dll'));
end;

procedure ShowRuntimeMissingPrompt(); forward;

function ShouldAutoInstallDotNetRuntime(): Boolean;
begin
#ifdef IsLiteBuild
  Result := WizardIsTaskSelected('autoinstalldotnetruntime');
#else
  Result := False;
#endif
end;

function InstallDotNetDesktopRuntimeIfNeeded(var NeedsRestart: Boolean): String;
var
  InstallerPath: String;
  ResultCode: Integer;
begin
  Result := '';

#ifdef IsLiteBuild
  if not ShouldAutoInstallDotNetRuntime() then
    exit;

  if IsDotNetDesktopRuntime8Installed() then
    exit;

  Log('Auto-install .NET Desktop Runtime 8 requested and runtime is missing, start silent install.');

  try
    ExtractTemporaryFile('{#DotNetDesktopRuntimeInstallerFileName}');
  except
    Log('Failed to extract bundled .NET Desktop Runtime installer, fallback to download prompt.');
    ShowRuntimeMissingPrompt();
    exit;
  end;

  InstallerPath := ExpandConstant('{tmp}\{#DotNetDesktopRuntimeInstallerFileName}');
  if not FileExists(InstallerPath) then
  begin
    Log('Bundled .NET Desktop Runtime installer not found, fallback to download prompt.');
    ShowRuntimeMissingPrompt();
    exit;
  end;

  if not Exec(InstallerPath, DotNetDesktopRuntimeSilentArgs, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('Failed to execute bundled .NET Desktop Runtime installer, fallback to download prompt.');
    ShowRuntimeMissingPrompt();
    exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1641) then
  begin
    Log('Bundled .NET Desktop Runtime installer exit code: ' + IntToStr(ResultCode) + ', fallback to download prompt.');
    ShowRuntimeMissingPrompt();
    exit;
  end;

  if (ResultCode = 3010) or (ResultCode = 1641) then
    NeedsRestart := True;

  Sleep(1500);

  if not IsDotNetDesktopRuntime8Installed() then
  begin
    Log('.NET Desktop Runtime still missing after silent install, fallback to download prompt.');
    ShowRuntimeMissingPrompt();
    exit;
  end;

  Log('.NET Desktop Runtime 8 silent installation completed.');
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := InstallDotNetDesktopRuntimeIfNeeded(NeedsRestart);
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
  FullInstallerUrl: String;
  ResultId: Integer;
begin
  if RuntimePromptShown then
    exit;

  RuntimePromptShown := True;
  DotNetUrl := '{#DotNetDesktopRuntimeDownloadUrl}';
  FullInstallerUrl := '{#MyAppURL}/releases';

  if IsChineseSystemLanguage() then
  begin
    TitleText := '缺少运行环境';
    MessageText :=
      '要运行此应用程序，你必须安装或更新 .NET。' + #13#10 + #13#10 +
      '当前安装包不带运行环境（.NET Desktop Runtime）。' + #13#10 +
      '检测到你的系统缺少相关运行环境，无法运行 XhMonitor。' + #13#10 + #13#10 +
      '你需要安装运行环境，或者下载包含运行环境的完整安装包。' + #13#10 + #13#10 +
      '运行环境下载：' + #13#10 + DotNetUrl + #13#10 + #13#10 +
      '完整安装包下载：' + #13#10 + FullInstallerUrl + #13#10 + #13#10 +
      '是否立即打开运行环境下载页面？';
  end
  else
  begin
    TitleText := 'Missing runtime';
    MessageText :=
      'You must install or update .NET to run this application.' + #13#10 + #13#10 +
      'This installer does not include the runtime (.NET Desktop Runtime).' + #13#10 +
      'Your system is missing the required runtime, so XhMonitor cannot run.' + #13#10 + #13#10 +
      'Install the runtime, or download the full installer that includes the runtime (self-contained).' + #13#10 + #13#10 +
      'Runtime download:' + #13#10 + DotNetUrl + #13#10 + #13#10 +
      'Full installer download:' + #13#10 + FullInstallerUrl + #13#10 + #13#10 +
      'Open the runtime download page now?';
  end;

  ResultId := MsgBox(MessageText, mbInformation, MB_YESNO);
  if ResultId = IDYES then
  begin
    OpenUrl(DotNetUrl);
  end;
end;

function CanLaunchDesktopApp(): Boolean;
begin
  if IsSelfContainedPackageInstalled() then
  begin
    Result := True;
    exit;
  end;

  if IsDotNetDesktopRuntime8Installed() then
  begin
    Result := True;
    exit;
  end;

  ShowRuntimeMissingPrompt();
  Result := False;
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

  // 精简版（不带运行环境）在运行前给出明确提示，避免弹出 .NET host 默认错误提示
  if CurStep = ssPostInstall then
  begin
    if (not IsSelfContainedPackageInstalled()) and (not IsDotNetDesktopRuntime8Installed()) then
    begin
      ShowRuntimeMissingPrompt();
    end;
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
