using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

internal static unsafe class TrayMenu
{
    /// <returns>Id выбранного пункта или 0.</returns>
    public static uint Show(nint owner, IReadOnlyList<TrayMenuItem> items)
    {
        var menu = Native.CreatePopupMenu();
        if (menu == 0)
        {
            return 0;
        }

        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
                    continue;
                }

                var flags = Native.MF_STRING
                            | (item.Enabled ? 0 : Native.MF_GRAYED)
                            | (item.Checked ? Native.MF_CHECKED : 0);
                Native.AppendMenuW(menu, flags, item.Id, item.Text);
            }

            Native.POINT pt;
            Native.GetCursorPos(&pt);
            // Без этого меню не закрывается по щелчку мимо (известная особенность TrackPopupMenu).
            Native.SetForegroundWindow(owner);
            var command = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY, pt.X, pt.Y, owner, null);
            Native.PostMessageW(owner, Native.WM_NULL, 0, 0);
            return (uint)Math.Max(0, command);
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }
}
