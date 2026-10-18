using System.Runtime.InteropServices;

namespace SaySo.Mac.Interop;

/// <summary>
/// Блоки Objective-C без компилятора Clang: глобальный блок (без захваченных переменных) — это статическая структура
/// Block_literal с isa = _NSConcreteGlobalBlock и C-функцией invoke; Block_copy возвращает его же. Состояние, которое
/// нужно обработчику, хранится в статических полях вызывающего. Блок живёт до конца процесса.
/// </summary>
internal static unsafe class Blocks
{
    private const int BlockIsGlobal = 1 << 28;

    /// <param name="invoke">C-функция, первый аргумент которой — сам блок, дальше — аргументы блока.</param>
    public static nint Global(nint invoke)
    {
        var system = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        var descriptor = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        descriptor[1] = 4 * sizeof(nint); // sizeof(Block_literal)
        var literal = (nint*)NativeMemory.AllocZeroed(4, (nuint)sizeof(nint));
        literal[0] = NativeLibrary.GetExport(system, "_NSConcreteGlobalBlock");
        ((int*)literal)[2] = BlockIsGlobal; // flags; следом reserved = 0
        literal[2] = invoke;
        literal[3] = (nint)descriptor;
        return (nint)literal;
    }
}
