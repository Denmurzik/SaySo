using System.Runtime.InteropServices;

namespace SaySo.Mac.Platform;

/// <summary>Возвращает память ОС после диктовки: сжатие кучи .NET и возврат свободных страниц malloc (как EmptyWorkingSet в Windows).</summary>
internal static partial class MemoryTrimmer
{
    public static void Trim()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        malloc_zone_pressure_relief(0, 0);
    }

    /// <summary>Физическая память процесса (phys_footprint — то, что показывает «Мониторинг системы»), байты.</summary>
    public static unsafe long Footprint()
    {
        var info = stackalloc byte[512];
        // RUSAGE_INFO_V2: ri_uuid[16], затем uint64 поля; ri_phys_footprint — восьмое (смещение 72).
        return proc_pid_rusage(Environment.ProcessId, 2, info) == 0 ? *(long*)(info + 72) : 0;
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial nuint malloc_zone_pressure_relief(nint zone, nuint goal);

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static unsafe partial int proc_pid_rusage(int pid, int flavor, byte* buffer);
}
