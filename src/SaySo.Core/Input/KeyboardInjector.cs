using SaySo.Core.Interop;

namespace SaySo.Core.Input;

/// <summary>Синтетические нажатия. Все помечены маркером, чтобы собственный хук SaySo их игнорировал.</summary>
public static class KeyboardInjector
{
    /// <summary>"SAYS" — значение dwExtraInfo у нажатий, отправленных SaySo.</summary>
    public const nuint InjectionMarker = 0x53415953;

    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;

    internal static Native.INPUT[] BuildCtrlV() =>
    [
        Key(VkControl, up: false),
        Key(VkV, up: false),
        Key(VkV, up: true),
        Key(VkControl, up: true),
    ];

    /// <returns>true, только если система приняла все 4 события (иначе — защищённый рабочий стол, BlockInput и т.п.).</returns>
    public static bool SendCtrlV()
    {
        var inputs = BuildCtrlV();
        return Send(inputs) == inputs.Length;
    }

    /// <summary>Неназначенная клавиша 0xE8: не даёт открыться «Пуску»/меню, если в комбинации Win или Alt.</summary>
    public static void SendMenuMask() => Send([Key(0xE8, up: false), Key(0xE8, up: true)]);

    public static bool IsKeyDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    public static async Task WaitForKeysReleasedAsync(IReadOnlyCollection<int> keys, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (keys.Any(IsKeyDown) && Environment.TickCount64 < deadline)
        {
            await Task.Delay(15).ConfigureAwait(true);
        }
    }

    private static Native.INPUT Key(ushort vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)Native.MapVirtualKeyW(vk, Native.MAPVK_VK_TO_VSC),
                dwFlags = up ? Native.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = InjectionMarker,
            },
        },
    };

    /// <returns>Сколько событий принято системой.</returns>
    private static unsafe int Send(Native.INPUT[] inputs)
    {
        fixed (Native.INPUT* p = inputs)
        {
            return (int)Native.SendInput((uint)inputs.Length, p, sizeof(Native.INPUT));
        }
    }
}
