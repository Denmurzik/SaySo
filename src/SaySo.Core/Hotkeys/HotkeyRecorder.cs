namespace SaySo.Core.Hotkeys;

/// <summary>Запись новой комбинации в настройках: клавиши в порядке нажатия, конец — когда отпущены все.</summary>
public sealed class HotkeyRecorder
{
    private readonly HashSet<int> _down = [];
    private readonly List<int> _combo = [];

    public IReadOnlyList<int>? Result { get; private set; }

    public bool IsCancelled { get; private set; }

    public bool OnKey(KeyEvent e)
    {
        if (e.IsInjected)
        {
            return false;
        }

        if (e.IsDown)
        {
            if (e.VirtualKey == VirtualKeys.Escape && _down.Count == 0)
            {
                IsCancelled = true;
                return true;
            }

            if (_down.Add(e.VirtualKey) && !_combo.Contains(e.VirtualKey))
            {
                _combo.Add(e.VirtualKey);
            }

            return false;
        }

        if (!_down.Remove(e.VirtualKey))
        {
            return false;
        }

        if (_down.Count == 0 && _combo.Count > 0)
        {
            Result = _combo.ToList();
            return true;
        }

        return false;
    }
}
