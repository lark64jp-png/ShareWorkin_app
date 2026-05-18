#define MyAppName "ShareWorkin"
#define MyAppVersion "1.19"
#define MyAppPublisher "株式会社メディアハウス"
#define MyAppURL "https://app.media-house.jp/"
#define MyAppCorporateURL "https://media-house.jp/"
#define MyAppExeName "ShareWorkin.exe"
#define SourceDir ".\dist\publish\ShareWorkin"

[Setup]
AppId={{7D6F9F2E-827D-4E8D-9D7C-81DD52D313E1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppCorporateURL}
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
OutputBaseFilename=ShareWorkin_v1.19_install
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Uninstallable=no
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

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作る"; GroupDescription: "追加アイコン:"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; AppUserModelID: "ShareWorkin.MediaHouse"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "ShareWorkin.MediaHouse"

[Code]
const
  APP_NAME = 'ShareWorkin';
  APP_VERSION = '1.18';
  APP_EXE = 'ShareWorkin.exe';
  TRAY_EXE = 'ShareWorkinTray.exe';
  STARTUP_REG_KEY = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run';
  INSTALL_DIR = 'C:\MyApps\ShareWorkin';
  SETTINGS_FILE = 'settings.json';
  SECURE_FILE = 'secure.dat';
  FRIENDS_FILE = 'friends.json';
  PERMISSIONS_FILE = 'permissions.json';
  INVITES_FILE = 'invites.json';
  HISTORY_FILE = 'history.json';
  HISTORY_JOURNAL_FILE = 'history-journal.jsonl';
  HOLD_DIR = 'hold';
  SETTINGS_BACKUP_FILE = 'ShareWorkin_settings_backup.json';
  SECURE_BACKUP_FILE = 'ShareWorkin_secure_backup.dat';
  FRIENDS_BACKUP_FILE = 'ShareWorkin_friends_backup.json';
  PERMISSIONS_BACKUP_FILE = 'ShareWorkin_permissions_backup.json';
  INVITES_BACKUP_FILE = 'ShareWorkin_invites_backup.json';
  HISTORY_BACKUP_FILE = 'ShareWorkin_history_backup.json';
  HISTORY_JOURNAL_BACKUP_FILE = 'ShareWorkin_history_journal_backup.jsonl';
  HOLD_BACKUP_DIR = 'ShareWorkin_hold_backup';
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

function SecureBackupPath(): String;
begin
  Result := ExpandConstant('{tmp}\' + SECURE_BACKUP_FILE);
end;

function FriendsBackupPath(): String;
begin
  Result := ExpandConstant('{tmp}\' + FRIENDS_BACKUP_FILE);
end;

function HoldBackupPath(): String;
begin
  Result := ExpandConstant('{tmp}\' + HOLD_BACKUP_DIR);
end;

function PermissionsBackupPath(): String;
begin
  Result := ExpandConstant('{tmp}\' + PERMISSIONS_BACKUP_FILE);
end;

function InvitesBackupPath(): String;
begin
  Result := ExpandConstant('{tmp}\' + INVITES_BACKUP_FILE);
end;

function HistoryBackupPath(): String;
begin
  Result := ExpandConstant('{tmp}\' + HISTORY_BACKUP_FILE);
end;

function HistoryJournalBackupPath(): String;
begin
  Result := ExpandConstant('{tmp}\' + HISTORY_JOURNAL_BACKUP_FILE);
end;

function FindExistingFile(FileName: String; var FoundPath: String): Boolean;
begin
  Result := False;
  FoundPath := '';

  if FileExists(INSTALL_DIR + '\' + FileName) then
  begin
    FoundPath := INSTALL_DIR + '\' + FileName;
    Result := True;
    Exit;
  end;

  if FileExists(OldDataDir() + '\' + FileName) then
  begin
    FoundPath := OldDataDir() + '\' + FileName;
    Result := True;
  end;
end;

function FindExistingDirectory(DirName: String; var FoundPath: String): Boolean;
begin
  Result := False;
  FoundPath := '';

  if DirExists(INSTALL_DIR + '\' + DirName) then
  begin
    FoundPath := INSTALL_DIR + '\' + DirName;
    Result := True;
    Exit;
  end;

  if DirExists(OldDataDir() + '\' + DirName) then
  begin
    FoundPath := OldDataDir() + '\' + DirName;
    Result := True;
  end;
end;

function CopyDirectoryContents(SourceDir, DestDir: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if not DirExists(SourceDir) then Exit;
  ForceDirectories(DestDir);
  if Exec(ExpandConstant('{cmd}'),
    '/C xcopy "' + SourceDir + '" "' + DestDir + '" /E /I /Y /Q >nul 2>&1',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 0);
end;

procedure BackupExistingSettings();
var
  Source: String;
begin
  DeleteFile(SettingsBackupPath());
  DeleteFile(SecureBackupPath());
  DeleteFile(FriendsBackupPath());
  DeleteFile(PermissionsBackupPath());
  DeleteFile(InvitesBackupPath());
  DeleteFile(HistoryBackupPath());
  DeleteFile(HistoryJournalBackupPath());
  DelTree(HoldBackupPath(), True, True, True);

  if FindExistingFile(SETTINGS_FILE, Source) then
    CopyFile(Source, SettingsBackupPath(), False);
  if FindExistingFile(SECURE_FILE, Source) then
    CopyFile(Source, SecureBackupPath(), False);
  if FindExistingFile(FRIENDS_FILE, Source) then
    CopyFile(Source, FriendsBackupPath(), False);
  if FindExistingFile(PERMISSIONS_FILE, Source) then
    CopyFile(Source, PermissionsBackupPath(), False);
  if FindExistingFile(INVITES_FILE, Source) then
    CopyFile(Source, InvitesBackupPath(), False);
  if FindExistingFile(HISTORY_FILE, Source) then
    CopyFile(Source, HistoryBackupPath(), False);
  if FindExistingFile(HISTORY_JOURNAL_FILE, Source) then
    CopyFile(Source, HistoryJournalBackupPath(), False);
  if FindExistingDirectory(HOLD_DIR, Source) then
    CopyDirectoryContents(Source, HoldBackupPath());
end;

procedure RestoreExistingSettings();
var
  AppDir: String;
begin
  AppDir := ExpandConstant('{app}');

  if FileExists(SettingsBackupPath()) then
  begin
    CopyFile(SettingsBackupPath(), AppDir + '\' + SETTINGS_FILE, False);
    DeleteFile(SettingsBackupPath());
  end;
  if FileExists(SecureBackupPath()) then
  begin
    CopyFile(SecureBackupPath(), AppDir + '\' + SECURE_FILE, False);
    DeleteFile(SecureBackupPath());
  end;
  if FileExists(FriendsBackupPath()) then
  begin
    CopyFile(FriendsBackupPath(), AppDir + '\' + FRIENDS_FILE, False);
    DeleteFile(FriendsBackupPath());
  end;
  if FileExists(PermissionsBackupPath()) then
  begin
    CopyFile(PermissionsBackupPath(), AppDir + '\' + PERMISSIONS_FILE, False);
    DeleteFile(PermissionsBackupPath());
  end;
  if FileExists(InvitesBackupPath()) then
  begin
    CopyFile(InvitesBackupPath(), AppDir + '\' + INVITES_FILE, False);
    DeleteFile(InvitesBackupPath());
  end;
  if FileExists(HistoryBackupPath()) then
  begin
    CopyFile(HistoryBackupPath(), AppDir + '\' + HISTORY_FILE, False);
    DeleteFile(HistoryBackupPath());
  end;
  if FileExists(HistoryJournalBackupPath()) then
  begin
    CopyFile(HistoryJournalBackupPath(), AppDir + '\' + HISTORY_JOURNAL_FILE, False);
    DeleteFile(HistoryJournalBackupPath());
  end;
  if DirExists(HoldBackupPath()) then
  begin
    CopyDirectoryContents(HoldBackupPath(), AppDir + '\' + HOLD_DIR);
    DelTree(HoldBackupPath(), True, True, True);
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
  for I := 1 to 50 do
  begin
    Sleep(100);
    if not IsProcessRunning(FileName) then
    begin
      Result := True;
      Exit;
    end;
    if (I mod 10) = 0 then
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

  if IsProcessRunning(APP_EXE) or IsProcessRunning(TRAY_EXE) then
  begin
    if MsgBox(APP_NAME + ' が実行中です。終了してセットアップを続けますか？',
      mbConfirmation, MB_OKCANCEL) = IDOK then
    begin
      Exec('taskkill', '/IM ' + TRAY_EXE + ' /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec('taskkill', '/IM ' + APP_EXE + ' /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if not WaitForProcessExit(APP_EXE) then
      begin
        Result := False;
        Exit;
      end;
      if not WaitForProcessExit(TRAY_EXE) then
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

function QueryInstalledAppVersionFromExe(var VersionText: String): Boolean;
var
  InstallLocation: String;
  ExePath: String;
begin
  Result := False;
  VersionText := '';

  ExePath := INSTALL_DIR + '\' + APP_EXE;
  if FileExists(ExePath) and GetVersionNumbersString(ExePath, VersionText) and (VersionText <> '') then
  begin
    Result := True;
    Exit;
  end;

  ExePath := OldInstallDir() + '\' + APP_EXE;
  if FileExists(ExePath) and GetVersionNumbersString(ExePath, VersionText) and (VersionText <> '') then
  begin
    Result := True;
    Exit;
  end;

  if QueryInstallLocation(InstallLocation) then
  begin
    ExePath := RemoveBackslashUnlessRoot(InstallLocation) + '\' + APP_EXE;
    if FileExists(ExePath) and GetVersionNumbersString(ExePath, VersionText) and (VersionText <> '') then
    begin
      Result := True;
      Exit;
    end;
  end;
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

procedure CleanupShareWorkinShares(); forward;
procedure CleanupShareWorkinAccount(); forward;

function CleanExistingInstall(Silent: Boolean; PreserveSettings: Boolean): Boolean;
var
  RegisteredInstallDir: String;
  ResultCode: Integer;
begin
  Result := True;

  if IsProcessRunning(TRAY_EXE) then
    Exec('taskkill', '/IM ' + TRAY_EXE + ' /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if IsProcessRunning(APP_EXE) then
  begin
    Exec('taskkill', '/IM ' + APP_EXE + ' /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if not WaitForProcessExit(APP_EXE) then
    begin
      Result := False;
      Exit;
    end;
  end;
  if IsProcessRunning(TRAY_EXE) then
  begin
    if not WaitForProcessExit(TRAY_EXE) then
    begin
      Result := False;
      Exit;
    end;
  end;

  if PreserveSettings then
    BackupExistingSettings()
  else
  begin
    // 草案4 §A: ホルダー削除で痕跡完全消滅。SMB 共有・swkguest アカウントも一掃する。
    CleanupShareWorkinShares();
    CleanupShareWorkinAccount();
  end;

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
  else if QueryInstalledAppVersionFromExe(DetectedAppVersion) then
  begin
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
  RadioInstallBoth.Caption := '新規インストール（削除して新規）';
  RadioInstallBoth.Font.Size := 9;
  RadioInstallBoth.Font.Style := [fsBold];
  RadioInstallBoth.Left := 0;
  RadioInstallBoth.Top := Y;
  RadioInstallBoth.Width := PageWidth;
  RadioInstallBoth.Height := 20;
  Y := Y + 20;

  LblDescBoth := TLabel.Create(WizardForm);
  LblDescBoth.Parent := CustomPage.Surface;
  LblDescBoth.Caption := '既存の ShareWorkin・設定・お友達アカウント・共有定義をすべて消し、白紙から入れ直します。.NET Desktop Runtime も同時に整えます。';
  LblDescBoth.Font.Size := 8;
  LblDescBoth.Left := 20;
  LblDescBoth.Top := Y;
  LblDescBoth.Width := PageWidth - 20;
  Y := Y + 28;

  RadioInstallApp := TNewRadioButton.Create(WizardForm);
  RadioInstallApp.Parent := CustomPage.Surface;
  RadioInstallApp.Caption := '更新インストール（情報を引き継いで更新）';
  RadioInstallApp.Font.Size := 9;
  RadioInstallApp.Font.Style := [fsBold];
  RadioInstallApp.Left := 0;
  RadioInstallApp.Top := Y;
  RadioInstallApp.Width := PageWidth;
  RadioInstallApp.Height := 20;
  Y := Y + 20;

  LblDescApp := TLabel.Create(WizardForm);
  LblDescApp.Parent := CustomPage.Surface;
  LblDescApp.Caption := '既存の設定・お店の鍵・保留物・お友達情報を引き継いだまま、ShareWorkin 本体のみを新しい版に置き換えます。';
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
  LblDescUninstall.Caption := 'ShareWorkin をアンインストールし、設定・お店の鍵・保留物・お友達アカウント・共有定義をすべて消去します。';
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
var
  ResultCode: Integer;
begin
  Result := False;

  if MsgBox(APP_NAME + ' をアンインストールします。よろしいですか？',
    mbConfirmation, MB_OKCANCEL) = IDCANCEL then
    Exit;

  if not CleanExistingInstall(False, False) then
    Exit;

  Exec(ExpandConstant('{sys}\schtasks.exe'),
    '/Delete /TN "ShareWorkin\ShareWorkinTray" /F',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

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

// 草案6 §D: 利用者から見える領域に並ぶファイルは「意味のあるもの」だけ。
// 実装上避けられない副産物(unins000.*, 内部用 .ico, .NET 配置物等)は不可視属性で配置する。
// Hidden 単独だと「隠しファイル表示」を有効にした閲覧者から見えてしまうため、System 属性も併用し、
// 「保護されたOSファイルを表示しない」(Win11 既定有効)が効く位置に置く。
procedure HideFile(const FileName: String);
var
  ResultCode: Integer;
  FullPath: String;
begin
  FullPath := ExpandConstant('{app}\' + FileName);
  Exec(ExpandConstant('{sys}\attrib.exe'), '+h +s "' + FullPath + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure HideInstallerArtifacts();
begin
  HideFile('app.ico');
  HideFile('ShareWorkin.dll');
  HideFile('ShareWorkin.deps.json');
  HideFile('ShareWorkin.runtimeconfig.json');
  HideFile('ShareWorkinTray.dll');
  HideFile('ShareWorkinTray.deps.json');
  HideFile('ShareWorkinTray.runtimeconfig.json');
  HideFile('ShareWorkin.SMB.dll');
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
      Exec(ExpandConstant('{sys}\schtasks.exe'),
        '/Create /TN "ShareWorkin\ShareWorkinTray" /TR "\"' +
        ExpandConstant('{app}\' + TRAY_EXE) + '\"" /SC ONLOGON /RL HIGHEST /F',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{app}\' + TRAY_EXE), '', ExpandConstant('{app}'), SW_HIDE, ewNoWait, ResultCode);
      MsgBox(APP_NAME + ' のインストールが完了しました。', mbInformation, MB_OK);
      Exec(ExpandConstant('{app}\' + APP_EXE), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
  end;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if WizardForm.Tag = 1 then
    Confirm := False;
end;

procedure CleanupShareWorkinShares();
var
  ShareNames: TArrayOfString;
  I: Integer;
  Multi: String;
  ResultCode: Integer;
begin
  if not RegGetValueNames(HKLM, 'SYSTEM\CurrentControlSet\Services\LanmanServer\Shares', ShareNames) then
    Exit;
  for I := 0 to GetArrayLength(ShareNames) - 1 do
  begin
    if ShareNames[I] = '' then Continue;
    if not RegQueryMultiStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\LanmanServer\Shares', ShareNames[I], Multi) then
      Continue;
    if Pos(#13#10 + 'Remark=ShareWorkin:', #13#10 + Multi) > 0 then
      Exec(ExpandConstant('{cmd}'),
        '/C net share "' + ShareNames[I] + '" /delete /y >nul 2>&1',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CleanupShareWorkinAccount();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'),
    '/C net user swkguest /delete >nul 2>&1',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
