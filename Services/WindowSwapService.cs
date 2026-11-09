using System.Text;
using ScreenSwap.Models;
using ScreenSwap.Native;

namespace ScreenSwap.Services;

internal sealed record WindowSnapshot(
    IntPtr Handle,
    int Left,
    int Top,
    int Width,
    int Height,
    bool Maximized,
    int MonitorIndex);

/// <summary>在切换主显示器前后，把两块屏幕上的顶层窗口整体互换位置。</summary>
internal static class WindowSwapService
{
    private static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow", "TaskListThumbnailWnd", "Windows.UI.Core.CoreWindow",
        "Xaml_WindowedPopupClass", "Tooltip", "ComboLBox", "Button"
    };

    internal static List<WindowSnapshot> Snapshot(IReadOnlyList<MonitorInfo> monitors)
    {
        var result = new List<WindowSnapshot>();
        uint myPid = (uint)Environment.ProcessId;

        WinApi.EnumWindowsProc callback = (hWnd, _) =>
        {
            if (!WinApi.IsWindowVisible(hWnd) || WinApi.IsIconic(hWnd))
                return true;

            if (WinApi.DwmGetWindowAttribute(hWnd, WinApi.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            WinApi.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == myPid)
                return true;

            if ((WinApi.GetWindowLong(hWnd, WinApi.GWL_EXSTYLE) & WinApi.WS_EX_TOOLWINDOW) != 0)
                return true;

            var clsSb = new StringBuilder(128);
            WinApi.GetClassName(hWnd, clsSb, clsSb.Capacity);
            if (SkipClasses.Contains(clsSb.ToString()))
                return true;

            var titleSb = new StringBuilder(256);
            WinApi.GetWindowText(hWnd, titleSb, titleSb.Capacity);
            if (string.IsNullOrWhiteSpace(titleSb.ToString()))
                return true;

            if (!WinApi.GetWindowRect(hWnd, out var rect))
                return true;

            if (rect.Width < 80 || rect.Height < 60)
                return true;

            int monitorIndex = FindMonitor(rect, monitors);
            if (monitorIndex < 0)
                return true;

            result.Add(new WindowSnapshot(
                hWnd, rect.Left, rect.Top, rect.Width, rect.Height,
                WinApi.IsZoomed(hWnd), monitorIndex));

            return true;
        };

        WinApi.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }

    /// <summary>把快照中的窗口移动到另一台显示器，并保持相对位置与大小。</summary>
    internal static int Move(
        IReadOnlyList<WindowSnapshot> windows,
        IReadOnlyList<MonitorInfo> before,
        IReadOnlyList<MonitorInfo> after)
    {
        int moved = 0;

        foreach (var w in windows)
        {
            int otherIndex = w.MonitorIndex == 0 ? 1 : 0;
            if (otherIndex >= before.Count)
                continue;

            MonitorInfo? destination = after.FirstOrDefault(
                m => string.Equals(m.DeviceName, before[otherIndex].DeviceName, StringComparison.OrdinalIgnoreCase));
            destination ??= after.FirstOrDefault(m => m.Index == otherIndex);
            if (destination is null)
                continue;

            var source = before[w.MonitorIndex];
            int x = destination.Left + (w.Left - source.Left);
            int y = destination.Top + (w.Top - source.Top);

            if (w.Maximized)
            {
                // 最大化窗口无法直接移动：先还原、移动，再重新最大化到目标显示器
                WinApi.ShowWindow(w.Handle, WinApi.SW_RESTORE);
                WinApi.MoveWindow(w.Handle, x, y, w.Width, w.Height, true);
                WinApi.ShowWindow(w.Handle, WinApi.SW_MAXIMIZE);
            }
            else
            {
                WinApi.MoveWindow(w.Handle, x, y, w.Width, w.Height, true);
            }
            moved++;
        }

        return moved;
    }

    private static int FindMonitor(WinApi.RECT window, IReadOnlyList<MonitorInfo> monitors)
    {
        int best = -1;
        long bestArea = 0;

        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            int left = Math.Max(window.Left, m.Left);
            int top = Math.Max(window.Top, m.Top);
            int right = Math.Min(window.Right, m.Left + m.Width);
            int bottom = Math.Min(window.Bottom, m.Top + m.Height);
            if (right <= left || bottom <= top)
                continue;

            long area = (long)(right - left) * (bottom - top);
            if (area > bestArea)
            {
                bestArea = area;
                best = i;
            }
        }

        return best;
    }
}
