using System.Runtime.InteropServices;
using System.Text;

namespace SaySo.Mac.Interop;

/// <summary>
/// Минимальный мост к Objective-C runtime: классы, селекторы, objc_msgSend через типизированные указатели на функции
/// (так вызов соответствует ABI arm64 и x86-64, в том числе со структурами и double) и создание своих классов для
/// обратных вызовов AppKit. Объекты — это <c>nint</c>; владение — по правилам Cocoa без ARC: alloc/new/copy дают +1
/// (освобождает тот, кто создал), остальные методы возвращают автоосвобождаемые объекты (живут до конца пула).
/// </summary>
internal static unsafe partial class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static readonly nint Handle = NativeLibrary.Load(Lib);

    /// <summary>objc_msgSend: все сообщения, кроме возвращающих большие структуры на x86-64.</summary>
    public static readonly nint MsgSend = NativeLibrary.GetExport(Handle, "objc_msgSend");

    /// <summary>
    /// Для методов, возвращающих NSRect (32 байта): на x86-64 такие структуры возвращаются через скрытый указатель, и нужен
    /// objc_msgSend_stret; на arm64 его нет, там NSRect (HFA) возвращается в регистрах обычным objc_msgSend.
    /// </summary>
    public static readonly nint MsgSendStret = RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? NativeLibrary.GetExport(Handle, "objc_msgSend_stret")
        : MsgSend;

    private static readonly Dictionary<string, nint> Selectors = [];

    /// <summary>
    /// Классы AppKit и Core Animation появляются в процессе только после загрузки фреймворков: без этого objc_getClass
    /// вернул бы NULL. Явный статический конструктор гарантирует загрузку до первого обращения к любому члену.
    /// </summary>
    static ObjC()
    {
        NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        NativeLibrary.Load("/System/Library/Frameworks/QuartzCore.framework/QuartzCore");
    }
    private static readonly Dictionary<string, nint> Classes = [];
    private static readonly object Gate = new();

    public static nint Class(string name)
    {
        lock (Gate)
        {
            if (!Classes.TryGetValue(name, out var cls))
            {
                cls = WithUtf8(name, static p => objc_getClass(p));
                if (cls == 0)
                {
                    throw new InvalidOperationException($"Класс Objective-C {name} не найден.");
                }

                Classes[name] = cls;
            }

            return cls;
        }
    }

    public static nint Sel(string name)
    {
        lock (Gate)
        {
            if (!Selectors.TryGetValue(name, out var sel))
            {
                sel = WithUtf8(name, static p => sel_registerName(p));
                Selectors[name] = sel;
            }

            return sel;
        }
    }

    /// <summary>
    /// Новый класс-наследник <paramref name="superclass"/> с методами-обратными вызовами. Методы — указатели на статические
    /// <c>[UnmanagedCallersOnly]</c> функции; <c>types</c> — кодировка Objective-C (например, <c>v@:@</c>).
    /// </summary>
    public static nint DefineClass(string name, string superclass, params (string Selector, nint Imp, string Types)[] methods)
    {
        lock (Gate)
        {
            if (Classes.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var super = Class(superclass);
            var cls = WithUtf8(name, p => objc_allocateClassPair(super, p, 0));
            if (cls == 0)
            {
                // Класс с таким именем уже есть в процессе (повторная регистрация): берём его.
                cls = WithUtf8(name, static p => objc_getClass(p));
                Classes[name] = cls;
                return cls;
            }

            foreach (var (selector, imp, types) in methods)
            {
                var sel = Sel(selector);
                var added = WithUtf8(types, p => class_addMethod(cls, sel, imp, p));
                if (added == 0)
                {
                    throw new InvalidOperationException($"Не удалось добавить метод {selector} в класс {name}.");
                }
            }

            objc_registerClassPair(cls);
            Classes[name] = cls;
            return cls;
        }
    }

    public static nint PoolPush() => objc_autoreleasePoolPush();

    public static void PoolPop(nint pool) => objc_autoreleasePoolPop(pool);

    public static nint Retain(nint obj) => obj == 0 ? 0 : objc_retain(obj);

    public static void Release(nint obj)
    {
        if (obj != 0)
        {
            objc_release(obj);
        }
    }

    /// <summary>[[cls alloc] init]: объект +1.</summary>
    public static nint New(string cls) => Send(Send(Class(cls), Sel("alloc")), Sel("init"));

    public static nint Alloc(string cls) => Send(Class(cls), Sel("alloc"));

    // ---------- objc_msgSend по сигнатурам ----------
    // BOOL передаётся параметром bool (а в вызов — байтом): целочисленная константа не может случайно выбрать перегрузку
    // с BOOL вместо NSInteger/id. Числа с плавающей точкой — только double (CGFloat) или явный float.

    public static nint Send(nint obj, nint sel) => ((delegate* unmanaged<nint, nint, nint>)MsgSend)(obj, sel);

    public static nint Send(nint obj, nint sel, nint a) => ((delegate* unmanaged<nint, nint, nint, nint>)MsgSend)(obj, sel, a);

    public static nint Send(nint obj, nint sel, nint a, nint b) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint>)MsgSend)(obj, sel, a, b);

    public static nint Send(nint obj, nint sel, nint a, nint b, nint c) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)MsgSend)(obj, sel, a, b, c);

    public static nint Send(nint obj, nint sel, nint a, nint b, nint c, nint d) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)MsgSend)(obj, sel, a, b, c, d);

    public static nint Send(nint obj, nint sel, double a) => ((delegate* unmanaged<nint, nint, double, nint>)MsgSend)(obj, sel, a);

    public static nint Send(nint obj, nint sel, double a, double b) =>
        ((delegate* unmanaged<nint, nint, double, double, nint>)MsgSend)(obj, sel, a, b);

    public static nint Send(nint obj, nint sel, double a, double b, double c, double d) =>
        ((delegate* unmanaged<nint, nint, double, double, double, double, nint>)MsgSend)(obj, sel, a, b, c, d);

    public static nint Send(nint obj, nint sel, CGRect a) => ((delegate* unmanaged<nint, nint, CGRect, nint>)MsgSend)(obj, sel, a);

    public static nint Send(nint obj, nint sel, CGRect a, nint b) =>
        ((delegate* unmanaged<nint, nint, CGRect, nint, nint>)MsgSend)(obj, sel, a, b);

    public static nint Send(nint obj, nint sel, CGRect a, nint b, nint c, bool d) =>
        ((delegate* unmanaged<nint, nint, CGRect, nint, nint, byte, nint>)MsgSend)(obj, sel, a, b, c, B(d));

    public static nint Send(nint obj, nint sel, CGSize a) => ((delegate* unmanaged<nint, nint, CGSize, nint>)MsgSend)(obj, sel, a);

    public static nint Send(nint obj, nint sel, CGPoint a) => ((delegate* unmanaged<nint, nint, CGPoint, nint>)MsgSend)(obj, sel, a);

    public static nint Send(nint obj, nint sel, nint a, double b) =>
        ((delegate* unmanaged<nint, nint, nint, double, nint>)MsgSend)(obj, sel, a, b);

    public static nint Send(nint obj, nint sel, double a, nint b) =>
        ((delegate* unmanaged<nint, nint, double, nint, nint>)MsgSend)(obj, sel, a, b);

    public static nint Send(nint obj, nint sel, nint a, bool b) =>
        ((delegate* unmanaged<nint, nint, nint, byte, nint>)MsgSend)(obj, sel, a, B(b));

    public static nint Send(nint obj, nint sel, bool a) => ((delegate* unmanaged<nint, nint, byte, nint>)MsgSend)(obj, sel, B(a));

    public static nint Send(nint obj, nint sel, float a) => ((delegate* unmanaged<nint, nint, float, nint>)MsgSend)(obj, sel, a);

    public static nint Send(nint obj, nint sel, nint a, nint b, bool c) =>
        ((delegate* unmanaged<nint, nint, nint, nint, byte, nint>)MsgSend)(obj, sel, a, b, B(c));

    public static nint Send(nint obj, nint sel, double a, nint b, nint c, nint d, bool e) =>
        ((delegate* unmanaged<nint, nint, double, nint, nint, nint, byte, nint>)MsgSend)(obj, sel, a, b, c, d, B(e));

    public static nint Send(nint obj, nint sel, double a, double b, double c, nint d, nint e) =>
        ((delegate* unmanaged<nint, nint, double, double, double, nint, nint, nint>)MsgSend)(obj, sel, a, b, c, d, e);

    public static nint Send(nint obj, nint sel, CGRect a, bool b) =>
        ((delegate* unmanaged<nint, nint, CGRect, byte, nint>)MsgSend)(obj, sel, a, B(b));

    public static void SendVoid(nint obj, nint sel) => ((delegate* unmanaged<nint, nint, void>)MsgSend)(obj, sel);

    public static void SendVoid(nint obj, nint sel, nint a) => ((delegate* unmanaged<nint, nint, nint, void>)MsgSend)(obj, sel, a);

    public static void SendVoid(nint obj, nint sel, nint a, nint b) =>
        ((delegate* unmanaged<nint, nint, nint, nint, void>)MsgSend)(obj, sel, a, b);

    public static void SendVoid(nint obj, nint sel, bool a) => ((delegate* unmanaged<nint, nint, byte, void>)MsgSend)(obj, sel, B(a));

    public static void SendVoid(nint obj, nint sel, double a) => ((delegate* unmanaged<nint, nint, double, void>)MsgSend)(obj, sel, a);

    public static void SendVoid(nint obj, nint sel, CGRect a) => ((delegate* unmanaged<nint, nint, CGRect, void>)MsgSend)(obj, sel, a);

    public static void SendVoid(nint obj, nint sel, CGRect a, bool b) =>
        ((delegate* unmanaged<nint, nint, CGRect, byte, void>)MsgSend)(obj, sel, a, B(b));

    public static void SendVoid(nint obj, nint sel, CGPoint a) => ((delegate* unmanaged<nint, nint, CGPoint, void>)MsgSend)(obj, sel, a);

    public static void SendVoid(nint obj, nint sel, CGSize a) => ((delegate* unmanaged<nint, nint, CGSize, void>)MsgSend)(obj, sel, a);

    public static void SendVoid(nint obj, nint sel, float a) => ((delegate* unmanaged<nint, nint, float, void>)MsgSend)(obj, sel, a);

    public static void SendVoid(nint obj, nint sel, nint a, bool b) =>
        ((delegate* unmanaged<nint, nint, nint, byte, void>)MsgSend)(obj, sel, a, B(b));

    public static void SendVoid(nint obj, nint sel, float a, nint b) =>
        ((delegate* unmanaged<nint, nint, float, nint, void>)MsgSend)(obj, sel, a, b);

    public static void SendVoid(nint obj, nint sel, nint a, nint b, nint c) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)MsgSend)(obj, sel, a, b, c);

    public static void SendVoid(nint obj, nint sel, nint a, nint b, nint c, nint d) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)MsgSend)(obj, sel, a, b, c, d);

    public static void SendVoid(nint obj, nint sel, double a, double b) =>
        ((delegate* unmanaged<nint, nint, double, double, void>)MsgSend)(obj, sel, a, b);

    public static bool SendBool(nint obj, nint sel) => ((delegate* unmanaged<nint, nint, byte>)MsgSend)(obj, sel) != 0;

    public static bool SendBool(nint obj, nint sel, nint a) => ((delegate* unmanaged<nint, nint, nint, byte>)MsgSend)(obj, sel, a) != 0;

    public static bool SendBool(nint obj, nint sel, nint a, nint b) =>
        ((delegate* unmanaged<nint, nint, nint, nint, byte>)MsgSend)(obj, sel, a, b) != 0;

    public static double SendDouble(nint obj, nint sel) => ((delegate* unmanaged<nint, nint, double>)MsgSend)(obj, sel);

    public static long SendLong(nint obj, nint sel) => ((delegate* unmanaged<nint, nint, long>)MsgSend)(obj, sel);

    public static long SendLong(nint obj, nint sel, nint a) => ((delegate* unmanaged<nint, nint, nint, long>)MsgSend)(obj, sel, a);

    public static CGRect SendRect(nint obj, nint sel) => ((delegate* unmanaged<nint, nint, CGRect>)MsgSendStret)(obj, sel);

    public static CGSize SendSize(nint obj, nint sel) => ((delegate* unmanaged<nint, nint, CGSize>)MsgSend)(obj, sel);

    private static byte B(bool value) => value ? (byte)1 : (byte)0;

    // ---------- строки ----------

    /// <summary>Автоосвобождаемая NSString (живёт до конца текущего пула).</summary>
    public static nint Str(string text)
    {
        fixed (char* p = text)
        {
            var s = Send(Alloc("NSString"), Sel("initWithCharacters:length:"), (nint)p, text.Length);
            return Send(s, Sel("autorelease"));
        }
    }

    /// <summary>
    /// Строка NSString/CFString (они взаимозаменяемы) или null для nil. Символы копируются через CFStringGetCharacters:
    /// без автоосвобождаемых буферов, поэтому вызывать можно с любого потока и без пула.
    /// </summary>
    public static string? ToStr(nint nsString)
    {
        if (nsString == 0)
        {
            return null;
        }

        var length = CFStringGetLength(nsString);
        if (length <= 0)
        {
            return "";
        }

        return string.Create((int)length, nsString, static (span, s) =>
        {
            fixed (char* p = span)
            {
                CFStringGetCharacters(s, new CFRange(0, span.Length), p);
            }
        });
    }

    // ---------- runtime ----------

    private static T WithUtf8<T>(string text, Func<nint, T> body)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* p = bytes)
        {
            return body((nint)p);
        }
    }

    private static nint objc_getClass(nint name) => ObjcGetClass((byte*)name);

    private static nint sel_registerName(nint name) => SelRegisterName((byte*)name);

    private static nint objc_allocateClassPair(nint super, nint name, nint extra) => ObjcAllocateClassPair(super, (byte*)name, extra);

    private static byte class_addMethod(nint cls, nint sel, nint imp, nint types) => ClassAddMethod(cls, sel, imp, (byte*)types);

    [LibraryImport(Lib, EntryPoint = "objc_getClass")]
    private static partial nint ObjcGetClass(byte* name);

    [LibraryImport(Lib, EntryPoint = "sel_registerName")]
    private static partial nint SelRegisterName(byte* name);

    [LibraryImport(Lib, EntryPoint = "objc_allocateClassPair")]
    private static partial nint ObjcAllocateClassPair(nint superclass, byte* name, nint extraBytes);

    [LibraryImport(Lib, EntryPoint = "class_addMethod")]
    private static partial byte ClassAddMethod(nint cls, nint sel, nint imp, byte* types);

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFStringGetLength(nint text);

    [LibraryImport(CoreFoundationLib)]
    private static partial void CFStringGetCharacters(nint text, CFRange range, char* buffer);

    [LibraryImport(Lib)]
    private static partial void objc_registerClassPair(nint cls);

    [LibraryImport(Lib)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(Lib)]
    private static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(Lib)]
    private static partial nint objc_retain(nint obj);

    [LibraryImport(Lib)]
    private static partial void objc_release(nint obj);
}

[StructLayout(LayoutKind.Sequential)]
internal struct CFRange(nint location, nint length)
{
    public nint Location = location;
    public nint Length = length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint(double x, double y)
{
    public double X = x;
    public double Y = y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize(double width, double height)
{
    public double Width = width;
    public double Height = height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect(double x, double y, double width, double height)
{
    public CGPoint Origin = new(x, y);
    public CGSize Size = new(width, height);

    public readonly double X => Origin.X;

    public readonly double Y => Origin.Y;

    public readonly double Width => Size.Width;

    public readonly double Height => Size.Height;

    public readonly double MaxX => Origin.X + Size.Width;

    public readonly double MaxY => Origin.Y + Size.Height;

    public readonly bool Contains(CGPoint p) => p.X >= X && p.X < MaxX && p.Y >= Y && p.Y < MaxY;
}

/// <summary>Пул автоосвобождения на время блока: обязателен в обратных вызовах вне цикла AppKit и на своих потоках.</summary>
internal readonly struct AutoreleasePool : IDisposable
{
    private readonly nint _pool;

    private AutoreleasePool(nint pool) => _pool = pool;

    public static AutoreleasePool Push() => new(ObjC.PoolPush());

    public void Dispose() => ObjC.PoolPop(_pool);
}
