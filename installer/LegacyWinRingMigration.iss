// 仅迁移本程序旧安装中的 WinRing0；不停止驱动，不扫描或删除系统目录。
const
  LegacyServicesKey = 'SYSTEM\CurrentControlSet\Services';
  LegacyUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppId}_is1';
  LegacyStoppedState = 1;

type
  TLegacyHandle = INT_PTR;
  TLegacyFileInformation = record
    Attributes: LongWord;
    CreationTime: Int64;
    LastAccessTime: Int64;
    LastWriteTime: Int64;
    VolumeSerialNumber: LongWord;
    FileSizeHigh: LongWord;
    FileSizeLow: LongWord;
    NumberOfLinks: LongWord;
    FileIndexHigh: LongWord;
    FileIndexLow: LongWord;
  end;
  TLegacyServiceStatus = record
    ServiceType: LongWord;
    CurrentState: LongWord;
    ControlsAccepted: LongWord;
    Win32ExitCode: LongWord;
    ServiceSpecificExitCode: LongWord;
    CheckPoint: LongWord;
    WaitHint: LongWord;
  end;
  TLegacyDriver = record
    Name: String;
    ImagePath: String;
    ServiceType: LongWord;
  end;
  TLegacyDrivers = array of TLegacyDriver;

var
  LegacyCompletionNoticeShown: Boolean;

function LegacyCreateFile(FileName: String; Access, Share: LongWord; Security: INT_PTR;
  Creation, Flags: LongWord; Template: TLegacyHandle): TLegacyHandle;
  external 'CreateFileW@kernel32.dll stdcall';
function LegacyCloseHandle(Handle: TLegacyHandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function LegacyGetFinalPath(Handle: TLegacyHandle; Buffer: String; Capacity, Flags: LongWord): LongWord;
  external 'GetFinalPathNameByHandleW@kernel32.dll stdcall';
function LegacyGetFileInformation(Handle: TLegacyHandle; var Info: TLegacyFileInformation): Boolean;
  external 'GetFileInformationByHandle@kernel32.dll stdcall';
function LegacyGetFileAttributes(Path: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function LegacySetDisposition(Handle: TLegacyHandle; InfoClass: LongWord;
  var DeleteOnClose: Byte; Size: LongWord): Boolean;
  external 'SetFileInformationByHandle@kernel32.dll stdcall';
function LegacyExpandEnvironment(Source, Buffer: String; Capacity: LongWord): LongWord;
  external 'ExpandEnvironmentStringsW@kernel32.dll stdcall';
function LegacyOpenSCManager(Machine, Database: INT_PTR; Access: LongWord): TLegacyHandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function LegacyOpenService(Manager: TLegacyHandle; Name: String; Access: LongWord): TLegacyHandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function LegacyQueryServiceStatus(Service: TLegacyHandle; var Status: TLegacyServiceStatus): Boolean;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function LegacyDeleteService(Service: TLegacyHandle): Boolean;
  external 'DeleteService@advapi32.dll stdcall';
function LegacyCloseServiceHandle(Handle: TLegacyHandle): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';

procedure AddLegacyWarning(var Details: String; const MessageText: String);
begin
  Log('Legacy WinRing0: ' + MessageText);
  Details := Details + MessageText + #13#10;
end;

function LegacyKnownFiles(const AppRoot: String): TArrayOfString;
begin
  SetArrayLength(Result, 5);
  Result[0] := AddBackslash(AppRoot) + 'Service\tools\RyzenAdj\WinRing0x64.sys';
  Result[1] := AddBackslash(AppRoot) + 'Service\tools\RyzenAdj\WinRing0x64.dll';
  Result[2] := AddBackslash(AppRoot) + 'Service\lhm-bridge.sys';
  Result[3] := AddBackslash(AppRoot) + 'Service\XhMonitor.Service.sys';
  Result[4] := AddBackslash(AppRoot) + 'Service\LibreHardwareMonitorLib.sys';
end;

function LegacySamePath(const Left, Right: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(ExpandFileName(Left)),
    RemoveBackslashUnlessRoot(ExpandFileName(Right))) = 0;
end;

function IsRegisteredLegacyRoot(const AppRoot, RegisteredRoot: String): Boolean;
var
  Root: String;
begin
  Result := False;
  if (Length(AppRoot) < 4) or (Length(RegisteredRoot) < 4) then
    exit;
  // 拒绝相对目录、UNC、盘符根目录和 Windows 目录。
  if (AppRoot[2] <> ':') or (AppRoot[3] <> '\') or
     (RegisteredRoot[2] <> ':') or (RegisteredRoot[3] <> '\') then
    exit;
  Root := AddBackslash(ExpandFileName(AppRoot));
  if Length(Root) <= 3 then
    exit;
  if CompareText(Copy(Root, 1, Length(AddBackslash(ExpandConstant('{win}')))),
    AddBackslash(ExpandConstant('{win}'))) = 0 then
    exit;
  if LegacySamePath(AppRoot, ExpandConstant('{pf32}')) then
    exit;
  if IsWin64 and LegacySamePath(AppRoot, ExpandConstant('{pf64}')) then
    exit;
  Result := LegacySamePath(AppRoot, RegisteredRoot);
end;

function ReadRegisteredLegacyRoot(const AppRoot: String): String;
var
  PreviousRoot: String;
begin
  Result := '';
  // 旧安装器要求管理员权限；不把可由普通用户写入的 HKCU 当作归属证明。
  if IsWin64 and RegQueryStringValue(HKLM64, LegacyUninstallKey,
    'Inno Setup: App Path', PreviousRoot) and IsRegisteredLegacyRoot(AppRoot, PreviousRoot) then
    Result := PreviousRoot
  else if RegQueryStringValue(HKLM32, LegacyUninstallKey,
    'Inno Setup: App Path', PreviousRoot) and IsRegisteredLegacyRoot(AppRoot, PreviousRoot) then
    Result := PreviousRoot;
end;

function LegacyFinalPath(Handle: TLegacyHandle; var Path: String): Boolean;
var
  Count: LongWord;
begin
  Path := StringOfChar(#0, 32768);
  Count := LegacyGetFinalPath(Handle, Path, Length(Path), 0);
  Result := (Count > 0) and (Count < LongWord(Length(Path)));
  if not Result then
    exit;
  SetLength(Path, Count);
  if Copy(Path, 1, 4) = '\\?\' then
    Delete(Path, 1, 4);
end;

function LegacyCanonicalDirectory(const Path: String): Boolean;
var
  Handle: TLegacyHandle;
  FinalPath: String;
begin
  Result := False;
  Handle := LegacyCreateFile(Path, 0, 7, 0, 3, $02000000, 0);
  if Handle = -1 then
    exit;
  try
    Result := LegacyFinalPath(Handle, FinalPath) and LegacySamePath(Path, FinalPath);
  finally
    LegacyCloseHandle(Handle);
  end;
end;

function NormalizeLegacyDriverPath(const RawPath: String; var Path, FailureReason: String): Boolean;
var
  Buffer, SystemPath, FinalPath: String;
  Count: LongWord;
  Handle: TLegacyHandle;
  ErrorCode: LongWord;
begin
  Result := False;
  FailureReason := '';
  Path := Trim(RawPath);
  if (Length(Path) >= 2) and (Path[1] = '"') and (Path[Length(Path)] = '"') then
    Path := Copy(Path, 2, Length(Path) - 2);
  if Pos('"', Path) > 0 then
  begin
    FailureReason := 'ImagePath contains unmatched quotes or arguments';
    exit;
  end;
  Buffer := StringOfChar(#0, 32768);
  Count := LegacyExpandEnvironment(Path, Buffer, Length(Buffer));
  if (Count = 0) or (Count > LongWord(Length(Buffer))) then
  begin
    FailureReason := 'ExpandEnvironmentStringsW failed or exceeded the buffer limit (error ' + IntToStr(DLLGetLastError) + ')';
    exit;
  end;
  SetLength(Buffer, Count - 1);
  Path := Buffer;
  if CompareText(Copy(Path, 1, 12), '\SystemRoot\') = 0 then
    Path := AddBackslash(ExpandConstant('{win}')) + Copy(Path, 13, Length(Path))
  else if CompareText(Copy(Path, 1, 9), 'System32\') = 0 then
    Path := AddBackslash(ExpandConstant('{win}')) + Path;
  if Copy(Path, 1, 4) = '\??\' then
    Delete(Path, 1, 4);
  if Copy(Path, 1, 4) = '\\?\' then
    Delete(Path, 1, 4);
  if (Length(Path) < 3) or (Path[2] <> ':') or (Path[3] <> '\') then
  begin
    FailureReason := 'Unsupported expanded driver ImagePath: "' + Path + '"';
    exit;
  end;
  Path := ExpandFileName(Path);
  // 原生 DLL 调用不会自动绕过 32 位安装器的 System32 重定向。
  Buffer := Path;
  SystemPath := AddBackslash(ExpandConstant('{win}\System32'));
  if IsWin64 and (CompareText(Copy(Buffer, 1, Length(SystemPath)), SystemPath) = 0) then
    Buffer := AddBackslash(ExpandConstant('{sysnative}')) + Copy(Buffer, Length(SystemPath) + 1, Length(Buffer));
  Handle := LegacyCreateFile(Buffer, 0, 7, 0, 3, 0, 0);
  if Handle = -1 then
  begin
    ErrorCode := DLLGetLastError;
    // 已不存在的映像不会引用当前仍存在的候选文件；其他错误均拒绝清理。
    Result := (ErrorCode = 2) or (ErrorCode = 3);
    if not Result then
      FailureReason := 'CreateFileW failed (error ' + IntToStr(ErrorCode) + '); openedPath="' + Buffer + '"';
    exit;
  end;
  try
    Result := LegacyFinalPath(Handle, FinalPath);
    if Result then
      Path := FinalPath
    else
      FailureReason := 'GetFinalPathNameByHandleW could not verify the image (error ' + IntToStr(DLLGetLastError) + ')';
  finally
    LegacyCloseHandle(Handle);
  end;
end;

function ReadLegacyDrivers(var Drivers: TLegacyDrivers; var Details: String): Boolean;
var
  Names: TArrayOfString;
  RootKey, ServiceType: LongWord;
  Index, Count: Integer;
  Key, RawPath, ImagePath, FailureReason: String;
begin
  Result := False;
  SetArrayLength(Drivers, 0);
  RootKey := HKLM32;
  if IsWin64 then
    RootKey := HKLM64;
  if not RegGetSubkeyNames(RootKey, LegacyServicesKey, Names) then
  begin
    AddLegacyWarning(Details, 'Cannot enumerate driver services; all legacy files were retained.');
    exit;
  end;
  for Index := 0 to GetArrayLength(Names) - 1 do
  begin
    Key := LegacyServicesKey + '\' + Names[Index];
    if not RegQueryDWordValue(RootKey, Key, 'Type', ServiceType) then
    begin
      // 性能计数器等非服务项没有 Type；可读取且确认缺失时才跳过。
      if RegKeyExists(RootKey, Key) and not RegValueExists(RootKey, Key, 'Type') then
        continue;
      AddLegacyWarning(Details, 'Cannot read service type: ' + Names[Index] + '; all legacy files were retained.');
      exit;
    end;
    if (ServiceType and 3) <> 0 then
    begin
      if not RegQueryStringValue(RootKey, Key, 'ImagePath', RawPath) then
      begin
        if RegValueExists(RootKey, Key, 'ImagePath') then
        begin
          AddLegacyWarning(Details, 'Cannot read driver image: ' + Names[Index] + '; all legacy files were retained.');
          exit;
        end;
        // Windows 允许内核服务省略 ImagePath，此时使用系统驱动目录。
        RawPath := ExpandConstant('{win}\System32\drivers\') + Names[Index] + '.sys';
      end;
      if not NormalizeLegacyDriverPath(RawPath, ImagePath, FailureReason) then
      begin
        AddLegacyWarning(Details, 'Cannot resolve driver image: ' + Names[Index] + '; image="' + RawPath +
          '"; reason=' + FailureReason + '; all legacy files were retained.');
        exit;
      end;
      Count := GetArrayLength(Drivers);
      SetArrayLength(Drivers, Count + 1);
      Drivers[Count].Name := Names[Index];
      Drivers[Count].ImagePath := ImagePath;
      Drivers[Count].ServiceType := ServiceType;
    end;
  end;
  Result := True;
end;

function IsPrivateLegacyServiceName(const Name: String): Boolean;
begin
  Result := (CompareText(Name, 'R0lhm-bridge') = 0) or
    (CompareText(Name, 'R0XhMonitor_Service') = 0);
end;

function IsOwnedLegacyService(const AppRoot: String; const Driver: TLegacyDriver): Boolean;
var
  Files: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  if (Driver.ServiceType <> 1) or not IsPrivateLegacyServiceName(Driver.Name) then
    exit;
  Files := LegacyKnownFiles(AppRoot);
  for Index := 2 to 4 do
    if LegacySamePath(Driver.ImagePath, Files[Index]) then
      Result := LegacyCanonicalDirectory(ExtractFileDir(Files[Index]));
end;

function CanDeleteLegacyService(const AppRoot, RegisteredRoot: String;
  const Driver: TLegacyDriver; State: LongWord): Boolean;
begin
  Result := IsRegisteredLegacyRoot(AppRoot, RegisteredRoot) and
    (State = LegacyStoppedState) and IsOwnedLegacyService(AppRoot, Driver);
end;

function RemoveStoppedLegacyService(const AppRoot, RegisteredRoot: String;
  const Driver: TLegacyDriver; var Details: String): Boolean;
var
  Manager, Service: TLegacyHandle;
  Status: TLegacyServiceStatus;
begin
  Result := False;
  if not IsRegisteredLegacyRoot(AppRoot, RegisteredRoot) or not IsOwnedLegacyService(AppRoot, Driver) then
    exit;
  Manager := LegacyOpenSCManager(0, 0, 1);
  if Manager = 0 then
  begin
    AddLegacyWarning(Details, 'Cannot open service manager; retained ' + Driver.Name);
    exit;
  end;
  try
    Service := LegacyOpenService(Manager, Driver.Name, $00010004);
    if Service = 0 then
    begin
      AddLegacyWarning(Details, 'Cannot open legacy service; retained ' + Driver.Name);
      exit;
    end;
    try
      if not LegacyQueryServiceStatus(Service, Status) then
        AddLegacyWarning(Details, 'Cannot query legacy service; retained ' + Driver.Name)
      else if Status.ServiceType <> 1 then
        AddLegacyWarning(Details, 'Service is not a kernel driver; retained ' + Driver.Name)
      else if not CanDeleteLegacyService(AppRoot, RegisteredRoot, Driver, Status.CurrentState) then
        AddLegacyWarning(Details, 'Driver is not stopped; retained ' + Driver.Name + '. Restart Windows and rerun setup to retry. No driver was stopped.')
      else if not LegacyDeleteService(Service) then
        AddLegacyWarning(Details, 'Cannot delete stopped legacy service; retained ' + Driver.Name)
      else
      begin
        Log('Legacy WinRing0: requested deletion of stopped, application-owned service ' + Driver.Name);
        Result := True;
      end;
    finally
      LegacyCloseServiceHandle(Service);
    end;
  finally
    LegacyCloseServiceHandle(Manager);
  end;
end;

function DeleteOwnedLegacyFile(const Path: String; var Details: String): Boolean;
var
  Handle: TLegacyHandle;
  Info: TLegacyFileInformation;
  FinalPath: String;
  DeleteOnClose: Byte;
begin
  Result := False;
  // 独占打开并按同一句柄删除，避免检查后替换路径；不排队重启删除。
  Handle := LegacyCreateFile(Path, $00010080, 0, 0, 3, $00200000, 0);
  if Handle = -1 then
  begin
    AddLegacyWarning(Details, 'File is inaccessible or in use; retained ' + Path);
    exit;
  end;
  try
    if not LegacyGetFileInformation(Handle, Info) or
       ((Info.Attributes and $00000410) <> 0) or (Info.NumberOfLinks <> 1) or
       not LegacyFinalPath(Handle, FinalPath) or not LegacySamePath(Path, FinalPath) then
    begin
      AddLegacyWarning(Details, 'File ownership is ambiguous (link or redirected path); retained ' + Path);
      exit;
    end;
    DeleteOnClose := 1;
    Result := LegacySetDisposition(Handle, 4, DeleteOnClose, 1);
    if Result then
      Log('Legacy WinRing0: deleted application-owned file ' + Path)
    else
      AddLegacyWarning(Details, 'Cannot delete legacy file; retained ' + Path);
  finally
    LegacyCloseHandle(Handle);
  end;
end;

function LegacyCandidateExists(const Path: String; var Details: String): Boolean;
var
  ErrorCode: LongWord;
begin
  Result := LegacyGetFileAttributes(Path) <> $FFFFFFFF;
  if not Result then
  begin
    ErrorCode := DLLGetLastError;
    // FileExists 会把拒绝访问、不受信任的装入点等错误误当作文件不存在。
    if (ErrorCode <> 2) and (ErrorCode <> 3) then
      AddLegacyWarning(Details, 'Cannot inspect legacy path (error ' + IntToStr(ErrorCode) + '); retained ' + Path);
  end;
end;

procedure CleanupLegacyWinRingFiles(const AppRoot, RegisteredRoot: String;
  const Drivers: TLegacyDrivers; InventoryComplete: Boolean; var Details: String);
var
  Files: TArrayOfString;
  FileIndex, DriverIndex: Integer;
  Referenced: Boolean;
begin
  Files := LegacyKnownFiles(AppRoot);
  for FileIndex := 0 to GetArrayLength(Files) - 1 do
    if LegacyCandidateExists(Files[FileIndex], Details) then
    begin
      if not IsRegisteredLegacyRoot(AppRoot, RegisteredRoot) or not InventoryComplete then
        AddLegacyWarning(Details, 'Installation ownership or driver inventory is unverified; retained ' + Files[FileIndex])
      else
      begin
        Referenced := False;
        for DriverIndex := 0 to GetArrayLength(Drivers) - 1 do
          if LegacySamePath(Drivers[DriverIndex].ImagePath, Files[FileIndex]) then
            Referenced := True;
        if Referenced then
          AddLegacyWarning(Details, 'A registered driver still references this file; retained ' + Files[FileIndex])
        else
          DeleteOwnedLegacyFile(Files[FileIndex], Details);
      end;
    end;
end;

procedure MigrateLegacyWinRing(const AppRoot: String; var Details: String);
var
  RegisteredRoot: String;
  Drivers: TLegacyDrivers;
  Index: Integer;
  InventoryComplete, DeletedService: Boolean;
begin
  RegisteredRoot := ReadRegisteredLegacyRoot(AppRoot);
  InventoryComplete := False;
  SetArrayLength(Drivers, 0);
  if IsRegisteredLegacyRoot(AppRoot, RegisteredRoot) then
  begin
    InventoryComplete := ReadLegacyDrivers(Drivers, Details);
    if InventoryComplete then
    begin
      DeletedService := False;
      for Index := 0 to GetArrayLength(Drivers) - 1 do
      begin
        if IsOwnedLegacyService(AppRoot, Drivers[Index]) then
        begin
          if RemoveStoppedLegacyService(AppRoot, RegisteredRoot, Drivers[Index], Details) then
            DeletedService := True;
        end
        else if IsPrivateLegacyServiceName(Drivers[Index].Name) or
          (Pos('winring', Lowercase(Drivers[Index].Name)) > 0) or
          (Pos('winring', Lowercase(Drivers[Index].ImagePath)) > 0) or
          (CompareText(Drivers[Index].Name, 'R0LibreHardwareMonitorLib') = 0) then
          AddLegacyWarning(Details, 'Shared or unowned driver service was not changed: ' + Drivers[Index].Name);
      end;
      // DeleteService 可能只标记删除；重新读取后仍有引用的文件必须保留。
      if DeletedService then
        InventoryComplete := ReadLegacyDrivers(Drivers, Details);
    end;
  end;
  CleanupLegacyWinRingFiles(AppRoot, RegisteredRoot, Drivers, InventoryComplete, Details);
end;

procedure ShowLegacyWinRingCompletionNotice(const Notice: String);
var
  RequiredTop, Shift: Integer;
begin
  if LegacyCompletionNoticeShown or (Notice = '') then
    exit;
  // 沿用原生成功页；诊断详情只进入日志，不再弹出阻断对话框。
  WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + Notice;
  WizardForm.FinishedLabel.AdjustHeight;
  RequiredTop := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(12);
  if RequiredTop > WizardForm.RunList.Top then
  begin
    Shift := RequiredTop - WizardForm.RunList.Top;
    WizardForm.RunList.Top := RequiredTop;
    WizardForm.RunList.Height := WizardForm.RunList.Height - Shift;
  end;
  LegacyCompletionNoticeShown := True;
end;
