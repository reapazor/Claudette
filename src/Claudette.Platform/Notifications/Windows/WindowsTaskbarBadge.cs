using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Claudette.Platform.Notifications.Windows;

/// <summary>
/// Claudette's taskbar button (DESIGN.md §10). Its overlay icon, through <c>ITaskbarList3::SetOverlayIcon</c>, shows the
/// number of tabs needing input, or else a frame of the working animation; Windows takes the button's own icon from the
/// package or the AppUserModelID, so the overlay is what can change. The app draws both as PNGs; this turns them into
/// icons. The button flashes through <c>FlashWindowEx</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsTaskbarBadge(Func<nint> windowHandle, Func<int, byte[]?> renderIcon, ILogger logger) : IAppBadge
{
    private static readonly Guid TaskbarListClsid = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    /// <summary>For <c>CreateIconFromResourceEx</c>: icon resources are version 3.0.</summary>
    private const uint IconResourceVersion = 0x0003_0000;

    /// <summary>An animation's few frames are shown over and over, so each is made into an icon once.</summary>
    private readonly Dictionary<byte[], nint> _frameIcons = new(ReferenceEqualityComparer.Instance);

    private ITaskbarList3? _taskbar;
    private nint _countIcon;
    private int _count;
    private byte[]? _frame;
    private string? _frameDescription;

    public AppIconSurface Surface => AppIconSurface.Overlay;

    public void SetCount(int count)
    {
        if (count == _count)
        {
            return;
        }
        var icon = count > 0 ? CreateIcon(renderIcon(count)) : 0;
        var shown = icon != 0
            ? SetOverlay(icon, $"{count} {(count == 1 ? "tab needs" : "tabs need")} your input")
            : SetOverlay(FrameIcon(), _frameDescription);
        if (!shown)
        {
            // The taskbar button may not exist yet; the next change tries again.
            if (icon != 0)
            {
                WinRt.DestroyIcon(icon);
            }
            return;
        }
        if (_countIcon != 0)
        {
            WinRt.DestroyIcon(_countIcon);
        }
        _countIcon = icon;
        _count = count;
    }

    public void ShowFrame(byte[]? png, string? description)
    {
        _frame = png;
        _frameDescription = description;
        if (_count == 0)
        {
            SetOverlay(FrameIcon(), description);
        }
    }

    public void Flash(bool on)
    {
        var hwnd = windowHandle();
        if (hwnd == 0)
        {
            return;
        }
        var info = new FlashWindowInfo
        {
            Size = (uint)sizeof(FlashWindowInfo),
            Window = hwnd,
            Flags = on ? WinRt.FlashTray | WinRt.FlashUntilForeground : WinRt.FlashStop,
        };
        WinRt.FlashWindowEx(&info);
    }

    private bool SetOverlay(nint icon, string? description)
    {
        var hwnd = windowHandle();
        if (hwnd == 0)
        {
            return false;
        }
        try
        {
            _taskbar ??= CreateTaskbarList();
            _taskbar.SetOverlayIcon(hwnd, icon, icon != 0 ? description : null);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Couldn't set the taskbar overlay.");
            return false;
        }
    }

    private nint FrameIcon()
    {
        if (_frame is null)
        {
            return 0;
        }
        if (!_frameIcons.TryGetValue(_frame, out var icon))
        {
            _frameIcons[_frame] = icon = CreateIcon(_frame);
        }
        return icon;
    }

    private static ITaskbarList3 CreateTaskbarList()
    {
        var taskbar = WinRt.CreateComInstance<ITaskbarList3>(TaskbarListClsid);
        taskbar.HrInit();
        return taskbar;
    }

    private static nint CreateIcon(byte[]? png)
    {
        if (png is not { Length: > 0 })
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
