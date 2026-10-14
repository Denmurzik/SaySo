using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using SaySo.Core.Diagnostics;
using SaySo.Core.Hotkeys;
using SaySo.Core.Input;
using SaySo.Core.Interop;
using SaySo.Core.Platform;
using SaySo.Core.Settings;
using SaySo.Core.Storage;

namespace SaySo.Daemon;

/// <summary>Фоновый процесс SaySo: трей, горячая клавиша, IPC. XAML здесь не используется.</summary>
internal sealed class DaemonHost : IDisposable
{
    private const uint CmdInsertLast = 1, CmdCopyLast = 2, CmdSettings = 3, CmdPause = 4, CmdExit = 5;
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
        _tray = new TrayIcon(_window.Handle, DaemonWindow.TrayMessage);
        CreateHotkey();

        _window.HotkeyPressed += OnHotkey;
        _window.TrayEvent += OnTrayEvent;
        _window.IpcMessage += OnIpc;
        _window.CloseRequested += OnCloseRequested;
        _window.TaskbarCreated += OnTaskbarCreated;
        _window.SessionLockChanged += OnSessionLockChanged;
        _window.PowerResumed += () => TryReinstallHotkey("выход из сна");
        _window.ThemeChanged += RefreshIcons;
        UpdateTray(TrayState.Idle);
    }

    /// <summary>Окно, в которое была последняя вставка (для «Вставить последнее»). Заполняется в задаче 16.</summary>
    internal nint LastTarget { get; set; }

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
        _hotkey?.Dispose();
        _hotkey = null;
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
            SendOpenSettings();
            return null;
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

    // Заглушка до задачи 16: только индикация.
    private void OnHotkey(HotkeyAction action)
    {
        Log.Info($"Горячая клавиша: {action}");
        switch (action)
        {
            case HotkeyAction.StartRecording:
                UpdateTray(TrayState.Recording);
                if (_settings.SoundsEnabled)
                {
                    SoundPlayer.Play(SoundKind.Start);
                }

                break;
            case HotkeyAction.StopRecording:
            case HotkeyAction.Cancel:
                UpdateTray(TrayState.Idle);
                _hotkey?.Reset();
                if (_settings.SoundsEnabled && action == HotkeyAction.StopRecording)
                {
                    SoundPlayer.Play(SoundKind.Stop);
                }

                break;
        }
    }

    private void OnTrayEvent(uint trayEvent)
    {
        switch (trayEvent)
        {
            case Native.WM_LBUTTONUP:
                // Щелчок по значку — путь восстановления для пользователя, если система сняла хук.
                TryReinstallHotkey("щелчок по значку");
                break;
            case Native.WM_LBUTTONDBLCLK:
                OpenSettings();
                break;
            case Native.WM_CONTEXTMENU:
                TryReinstallHotkey("меню значка");
                ShowMenu();
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
        var command = TrayMenu.Show(_window.Handle,
        [
            new TrayMenuItem(CmdInsertLast, "Вставить последнее", canInsert),
            new TrayMenuItem(CmdCopyLast, "Скопировать последнее", last is not null),
            TrayMenuItem.Separator,
            new TrayMenuItem(CmdSettings, "Настройки…"),
            new TrayMenuItem(CmdPause, "Приостановить", Checked: _paused),
            TrayMenuItem.Separator,
            new TrayMenuItem(CmdExit, "Выход"),
        ]);

        switch (command)
        {
            case CmdInsertLast when last is not null:
                _ = InsertLastAsync(last.Text);
                break;
            case CmdCopyLast when last is not null:
                if (!ClipboardText.SetText(_window.Handle, last.Text, excludeFromHistory: false))
                {
                    Log.Info("Скопировать последнее: буфер обмена недоступен");
                }

                break;
            case CmdSettings:
                OpenSettings();
                break;
            case CmdPause:
                _paused = !_paused;
                _ipcSuspended = false; // ручная пауза/возобновление отменяет «зависшую» приостановку от окна настроек
                ApplySuspension();
                UpdateTray(_paused ? TrayState.Paused : TrayState.Idle);
                break;
            case CmdExit:
                RequestQuit();
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
        }
        catch (Exception e)
        {
            Log.Error("Вставить последнее", e);
        }
    }

    private void OnIpc(string message)
    {
        Log.Info($"IPC: {message}");
        switch (message)
        {
            case IpcProtocol.ReloadSettings:
                _settings = _settingsStore.Load();
                _ipcSuspended = false; // окно настроек закончило запись комбинации (или умерло, не прислав Resume)
                ReconfigureHotkey();
                ApplySuspension();
                SyncAutostart();
                UpdateTray(_paused ? TrayState.Paused : TrayState.Idle);
                break;
            case IpcProtocol.SuspendHotkey:
                _ipcSuspended = true;
                ApplySuspension();
                break;
            case IpcProtocol.ResumeHotkey:
                _ipcSuspended = false;
                ApplySuspension();
                break;
            case IpcProtocol.OpenSettings:
                OpenSettings();
                break;
            case IpcProtocol.Quit:
                RequestQuit();
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
    private static void RequestQuit() => Native.PostQuitMessage(0);

    private void OnSessionLockChanged(bool locked)
    {
        if (locked)
        {
            _hotkey?.Reset();
            return;
        }

        TryReinstallHotkey("разблокировка сеанса");
    }

    private HotkeyListener CreateListener() =>
        new(_window.Handle, DaemonWindow.HotkeyMessage, _settings.Hotkey.Keys, _settings.Hotkey.TapThresholdMs);

    private void CreateHotkey()
    {
        try
        {
            _hotkey = CreateListener();
            ApplySuspension();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or ArgumentException)
        {
            // Без хука демон остаётся жить (значок, меню, IPC); следующее событие переустановки попробует снова.
            Log.Error("Установка хука клавиатуры", e);
            _hotkey = null;
        }
    }

    // Windows молча снимает WH_KEYBOARD_LL после тайм-аута колбэка. Таймеров в простое нет (§1/§11):
    // хук переустанавливается только по событиям (разблокировка, выход из сна, щелчок по значку), и только в простое.
    private void TryReinstallHotkey(string reason)
    {
        if (_trayState is not (TrayState.Idle or TrayState.Paused))
        {
            return;
        }

        if (_settings.Hotkey.Keys.Any(KeyboardInjector.IsKeyDown))
        {
            return;
        }

        var old = _hotkey;
        old?.Dispose();
        try
        {
            _hotkey = CreateListener();
            ApplySuspension();
            Log.Info($"Хук клавиатуры переустановлен ({reason})");
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or ArgumentException)
        {
            // Например, поток старого хука не завершился и он ещё занимает единственный слот: остаёмся на прежнем слушателе.
            Log.Error($"Переустановка хука клавиатуры ({reason})", e);
            _hotkey = old;
        }
    }

    private void ReconfigureHotkey()
    {
        if (_hotkey is null)
        {
            CreateHotkey();
            return;
        }

        try
        {
            _hotkey.Configure(_settings.Hotkey.Keys, _settings.Hotkey.TapThresholdMs);
        }
        catch (ArgumentException e)
        {
            Log.Error("Настройка горячей клавиши", e);
        }
    }

    // Хук пропускает все клавиши, пока демон приостановлен из трея или окно настроек записывает новую комбинацию.
    private void ApplySuspension() => _hotkey?.SetSuspended(_paused || _ipcSuspended);

    private void OpenSettings()
    {
        // Окно настроек запускается заново — «зависшая» приостановка от прежнего окна больше не нужна.
        _ipcSuspended = false;
        ApplySuspension();
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

    private void UpdateTray(TrayState state)
    {
        _trayState = state;
        var tooltip = state switch
        {
            TrayState.Paused => "SaySo — приостановлено",
            TrayState.Recording => "SaySo — запись…",
            _ => $"SaySo — удерживайте {HotkeyFormatter.Format(_settings.Hotkey.Keys)}",
        };
        _tray.Set(_icons.Get(state), tooltip);
    }
}
