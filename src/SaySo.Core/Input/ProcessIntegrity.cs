using SaySo.Core.Interop;

namespace SaySo.Core.Input;

/// <summary>Уровни целостности процессов (UIPI): Medium 0x2000, High 0x3000 (администратор).</summary>
public static unsafe class ProcessIntegrity
{
    public const int Medium = 0x2000;
    public const int High = 0x3000;

    private static readonly Lazy<int> CurrentLevel = new(() => ForProcess(Native.GetCurrentProcess()) ?? Medium);
    private static readonly Lazy<bool> Elevated = new(ReadElevation);

    public static int Current => CurrentLevel.Value;

    public static bool IsElevated => Elevated.Value;

    public static int? ForWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        uint pid;
        Native.GetWindowThreadProcessId(hwnd, &pid);
        if (pid == 0)
        {
            return null;
        }

        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            return ForProcess(process);
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    public static bool IsHigherThanCurrent(nint hwnd) => ForWindow(hwnd) is { } level && level > Current;

    private static int? ForProcess(nint process)
    {
        nint token;
        if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, &token))
        {
            return null;
        }

        try
        {
            uint length;
            Native.GetTokenInformation(token, Native.TokenIntegrityLevel, null, 0, &length);
            if (length == 0)
            {
                return null;
            }

            var buffer = stackalloc byte[(int)length];
            if (!Native.GetTokenInformation(token, Native.TokenIntegrityLevel, buffer, length, &length))
            {
                return null;
            }

            var sid = ((Native.TOKEN_MANDATORY_LABEL*)buffer)->Label.Sid;
            var count = *Native.GetSidSubAuthorityCount(sid);
            return count == 0 ? null : (int)*Native.GetSidSubAuthority(sid, (uint)(count - 1));
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    private static bool ReadElevation()
    {
        nint token;
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_QUERY, &token))
        {
            return false;
        }

        try
        {
            uint elevated = 0;
            uint length;
            return Native.GetTokenInformation(token, Native.TokenElevation, &elevated, sizeof(uint), &length) && elevated != 0;
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }
}
