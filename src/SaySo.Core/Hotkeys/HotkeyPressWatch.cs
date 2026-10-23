namespace SaySo.Core.Hotkeys;

/// <summary>
/// Нажатия горячей клавиши, увиденные через Raw Input. Хук отвечает на каждое свежее нажатие комбинации сообщением
/// главному окну раньше, чем приходит Raw Input; если ответа нет, хук не работает (снят системой или его автомат сбился).
/// </summary>
public sealed class HotkeyPressWatch
{
    private readonly Dictionary<int, uint> _downAt = [];

    /// <returns>true — это свежее нажатие, которым комбинация собрана целиком (не автоповтор).</returns>
    public bool OnRawKey(IReadOnlyCollection<int> hotkey, int vk, bool down, uint time)
    {
        if (!down)
        {
            _downAt.Remove(vk);
            return false;
        }

        var fresh = !_downAt.TryGetValue(vk, out var previous)
            || unchecked(time - previous) > HotkeyStateMachine.StaleRepeatMs; // отпускание потеряно (экран блокировки, UAC)
        _downAt[vk] = time;
        return fresh && hotkey.Contains(vk) && hotkey.All(_downAt.ContainsKey);
    }

    /// <summary>Забыть нажатые клавиши (блокировка сеанса: их отпускания сюда не придут).</summary>
    public void Reset() => _downAt.Clear();

    /// <summary>Ответил ли хук на нажатие: его сообщение пришло не раньше чем за <see cref="HookWatchdog.StaleMs"/> до нажатия.</summary>
    public static bool HookAnswered(uint pressTime, uint lastHookMessageTime) =>
        !HookWatchdog.HookMissed(pressTime, lastHookMessageTime);
}
