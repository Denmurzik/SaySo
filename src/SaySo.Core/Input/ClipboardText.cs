using SaySo.Core.Interop;

namespace SaySo.Core.Input;

/// <summary>
/// Текст в буфере обмена. Каждая пара OpenClipboard/CloseClipboard целиком выполняется внутри одного синхронного вызова
/// (на одном потоке), между вызовами буфер не удерживается. Окно-владелец должно быть создано тем же потоком, который вызывает методы.
/// </summary>
public static unsafe class ClipboardText
{
    private static readonly uint ExcludeFromMonitoring = Native.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CanIncludeInHistory = Native.RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    private static readonly uint CanUploadToCloud = Native.RegisterClipboardFormatW("CanUploadToCloudClipboard");

    public static uint SequenceNumber => Native.GetClipboardSequenceNumber();

    /// <returns>false — буфер занят другим приложением; true и text == null — текста в буфере нет.</returns>
    public static bool TryGetText(nint owner, out string? text)
    {
        text = null;
        if (!Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT))
        {
            return true;
        }

        if (!TryOpen(owner))
        {
            return false;
        }

        try
        {
            var handle = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (handle == 0)
            {
                return true;
            }

            var p = (char*)Native.GlobalLock(handle);
            if (p == null)
            {
                return true;
            }

            try
            {
                var max = (int)(Native.GlobalSize(handle) / sizeof(char));
                var length = 0;
                while (length < max && p[length] != '\0')
                {
                    length++;
                }

                text = new string(p, 0, length);
                return true;
            }
            finally
            {
                Native.GlobalUnlock(handle);
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    public static bool SetText(nint owner, string text, bool excludeFromHistory)
    {
        // Память под текст готовим до EmptyClipboard: при сбое выделения прежнее содержимое буфера не теряется.
        var hText = AllocText(text);
        if (hText == 0)
        {
            return false;
        }

        if (!TryOpen(owner))
        {
            Native.GlobalFree(hText);
            return false;
        }

        try
        {
            if (!Native.EmptyClipboard())
            {
                Native.GlobalFree(hText);
                return false;
            }

            if (Native.SetClipboardData(Native.CF_UNICODETEXT, hText) == 0)
            {
                Native.GlobalFree(hText);
                return false;
            }

            if (excludeFromHistory)
            {
                SetDword(ExcludeFromMonitoring, 0);
                SetDword(CanIncludeInHistory, 0);
                SetDword(CanUploadToCloud, 0);
            }

            return true;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    // Другое приложение может держать буфер открытым — до 10 попыток по 15 мс.
    private static bool TryOpen(nint owner)
    {
        for (var i = 0; i < 10; i++)
        {
            if (Native.OpenClipboard(owner))
            {
                return true;
            }

            Thread.Sleep(15);
        }

        return false;
    }

    /// <returns>Дескриптор памяти с текстом или 0, если выделить/заблокировать память не удалось.</returns>
    private static nint AllocText(string text)
    {
        var bytes = (nuint)((text.Length + 1) * sizeof(char));
        var handle = Native.GlobalAlloc(Native.GMEM_MOVEABLE, bytes);
        if (handle == 0)
        {
            return 0;
        }

        var p = (char*)Native.GlobalLock(handle);
        if (p == null)
        {
            Native.GlobalFree(handle);
            return 0;
        }

        text.AsSpan().CopyTo(new Span<char>(p, text.Length));
        p[text.Length] = '\0';
        Native.GlobalUnlock(handle);
        return handle;
    }

    private static void SetDword(uint format, uint value)
    {
        var handle = Native.GlobalAlloc(Native.GMEM_MOVEABLE, sizeof(uint));
        if (handle == 0)
        {
            return;
        }

        var p = (uint*)Native.GlobalLock(handle);
        if (p == null)
        {
            Native.GlobalFree(handle);
            return;
        }

        *p = value;
        Native.GlobalUnlock(handle);
        if (Native.SetClipboardData(format, handle) == 0)
        {
            Native.GlobalFree(handle);
        }
    }
}
