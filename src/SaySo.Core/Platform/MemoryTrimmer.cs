using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

/// <summary>Возвращает память ОС после диктовки: сжатие кучи и сброс рабочего набора.</summary>
public static class MemoryTrimmer
{
    public static void Trim()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Native.K32EmptyWorkingSet(Native.GetCurrentProcess());
    }
}
