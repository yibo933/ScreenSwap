using ScreenSwap.Models;
using ScreenSwap.Native;

namespace ScreenSwap.Services;

internal enum SwapMode
{
    /// <summary>在当前主显示器与另一台之间切换。</summary>
    Toggle,
    /// <summary>把按位置排序的第 1 台显示器设为主显示器。</summary>
    First,
    /// <summary>把按位置排序的第 2 台显示器设为主显示器。</summary>
    Second
}

internal sealed class SwapService
{
    private readonly AppSettings _settings;

    /// <summary>(消息, 是否为警告)</summary>
    public event Action<string, bool>? Message;

    public SwapService(AppSettings settings) => _settings = settings;

    public void Execute(SwapMode mode)
    {
        var before = DisplayService.GetMonitors();

        // 硬性前提：必须且只能是两台显示器
        if (before.Count != 2)
        {
            Message?.Invoke(
                $"拒绝调换!当前检测到 {before.Count} 台显示器,需要两台显示器并且显示模式为扩展模式！",
                true);
            return;
        }

        int currentPrimary = before.FindIndex(m => m.IsPrimary);
        if (currentPrimary < 0)
        {
            Message?.Invoke("无法确定当前主显示器，已拒绝调换", true);
            return;
        }

        int targetIndex = mode switch
        {
            SwapMode.First => 0,
            SwapMode.Second => 1,
            _ => currentPrimary == 0 ? 1 : 0
        };

        if (targetIndex == currentPrimary)
        {
            Message?.Invoke($"「{before[targetIndex].FriendlyName}」已经是主显示器，无需切换", false);
            return;
        }

        // 切换前先记录窗口位置，切换后再按「另一台显示器」的新坐标移动
        var windows = _settings.SwapWindows
            ? WindowSwapService.Snapshot(before)
            : new List<WindowSnapshot>();

        if (!DisplayConfigApi.SetPrimary(before[targetIndex], targetIndex, out string error))
        {
            Message?.Invoke("切换主显示器失败：" + error, true);
            return;
        }

        var after = WaitForLayout(before[targetIndex].DeviceName);
        int moved = windows.Count > 0 && after.Count == 2
            ? WindowSwapService.Move(windows, before, after)
            : 0;

        var newPrimary = after.FirstOrDefault(m => m.IsPrimary);
        string name = newPrimary?.FriendlyName ?? before[targetIndex].FriendlyName;

        string text;
        if (_settings.SwapWindows)
        {
            text = $"主显示器已成功切换";
        }
       else
        {
            text = $"主显示器已成功切换为「{name}」";
        }

        Message?.Invoke(text, false);
    }

    /// <summary>显示配置变更是异步生效的，等待主显示器标志刷新后再移动窗口。</summary>
    private static List<MonitorInfo> WaitForLayout(string expectedPrimaryDevice)
    {
        for (int i = 0; i < 12; i++)
        {
            var now = DisplayService.GetMonitors();
            if (now.Count == 2 &&
                now.Any(m => m.IsPrimary &&
                             string.Equals(m.DeviceName, expectedPrimaryDevice, StringComparison.OrdinalIgnoreCase)))
                return now;

            Thread.Sleep(60);
        }
        return DisplayService.GetMonitors();
    }
}
