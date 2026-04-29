#define MyAppName "ShareWorkin"
#define MyAppVersion "1.04"
#define MyAppPublisher "ShareWorkin"
#define MyAppURL "https://app.media-house.jp/"
#define MyAppExeName "ShareWorkin.exe"
#define SourceDir ".\dist\publish\ShareWorkin"

[Setup]
AppId={{7D6F9F2E-827D-4E8D-9D7C-81DD52D313E1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName=C:\MyApps\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableDirPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
OutputDir=.
OutputBaseFilename=ShareWorkin_v1.04_install
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=ShareWorkin\app.ico
AppReadmeFile={app}\ご利用にあたって.txt
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
VersionInfoTextVersion={#MyAppVersion}

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Dirs]
Name: "{app}"; Permissions: users-modify

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: ".\ご利用にあたって.txt"; DestDir: "{app}"; Flags: ignoreversion

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  APP_NAME = 'ShareWorkin';
  APP_VERSION = '1.04';
  APP_EXE = 'ShareWorkin.exe';
  INSTALL_DIR = 'C:\MyApps\ShareWorkin';
  SETTINGS_FILE = 'settings.json';
  SETTINGS_BACKUP_FILE = 'ShareWorkin_settings_backup.json';
  OPTIMAL_RUNTIME_VERSION = '8.0.24';
  RUNTIME_REG_KEY = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  UNINSTALL_REG_KEY = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7D6F9F2E-827D-4E8D-9D7C-81DD52D313E1}_is1';
  COLOR_RED = $004040FF;
  COLOR_GREEN = $00008200;
  COLOR_ORANGE = $000080FF;
  COLOR_DESC = $00808080;
  COLOR_DESC_DISABLED = $00C0C0C0;

var
  CustomPage: TWizardPage;
  LblSectionTitle: TLabel;
  LblRuntimeStatus: TLabel;
  LblAppStatus: TLabel;
  RadioInstallBoth: TNewRadioButton;
  RadioInstallApp: TNewRadioButton;
  RadioUninstall: TNewRadioButton;
  LblDescBoth: TLabel;
  LblDescApp: TLabel;
  LblDescUninstall: TLabel;
  DetectedRuntimeStatus: Integer;
  DetectedRuntimeVersion: String;
  DetectedAppStatus: Integer;
  DetectedAppVersion: String;
  SelectedAction: String;

function OldInstallDir(): String;
begin
  Result := ExpandConstant('{localappdata}\Programs\' + APP_NAME);
end;

function OldDataDir(): String;
begin
  Result := ExpandConstant('{localappdata}\' + APP_NAME);
end;

function RuntimeDir(): String;
begin
  Result := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
end;

function SettingsBackupPath(): String;
begin
  Result := ExpandConstant('{tmp}\' + SETTINGS_BACKUP_FILE);
end;

function FindExistingSettings(var SettingsPath: String): Boolean;
begin
  Result := False;
  SettingsPath := '';

  if FileExists(INSTALL_DIR + '\' + SETTINGS_FILE) then
  begin
    SettingsPath := INSTALL_DIR + '\' + SETTINGS_FILE;
    Result := True;
    Exit;
  end;

  if FileExists(OldDataDir() + '\' + SETTINGS_FILE) then
  begin
    SettingsPath := OldDataDir() + '\' + SETTINGS_FILE;
    Result := True;
    Exit;
  end;
end;

procedure BackupExistingSettings();
var
  SettingsPath: String;
begin
  DeleteFile(SettingsBackupPath());
  if FindExistingSettings(SettingsPath) then
    CopyFile(SettingsPath, SettingsBackupPath(), False);
end;

procedure RestoreExistingSettings();
begin
  if FileExists(SettingsBackupPath()) then
  begin
    CopyFile(SettingsBackupPath(), ExpandConstant('{app}\' + SETTINGS_FILE), False);
    DeleteFile(SettingsBackupPath());
  end;
end;

function IsProcessRunning(FileName: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq ' + FileName + '" /NH 2>nul | find /I "' + FileName + '" >nul',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 0);
end;

function WaitForProcessExit(FileName: String): Boolean;
var
  ResultCode: Integer;
  I: Integer;
begin
  Result := False;
  for I := 1 to 5 do
  begin
    Sleep(1000);
    if not IsProcessRunning(FileName) then
    begin
      Result := True;
      Exit;
    end;
    Exec('taskkill', '/IM ' + FileName + ' /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  MsgBox(APP_NAME + ' を終了できませんでした。' + #13#10 +
    'タスクマネージャーから手動で終了してください。', mbError, MB_OK);
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  SelectedAction := '';

  if IsProcessRunning(APP_EXE) then
  begin
    if MsgBox(APP_NAME + ' が実行中です。終了してセットアップを続けますか？',
      mbConfirmation, MB_OKCANCEL) = IDOK then
    begin
      Exec('taskkill', '/IM ' + APP_EXE + ' /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if not WaitForProcessExit(APP_EXE) then
      begin
        Result := False;
        Exit;
      end;
    end
    else
      Result := False;
  end;
end;

function QueryInstallLocation(var InstallLocation: String): Boolean;
begin
  Result := False;
  InstallLocation := '';

  if RegQueryStringValue(HKLM64, UNINSTALL_REG_KEY, 'InstallLocation', InstallLocation) and (InstallLocation <> '') then
  begin
    Result := True;
    Exit;
  end;

  if RegQueryStringValue(HKLM64, UNINSTALL_REG_KEY, 'Inno Setup: App Path', InstallLocation) and (InstallLocation <> '') then
  begin
    Result := True;
    Exit;
  end;

  if RegQueryStringValue(HKLM, UNINSTALL_REG_KEY, 'InstallLocation', InstallLocation) and (InstallLocation <> '') then
  begin
    Result := True;
    Exit;
  end;

  if RegQueryStringValue(HKLM, UNINSTALL_REG_KEY, 'Inno Setup: App Path', InstallLocation) and (InstallLocation <> '') then
  begin
    Result := True;
    Exit;
  end;

  if RegQueryStringValue(HKCU, UNINSTALL_REG_KEY, 'InstallLocation', InstallLocation) and (InstallLocation <> '') then
  begin
    Result := True;
    Exit;
  end;

  if RegQueryStringValue(HKCU, UNINSTALL_REG_KEY, 'Inno Setup: App Path', InstallLocation) and (InstallLocation <> '') then
    Result := True;
end;

procedure DeleteDirIfExists(DirName: String);
begin
  if DirExists(DirName) then
    DelTree(DirName, True, True, True);
end;

procedure DeleteShareWorkinDirIfExists(DirName: String);
begin
  if (DirName <> '') and (CompareText(ExtractFileName(RemoveBackslash(DirName)), APP_NAME) = 0) then
    DeleteDirIfExists(DirName);
end;

procedure DeleteRegistryIfExists();
begin
  if RegKeyExists(HKLM64, UNINSTALL_REG_KEY) then
    RegDeleteKeyIncludingSubkeys(HKLM64, UNINSTALL_REG_KEY);
  if RegKeyExists(HKLM, UNINSTALL_REG_KEY) then
    RegDeleteKeyIncludingSubkeys(HKLM, UNINSTALL_REG_KEY);
  if RegKeyExists(HKCU, UNINSTALL_REG_KEY) then
    RegDeleteKeyIncludingSubkeys(HKCU, UNINSTALL_REG_KEY);
end;

function CleanExistingInstall(Silent: Boolean; PreserveSettings: Boolean): Boolean;
var
  RegisteredInstallDir: String;
  ResultCode: Integer;
begin
  Result := True;

  if IsProcessRunning(APP_EXE) then
  begin
    Exec('taskkill', '/IM ' + APP_EXE + ' /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if not WaitForProcessExit(APP_EXE) then
    begin
      Result := False;
      Exit;
    end;
  end;

  if PreserveSettings then
    BackupExistingSettings();

  QueryInstallLocation(RegisteredInstallDir);

  DeleteDirIfExists(INSTALL_DIR);
  DeleteDirIfExists(OldInstallDir());
  DeleteDirIfExists(OldDataDir());
  DeleteShareWorkinDirIfExists(RegisteredInstallDir);
  DeleteRegistryIfExists();
end;

procedure DetectEnvironment();
var
  Names: TArrayOfString;
  I: Integer;
  Value: Cardinal;
  FindRec: TFindRec;
begin
  DetectedRuntimeStatus := 0;
  DetectedRuntimeVersion := '';

  if RegGetValueNames(HKLM64, RUNTIME_REG_KEY, Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if RegQueryDWordValue(HKLM64, RUNTIME_REG_KEY, Names[I], Value) and (Value = 1) then
      begin
        if Names[I] = OPTIMAL_RUNTIME_VERSION then
        begin
          DetectedRuntimeStatus := 1;
          DetectedRuntimeVersion := Names[I];
          Break;
        end
        else
        begin
          DetectedRuntimeStatus := 2;
          if (DetectedRuntimeVersion = '') or (CompareStr(Names[I], DetectedRuntimeVersion) > 0) then
            DetectedRuntimeVersion := Names[I];
        end;
      end;
    end;
  end;

  if (DetectedRuntimeStatus = 0) and FindFirst(RuntimeDir() + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
           (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          if FindRec.Name = OPTIMAL_RUNTIME_VERSION then
          begin
            DetectedRuntimeStatus := 1;
            DetectedRuntimeVersion := FindRec.Name;
            Break;
          end
          else if DetectedRuntimeStatus = 0 then
          begin
            DetectedRuntimeStatus := 2;
            DetectedRuntimeVersion := FindRec.Name;
          end
          else if (DetectedRuntimeStatus = 2) and (CompareStr(FindRec.Name, DetectedRuntimeVersion) > 0) then
            DetectedRuntimeVersion := FindRec.Name;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;

  DetectedAppStatus := 0;
  DetectedAppVersion := '';

  if RegQueryStringValue(HKLM64, UNINSTALL_REG_KEY, 'DisplayVersion', DetectedAppVersion) or
     RegQueryStringValue(HKLM, UNINSTALL_REG_KEY, 'DisplayVersion', DetectedAppVersion) or
     RegQueryStringValue(HKCU, UNINSTALL_REG_KEY, 'DisplayVersion', DetectedAppVersion) then
  begin
    if DetectedAppVersion = '' then
      DetectedAppVersion := '(不明)';
    DetectedAppStatus := 1;
  end
  else if FileExists(INSTALL_DIR + '\' + APP_EXE) or FileExists(OldInstallDir() + '\' + APP_EXE) or DirExists(OldDataDir()) then
  begin
    DetectedAppStatus := 1;
    DetectedAppVersion := '(不明)';
  end;
end;

procedure ApplyDetectionResults();
begin
  case DetectedRuntimeStatus of
    0:
      begin
        LblRuntimeStatus.Caption := '.NET Desktop Runtime: インストールされていません';
        LblRuntimeStatus.Font.Color := COLOR_RED;
      end;
    1:
      begin
        LblRuntimeStatus.Caption := '.NET Desktop Runtime v' + DetectedRuntimeVersion + ': インストール済み';
        LblRuntimeStatus.Font.Color := COLOR_GREEN;
      end;
    2:
      begin
        LblRuntimeStatus.Caption := '.NET Desktop Runtime v' + DetectedRuntimeVersion + ': インストール済み(推奨 v' + OPTIMAL_RUNTIME_VERSION + ')';
        LblRuntimeStatus.Font.Color := COLOR_ORANGE;
      end;
  end;

  if DetectedAppStatus = 1 then
  begin
    LblAppStatus.Caption := APP_NAME + ' v' + DetectedAppVersion + ': 検出済み';
    LblAppStatus.Font.Color := COLOR_GREEN;
  end
  else
  begin
    LblAppStatus.Caption := APP_NAME + ': インストールされていません';
    LblAppStatus.Font.Color := COLOR_RED;
  end;

  RadioInstallBoth.Enabled := True;
  LblDescBoth.Font.Color := COLOR_DESC;
  RadioInstallApp.Enabled := True;
  LblDescApp.Font.Color := COLOR_DESC;

  RadioUninstall.Enabled := (DetectedAppStatus = 1);
  if RadioUninstall.Enabled then
    LblDescUninstall.Font.Color := COLOR_DESC
  else
    LblDescUninstall.Font.Color := COLOR_DESC_DISABLED;

  RadioInstallBoth.Checked := False;
  RadioInstallApp.Checked := False;
  RadioUninstall.Checked := False;

  if DetectedAppStatus = 1 then
    RadioInstallApp.Checked := True
  else
    RadioInstallBoth.Checked := True;
end;

procedure InitializeWizard();
var
  PageWidth: Integer;
  Y: Integer;
  Bevel: TBevel;
begin
  DetectEnvironment();

  CustomPage := CreateCustomPage(wpWelcome,
    APP_NAME + ' セットアップ',
    'お使いの環境を確認し、操作を選択してください。');

  PageWidth := CustomPage.SurfaceWidth;
  Y := 8;

  LblSectionTitle := TLabel.Create(WizardForm);
  LblSectionTitle.Parent := CustomPage.Surface;
  LblSectionTitle.Caption := '環境チェック結果';
  LblSectionTitle.Font.Size := 10;
  LblSectionTitle.Font.Style := [fsBold];
  LblSectionTitle.Left := 0;
  LblSectionTitle.Top := Y;
  LblSectionTitle.Width := PageWidth;
  Y := Y + 28;

  LblRuntimeStatus := TLabel.Create(WizardForm);
  LblRuntimeStatus.Parent := CustomPage.Surface;
  LblRuntimeStatus.Font.Size := 9;
  LblRuntimeStatus.Left := 12;
  LblRuntimeStatus.Top := Y;
  LblRuntimeStatus.Width := PageWidth - 12;
  Y := Y + 22;

  LblAppStatus := TLabel.Create(WizardForm);
  LblAppStatus.Parent := CustomPage.Surface;
  LblAppStatus.Font.Size := 9;
  LblAppStatus.Left := 12;
  LblAppStatus.Top := Y;
  LblAppStatus.Width := PageWidth - 12;
  Y := Y + 30;

  Bevel := TBevel.Create(WizardForm);
  Bevel.Parent := CustomPage.Surface;
  Bevel.Shape := bsTopLine;
  Bevel.Left := 0;
  Bevel.Top := Y;
  Bevel.Width := PageWidth;
  Bevel.Height := 2;
  Y := Y + 12;

  RadioInstallBoth := TNewRadioButton.Create(WizardForm);
  RadioInstallBoth.Parent := CustomPage.Surface;
  RadioInstallBoth.Caption := '標準インストール';
  RadioInstallBoth.Font.Size := 9;
  RadioInstallBoth.Font.Style := [fsBold];
  RadioInstallBoth.Left := 0;
  RadioInstallBoth.Top := Y;
  RadioInstallBoth.Width := PageWidth;
  RadioInstallBoth.Height := 20;
  Y := Y + 20;

  LblDescBoth := TLabel.Create(WizardForm);
  LblDescBoth.Parent := CustomPage.Surface;
  LblDescBoth.Caption := '既存の ShareWorkin と設定を一掃し、.NET Desktop Runtime と ShareWorkin を新規インストールします。';
  LblDescBoth.Font.Size := 8;
  LblDescBoth.Left := 20;
  LblDescBoth.Top := Y;
  LblDescBoth.Width := PageWidth - 20;
  Y := Y + 28;

  RadioInstallApp := TNewRadioButton.Create(WizardForm);
  RadioInstallApp.Parent := CustomPage.Surface;
  RadioInstallApp.Caption := 'アプリのみ入れ替え（既存の設定を残す）';
  RadioInstallApp.Font.Size := 9;
  RadioInstallApp.Font.Style := [fsBold];
  RadioInstallApp.Left := 0;
  RadioInstallApp.Top := Y;
  RadioInstallApp.Width := PageWidth;
  RadioInstallApp.Height := 20;
  Y := Y + 20;

  LblDescApp := TLabel.Create(WizardForm);
  LblDescApp.Parent := CustomPage.Surface;
  LblDescApp.Caption := '既存の設定（共有先や容量計算など）を残したまま、ShareWorkin 本体のみを上書きインストールします。';
  LblDescApp.Font.Size := 8;
  LblDescApp.Left := 20;
  LblDescApp.Top := Y;
  LblDescApp.Width := PageWidth - 20;
  Y := Y + 28;

  RadioUninstall := TNewRadioButton.Create(WizardForm);
  RadioUninstall.Parent := CustomPage.Surface;
  RadioUninstall.Caption := 'アンインストール';
  RadioUninstall.Font.Size := 9;
  RadioUninstall.Font.Style := [fsBold];
  RadioUninstall.Left := 0;
  RadioUninstall.Top := Y;
  RadioUninstall.Width := PageWidth;
  RadioUninstall.Height := 20;
  Y := Y + 20;

  LblDescUninstall := TLabel.Create(WizardForm);
  LblDescUninstall.Parent := CustomPage.Surface;
  LblDescUninstall.Caption := 'ShareWorkin をアンインストールし、検出済みの旧データも削除します。';
  LblDescUninstall.Font.Size := 8;
  LblDescUninstall.Left := 20;
  LblDescUninstall.Top := Y;
  LblDescUninstall.Width := PageWidth - 20;

  WizardForm.NextButton.Caption := '実行';
  WizardForm.BackButton.Visible := False;

  ApplyDetectionResults();
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = CustomPage.ID then
  begin
    WizardForm.NextButton.Caption := '実行';
    WizardForm.BackButton.Visible := False;
  end;
end;

function InstallRuntime(): Boolean;
var
  RuntimeInstaller: String;
  ResultCode: Integer;
begin
  Result := False;
  RuntimeInstaller := ExpandConstant('{src}\windowsdesktop-runtime-' + OPTIMAL_RUNTIME_VERSION + '-win-x64.exe');

  if not FileExists(RuntimeInstaller) then
  begin
    MsgBox('.NET Desktop Runtime のインストーラーが見つかりません。' + #13#10 +
      'セットアップと同じフォルダーに次のファイルを置いてください。' + #13#10#13#10 +
      'windowsdesktop-runtime-' + OPTIMAL_RUNTIME_VERSION + '-win-x64.exe',
      mbError, MB_OK);
    Exit;
  end;

  MsgBox('.NET Desktop Runtime のインストールを開始します。' + #13#10 +
    '表示される案内に従ってインストールしてください。',
    mbInformation, MB_OK);

  if Exec(RuntimeInstaller, '/install /passive /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
  begin
    if (ResultCode = 0) or (ResultCode = 1638) then
    begin
      Result := True;
      DetectEnvironment();
      ApplyDetectionResults();
    end
    else if ResultCode = 1602 then
      MsgBox('ランタイムのインストールがキャンセルされました。', mbInformation, MB_OK)
    else
      MsgBox('ランタイムのインストールに失敗しました。(エラーコード: ' + IntToStr(ResultCode) + ')', mbError, MB_OK);
  end
  else
    MsgBox('ランタイムインストーラーの起動に失敗しました。', mbError, MB_OK);
end;

function RunUninstall(): Boolean;
begin
  Result := False;

  if MsgBox(APP_NAME + ' をアンインストールします。よろしいですか？',
    mbConfirmation, MB_OKCANCEL) = IDCANCEL then
    Exit;

  if not CleanExistingInstall(False, False) then
    Exit;

  Result := True;
  MsgBox('アンインストールが完了しました。', mbInformation, MB_OK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  RuntimeResult: Boolean;
begin
  Result := True;

  if CurPageID = CustomPage.ID then
  begin
    if RadioInstallBoth.Checked then
    begin
      SelectedAction := 'both';
      if DetectedRuntimeStatus <> 1 then
      begin
        RuntimeResult := InstallRuntime();
        if not RuntimeResult then
        begin
          if MsgBox('ランタイムのインストールが完了していません。' + #13#10 +
            'ShareWorkin のインストールを続けますか？',
            mbConfirmation, MB_YESNO) = IDNO then
          begin
            Result := False;
            Exit;
          end;
        end;
      end;

      if not CleanExistingInstall(False, False) then
      begin
        Result := False;
        Exit;
      end;
    end
    else if RadioInstallApp.Checked then
    begin
      SelectedAction := 'app';
      if not CleanExistingInstall(False, True) then
      begin
        Result := False;
        Exit;
      end;
    end
    else if RadioUninstall.Checked then
    begin
      SelectedAction := 'uninstall';
      if RunUninstall() then
      begin
        WizardForm.Tag := 1;
        WizardForm.Close;
      end;
      Result := False;
    end;
  end;
end;

procedure HideInstallerArtifacts();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\attrib.exe'), '+h "' + ExpandConstant('{app}\unins000.exe') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\attrib.exe'), '+h "' + ExpandConstant('{app}\unins000.dat') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    HideInstallerArtifacts();
    RestoreExistingSettings();

    if (SelectedAction = 'both') or (SelectedAction = 'app') then
    begin
      MsgBox(APP_NAME + ' のインストールが完了しました。', mbInformation, MB_OK);
      Exec(ExpandConstant('{app}\' + APP_EXE), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
  end;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if WizardForm.Tag = 1 then
    Confirm := False;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if IsProcessRunning(APP_EXE) then
    begin
      Exec('taskkill', '/IM ' + APP_EXE + ' /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      WaitForProcessExit(APP_EXE);
    end;

    DeleteDirIfExists(INSTALL_DIR);
    DeleteDirIfExists(OldInstallDir());
    DeleteDirIfExists(OldDataDir());
  end;
end;
