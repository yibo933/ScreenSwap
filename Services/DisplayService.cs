using System.Runtime.InteropServices;
using ScreenSwap.Models;
using ScreenSwap.Native;

namespace ScreenSwap.Services;

internal static class DisplayService
{
    /// <summary>枚举当前所有显示器，按虚拟桌面坐标从左到右排序。</summary>
    internal static List<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();

        WinApi.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            var mi = new WinApi.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<WinApi.MONITORINFOEX>(),
                szDevice = string.Empty
            };

            if (!WinApi.GetMonitorInfo(hMonitor, ref mi))
                return true;

            string device = mi.szDevice ?? string.Empty;
            string friendly = string.IsNullOrWhiteSpace(device) ? "未知显示器" : device;

            // EnumDisplayDevices(deviceName, 0) 可以拿到显示器型号名称
            var dd = new WinApi.DISPLAY_DEVICE
            {
                cb = Marshal.SizeOf<WinApi.DISPLAY_DEVICE>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceID = string.Empty,
                DeviceKey = string.Empty
            };
            if (!string.IsNullOrWhiteSpace(device) &&
                WinApi.EnumDisplayDevices(device, 0, ref dd, 0) &&
                !string.IsNullOrWhiteSpace(dd.DeviceString))
            {
                friendly = dd.DeviceString.Trim();
            }

            result.Add(new MonitorInfo(
                Index: result.Count,
                DeviceName: device,
                FriendlyName: friendly,
                Left: mi.rcMonitor.Left,
                Top: mi.rcMonitor.Top,
                Width: mi.rcMonitor.Width,
                Height: mi.rcMonitor.Height,
                IsPrimary: (mi.dwFlags & WinApi.MONITORINFOF_PRIMARY) != 0));

            return true;
        };

        WinApi.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return result
            .OrderBy(m => m.Left)
            .ThenBy(m => m.Top)
            .Select((m, i) => m with { Index = i })
            .ToList();
    }
}
