using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SaySo.Core.Diagnostics;
using SaySo.Core.Dictation;
using SaySo.Core.Engines.Gemini;
using SaySo.Core.Hotkeys;
using SaySo.Core.Input;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;
using SaySo.Core.Settings;
using SaySo.Core.Storage;
using SaySo.Core.Updates;
using SaySo.Mac.Interop;
using SaySo.Mac.Overlay;
using SaySo.Mac.Platform;

namespace SaySo.Mac.Daemon;

/// <summary>
/// Фоновый процесс SaySo для macOS: значок в строке меню, горячая клавиша, IPC — то же устройство, что у Windows-демона
/// (сценарий диктовки, отложенный выход, переустановка перехвата клавиш), на AppKit. Всё — на главном потоке.
/// </summary>
internal sealed class DaemonHost : IDisposable
{
    private const uint CmdInsertLast = 1, CmdCopyLast = 2, CmdSettings = 3, CmdPause = 4, CmdExit = 5, CmdUpdate = 6, CmdAccessibility = 7;
    private const nuint TerminateNow = 1, TerminateLater = 2;
    private static readonly TimeSpan QuitWait = IpcProtocol.QuitGrace;
    private static readonly TimeSpan AccessibilityPoll = TimeSpan.FromSeconds(2);

    private readonly SettingsStore _settingsStore;
    private readonly SecretStore _secretStore;
    private readonly HistoryStore _history;
    private readonly LaunchAgent _autostart = new();
    private readonly MacTextInserter _inserter = new(TimeProvider.System);
    private readonly StatusItem _status;
    private readonly OverlayPanel _overlay;
    private readonly HttpClient _http = SettingsDictationEngines.CreateHttpClient();
    private readonly SettingsDictationEngines _engines;
    private readonly DictationCoordinator _dictation;
    private readonly UpdateCoordinator _updates;
    private readonly IpcServer _ipc;
    private readonly SystemEvents _system;
    private readonly PosixSignalRegistration _sigterm;
    private HotkeyTap? _hotkey;
    private IDisposable? _accessibilityPoll;
    private TrayState _trayState = TrayState.Idle;
    private AppSettings _settings;
    private bool _paused;
    private bool _ipcSuspended;
    private IDisposable? _trimTimer;

    // Что реально применено к перехвату. SetSuspended и Configure сбрасывают автомат (посреди удержания клавиши это ломает
    // сессию), поэтому вызываются только при изменении значения и только когда диктовка не идёт.
    private bool _appliedSuspended;
    private int[] _appliedKeys = [];
    private int _appliedThresholdMs;
    private bool _cancelInFlight;
    private bool _disposed;
    private string? _pendingReinstall;
    private int _pressTarget;
    private int _dictationTarget;
    private bool _quitAfterSession;
    private bool _terminatePending;
    private IDisposable? _quitTimer;
    private string? _lastErrorDetail;

    private DaemonHost(AppPaths paths, SettingsStore settingsStore, AppSettings settings)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _secretStore = new SecretStore(paths);
        _history = new HistoryStore(paths);
        _engines = new SettingsDictationEngines(() => _settings, _secretStore.Load, _http, () => new ClientWebSocketTransport());
        _overlay = new OverlayPanel(() => _dictation?.CurrentLevel ?? 0f);
        _dictation = new DictationCoordinator(
            new DictationPipeline(_engines),
            (onData, onError) => new MacAudioSource(_settings.MicrophoneDeviceId, _settings.MicrophoneDeviceName, onData, onError),
            _overlay,
            new InserterOutput(_inserter, () => _dictationTarget = _pressTarget),
            new DaemonFeedback(OnDictationTray, () => _settings.SoundTheme),
            new MainQueueScheduler(),
            _history,
            () => _settings,
            () => _hotkey?.Reset(),
            // Асинхронно: onError приходит с потока Core Audio, а остановка захвата ждёт его.
            Dispatch.Post,
            TimeProvider.System);
        _updates = new UpdateCoordinator(
            new MacUpdateHost(_overlay, () => _dictation.State, () => _updates, OnUpdateOperationFinished),
            new UpdateStateStore(paths),
            typeof(DaemonHost).Assembly.GetName().Version ?? new Version(0, 0, 0),
            UpdateEnvironment.Current(),
            () => _settings.CheckForUpdates,
            UpdateHttp.Create,
            TimeProvider.System);
        _status = new StatusItem(BuildMenu, OnMenuCommand, () => TryReinstallHotkey("меню значка"));
        _ipc = new IpcServer(OnIpc);
        _system = new SystemEvents();
        _system.Resumed += () => TryReinstallHotkey("выход из сна", cancelRecording: true);
        _system.Resumed += _updates.OnResume;
        _system.LockChanged += OnSessionLockChanged;
        // SIGTERM (kill, launchctl stop) — штатное завершение, как IPC Quit.
        _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Dispatch.Post(RequestQuit);
        });
        _dictation.SessionEnded += OnSessionEnded;
        _overlay.Released += () =>
        {
            if (!_disposed)
            {
                ScheduleTrim(TimeSpan.FromSeconds(1));
            }
        };
        CreateHotkey();
        UpdateTray(TrayState.Idle);
        ScheduleTrim(TimeSpan.FromSeconds(3));
        _updates.OnStart();
    }

    /// <summary>
    /// Приложение для «Вставить последнее»: то, что было на переднем плане в момент нажатия горячей клавиши последней
    /// диктовки, давшей текст. До первой такой диктовки — приложение последней вставки.
    /// </summary>
    private int LastTarget => _dictationTarget != 0 ? _dictationTarget : (int)(_inserter.Last?.Window ?? 0);

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SaySo");

    public static int Run()
    {
        var instance = InstanceLock.TryAcquire("daemon");
        if (instance is null)
        {
            SendOpenSettings();
            return 0;
        }

        using (instance)
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

            DaemonHost? host = null;
            using var appDelegate = new Bridge
            {
                // Повторный запуск SaySo.app (Finder, Launchpad, Spotlight), пока демон работает: открыть настройки.
                ShouldHandleReopen = () =>
                {
                    host?.OpenSettings();
                    return false;
                },
                ShouldTerminate = () => host?.OnTerminateRequested() ?? TerminateNow,
            };
            AppHost.Initialize(NS.ActivationAccessory, appDelegate);
            using (host = new DaemonHost(paths, store, settings))
            {
                host.SyncAutostart();
                Log.Info($"Демон запущен (macOS {Environment.OSVersion.Version}, {RuntimeInformation.ProcessArchitecture})");
                host.RequestPermissions();
                var secrets = host._secretStore.Load();
                if (firstRun || (secrets.GeminiApiKeys.Count == 0 && string.IsNullOrWhiteSpace(secrets.GroqApiKey)))
                {
                    host.OpenSettings();
                }

                AppHost.Run();
                Log.Info("Демон завершён");
            }
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Идущую запись останавливаем до разрушения окон: иначе микрофон остался бы открытым.
        _disposed = true;
        if (_dictation.State == DictationState.Recording)
        {
            _ = ObserveAsync(_dictation.HandleAsync(HotkeyAction.Cancel), "Отмена диктовки при выходе");
        }

        _accessibilityPoll?.Dispose();
        _quitTimer?.Dispose();
        _trimTimer?.Dispose();
        _hotkey?.Dispose();
        _hotkey = null;
        _sigterm.Dispose();
        _ipc.Dispose();
        _system.Dispose();
        _updates.Dispose();
        _overlay.Dispose();
        _http.Dispose();
        _status.Dispose();
    }

    /// <summary>Окно демона появляется не сразу после захвата блокировки, поэтому несколько секунд повторяем отправку.</summary>
    private static void SendOpenSettings()
    {
        var timer = Stopwatch.StartNew();
        while (!Ipc.Send(IpcProtocol.OpenSettings) && timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// Разрешения macOS при старте: микрофон спрашиваем сразу (иначе системный диалог появился бы посреди первой диктовки),
    /// «Универсальный доступ» — системным окном со ссылкой в Системные настройки.
    /// </summary>
    private void RequestPermissions()
    {
        if (MicrophonePermission.Current() == MicrophonePermission.Status.NotDetermined)
        {
            _ = MicrophonePermission.RequestAsync();
        }

        if (!Accessibility.IsTrusted)
        {
            Log.Info("«Универсальный доступ» не разрешён: горячая клавиша заработает после разрешения");
            Accessibility.Prompt();
        }
    }

    private void OnHotkey(HotkeyAction action)
    {
        if (_disposed || (action == HotkeyAction.StartRecording && Suspended))
        {
            return;
        }

        Log.Info($"Горячая клавиша: {action}");
        if (action == HotkeyAction.StartRecording && _dictation.State == DictationState.Idle)
        {
            _pressTarget = NS.FrontmostPid(); // станет целью «Вставить последнее», только если диктовка даст текст
        }

        _ = ObserveAsync(_dictation.HandleAsync(action), "Диктовка");
    }

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

    private IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        var last = _history.Load().FirstOrDefault();
        var canInsert = last is not null && LastTarget != 0 && NS.IsRunning(LastTarget);
        var items = new List<TrayMenuItem>();
        if (!Accessibility.IsTrusted)
        {
            items.Add(new TrayMenuItem(CmdAccessibility, "Разрешить Универсальный доступ…"));
            items.Add(TrayMenuItem.Separator);
        }

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
            new TrayMenuItem(CmdExit, "Выйти из SaySo"),
        ]);
        return items;
    }

    private void OnMenuCommand(uint command)
    {
        switch (command)
        {
            case CmdInsertLast:
                if (_history.Load().FirstOrDefault() is { } last)
                {
                    _ = InsertLastAsync(last.Text);
                }

                break;
            case CmdCopyLast:
                if (_history.Load().FirstOrDefault() is { } copied && !Pasteboard.SetText(copied.Text, transient: false))
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
                UpdateTray(_dictation.State == DictationState.Recording ? _trayState : RestingTrayState);
                break;
            case CmdExit:
                RequestQuit();
                break;
            case CmdUpdate:
                _ = ObserveAsync(_updates.InstallAsync(), "Обновление из меню");
                break;
            case CmdAccessibility:
                Accessibility.Prompt();
                NS.Open(Accessibility.SettingsUrl);
                break;
        }
    }

    private async Task InsertLastAsync(string text)
    {
        try
        {
            var target = LastTarget;
            NS.Activate(target); // после меню значка фокус может быть не у того приложения
            await Task.Delay(150);
            var outcome = await _inserter.InsertAsync(text, [], expectedPid: target);
            Log.Info($"Вставить последнее: {outcome}");
            ShowMenuActionProblem(outcome);
        }
        catch (Exception e)
        {
            Log.Error("Вставить последнее", e);
        }
    }

    // Пункты меню сообщают о неудаче теми же строками плашки, что и диктовка; иначе щелчок «ничего не делает».
    private void ShowMenuActionProblem(InsertOutcome outcome)
    {
        if (OverlayMessages.ForMenuInsertOutcome(outcome) is { } text && _dictation.State == DictationState.Idle)
        {
            _overlay.ShowMessage(text, OverlayTone.Error);
        }
    }

    private void OnIpc(string message)
    {
        if (_disposed)
        {
            return;
        }

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
                    Log.Info("settings.json не прочитан — остаются прежние настройки");
                }

                _engines.ResetKeyPauses(); // настройки изменены пользователем: ключ мог быть исправлен, а VPN включён
                _ipcSuspended = false;
                if (_hotkey is null)
                {
                    CreateHotkey();
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

    /// <summary>
    /// Выход. Во время «Finishing» текст ещё вставляется, а буфер обмена восстанавливается — выходим по окончании сессии,
    /// но не дольше QuitWait (страховка от зависшего движка). Повторный запрос выхода — немедленный выход.
    /// </summary>
    private void RequestQuit()
    {
        if (_dictation.State != DictationState.Finishing || _quitAfterSession)
        {
            QuitNow();
            return;
        }

        _quitAfterSession = true;
        Log.Info("Выход отложен до завершения диктовки");
        _quitTimer = Dispatch.After(QuitWait, QuitNow);
    }

    private void QuitNow()
    {
        _quitTimer?.Dispose();
        _quitTimer = null;
        if (_terminatePending)
        {
            // Завершение по запросу системы (выход из учётной записи, «Завершить» в Мониторинге системы): ресурсы освобождаем
            // сами, затем AppKit завершает процесс.
            _terminatePending = false;
            Dispose();
            ObjC.SendVoid(NS.App, ObjC.Sel("replyToApplicationShouldTerminate:"), true);
            return;
        }

        AppHost.Stop(); // Run вернётся, и Dispose выполнит using в Run
    }

    /// <summary>applicationShouldTerminate: — система или пользователь завершают SaySo не через наше меню.</summary>
    private nuint OnTerminateRequested()
    {
        if (_disposed)
        {
            return TerminateNow;
        }

        if (_dictation.State != DictationState.Finishing)
        {
            Dispose();
            return TerminateNow;
        }

        _terminatePending = true;
        RequestQuit();
        return TerminateLater;
    }

    private void OnSessionLockChanged(bool locked)
    {
        if (locked)
        {
            Log.Info("Экран заблокирован");
            _ = ObserveAsync(_dictation.HandleAsync(HotkeyAction.Cancel), "Отмена диктовки при блокировке");
            _hotkey?.Reset();
            return;
        }

        TryReinstallHotkey("разблокировка экрана");
        _updates.OnUnlock();
    }

    private void CreateHotkey()
    {
        if (!Accessibility.IsTrusted)
        {
            // Без «Универсального доступа» перехват заведомо не создастся: ждём разрешения, не засоряя журнал ошибками.
            _hotkey = null;
            StartAccessibilityPoll();
            return;
        }

        try
        {
            _hotkey = new HotkeyTap(_settings.Hotkey.Keys, _settings.Hotkey.TapThresholdMs, OnHotkey);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // Без перехвата демон остаётся жить (значок, меню, IPC). Обычная причина — нет «Универсального доступа»:
            // пока его нет, раз в 2 с проверяем, не выдал ли его пользователь (других таймеров в простое нет).
            Log.Error("Перехват клавиатуры", e);
            _hotkey = null;
            StartAccessibilityPoll();
            return;
        }

        _accessibilityPoll?.Dispose();
        _accessibilityPoll = null;
        _appliedSuspended = false;
        _appliedKeys = [.. _settings.Hotkey.Keys];
        _appliedThresholdMs = _settings.Hotkey.TapThresholdMs;
        ReconcileHotkey();
        UpdateTray(_trayState);
        Log.Info("Перехват клавиатуры установлен");
    }

    private void StartAccessibilityPoll()
    {
        _accessibilityPoll ??= Dispatch.Every(AccessibilityPoll, () =>
        {
            if (_hotkey is null && Accessibility.IsTrusted && _dictation.State == DictationState.Idle)
            {
                CreateHotkey();
            }
        });
    }

    // Система может выключить перехват (смена прав, сон); переустанавливаем по событиям (выход из сна, разблокировка,
    // открытие меню значка) и только в простое.
    private void TryReinstallHotkey(string reason, bool cancelRecording = false)
    {
        if (_hotkey is { IsEnabled: true })
        {
            return; // перехват жив: пересоздавать нечего
        }

        if (_dictation.State != DictationState.Idle)
        {
            _pendingReinstall = reason;
            if (cancelRecording && _dictation.State == DictationState.Recording && !_cancelInFlight)
            {
                _ = CancelThenReconcileAsync();
            }

            return;
        }

        _pendingReinstall = null;
        if (_settings.Hotkey.Keys.Any(Keyboard.IsKeyDown))
        {
            return;
        }

        _hotkey?.Dispose();
        _hotkey = null;
        CreateHotkey();
        if (_hotkey is not null)
        {
            Log.Info($"Перехват клавиатуры переустановлен ({reason})");
        }
    }

    private void OnSessionEnded()
    {
        if (_disposed)
        {
            return;
        }

        if (_quitAfterSession)
        {
            QuitNow();
            return;
        }

        ScheduleTrim(TimeSpan.FromSeconds(2));
        ApplyPendingHotkeyChanges();
        _updates.OnSessionEnded();
    }

    private void ApplyPendingHotkeyChanges()
    {
        if (_pendingReinstall is { } reason)
        {
            TryReinstallHotkey(reason);
        }

        ReconcileHotkey();
    }

    private bool Suspended => _paused || _ipcSuspended;

    /// <summary>
    /// Приводит перехват к нужному состоянию (пауза, клавиши из настроек), вызывая SetSuspended/Configure только при
    /// изменении. То, что сбрасывает автомат, не применяется посреди диктовки: запись отменяется, а при «Finishing»
    /// применение откладывается до <see cref="OnSessionEnded"/>. Снять паузу можно в любой момент.
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
            Log.Error("Отмена диктовки перед перенастройкой перехвата", e);
        }
        finally
        {
            _cancelInFlight = false;
        }

        if (_dictation.State != DictationState.Recording && !_disposed)
        {
            ApplyPendingHotkeyChanges();
        }
    }

    private void OpenSettings()
    {
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
        try
        {
            _autostart.Set(_settings.AutostartEnabled, ExePath);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Log.Error("Автозапуск", e);
        }
    }

    private TrayState RestingTrayState => _paused ? TrayState.Paused : TrayState.Idle;

    private void OnDictationTray(TrayState state, string? detail) =>
        UpdateTray(state == TrayState.Idle && _paused ? TrayState.Paused : state, detail);

    private void OnUpdateOperationFinished()
    {
        if (!_disposed)
        {
            ScheduleTrim(TimeSpan.FromSeconds(2));
        }
    }

    private void ScheduleTrim(TimeSpan delay)
    {
        _trimTimer?.Dispose();
        _trimTimer = Dispatch.After(delay, () =>
        {
            if (_dictation.State == DictationState.Idle)
            {
                MemoryTrimmer.Trim();
            }
        });
    }

    private void UpdateTray(TrayState state, string? errorDetail = null)
    {
        _trayState = state;
        if (state == TrayState.Error && errorDetail is not null)
        {
            _lastErrorDetail = errorDetail;
        }

        var tooltip = state switch
        {
            TrayState.Paused => TrayTooltip.Paused,
            TrayState.Recording => TrayTooltip.Recording,
            TrayState.Error => TrayTooltip.ForError(_lastErrorDetail),
            _ when _hotkey is null => "SaySo: разрешите Универсальный доступ в Системных настройках",
            _ => TrayTooltip.Idle(HotkeyFormatter.Format(_settings.Hotkey.Keys)),
        };
        _status.Set(state, tooltip);
    }
}
