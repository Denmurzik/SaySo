using SaySo.Core.Diagnostics;
using SaySo.Core.Hotkeys;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Platform;

/// <summary>Состояние клавиш и синтетическое Cmd+V (аналог KeyboardInjector в Windows).</summary>
internal static class Keyboard
{
    /// <summary>
    /// Метка в kCGEventSourceUserData у наших синтетических нажатий: перехват SaySo узнаёт их и не принимает за
    /// пользователя (в Windows для этого есть флаг LLKHF_INJECTED).
    /// </summary>
    public const long InjectedMark = 0x5341_5953_4F21; // «SAYSO!»

    private const ushort FallbackVKeyCode = MacKeyCodes.V;

    /// <summary>Физически нажата ли клавиша (по VK из настроек).</summary>
    public static bool IsKeyDown(int vk)
    {
        var code = MacKeyCodes.FromVirtualKey(vk);
        return code >= 0 && Quartz.CGEventSourceKeyState(Quartz.StateHidSystem, (ushort)code) != 0;
    }

    /// <summary>Ждёт, пока отпущены клавиши комбинации: иначе удержанный модификатор сложился бы с нашим Cmd+V.</summary>
    public static async Task WaitForKeysReleasedAsync(IReadOnlyCollection<int> keys, TimeSpan timeout)
    {
        var until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (keys.Any(IsKeyDown) && Environment.TickCount64 < until)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    /// <summary>Разрешён ли SaySo «Универсальный доступ»: без него перехват клавиш и синтетические нажатия не работают.</summary>
    public static bool IsAccessibilityTrusted => Quartz.AXIsProcessTrusted() != 0;

    /// <summary>
    /// Нажимает Cmd+V в активном окне. Флаги события заданы явно (только Cmd): удерживаемые пользователем модификаторы
    /// к вставке не примешиваются. Клавиша «V» берётся из текущей латинской раскладки (Dvorak и т. п.).
    /// </summary>
    /// <returns>false — событие не создалось.</returns>
    public static bool SendCmdV()
    {
        var source = Quartz.CGEventSourceCreate(Quartz.StateCombinedSession);
        try
        {
            var key = PasteKeyCode();
            var down = Quartz.CGEventCreateKeyboardEvent(source, key, 1);
            var up = Quartz.CGEventCreateKeyboardEvent(source, key, 0);
            if (down == 0 || up == 0)
            {
                Release(down);
                Release(up);
                return false;
            }

            foreach (var ev in new[] { down, up })
            {
                Quartz.CGEventSetFlags(ev, Quartz.FlagCommand);
                Quartz.CGEventSetIntegerValueField(ev, Quartz.EventSourceUserData, InjectedMark);
            }

            Quartz.CGEventPost(0 /* kCGHIDEventTap */, down);
            Quartz.CGEventPost(0, up);
            Release(down);
            Release(up);
            return true;
        }
        finally
        {
            Release(source);
        }
    }

    private static void Release(nint obj)
    {
        if (obj != 0)
        {
            Quartz.CFRelease(obj);
        }
    }

    /// <summary>Код клавиши, которая в текущей латинской раскладке печатает «v»; не нашлась — позиция V в ANSI.</summary>
    private static unsafe ushort PasteKeyCode()
    {
        nint source = 0;
        try
        {
            source = Quartz.TISCopyCurrentASCIICapableKeyboardLayoutInputSource();
            if (source == 0)
            {
                return FallbackVKeyCode;
            }

            var data = Quartz.TISGetInputSourceProperty(source, Quartz.CarbonConstant("kTISPropertyUnicodeKeyLayoutData"));
            if (data == 0)
            {
                return FallbackVKeyCode;
            }

            var layout = Quartz.CFDataGetBytePtr(data);
            var keyboardType = Quartz.LMGetKbdType();
            var chars = stackalloc char[4];
            for (ushort code = 0; code < 128; code++)
            {
                uint dead = 0;
                nuint length = 0;
                // kUCKeyActionDown = 0, без модификаторов, kUCKeyTranslateNoDeadKeysBit = 0 (бит 0 → маска 1).
                if (Quartz.UCKeyTranslate(layout, code, 0, 0, keyboardType, 1, &dead, 4, &length, chars) == 0
                    && length == 1 && chars[0] == 'v')
                {
                    return code;
                }
            }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Error("Раскладка для Cmd+V", e);
        }
        finally
        {
            if (source != 0)
            {
                Quartz.CFRelease(source);
            }
        }

        return FallbackVKeyCode;
    }
}
