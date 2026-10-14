using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using SaySo.Core.Diagnostics;
using SaySo.Core.Dictation;
using SaySo.Core.Engines.Gemini;
using SaySo.Core.Hotkeys;
using SaySo.Core.Input;
using SaySo.Core.Interop;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;
using SaySo.Core.Settings;
using SaySo.Core.Storage;
using SaySo.Core.Updates;

namespace SaySo.Daemon;

/// <summary>Фоновый процесс SaySo: трей, горячая клавиша, IPC. XAML здесь не используется.</summary>
internal sealed class DaemonHost : IDisposable
{
    private const uint CmdInsertLast = 1, CmdCopyLast = 2, CmdSettings = 3, CmdPause = 4, CmdExit = 5, CmdUpdate = 6;
    private static readonly TimeSpan QuitWait = IpcProtocol.QuitGrace;
    private const string MutexName = @"Local\SaySo.Daemon";

    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly SecretStore _secretStore;
    private readonly HistoryStore _history;
    private readonly AutostartManager _autostart = new();
    private readonly DaemonWindow _window;
    private readonly TrayIcon _tray;
    private readonly TextInserter _inserter;
    private HotkeyListener? _hotkey;
    private TrayIcons _icons;
    private TrayState _trayState = TrayState.Idle;
    private AppSettings _settings;
    private bool _paused;
    private bool _ipcSuspended;
    private readonly OverlayWindow _overlay;
    private readonly HttpClient _http = SettingsDictationEngines.CreateHttpClient();
    private readonly SettingsDictationEngines _engines;
    private readonly DictationCoordinator _dictation;
    private readonly UpdateCoordinator _updates;
    private IDisposable? _trimTimer;

    // Что реально применено к слушателю хука. SetSuspended и Configure сбрасывают автомат (посреди удержания клавиши
    // это ломает сессию), поэтому вызываются только при изменении значения и только когда диктовка не идёт.
    private bool _appliedSuspended;
    private int[] _appliedKeys = [];
    private int _appliedThresholdMs;
    private bool _cancelInFlight;
    private bool _failingSession;
    private bool _disposing;
    private string? _pendingReinstall;
    private nint _pressTarget;
    private nint _dictationTarget;
    private bool _quitAfterSession;
    private IDisposable? _quitTimer;
    private string? _lastErrorDetail;
    private readonly MinIntervalGate _clickReinstallGate = new(TimeSpan.FromSeconds(1), TimeProvider.System);

    private DaemonHost(AppPaths paths, SettingsStore settingsStore, AppSettings settings)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _settings = settings;
        _secretStore = new SecretStore(paths);
        _history = new HistoryStore(paths);
        _window = new DaemonWindow();
        SynchronizationContext.SetSynchronizationContext(_window.Sync);
        _inserter = new TextInserter(_window.Handle, TimeProvider.System);
        _icons = TrayIcons.Create();
        _tray = new TrayIcon(_window.Handle, DaemonWindow.TrayMessage, (due, action) => _window.SetTimer(due, action));

        // Контекст синхронизации главного потока уже установлен выше: продолжения HandleAsync возвращаются на этот поток.
        _engines = new SettingsDictationEngines(() => _settings, _secretStore.Load, _http, () => new ClientWebSocketTransport());
        _overlay = new OverlayWindow(() => _dictation?.CurrentLevel ?? 0f);
        _dictation = new DictationCoordinator(
            new DictationPipeline(_engines),
            (onData, onError) => new WasapiAudioSource(_settings.MicrophoneDeviceId, _settings.MicrophoneDeviceName, onData, onError),
            _overlay,
            new InserterOutput(_inserter, () => _dictationTarget = _pressTarget),
            new DaemonFeedback(OnDictationTray),
            new WindowScheduler(_window, OnSchedulingFailed),
            _history,
            () => _settings,
            () => _hotkey?.Reset(),
            // Именно асинхронный Post: onError приходит с потока WASAPI, а StopCapture ждёт его завершения — Send зашёл бы в тупик.
            action => _window.Sync.Post(_ => action(), null),
            TimeProvider.System);
        _updates = new UpdateCoordinator(
            new DaemonUpdateHost(_window, _tray, _overlay, () => _dictation.State, OnUpdateOperationFinished),
            new UpdateStateStore(paths),
            typeof(DaemonHost).Assembly.GetName().Version ?? new Version(0, 0, 0),
            UpdateEnvironment.Current(),
            () => _settings.CheckForUpdates,
            UpdateHttp.Create, // свой HttpClient на каждую операцию: после неё в процессе нет ни одного сокета
            TimeProvider.System);
        CreateHotkey();

        _window.HotkeyPressed += OnHotkey;
        _window.TrayEvent += OnTrayEvent;
        _window.IpcMessage += OnIpc;
        _window.CloseRequested += OnCloseRequested;
        _window.TaskbarCreated += OnTaskbarCreated;
        _window.SessionLockChanged += OnSessionLockChanged;
        _window.PowerResumed += () => TryReinstallHotkey("выход из сна", cancelRecording: true);
        _window.PowerResumed += _updates.OnResume;
        _window.ThemeChanged += RefreshIcons;
        _dictation.SessionEnded += OnSessionEnded;
        UpdateTray(TrayState.Idle);
        ScheduleTrim(TimeSpan.FromSeconds(3));
        _updates.OnStart(); // одноразовый таймер на 60 с, если с прошлой проверки прошли сутки
    }

    /// <summary>
    /// Окно для «Вставить последнее»: окно переднего плана в момент нажатия горячей клавиши последней диктовки,
    /// давшей текст (отменённые, короткие и неудавшиеся записи не в счёт). До первой такой диктовки — окно последней вставки.
    /// </summary>
    internal nint LastTarget => _dictationTarget != 0 ? _dictationTarget : _inserter.Last?.Window ?? 0;

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SaySo.exe");

    public static int Run()
    {
        var mutex = AcquireInstance();
        if (mutex is null)
        {
            return 0;
        }

        try
        {
            var paths = AppPaths.Default;
            paths.EnsureRoot();
            Log.FilePath = Path.Combine(paths.Root, "sayso.log");
            var store = new SettingsStore(paths);
            var firstRun = !File.Exists(paths.SettingsFile);
            var settings = store.Load();
            if (firstRun)
            {
                store.Save(settings);
            }

            if (settings.RunAsAdmin && !ProcessIntegrity.IsElevated && AdminTask.Exists())
            {
                Log.Info("Перезапуск с правами администратора через Планировщик");
                // Мьютекс нужно освободить и закрыть: элевированный экземпляр, запущенный Планировщиком, должен его создать.
                ReleaseInstance(mutex);
                mutex = null;
                var exitCode = AdminTask.Run();
                if (exitCode == 0)
                {
                    return 0;
                }

                Log.Info($"Планировщик не запустил задачу (код {exitCode}) — запуск без повышения прав");
                mutex = AcquireInstance();
                if (mutex is null)
                {
                    return 0;
                }
            }

            int code;
            using (var host = new DaemonHost(paths, store, settings))
            {
                host.SyncAutostart();
                Log.Info($"Демон запущен (elevated={ProcessIntegrity.IsElevated})");
                var secrets = host._secretStore.Load();
                if (firstRun || (secrets.GeminiApiKeys.Count == 0 && string.IsNullOrWhiteSpace(secrets.GroqApiKey)))
                {
                    host.OpenSettings();
                }

                code = MessageLoop.Run();
                Log.Info("Демон завершён");
            }

            return code;
        }
        finally
        {
            // Мьютекс отпускается после снятия хука и значка: новый экземпляр не застанет старый хук.
            if (mutex is not null)
            {
                ReleaseInstance(mutex);
            }
        }
    }

    public void Dispose()
    {
        // Идущую запись останавливаем до разрушения окон: иначе микрофон остался бы открытым, а текст — недоставленным.
        _disposing = true;
        if (_dictation.State == DictationState.Recording)
        {
            _ = ObserveAsync(_dictation.HandleAsync(HotkeyAction.Cancel), "Отмена диктовки при выходе");
        }

        _hotkey?.Dispose();
        _hotkey = null;
        _updates.Dispose();
        _overlay.Dispose();
        _http.Dispose();
        _tray.Dispose();
        _icons.Dispose();
        _window.Dispose();
    }

    /// <returns>Мьютекс единственного экземпляра или null, если уже работает другой (ему отправлено OpenSettings).</returns>
    private static Mutex? AcquireInstance()
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Мьютекс создан элевированным демоном (его DACL не пускает обычный процесс) — значит, демон уже работает.
            if (SendOpenSettings())
            {
                return null;
            }

            // Демон не отвечает: возможно, он как раз завершился и имя освободилось — одна повторная попытка.
            try
            {
                mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (createdNew)
        {
            return mutex;
        }

        if (SendOpenSettings())
        {
            mutex.Dispose();
            return null;
        }

        // Окно демона не отвечает: возможно, предыдущий экземпляр как раз завершается — ждём освобождения имени.
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // предыдущий экземпляр умер, не отпустив мьютекс
        }

        if (acquired)
        {
            return mutex;
        }

        mutex.Dispose();
        return null;
    }

    private static void ReleaseInstance(Mutex mutex)
    {
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // мьютекс этому потоку не принадлежит — закрываем только дескриптор
        }

        mutex.Dispose();
    }

    /// <summary>Окно демона появляется не сразу после создания мьютекса, поэтому несколько секунд повторяем отправку.</summary>
    private static bool SendOpenSettings()
    {
        var timer = Stopwatch.StartNew();
        while (!IpcClient.Send(IpcProtocol.OpenSettings))
        {
            if (timer.Elapsed >= TimeSpan.FromSeconds(3))
            {
                return false;
            }

            Thread.Sleep(200);
        }

        return true;
    }

    private void OnHotkey(HotkeyAction action)
    {
        // Сообщение могло быть отправлено хуком до паузы или до начала записи комбинации в настройках.
        if (action == HotkeyAction.StartRecording && Suspended)
        {
            return;
        }

        Log.Info($"Горячая клавиша: {action}");
        if (action == HotkeyAction.StartRecording && _dictation.State == DictationState.Idle)
        {
            _pressTarget = Native.GetForegroundWindow(); // станет целью «Вставить последнее», только если диктовка даст текст
        }

        _ = ObserveAsync(_dictation.HandleAsync(action), "Диктовка");
    }

    // Исключение из async-метода без ожидающего иначе пропало бы незамеченным.
    private static async Task ObserveAsync(Task task, string context)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            Log.Error(context, e);
        }
    }

    private void OnTrayEvent(uint trayEvent)
    {
        switch (trayEvent)
        {
            case Native.WM_LBUTTONUP:
                // Щелчок по значку — путь восстановления для пользователя, если система сняла хук.
                // Двойной щелчок приносит два WM_LBUTTONUP: второй раз в течение секунды хук не пересоздаём.
                if (_clickReinstallGate.TryPass())
                {
                    TryReinstallHotkey("щелчок по значку", cancelRecording: true);
                }

                break;
            case Native.WM_LBUTTONDBLCLK:
                OpenSettings();
                break;
            case Native.WM_CONTEXTMENU:
                TryReinstallHotkey("меню значка");
                ShowMenu();
                break;
            case Native.NIN_BALLOONUSERCLICK:
                _ = ObserveAsync(_updates.OnBalloonClickedAsync(), "Обновление по щелчку на уведомлении");
                break;
            case Native.NIN_BALLOONTIMEOUT or Native.NIN_BALLOONHIDE:
                _updates.OnBalloonClosed(); // закрыто крестиком или по таймауту: о этой версии больше не всплываем
                break;
        }
    }

    private void OnTaskbarCreated()
    {
        // Проводник перезапущен: значок добавляется заново, а наборы значков пересоздаются под DPI новой панели задач.
        _tray.Recreate();
        RefreshIcons();
    }

    private void ShowMenu()
    {
        var last = _history.Load().FirstOrDefault();
        var canInsert = last is not null && LastTarget != 0 && Native.IsWindow(LastTarget);
        var items = new List<TrayMenuItem>();
        if (_updates.Pending is { } update)
        {
            items.Add(new TrayMenuItem(CmdUpdate, UpdateMessages.MenuItem(update.Version, _updates.PendingNeedsManualInstall)));
            items.Add(TrayMenuItem.Separator);
        }

        items.AddRange(
        [
            new TrayMenuItem(CmdInsertLast, "Вставить последнее", canInsert),
            new TrayMenuItem(CmdCopyLast, "Скопировать последнее", last is not null),
            TrayMenuItem.Separator,
            new TrayMenuItem(CmdSettings, "Настройки…"),
            new TrayMenuItem(CmdPause, "Приостановить", Checked: _paused),
            TrayMenuItem.Separator,
            new TrayMenuItem(CmdExit, "Выход"),
        ]);
        var command = TrayMenu.Show(_window.Handle, items);

        switch (command)
        {
            case CmdInsertLast when last is not null:
                _ = InsertLastAsync(last.Text);
                break;
            case CmdCopyLast when last is not null:
                if (!ClipboardText.SetText(_window.Handle, last.Text, excludeFromHistory: false))
                {
                    Log.Info("Скопировать последнее: буфер обмена недоступен");
                    ShowMenuActionProblem(InsertOutcome.ClipboardUnavailable);
                }

                break;
            case CmdSettings:
                OpenSettings();
                break;
            case CmdPause:
                _paused = !_paused;
                _ipcSuspended = false; // ручная пауза/возобновление отменяет «зависшую» приостановку от окна настроек
                ReconcileHotkey();
                // Идущую запись отменит ReconcileHotkey; значок после неё станет «приостановлено» через OnDictationTray.
                UpdateTray(_dictation.State == DictationState.Recording ? _trayState : RestingTrayState);
                break;
            case CmdExit:
                RequestQuit();
                break;
            case CmdUpdate:
                _ = ObserveAsync(_updates.InstallAsync(), "Обновление из меню");
                break;
        }
    }

    private async Task InsertLastAsync(string text)
    {
        try
        {
            // После меню трея процесс на переднем плане и может вернуть фокус целевому окну.
            Native.SetForegroundWindow(LastTarget);
            await Task.Delay(100);
            var outcome = await _inserter.InsertAsync(text, [], expectedForeground: LastTarget);
            Log.Info($"Вставить последнее: {outcome}");
            ShowMenuActionProblem(outcome);
        }
        catch (Exception e)
        {
            Log.Error("Вставить последнее", e);
        }
    }

    // Пункты меню трея сообщают о неудаче теми же строками оверлея, что и диктовка; иначе щелчок «ничего не делает».
    private void ShowMenuActionProblem(InsertOutcome outcome)
    {
        if (OverlayMessages.ForMenuInsertOutcome(outcome) is { } text && _dictation.State == DictationState.Idle)
        {
            _overlay.ShowMessage(text, OverlayTone.Error);
        }
    }

    private void OnIpc(string message)
    {
        Log.Info($"IPC: {message}");
        switch (message)
        {
            case IpcProtocol.ReloadSettings:
                if (_settingsStore.TryLoad(out var reloaded))
                {
                    _settings = reloaded;
                }
                else
                {
                    // Файл временно занят: откат на умолчания сбросил бы горячую клавишу и пересоздал выключенный автозапуск.
                    Log.Info("settings.json не прочитан — остаются прежние настройки");
                }

                _engines.ResetKeyPauses(); // настройки изменены пользователем: ключ мог быть исправлен, а VPN включён
                _ipcSuspended = false; // окно настроек закончило запись комбинации (или умерло, не прислав Resume)
                if (_hotkey is null)
                {
                    CreateHotkey(); // прежняя попытка не удалась — пробуем снова, уже с новыми клавишами
                }
                else
                {
                    ReconcileHotkey();
                }

                SyncAutostart();
                UpdateTray(_dictation.State == DictationState.Idle ? RestingTrayState : _trayState);
                break;
            case IpcProtocol.SuspendHotkey:
                _ipcSuspended = true;
                ReconcileHotkey();
                break;
            case IpcProtocol.ResumeHotkey:
                _ipcSuspended = false;
                ReconcileHotkey();
                break;
            case IpcProtocol.OpenSettings:
                OpenSettings();
                break;
            case IpcProtocol.Quit:
                RequestQuit();
                break;
            case IpcProtocol.CheckUpdates:
                _ = ObserveAsync(_updates.CheckNowAsync(), "Проверка обновлений по просьбе окна настроек");
                break;
            case IpcProtocol.InstallUpdate:
                _ = ObserveAsync(_updates.InstallAsync(), "Установка обновления по просьбе окна настроек");
                break;
        }
    }

    // WM_CLOSE (taskkill без /F) — то же штатное завершение, что и по IPC Quit.
    private void OnCloseRequested()
    {
        Log.Info("WM_CLOSE: штатное завершение");
        RequestQuit();
    }

    // Выход из цикла сообщений; хук и значок снимает Dispose сразу после него.
    // Во время «Finishing» текст ещё вставляется, а буфер обмена восстанавливается — выходим по окончании сессии,
    // но не дольше QuitWait (страховка от зависшего движка). Повторный запрос выхода — немедленный выход.
    private void RequestQuit()
    {
        if (_dictation.State != DictationState.Finishing || _quitAfterSession)
        {
            Native.PostQuitMessage(0);
            return;
        }

        _quitAfterSession = true;
        Log.Info("Выход отложен до завершения диктовки");
        try
        {
            _quitTimer = _window.SetTimer(QuitWait, () => Native.PostQuitMessage(0));
        }
        catch (Win32Exception e)
        {
            Log.Error("Страховочный таймер выхода", e);
            Native.PostQuitMessage(0); // без страховки ждать сессию нельзя
        }
    }

    private void OnSessionLockChanged(bool locked)
    {
        if (locked)
        {
            Log.Info("Сеанс заблокирован");
            _ = ObserveAsync(_dictation.HandleAsync(HotkeyAction.Cancel), "Отмена диктовки при блокировке");
            _hotkey?.Reset();
            return;
        }

        TryReinstallHotkey("разблокировка сеанса");
        _updates.OnUnlock();
    }

    private HotkeyListener CreateListener() =>
        new(_window.Handle, DaemonWindow.HotkeyMessage, _settings.Hotkey.Keys, _settings.Hotkey.TapThresholdMs);

    private void CreateHotkey()
    {
        try
        {
            _hotkey = CreateListener();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or ArgumentException)
        {
            // Без хука демон остаётся жить (значок, меню, IPC); следующее событие переустановки или ReloadSettings попробует снова.
            Log.Error("Установка хука клавиатуры", e);
            _hotkey = null;
            return;
        }

        // Новый слушатель стартует без паузы и с текущими клавишами из настроек.
        _appliedSuspended = false;
        _appliedKeys = [.. _settings.Hotkey.Keys];
        _appliedThresholdMs = _settings.Hotkey.TapThresholdMs;
        ReconcileHotkey();
    }

    // Windows молча снимает WH_KEYBOARD_LL после тайм-аута колбэка. Таймеров в простое нет (§1/§11):
    // хук переустанавливается только по событиям (разблокировка, выход из сна, щелчок по значку), и только в простое.
    private void TryReinstallHotkey(string reason, bool cancelRecording = false)
    {
        if (_dictation.State != DictationState.Idle)
        {
            // Хук мог быть снят системой посреди удержания — клавиша отпущена «в пустоту», а запись идёт.
            // Явное действие пользователя (щелчок по значку) или выход из сна отменяет запись; иначе ждём конца сессии.
            _pendingReinstall = reason;
            if (cancelRecording && _dictation.State == DictationState.Recording && !_cancelInFlight)
            {
                _ = CancelThenReconcileAsync();
            }

            return;
        }

        _pendingReinstall = null;
        if (_settings.Hotkey.Keys.Any(KeyboardInjector.IsKeyDown))
        {
            return;
        }

        _hotkey?.Dispose();
        _hotkey = null;
        CreateHotkey(); // при неудаче _hotkey остаётся null — ReloadSettings или следующее событие повторят попытку
        if (_hotkey is not null)
        {
            Log.Info($"Хук клавиатуры переустановлен ({reason})");
        }
    }

    private void OnSessionEnded()
    {
        if (_disposing)
        {
            return;
        }

        if (_quitAfterSession)
        {
            _quitTimer?.Dispose(); // страховка больше не нужна: сессия закончилась сама
            _quitTimer = null;
            Native.PostQuitMessage(0);
            return;
        }

        ScheduleTrim(TimeSpan.FromSeconds(2));
        ApplyPendingHotkeyChanges();
        _updates.OnSessionEnded(); // отложенное уведомление, проверка или запуск Setup
    }

    private void ApplyPendingHotkeyChanges()
    {
        if (_pendingReinstall is { } reason)
        {
            TryReinstallHotkey(reason);
        }

        ReconcileHotkey();
    }

    // Таймеры сессии (старт движка, автостоп) не поставились — запись нельзя оставлять без автостопа: сессия отменяется с ошибкой.
    private void OnSchedulingFailed() => _window.Sync.Post(_ => _ = FailSessionAsync(), null);

    private async Task FailSessionAsync()
    {
        if (_failingSession || _dictation.State != DictationState.Recording)
        {
            return;
        }

        _failingSession = true;
        try
        {
            // Синхронная часть отмены гасит плашку и значок; ошибку показываем сразу после неё, не дожидаясь закрытия сессии движка.
            var cancel = _dictation.HandleAsync(HotkeyAction.Cancel);
            _overlay.ShowMessage(OverlayMessages.StartFailed, OverlayTone.Error);
            UpdateTray(TrayState.Error, OverlayMessages.StartFailed);
            if (_settings.SoundsEnabled)
            {
                SoundPlayer.Play(SoundKind.Error);
            }

            await cancel;
        }
        catch (Exception e)
        {
            Log.Error("Отмена сессии после сбоя таймера", e);
        }
        finally
        {
            _failingSession = false;
        }
    }

    // Хук пропускает все клавиши, пока демон приостановлен из трея или окно настроек записывает новую комбинацию.
    private bool Suspended => _paused || _ipcSuspended;

    /// <summary>
    /// Приводит слушатель хука к нужному состоянию (пауза, клавиши из настроек), вызывая SetSuspended/Configure только
    /// при изменении. То, что сбрасывает автомат, не применяется посреди диктовки: запись отменяется, а при
    /// «Finishing» применение откладывается до <see cref="OnSessionEnded"/>. Снять паузу можно в любой момент.
    /// </summary>
    private void ReconcileHotkey()
    {
        if (_hotkey is not { } hotkey)
        {
            return;
        }

        var suspend = Suspended;
        var keys = _settings.Hotkey.Keys;
        var threshold = _settings.Hotkey.TapThresholdMs;
        var keysChanged = !keys.SequenceEqual(_appliedKeys) || threshold != _appliedThresholdMs;
        if ((keysChanged || (suspend && !_appliedSuspended)) && _dictation.State != DictationState.Idle)
        {
            if (_dictation.State == DictationState.Recording && !_cancelInFlight)
            {
                _ = CancelThenReconcileAsync();
            }

            return;
        }

        if (keysChanged)
        {
            try
            {
                hotkey.Configure(keys, threshold);
            }
            catch (ArgumentException e)
            {
                Log.Error("Настройка горячей клавиши", e);
            }

            // Некорректную комбинацию повторно не применяем: иначе каждый вызов отменял бы диктовку.
            _appliedKeys = [.. keys];
            _appliedThresholdMs = threshold;
        }

        if (suspend != _appliedSuspended)
        {
            hotkey.SetSuspended(suspend);
            _appliedSuspended = suspend;
        }
    }

    private async Task CancelThenReconcileAsync()
    {
        _cancelInFlight = true;
        try
        {
            await _dictation.HandleAsync(HotkeyAction.Cancel);
        }
        catch (Exception e)
        {
            Log.Error("Отмена диктовки перед перенастройкой хука", e);
        }
        finally
        {
            _cancelInFlight = false;
        }

        // Отмена сама поднимает SessionEnded (в том числе при сбое микрофона); этот вызов страхует случай, когда отменять было уже нечего.
        if (_dictation.State != DictationState.Recording && !_disposing)
        {
            ApplyPendingHotkeyChanges();
        }
    }

    private void OpenSettings()
    {
        // Окно настроек запускается заново — «зависшая» приостановка от прежнего окна больше не нужна.
        _ipcSuspended = false;
        ReconcileHotkey();
        try
        {
            Process.Start(new ProcessStartInfo(ExePath, "--settings") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Error("Запуск окна настроек", e);
        }
    }

    private void SyncAutostart()
    {
        if (_settings.RunAsAdmin)
        {
            return; // автозапуск обеспечивает задача Планировщика
        }

        try
        {
            _autostart.Set(_settings.AutostartEnabled, ExePath);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or SecurityException)
        {
            Log.Error("Автозапуск", e);
        }
    }

    // Новые значки создаются и выставляются до уничтожения старых: трей не остаётся с освобождённым HICON.
    private void RefreshIcons()
    {
        var old = _icons;
        _icons = TrayIcons.Create();
        UpdateTray(_trayState);
        old.Dispose();
    }

    private TrayState RestingTrayState => _paused ? TrayState.Paused : TrayState.Idle;

    // Координатор сообщает «Idle» после каждой сессии; при ручной паузе значок остаётся «приостановлено».
    private void OnDictationTray(TrayState state, string? detail) =>
        UpdateTray(state == TrayState.Idle && _paused ? TrayState.Paused : state, detail);

    // Проверка или скачивание обновления закончились: HttpClient уничтожен, остаётся вернуть системе память кучи.
    private void OnUpdateOperationFinished()
    {
        if (!_disposing)
        {
            ScheduleTrim(TimeSpan.FromSeconds(2));
        }
    }

    private void ScheduleTrim(TimeSpan delay)
    {
        _trimTimer?.Dispose();
        try
        {
            _trimTimer = _window.SetTimer(delay, () =>
            {
                if (_dictation.State == DictationState.Idle)
                {
                    MemoryTrimmer.Trim();
                }
            });
        }
        catch (Win32Exception e)
        {
            Log.Error("Таймер сброса памяти", e); // не критично: память вернётся позже
            _trimTimer = null;
        }
    }

    private void UpdateTray(TrayState state, string? errorDetail = null)
    {
        _trayState = state;
        if (state == TrayState.Error && errorDetail is not null)
        {
            _lastErrorDetail = errorDetail; // RefreshIcons перерисовывает значок без текста: берём запомненный
        }

        var tooltip = state switch
        {
            TrayState.Paused => "SaySo — приостановлено",
            TrayState.Recording => "SaySo — запись…",
            TrayState.Error => TrayTooltip.ForError(_lastErrorDetail),
            _ => $"SaySo — удерживайте {HotkeyFormatter.Format(_settings.Hotkey.Keys)}",
        };
        _tray.Set(_icons.Get(state), tooltip);
    }
}
