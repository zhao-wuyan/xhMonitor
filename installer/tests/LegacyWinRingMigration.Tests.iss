#define MyAppId "{872408EB-D2E4-40EB-BDB6-5386C11C9348}"

[Setup]
AppId={{872408EB-D2E4-40EB-BDB6-5386C11C9348}
AppName=Legacy WinRing Migration Behavior Tests
AppVersion=1.0
PrivilegesRequired=lowest
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
DisableWelcomePage=yes
DisableReadyPage=yes
SetupLogging=yes
OutputBaseFilename=LegacyWinRingMigration.Tests

[Code]
#include "../LegacyWinRingMigration.iss"

const
  FixtureContents = 'Legacy migration test fixture; not a driver.';

var
  FixtureBase: String;
  PassedAssertions: Integer;

function TestCreateHardLink(NewName, ExistingName: String; Security: INT_PTR): Boolean;
  external 'CreateHardLinkW@kernel32.dll stdcall';
function TestRemoveDirectory(Path: String): Boolean;
  external 'RemoveDirectoryW@kernel32.dll stdcall';
function TestSetEnvironmentVariable(Name, Value: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

procedure Assert(Condition: Boolean; const MessageText: String);
begin
  if not Condition then
  begin
    Log('FAIL: ' + MessageText);
    RaiseException(MessageText);
  end;
  PassedAssertions := PassedAssertions + 1;
  Log('PASS: ' + MessageText);
end;

procedure WriteFixture(const Path: String);
begin
  Assert(ForceDirectories(ExtractFileDir(Path)), 'Create fixture directory: ' + Path);
  Assert(SaveStringToFile(Path, FixtureContents, False), 'Write fixture: ' + Path);
end;

procedure AssertPreserved(const Path, Reason: String);
var
  Contents: AnsiString;
begin
  Assert(FileExists(Path), Reason + ': file exists');
  Assert(LoadStringFromFile(Path, Contents), Reason + ': file readable');
  Assert(Contents = FixtureContents, Reason + ': contents unchanged');
end;

function CreateLegacyFixture(const CaseName: String): String;
begin
  Result := AddBackslash(FixtureBase) + CaseName;
  WriteFixture(Result + '\Service\tools\RyzenAdj\WinRing0x64.sys');
  WriteFixture(Result + '\Service\tools\RyzenAdj\WinRing0x64.dll');
  WriteFixture(Result + '\Service\lhm-bridge.sys');
  WriteFixture(Result + '\Service\XhMonitor.Service.sys');
  WriteFixture(Result + '\Service\LibreHardwareMonitorLib.sys');
end;

procedure AssertAllPreserved(const Root, Reason: String);
begin
  AssertPreserved(Root + '\Service\tools\RyzenAdj\WinRing0x64.sys', Reason);
  AssertPreserved(Root + '\Service\tools\RyzenAdj\WinRing0x64.dll', Reason);
  AssertPreserved(Root + '\Service\lhm-bridge.sys', Reason);
  AssertPreserved(Root + '\Service\XhMonitor.Service.sys', Reason);
  AssertPreserved(Root + '\Service\LibreHardwareMonitorLib.sys', Reason);
end;

procedure TestNormalDeletion;
var
  Root, Details: String;
  Drivers: TLegacyDrivers;
begin
  Root := CreateLegacyFixture('normal');
  SetArrayLength(Drivers, 0);
  Details := '';
  CleanupLegacyWinRingFiles(Root, Root, Drivers, True, Details);
  Assert(not FileExists(Root + '\Service\tools\RyzenAdj\WinRing0x64.sys'), 'Delete old RyzenAdj SYS');
  Assert(not FileExists(Root + '\Service\tools\RyzenAdj\WinRing0x64.dll'), 'Delete old RyzenAdj DLL');
  Assert(not FileExists(Root + '\Service\lhm-bridge.sys'), 'Delete old bridge SYS');
  Assert(not FileExists(Root + '\Service\XhMonitor.Service.sys'), 'Delete old service SYS');
  Assert(not FileExists(Root + '\Service\LibreHardwareMonitorLib.sys'), 'Delete old LHM SYS');
  Assert(Details = '', 'Normal deletion has no warnings');
end;

procedure TestUnverifiedRootsAndInventory;
var
  Root, OtherRoot, Details: String;
  Drivers: TLegacyDrivers;
begin
  SetArrayLength(Drivers, 0);
  Root := CreateLegacyFixture('unregistered');
  Details := '';
  CleanupLegacyWinRingFiles(Root, '', Drivers, True, Details);
  AssertAllPreserved(Root, 'Unregistered installation');
  Assert(Details <> '', 'Unregistered installation reports retention');

  Root := CreateLegacyFixture('mismatched');
  OtherRoot := CreateLegacyFixture('different-installation');
  Details := '';
  CleanupLegacyWinRingFiles(Root, OtherRoot, Drivers, True, Details);
  AssertAllPreserved(Root, 'Mismatched registered root');
  AssertAllPreserved(OtherRoot, 'Other installation untouched');

  Root := CreateLegacyFixture('adjacent');
  OtherRoot := CreateLegacyFixture('adjacent-extra');
  Details := '';
  CleanupLegacyWinRingFiles(OtherRoot, Root, Drivers, True, Details);
  AssertAllPreserved(OtherRoot, 'Adjacent root is not a child or exact match');
  AssertAllPreserved(Root, 'Registered adjacent installation untouched');

  Root := CreateLegacyFixture('incomplete-inventory');
  Details := '';
  CleanupLegacyWinRingFiles(Root, Root, Drivers, False, Details);
  AssertAllPreserved(Root, 'Incomplete driver snapshot');
  Assert(Details <> '', 'Incomplete snapshot reports retention');
end;

procedure TestReferencedAndUnknownFiles;
var
  Root, Details: String;
  Drivers: TLegacyDrivers;
  Files: TArrayOfString;
  Index: Integer;
begin
  Root := CreateLegacyFixture('referenced');
  Files := LegacyKnownFiles(Root);
  SetArrayLength(Drivers, GetArrayLength(Files));
  for Index := 0 to GetArrayLength(Files) - 1 do
  begin
    Drivers[Index].Name := 'SyntheticReference' + IntToStr(Index);
    Drivers[Index].ImagePath := Files[Index];
  end;
  Details := '';
  CleanupLegacyWinRingFiles(Root, Root, Drivers, True, Details);
  AssertAllPreserved(Root, 'Any service reference prevents deletion');
  Assert(Details <> '', 'Service references report retention');

  Root := CreateLegacyFixture('unknown-files');
  WriteFixture(Root + '\Service\PawnIO.sys');
  WriteFixture(Root + '\Service\unknown.sys');
  WriteFixture(Root + '\Service\tools\RyzenAdj\WinRing0x64.sys.extra');
  SetArrayLength(Drivers, 0);
  Details := '';
  CleanupLegacyWinRingFiles(Root, Root, Drivers, True, Details);
  AssertPreserved(Root + '\Service\PawnIO.sys', 'PawnIO is not a legacy candidate');
  AssertPreserved(Root + '\Service\unknown.sys', 'Unknown file is not a legacy candidate');
  AssertPreserved(Root + '\Service\tools\RyzenAdj\WinRing0x64.sys.extra', 'Near-match filename is not a candidate');
end;

procedure TestOccupiedFile;
var
  Root, Path, Details: String;
  Handle: TLegacyHandle;
  Drivers: TLegacyDrivers;
begin
  Root := CreateLegacyFixture('occupied');
  Path := Root + '\Service\lhm-bridge.sys';
  Handle := LegacyCreateFile(Path, $80000000, 0, 0, 3, 0, 0);
  Assert(Handle <> -1, 'Exclusively open occupied fixture');
  try
    SetArrayLength(Drivers, 0);
    Details := '';
    CleanupLegacyWinRingFiles(Root, Root, Drivers, True, Details);
    Assert(FileExists(Path), 'Occupied legacy file is retained');
    Assert(Details <> '', 'Occupied legacy file reports retention');
  finally
    Assert(LegacyCloseHandle(Handle), 'Release occupied fixture');
  end;
  AssertPreserved(Path, 'Occupied file unchanged after releasing handle');
end;

procedure TestHardLink;
var
  Root, Path, AliasPath, Details: String;
  Drivers: TLegacyDrivers;
begin
  Root := CreateLegacyFixture('hardlink');
  Path := Root + '\Service\lhm-bridge.sys';
  AliasPath := FixtureBase + '\hardlink-alias.sys';
  Assert(TestCreateHardLink(AliasPath, Path, 0), 'Create real hardlink; failure is not skipped');
  SetArrayLength(Drivers, 0);
  Details := '';
  CleanupLegacyWinRingFiles(Root, Root, Drivers, True, Details);
  AssertPreserved(Path, 'Hardlinked legacy candidate retained');
  AssertPreserved(AliasPath, 'Hardlink alias retained');
  Assert(Details <> '', 'Hardlink reports ambiguous ownership');
end;

procedure TestDirectoryJunction;
var
  Root, ExternalRoot, Junction, Details: String;
  ExitCode: Integer;
  Drivers: TLegacyDrivers;
  LinkHandle: TLegacyHandle;
  Info: TLegacyFileInformation;
begin
  Root := FixtureBase + '\junction-installation';
  ExternalRoot := CreateLegacyFixture('junction-external');
  Assert(ForceDirectories(Root), 'Create junction installation root');
  Junction := Root + '\Service';
  Assert(Exec(ExpandConstant('{cmd}'), '/D /C mklink /J "' + Junction + '" "' +
    ExternalRoot + '\Service"', FixtureBase, SW_HIDE, ewWaitUntilTerminated, ExitCode),
    'Launch mklink for self-created fixture');
  Assert(ExitCode = 0, 'Create directory junction; failure is not skipped');
  try
    // 系统可能直接拒绝遍历不受信任的装入点；仍需证明创建的是真实 junction。
    LinkHandle := LegacyCreateFile(Junction, 0, 7, 0, 3, $02200000, 0);
    Assert(LinkHandle <> -1, 'Open junction itself without following it');
    try
      Assert(LegacyGetFileInformation(LinkHandle, Info), 'Read junction metadata');
      Assert((Info.Attributes and $400) <> 0, 'Fixture is a real reparse point');
    finally
      Assert(LegacyCloseHandle(LinkHandle), 'Close junction metadata handle');
    end;
    SetArrayLength(Drivers, 0);
    Details := '';
    CleanupLegacyWinRingFiles(Root, Root, Drivers, True, Details);
    AssertAllPreserved(ExternalRoot, 'Junction target is never deleted');
    Assert(Details <> '', 'Junction reports ambiguous ownership');
  finally
    // 只移除 junction 本身，之后由 Inno 清理临时夹具目录。
    Assert(TestRemoveDirectory(Junction), 'Remove fixture junction without traversing target');
  end;
  AssertAllPreserved(ExternalRoot, 'External fixture survives junction removal');
end;

procedure TestServicePolicy;
var
  Root, OtherRoot: String;
  Driver: TLegacyDriver;
  Names, Files: TArrayOfString;
  NameIndex, FileIndex, State: Integer;
begin
  Root := CreateLegacyFixture('service-policy');
  OtherRoot := CreateLegacyFixture('service-policy-other');
  SetArrayLength(Names, 2);
  Names[0] := 'R0lhm-bridge';
  Names[1] := 'R0XhMonitor_Service';
  Files := LegacyKnownFiles(Root);
  Driver.ServiceType := 1;
  for NameIndex := 0 to 1 do
  begin
    Driver.Name := Names[NameIndex];
    for FileIndex := 2 to 4 do
    begin
      Driver.ImagePath := Files[FileIndex];
      Assert(CanDeleteLegacyService(Root, Root, Driver, LegacyStoppedState),
        'Stopped exact-owned service allowed: ' + Driver.Name + ' / ' + Driver.ImagePath);
      for State := 0 to 8 do
        if State <> LegacyStoppedState then
          Assert(not CanDeleteLegacyService(Root, Root, Driver, State),
            'Reject unknown, pending, running or paused state ' + IntToStr(State));
      Assert(not CanDeleteLegacyService(Root, Root, Driver, $FFFFFFFF), 'Reject unknown large state');
      Assert(not CanDeleteLegacyService(Root, '', Driver, LegacyStoppedState), 'Reject unregistered service root');
      Assert(not CanDeleteLegacyService(Root, OtherRoot, Driver, LegacyStoppedState), 'Reject mismatched service root');
    end;
    Driver.ImagePath := OtherRoot + '\Service\lhm-bridge.sys';
    Assert(not CanDeleteLegacyService(Root, Root, Driver, LegacyStoppedState), 'Reject image in another installation');
    Driver.ImagePath := Root + '\Service\lhm-bridge.sys.extra';
    Assert(not CanDeleteLegacyService(Root, Root, Driver, LegacyStoppedState), 'Reject near-match image');
    Driver.ImagePath := Files[0];
    Assert(not CanDeleteLegacyService(Root, Root, Driver, LegacyStoppedState), 'Reject RyzenAdj service image');
    Driver.ImagePath := Root + '\Service\PawnIO.sys';
    Assert(not CanDeleteLegacyService(Root, Root, Driver, LegacyStoppedState), 'Reject PawnIO image');
  end;
  Driver.Name := 'R0lhm-bridge';
  Driver.ImagePath := Files[2];
  Driver.ServiceType := 2;
  Assert(not CanDeleteLegacyService(Root, Root, Driver, LegacyStoppedState), 'Reject filesystem driver type');
  Driver.ServiceType := 0;
  Assert(not CanDeleteLegacyService(Root, Root, Driver, LegacyStoppedState), 'Reject unknown driver type');
  Driver.ServiceType := $10;
  Assert(not CanDeleteLegacyService(Root, Root, Driver, LegacyStoppedState), 'Reject user-mode service type');
  Driver.ServiceType := 1;
  SetArrayLength(Names, 5);
  Names[0] := 'WinRing0_1_2_0';
  Names[1] := 'PawnIO';
  Names[2] := 'R0LibreHardwareMonitorLib';
  Names[3] := 'OtherDriver';
  Names[4] := 'R0lhm-bridge-extra';
  Driver.ImagePath := Files[2];
  for NameIndex := 0 to GetArrayLength(Names) - 1 do
  begin
    Driver.Name := Names[NameIndex];
    Assert(not CanDeleteLegacyService(Root, Root, Driver, LegacyStoppedState),
      'Reject unowned service name even with owned image: ' + Driver.Name);
  end;
end;

procedure AssertNormalized(const RawPath, ExpectedPath, Reason: String);
var
  Path: String;
begin
  Assert(NormalizeLegacyDriverPath(RawPath, Path), Reason + ': normalization succeeds');
  Assert(LegacySamePath(Path, ExpectedPath), Reason + ': exact normalized path');
end;

procedure TestPathNormalization;
var
  Root, Path, Normalized: String;
begin
  Root := CreateLegacyFixture('normalization');
  Path := Root + '\Service\lhm-bridge.sys';
  AssertNormalized('"' + Path + '"', Path, 'Quoted image');
  AssertNormalized('\??\' + Path, Path, 'NT DOS prefix');
  AssertNormalized('\\?\' + Path, Path, 'Extended-length prefix');
  Assert(TestSetEnvironmentVariable('XHM_MIGRATION_TEST_ROOT', Root), 'Set process-local fixture environment variable');
  try
    AssertNormalized('%XHM_MIGRATION_TEST_ROOT%\Service\lhm-bridge.sys', Path, 'Environment-variable image');
  finally
    Assert(TestSetEnvironmentVariable('XHM_MIGRATION_TEST_ROOT', ''), 'Clear process-local fixture environment variable');
  end;
  Assert(not NormalizeLegacyDriverPath('"' + Path + '" --argument', Normalized), 'Reject embedded quotes and arguments');
  Assert(not NormalizeLegacyDriverPath('Service\lhm-bridge.sys', Normalized), 'Reject relative driver path');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  TempHandle: TLegacyHandle;
  CanonicalTemp: String;
begin
  if CurStep <> ssInstall then
    exit;
  PassedAssertions := 0;
  // 临时目录自身可能经过用户目录 junction；夹具使用实际目录，不放宽生产校验。
  TempHandle := LegacyCreateFile(ExpandConstant('{tmp}'), 0, 7, 0, 3, $02000000, 0);
  Assert(TempHandle <> -1, 'Open isolated temporary directory');
  try
    Assert(LegacyFinalPath(TempHandle, CanonicalTemp), 'Resolve real temporary directory');
  finally
    Assert(LegacyCloseHandle(TempHandle), 'Close temporary directory handle');
  end;
  FixtureBase := AddBackslash(CanonicalTemp) + 'LegacyWinRingMigrationFixtures';
  Assert(not IsRegisteredLegacyRoot('C:\.', 'C:\.'), 'Reject normalized drive root');
  Assert(not IsRegisteredLegacyRoot(ExpandConstant('{win}'), ExpandConstant('{win}')), 'Reject Windows root');
  Assert(not DirExists(FixtureBase), 'Fixture root starts absent');
  Assert(ForceDirectories(FixtureBase), 'Create isolated temporary fixture root');
  // 只运行纯策略和使用合成驱动快照的文件清理。
  // 不写注册表、不调用 SCM、不加载驱动，也不调用完整迁移入口。
  TestNormalDeletion;
  TestUnverifiedRootsAndInventory;
  TestReferencedAndUnknownFiles;
  TestOccupiedFile;
  TestHardLink;
  TestDirectoryJunction;
  TestServicePolicy;
  TestPathNormalization;
  Log('LegacyWinRingMigration.Tests: all ' + IntToStr(PassedAssertions) + ' assertions passed.');
end;
