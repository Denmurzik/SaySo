using SaySo.Core.Engines;

namespace SaySo.Core.Dictation;

/// <summary>Движки по текущим настройкам. Свойства читаются при каждом старте сессии.</summary>
public interface IDictationEngines
{
    IStreamingEngine? Primary { get; }

    IBatchEngine? Fallback { get; }
}
