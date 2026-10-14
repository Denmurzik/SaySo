; Установщик SaySo (Inno Setup 6.3+). Сборка: ISCC /DAppVersion=X.Y.Z [/DSourceDir=<publish>] [/O<папка>] installer\SaySo.iss
; Обычно запускается через build-installer.ps1, который берёт версию из Directory.Build.props.
; Файл сохранён в UTF-8 с BOM — иначе русские строки в [CustomMessages] и [Code] испортятся.

#ifndef AppVersion
  #error Не задана версия: ISCC /DAppVersion=X.Y.Z (build-installer.ps1 берёт её из Directory.Build.props)
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "SaySo"
#define AppExe "SaySo.exe"
#define AppUrl "https://github.com/Denmurzik/SaySo"

[Setup]
; AppId неизменен во всех версиях: так новый Setup встаёт поверх старого, а запись об удалении остаётся одна.
AppId={{4C827BFC-9A72-4512-910C-1DDC1336642F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Denmurzik
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=© 2026 Denis Muruev
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoDescription=Установщик {#AppName}
LicenseFile=..\LICENSE
SetupIconFile=..\src\SaySo\Assets\SaySo.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

; По умолчанию «только для меня» (без UAC, %LOCALAPPDATA%\Programs\SaySo); в мастере или ключами /CURRENTUSER и /ALLUSERS можно выбрать иначе.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

; Остановкой работающего SaySo занимается [Code]: Restart Manager не умеет штатно закрыть безоконный трей-процесс.
CloseApplications=no
RestartApplications=no
SetupMutex=SaySo.Setup
ShowLanguageDialog=no

WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=SaySo-Setup-{#AppVersion}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "{cm:ShortcutComment}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "{cm:ShortcutComment}"; Tasks: desktopicon

[Run]
; Обычная установка: галочка «Запустить SaySo» на последней странице.
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Обновление из приложения (/VERYSILENT … /UPDATE): SaySo перезапускается сам, от имени исходного пользователя даже при установке «для всех».
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifnotsilent runasoriginaluser; Check: IsUpdateMode

[CustomMessages]
russian.ShortcutComment=SaySo: голосовая диктовка
english.ShortcutComment=SaySo: voice dictation
russian.StopFailed=Не удалось закрыть работающий SaySo (возможно, он запущен с правами администратора). Закройте его (значок в трее → «Выход») и повторите установку.
english.StopFailed=Could not close the running SaySo (it may be running as administrator). Close it (tray icon → Exit) and run the setup again.
russian.StopTitle=Закрытие SaySo
english.StopTitle=Closing SaySo
russian.StopDescription=Подождите, пока работающий SaySo завершит работу.
english.StopDescription=Please wait while the running SaySo exits.
russian.UninstallStopFailed=Не удалось закрыть работающий SaySo (возможно, он запущен с правами администратора или в сеансе другого пользователя). Часть файлов в папке «%1» может остаться: закройте SaySo и удалите папку вручную, после перезагрузки это тоже получится.
english.UninstallStopFailed=Could not close the running SaySo (it may be running as administrator or in another user's session). Some files in "%1" may remain: close SaySo and delete the folder manually; a restart also helps.
russian.DeleteData=Удалить также настройки, ключи и историю (%LOCALAPPDATA%\SaySo)?
english.DeleteData=Also delete settings, keys and history (%LOCALAPPDATA%\SaySo)?
russian.TaskElevate=Автозапуск от имени администратора (задача Планировщика «SaySo») можно удалить только с правами администратора.%n%nУдалить её сейчас? Появится запрос UAC.
english.TaskElevate=The administrator autostart (Task Scheduler task "SaySo") can only be removed with administrator rights.%n%nRemove it now? A UAC prompt will appear.
russian.TaskLeft=Задача Планировщика «SaySo» не удалена. Удалите её вручную в командной строке от имени администратора:%n%nschtasks /Delete /TN SaySo /F
english.TaskLeft=The Task Scheduler task "SaySo" was not removed. Remove it manually from an elevated command prompt:%n%nschtasks /Delete /TN SaySo /F

[Code]
const
  AppExeName = 'SaySo.exe';
  DaemonWindowClass = 'SaySo.Daemon.Ipc';
  SettingsWindowClass = 'WinUIDesktopWin32WindowClass';
  SettingsWindowTitle = 'SaySo';
  QuitMessage = 'SaySo.Quit';
  TaskName = 'SaySo';
  RunKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValueName = 'SaySo';
  // Сколько ждём штатного выхода демона, прежде чем завершать процессы принудительно (демон сам может ждать конца диктовки до 10 с).
  QuitWaitMs = 10000;
  KillWaitMs = 3000;
  PollMs = 200;
  TH32CS_SNAPPROCESS = $00000002;
  PROCESS_TERMINATE = $0001;
  PROCESS_QUERY_LIMITED_INFORMATION = $1000;
  SYNCHRONIZE = $00100000;
  WM_CLOSE = $0010;
  InvalidHandle = $FFFFFFFF;
  ProcessEntrySize = 556; // PROCESSENTRY32W в 32-разрядном процессе: 9 DWORD + 260 WCHAR

type
  TProcessEntry32 = record
    dwSize: LongWord;
    cntUsage: LongWord;
    th32ProcessID: LongWord;
    th32DefaultHeapID: LongWord;
    th32ModuleID: LongWord;
    cntThreads: LongWord;
    th32ParentProcessID: LongWord;
    pcPriClassBase: LongInt;
    dwFlags: LongWord;
    szExeFile: array[0..259] of Char;
  end;

function CreateToolhelp32Snapshot(dwFlags, th32ProcessID: LongWord): LongWord;
  external 'CreateToolhelp32Snapshot@kernel32.dll stdcall';
function Process32FirstW(hSnapshot: LongWord; var lppe: TProcessEntry32): LongInt;
  external 'Process32FirstW@kernel32.dll stdcall';
function Process32NextW(hSnapshot: LongWord; var lppe: TProcessEntry32): LongInt;
  external 'Process32NextW@kernel32.dll stdcall';
function OpenProcess(dwDesiredAccess: LongWord; bInheritHandle: LongInt; dwProcessId: LongWord): LongWord;
  external 'OpenProcess@kernel32.dll stdcall';
function CloseHandle(hObject: LongWord): LongInt;
  external 'CloseHandle@kernel32.dll stdcall';
function TerminateProcess(hProcess, uExitCode: LongWord): LongInt;
  external 'TerminateProcess@kernel32.dll stdcall';
function QueryFullProcessImageNameW(hProcess, dwFlags: LongWord; lpExeName: String; var lpdwSize: LongWord): LongInt;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function ProcessIdToSessionId(dwProcessId: LongWord; var pSessionId: LongWord): LongInt;
  external 'ProcessIdToSessionId@kernel32.dll stdcall';
function GetCurrentProcessId: LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';
function FindWindowExByClass(hwndParent, hwndChildAfter: LongWord; lpszClass: String; lpszWindow: LongWord): LongWord;
  external 'FindWindowExW@user32.dll stdcall';
function FindWindowExByClassAndTitle(hwndParent, hwndChildAfter: LongWord; lpszClass, lpszWindow: String): LongWord;
  external 'FindWindowExW@user32.dll stdcall';
function GetWindowThreadProcessId(hWnd: LongWord; var lpdwProcessId: LongWord): LongWord;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function RegisterWindowMessageW(lpString: String): LongWord;
  external 'RegisterWindowMessageW@user32.dll stdcall';
function PostMessageW(hWnd, Msg: LongWord; wParam, lParam: LongInt): LongInt;
  external 'PostMessageW@user32.dll stdcall';

var
  InstallSucceeded: Boolean;
  StopPage: TOutputMarqueeProgressWizardPage;
  StopPageShown: Boolean;

// Ключ /UPDATE без значения {param:…} не читает — ищем его сами.
function IsUpdateMode: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/UPDATE') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function IsInsideDir(const Path, Dir: String): Boolean;
var
  Prefix: String;
begin
  Prefix := AddBackslash(Dir);
  Result := CompareText(Copy(Path, 1, Length(Prefix)), Prefix) = 0;
end;

// Полный путь к exe процесса; пусто, если процесс недоступен (например, элевированный).
function GetProcessImagePath(Pid: LongWord): String;
var
  Process, Size: LongWord;
  Buffer: String;
begin
  Result := '';
  Process := OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, Pid);
  if Process = 0 then
    Exit;
  try
    Size := 1024;
    SetLength(Buffer, Size);
    if QueryFullProcessImageNameW(Process, 0, Buffer, Size) <> 0 then
      Result := Copy(Buffer, 1, Size);
  finally
    CloseHandle(Process);
  end;
end;

// Тот же сеанс Windows, что и у Setup: чужие сеансы (другой пользователь по RDP) не трогаем.
function IsSameSession(Pid: LongWord): Boolean;
var
  Mine, Theirs: LongWord;
begin
  Result := True;
  if (ProcessIdToSessionId(GetCurrentProcessId, Mine) <> 0) and (ProcessIdToSessionId(Pid, Theirs) <> 0) then
    Result := Mine = Theirs;
end;

// Процесс принадлежит установке, только если его образ лежит в её папке. Путь, который не удалось прочитать, — не «наш»:
// такому процессу не шлём Quit, его не считаем и не завершаем. Для элевированного демона того же пользователя путь читается
// (PROCESS_QUERY_LIMITED_INFORMATION разрешён и к процессам с более высоким уровнем целостности), так что он остаётся «нашим».
function BelongsToInstall(Pid: LongWord; const AppDir: String): Boolean;
var
  Path: String;
begin
  Result := False;
  if not IsSameSession(Pid) then
    Exit;
  Path := GetProcessImagePath(Pid);
  Result := (Path <> '') and IsInsideDir(Path, AppDir);
end;

// Считает процессы SaySo.exe из папки установки; Kill = True — завершает их принудительно.
function CountAppProcesses(const AppDir: String; Kill: Boolean): Integer;
var
  Snapshot, Process: LongWord;
  Entry: TProcessEntry32;
  ExeName: String;
  I: Integer;
  More: Boolean;
begin
  Result := 0;
  Snapshot := CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
  if Snapshot = InvalidHandle then
    Exit;
  try
    Entry.dwSize := ProcessEntrySize;
    More := Process32FirstW(Snapshot, Entry) <> 0;
    while More do
    begin
      ExeName := '';
      for I := 0 to 259 do
      begin
        if Entry.szExeFile[I] = #0 then
          Break;
        ExeName := ExeName + Entry.szExeFile[I];
      end;
      if (CompareText(ExeName, AppExeName) = 0) and BelongsToInstall(Entry.th32ProcessID, AppDir) then
      begin
        Result := Result + 1;
        if Kill then
        begin
          Process := OpenProcess(PROCESS_TERMINATE, 0, Entry.th32ProcessID);
          if Process <> 0 then
          begin
            if TerminateProcess(Process, 1) = 0 then
              Log(Format('SaySo: TerminateProcess(%d) не удался', [Entry.th32ProcessID]))
            else
              Log(Format('SaySo: процесс %d завершён принудительно', [Entry.th32ProcessID]));
            CloseHandle(Process);
          end
          else
            Log(Format('SaySo: нет доступа к процессу %d (запущен с повышенными правами?)', [Entry.th32ProcessID]));
        end;
      end;
      Entry.dwSize := ProcessEntrySize;
      More := Process32NextW(Snapshot, Entry) <> 0;
    end;
  finally
    CloseHandle(Snapshot);
  end;
end;

// Просит демон из папки установки выйти штатно (то же сообщение, что отправляет окно настроек), окно настроек — закрыться.
procedure AskAppToExit(const AppDir: String);
var
  Window, Pid, QuitId: LongWord;
begin
  QuitId := RegisterWindowMessageW(QuitMessage);

  Window := 0;
  repeat
    Window := FindWindowExByClass(0, Window, DaemonWindowClass, 0);
    if Window <> 0 then
    begin
      GetWindowThreadProcessId(Window, Pid);
      if BelongsToInstall(Pid, AppDir) and (QuitId <> 0) then
      begin
        Log(Format('SaySo: отправляю SaySo.Quit демону (процесс %d)', [Pid]));
        PostMessageW(Window, QuitId, 0, 0);
      end;
    end;
  until Window = 0;

  Window := 0;
  repeat
    Window := FindWindowExByClassAndTitle(0, Window, SettingsWindowClass, SettingsWindowTitle);
    if Window <> 0 then
    begin
      GetWindowThreadProcessId(Window, Pid);
      if BelongsToInstall(Pid, AppDir) then
      begin
        Log(Format('SaySo: закрываю окно настроек (процесс %d)', [Pid]));
        PostMessageW(Window, WM_CLOSE, 0, 0);
      end;
    end;
  until Window = 0;
end;

procedure InitializeWizard;
begin
  // Страница с бегущей полосой на время остановки SaySo; показывается вручную (только в установщике, в тихом режиме её нет).
  StopPage := CreateOutputMarqueeProgressPage(CustomMessage('StopTitle'), CustomMessage('StopDescription'));
end;

procedure ShowStopPage;
begin
  if (StopPage = nil) or WizardSilent then
    Exit;
  try
    StopPage.Show;
    StopPageShown := True;
  except
    StopPageShown := False; // полоса — только удобство, установке она не нужна
  end;
end;

procedure HideStopPage;
begin
  if not StopPageShown then
    Exit;
  StopPageShown := False;
  try
    StopPage.Hide;
  except
  end;
end;

function WaitForAppExit(const AppDir: String; TimeoutMs: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while (CountAppProcesses(AppDir, False) > 0) and (Waited < TimeoutMs) do
  begin
    Sleep(PollMs);
    Waited := Waited + PollMs;
    if StopPageShown then
      StopPage.Animate; // заодно обрабатывает сообщения, окно мастера не «зависает»
  end;
  Result := CountAppProcesses(AppDir, False) = 0;
end;

// Останавливает SaySo из папки установки: штатно по IPC, через 10 с — принудительно. True — процессов не осталось.
function StopApp(const AppDir: String): Boolean;
begin
  if CountAppProcesses(AppDir, False) = 0 then
  begin
    Log('SaySo: не запущен, останавливать нечего');
    Result := True;
    Exit;
  end;

  ShowStopPage;
  try
    AskAppToExit(AppDir);
    if not WaitForAppExit(AppDir, QuitWaitMs) then
    begin
      Log('SaySo: не вышел за 10 с, завершаю принудительно');
      CountAppProcesses(AppDir, True);
      WaitForAppExit(AppDir, KillWaitMs);
    end;
    Result := CountAppProcesses(AppDir, False) = 0;
  finally
    HideStopPage;
  end;
  if Result then
    Log('SaySo: остановлен')
  else
    Log('SaySo: остановить не удалось');
end;

// Вызывается после выбора папки, до копирования файлов: файлы не будут заняты работающим SaySo.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopApp(ExpandConstant('{app}')) then
    Result := CustomMessage('StopFailed');
end;

// Все шаги установки (файлы, ярлыки, реестр) прошли без ошибок; дальше ошибок отката уже не будет.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallSucceeded := True;
end;

// Неудачное обновление не должно оставить пользователя без SaySo: демон уже остановлен, а Inno при ошибке откатил изменения,
// поэтому в папке установки лежит прежний SaySo.exe — запускаем его от имени исходного пользователя.
procedure DeinitializeSetup;
var
  AppDir, ExePath: String;
  ResultCode: Integer;
begin
  if (not IsUpdateMode) or InstallSucceeded then
    Exit;
  try
    AppDir := ExpandConstant('{app}');
    ExePath := AddBackslash(AppDir) + AppExeName;
    if not FileExists(ExePath) then
    begin
      Log('Обновление не удалось, прежнего ' + ExePath + ' нет — перезапускать нечего');
      Exit;
    end;
    if CountAppProcesses(AppDir, False) > 0 then
    begin
      Log('Обновление не удалось, но SaySo уже работает — перезапуск не нужен');
      Exit;
    end;
    Log('Обновление не удалось — запускаю прежний SaySo: ' + ExePath);
    if not ExecAsOriginalUser(ExePath, '', AppDir, SW_SHOWNORMAL, ewNoWait, ResultCode) then
      Log('Не удалось запустить прежний SaySo (ошибка ' + IntToStr(ResultCode) + ')');
  except
    Log('Перезапуск прежнего SaySo после неудачного обновления не удался: ' + GetExceptionMessage);
  end;
end;

function TaskExists: Boolean;
var
  Service, Folder, Task: Variant;
begin
  Result := False;
  try
    Service := CreateOleObject('Schedule.Service');
    Service.Connect();
    Folder := Service.GetFolder('\');
    Task := Folder.GetTask(TaskName);
    Result := True;
  except
  end;
end;

// Удаляет задачу Планировщика «SaySo», только если она запускает SaySo.exe из этой установки.
procedure RemoveSchedulerTask(const AppDir: String);
var
  Service, Folder, Task: Variant;
  TaskXml: String;
  ResultCode: Integer;
begin
  try
    Service := CreateOleObject('Schedule.Service');
    Service.Connect();
    Folder := Service.GetFolder('\');
    Task := Folder.GetTask(TaskName);
    TaskXml := Task.Xml;
  except
    Log('Планировщик: задачи SaySo нет или прочитать её не удалось');
    Exit;
  end;

  if Pos(Lowercase(AddBackslash(AppDir) + AppExeName), Lowercase(TaskXml)) = 0 then
  begin
    Log('Планировщик: задача SaySo относится к другой установке — оставлена');
    Exit;
  end;

  try
    Folder.DeleteTask(TaskName, 0);
    Log('Планировщик: задача SaySo удалена');
  except
    Log('Планировщик: удалить задачу SaySo без прав администратора не удалось');
  end;

  if TaskExists and not UninstallSilent then
  begin
    // Задача создана с наивысшими правами: удалить её может только администратор.
    if SuppressibleMsgBox(CustomMessage('TaskElevate'), mbConfirmation, MB_YESNO, IDYES) = IDYES then
      ShellExec('runas', 'schtasks.exe', '/Delete /TN ' + TaskName + ' /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if TaskExists then
      SuppressibleMsgBox(CustomMessage('TaskLeft'), mbInformation, MB_OK, IDOK);
  end
  else if TaskExists then
    Log('Планировщик: задача SaySo осталась (нужны права администратора): schtasks /Delete /TN SaySo /F');
end;

// Снимает автозапуск, только если значение Run указывает в папку установки.
procedure RemoveRunValue(const AppDir: String);
var
  Value: String;
begin
  if not RegQueryStringValue(HKCU, RunKeyPath, RunValueName, Value) then
    Exit;
  Value := Trim(Value);
  if (Length(Value) > 0) and (Value[1] = '"') then
    Delete(Value, 1, 1);
  if IsInsideDir(Value, AppDir) then
  begin
    if RegDeleteValue(HKCU, RunKeyPath, RunValueName) then
      Log('Автозапуск: значение Run\SaySo удалено')
    else
      Log('Автозапуск: не удалось удалить значение Run\SaySo');
  end
  else
    Log('Автозапуск: значение Run\SaySo указывает на другую установку — оставлено');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir, DataDir: String;
begin
  AppDir := ExpandConstant('{app}');
  case CurUninstallStep of
    usUninstall:
      begin
        if not StopApp(AppDir) then
        begin
          Log('Удаление: SaySo не остановлен, файлы в ' + AppDir + ' могут остаться');
          if not UninstallSilent then
            SuppressibleMsgBox(FmtMessage(CustomMessage('UninstallStopFailed'), [AppDir]), mbError, MB_OK, IDOK);
        end;
        RemoveRunValue(AppDir);
        RemoveSchedulerTask(AppDir);
      end;
    usPostUninstall:
      // В тихом режиме данные пользователя не удаляем; в обычном спрашиваем, по умолчанию «Нет».
      if not UninstallSilent then
      begin
        DataDir := ExpandConstant('{localappdata}\SaySo');
        if DirExists(DataDir) and
           (SuppressibleMsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
        begin
          if DelTree(DataDir, True, True, True) then
            Log('Данные удалены: ' + DataDir)
          else
            Log('Данные удалить не удалось: ' + DataDir);
        end;
      end;
  end;
end;
