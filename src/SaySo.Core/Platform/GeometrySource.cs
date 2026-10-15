using System.Runtime.InteropServices;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

/// <summary>
/// Нативный COM-объект Windows.Graphics.IGeometrySource2D + IGeometrySource2DInterop над готовым ID2D1Geometry —
/// то, что CompositionPath принимает вместо Win2D. Таблицы методов статические, на [UnmanagedCallersOnly]:
/// без ComWrappers и рефлексии, совместимо с NativeAOT.
/// </summary>
internal static unsafe class GeometrySource
{
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    private static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    private static readonly Guid IID_IGeometrySource2D = new("CAFF7902-670C-4181-A624-DA977203B845");
    private static readonly Guid IID_IGeometrySource2DInterop = new("0657AF73-53FD-47CF-84FF-C8492D2A80A3");

    private struct Instance
    {
        public void** Inspectable; // IUnknown, IInspectable, IGeometrySource2D, IAgileObject
        public void** Interop;     // IGeometrySource2DInterop: следующее поле (this + sizeof(void*))
        public int References;
        public nint Geometry;
    }

    private static readonly void** InspectableTable = BuildInspectableTable();
    private static readonly void** InteropTable = BuildInteropTable();
    private static int _live;

    /// <summary>Живых объектов (для тестов: после освобождения плашки должно быть 0).</summary>
    public static int Live => Volatile.Read(ref _live);

    /// <summary>Новый объект со счётчиком 1; геометрию он удерживает сам (AddRef), вызывающий освобождает свою ссылку.</summary>
    public static nint Create(nint geometry)
    {
        var instance = (Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));
        instance->Inspectable = InspectableTable;
        instance->Interop = InteropTable;
        instance->References = 1;
        instance->Geometry = geometry;
        Native.AddRef(geometry);
        Interlocked.Increment(ref _live);
        return (nint)instance;
    }

    private static void** BuildInspectableTable()
    {
        var table = (void**)NativeMemory.Alloc(6, (nuint)sizeof(void*));
        table[0] = (delegate* unmanaged<Instance*, Guid*, void**, int>)&QueryInterface;
        table[1] = (delegate* unmanaged<Instance*, uint>)&AddRef;
        table[2] = (delegate* unmanaged<Instance*, uint>)&Release;
        table[3] = (delegate* unmanaged<Instance*, uint*, Guid**, int>)&GetIids;
        table[4] = (delegate* unmanaged<Instance*, nint*, int>)&GetRuntimeClassName;
        table[5] = (delegate* unmanaged<Instance*, int*, int>)&GetTrustLevel;
        return table;
    }

    private static void** BuildInteropTable()
    {
        var table = (void**)NativeMemory.Alloc(5, (nuint)sizeof(void*));
        table[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&InteropQueryInterface;
        table[1] = (delegate* unmanaged<void*, uint>)&InteropAddRef;
        table[2] = (delegate* unmanaged<void*, uint>)&InteropRelease;
        table[3] = (delegate* unmanaged<void*, nint*, int>)&GetGeometry;
        table[4] = (delegate* unmanaged<void*, nint, nint*, int>)&TryGetGeometryUsingFactory;
        return table;
    }

    private static Instance* FromInterop(void* self) => (Instance*)((byte*)self - sizeof(void*));

    private static int Query(Instance* self, Guid* iid, void** result)
    {
        if (*iid == IID_IUnknown || *iid == IID_IInspectable || *iid == IID_IGeometrySource2D || *iid == IID_IAgileObject)
        {
            *result = self;
        }
        else if (*iid == IID_IGeometrySource2DInterop)
        {
            *result = &self->Interop;
        }
        else
        {
            *result = null;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        Interlocked.Increment(ref self->References);
        return 0;
    }

    private static uint DoRelease(Instance* self)
    {
        var left = Interlocked.Decrement(ref self->References);
        if (left == 0)
        {
            Native.Release(self->Geometry);
            NativeMemory.Free(self);
            Interlocked.Decrement(ref _live);
        }

        return (uint)left;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(Instance* self, Guid* iid, void** result) => Query(self, iid, result);

    [UnmanagedCallersOnly]
    private static uint AddRef(Instance* self) => (uint)Interlocked.Increment(ref self->References);

    [UnmanagedCallersOnly]
    private static uint Release(Instance* self) => DoRelease(self);

    [UnmanagedCallersOnly]
    private static int GetIids(Instance* self, uint* count, Guid** iids)
    {
        *count = 0;
        *iids = null;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int GetRuntimeClassName(Instance* self, nint* name)
    {
        *name = 0; // пустая HSTRING: класса среды выполнения нет, проекция обёртывает интерфейс
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int GetTrustLevel(Instance* self, int* level)
    {
        *level = 0; // BaseTrust
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int InteropQueryInterface(void* self, Guid* iid, void** result) => Query(FromInterop(self), iid, result);

    [UnmanagedCallersOnly]
    private static uint InteropAddRef(void* self) => (uint)Interlocked.Increment(ref FromInterop(self)->References);

    [UnmanagedCallersOnly]
    private static uint InteropRelease(void* self) => DoRelease(FromInterop(self));

    [UnmanagedCallersOnly]
    private static int GetGeometry(void* self, nint* value)
    {
        var geometry = FromInterop(self)->Geometry;
        Native.AddRef(geometry);
        *value = geometry;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int TryGetGeometryUsingFactory(void* self, nint factory, nint* value)
    {
        *value = 0; // перестраивать под чужую фабрику не умеем: компоновщик возьмёт GetGeometry
        return 0;
    }
}
