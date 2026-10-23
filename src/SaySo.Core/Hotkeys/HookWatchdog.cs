namespace SaySo.Core.Hotkeys;

/// <summary>
/// Сторож хука клавиатуры. Windows молча снимает WH_KEYBOARD_LL, если колбэк не уложился в LowLevelHooksTimeout
/// (после долгого простоя страницы процесса выгружены, первое нажатие ждёт диск). Нажатия видит и поток Raw Input:
/// если он получил нажатие, которого хук не видел, хук снят и его надо поставить заново.
/// </summary>
public static class HookWatchdog
{
    /// <summary>Хук видит нажатие раньше Raw Input; отставание больше этого срока значит, что хука нет.</summary>
    public const int StaleMs = 1000;

    /// <summary>Не чаще: при окне администратора хук обычного процесса может не видеть нажатий, а Raw Input видит.</summary>
    public static readonly TimeSpan MinReinstallInterval = TimeSpan.FromSeconds(10);

    /// <param name="inputTime">Время нажатия из Raw Input (GetMessageTime, мс от старта системы).</param>
    /// <param name="lastHookTime">Время последнего нажатия, которое видел хук (KBDLLHOOKSTRUCT.time).</param>
    public static bool HookMissed(uint inputTime, uint lastHookTime) => unchecked((int)(inputTime - lastHookTime)) > StaleMs;
}
