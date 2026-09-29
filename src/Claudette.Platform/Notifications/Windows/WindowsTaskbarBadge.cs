using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Claudette.Platform.Notifications.Windows;

/// <summary>
/// The taskbar badge (DESIGN.md §10): an overlay icon on Claudette's taskbar button with the number of tabs needing
/// input, through <c>ITaskbarList3::SetOverlayIcon</c>. The app draws the number; this turns the PNG into an icon.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsTaskbarBadge(Func<nint> windowHandle, Func<int, byte[]?> renderIcon, ILogger logger) : IAppBadge
{
    private static readonly Guid TaskbarListClsid = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    /// <summary>For <c>CreateIconFromResourceEx</c>: icon resources are version 3.0.</summary>
    private const uint IconResourceVersion = 0x0003_0000;

    private ITaskbarList3? _taskbar;
    private nint _icon;
    private int _count;

    public void SetCount(int count)
    {
        if (count == _count)
        {
            return;
        }
        var hwnd = windowHandle();
        if (hwnd == 0)
        {
            return;
        }
        try
        {
            _taskbar ??= CreateTaskbarList();
            var icon = count > 0 ? CreateIcon(count) : 0;
            _taskbar.SetOverlayIcon(hwnd, icon, count > 0 ? $"{count} {(count == 1 ? "tab needs" : "tabs need")} your input" : null);
            if (_icon != 0)
            {
                WinRt.DestroyIcon(_icon);
            }
            _icon = icon;
            _count = count;
        }
        catch (Exception ex)
        {
            // The taskbar button may not exist yet; the next change tries again.
            logger.LogDebug(ex, "Couldn't set the taskbar badge.");
        }
    }

    private static ITaskbarList3 CreateTaskbarList()
    {
        var taskbar = WinRt.CreateComInstance<ITaskbarList3>(TaskbarListClsid);
        taskbar.HrInit();
        return taskbar;
    }

    private nint CreateIcon(int count)
    {
        if (renderIcon(count) is not { Length: > 0 } png)
        {
            return 0;
        }
        fixed (byte* bytes = png)
        {
            // Since Vista, an icon resource may be a whole PNG file.
            return WinRt.CreateIconFromResourceEx(bytes, (uint)png.Length, isIcon: true, IconResourceVersion, 0, 0, 0);
        }
    }
}
