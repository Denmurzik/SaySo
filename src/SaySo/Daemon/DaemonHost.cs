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

    /// <summary>Windows молча снимает WH_KEYBOARD_LL после тайм-аута колбэка — хук периодически пересоздаётся.</summary>
    private static readonly TimeSpan HookWatchdogInterval = TimeSpan.FromMinutes(5);

    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly SecretStore _secretStore;
    private readonly HistoryStore _history;
    private readonly AutostartManager _autostart = new();
    private readonly DaemonWindow _window;
    private readonly TrayIcon _tray;
    private readonly TextInserter _inserter;
    private readonly IDisposable _hookWatchdog;
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
        _window.TaskbarCreated += () => _tray.Recreate();
        _window.SessionLockChanged += OnSessionLockChanged;
        _window.ThemeChanged += () =>
        {
            _icons.Dispose();
            _icons = TrayIcons.Create();
            UpdateTray(_trayState);
        };
        _hookWatchdog = _window.SetTimer(HookWatchdogInterval, OnHookWatchdog, repeat: true);
        UpdateTray(TrayState.Idle);
    }

    /// <summary>Окно, в которое была последняя вставка (для «Вставить последнее»). Заполняется в задаче 16.</summary>
    internal nint LastTarget { get; set; }

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SaySo.exe");

    public static int Run()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SaySo.Daemon", out var createdNew);
        if (!createdNew)
        {
            IpcClient.Send(IpcProtocol.OpenSettings);
            return 0;
        }

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
            mutex.ReleaseMutex();
            AdminTask.Run();
            return 0;
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

        // Мьютекс отпускается после снятия хука и значка: новый экземпляр не застанет старый хук.
        mutex.ReleaseMutex();
        return code;
    }

    public void Dispose()
    {
        _hookWatchdog.Dispose();
        _hotkey?.Dispose();
        _hotkey = null;
        _tray.Dispose();
        _icons.Dispose();
        _window.Dispose();
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
            case Native.WM_LBUTTONDBLCLK:
                OpenSettings();
                break;
            case Native.WM_CONTEXTMENU:
                ShowMenu();
                break;
        }
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
                ClipboardText.SetText(_window.Handle, last.Text, excludeFromHistory: false);
                break;
            case CmdSettings:
                OpenSettings();
                break;
            case CmdPause:
                _paused = !_paused;
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
                ReconfigureHotkey();
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

        // После разблокировки Windows могла снять хук — ставим новый.
        RecreateHotkey();
    }

    // Сторожок хука: пересоздаём его только в простое и когда ни одна клавиша комбинации не нажата.
    private void OnHookWatchdog()
    {
        if (_trayState is not (TrayState.Idle or TrayState.Paused))
        {
            return;
        }

        if (_settings.Hotkey.Keys.Any(KeyboardInjector.IsKeyDown))
        {
            return;
        }

        RecreateHotkey();
    }

    private void CreateHotkey()
    {
        try
        {
            _hotkey = new HotkeyListener(_window.Handle, DaemonWindow.HotkeyMessage, _settings.Hotkey.Keys, _settings.Hotkey.TapThresholdMs);
            ApplySuspension();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or ArgumentException)
        {
            // Без хука демон остаётся жить (значок, меню, IPC); следующий тик сторожка попробует снова.
            Log.Error("Установка хука клавиатуры", e);
            _hotkey = null;
        }
    }

    private void RecreateHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        CreateHotkey();
        Log.Info(_hotkey is null ? "Хук клавиатуры не пересоздан" : "Хук клавиатуры пересоздан");
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
        try
        {
            Process.Start(new ProcessStartInfo(ExePath, "--settings") { UseShellExecute = false });
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
