using System.Text;
using ScreenSwap.Native;

namespace ScreenSwap.Services;

internal static class HotkeyText
{
    /// <summary>把 MOD_* / VK_* 转成可读文本，例如 "Ctrl + Alt + S"。</summary>
    public static string Describe(uint modifiers, uint vk)
    {
        if (vk == 0)
            return "未设置";

        var parts = new List<string>();
        if ((modifiers & WinApi.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & WinApi.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & WinApi.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & WinApi.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join(" + ", parts);
    }

    private static string KeyName(uint vk)
    {
        try
        {
            uint scan = WinApi.MapVirtualKey(vk, WinApi.MAPVK_VK_TO_VSC);
            int lParam = (int)(scan << 16);

            // 扩展键（方向键、Insert / Delete / Home / End / PageUp / PageDown / 小键盘回车等）
            bool extended = vk is >= 0x21 and <= 0x28
                            or 0x2D or 0x2E
                            or 0x24 or 0x23
                            or 0x0D
                            or 0x90 or 0x91;
            if (extended)
                lParam |= 1 << 24;

            var sb = new StringBuilder(64);
            if (WinApi.GetKeyNameText(lParam, sb, sb.Capacity) > 0 && sb.Length > 0)
                return sb.ToString();
        }
        catch
        {
            // 回退到简单文本
        }

        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39)
            return ((char)vk).ToString();
        return vk >= 0x70 && vk <= 0x87 ? $"F{vk - 0x6F}" : $"VK 0x{vk:X2}";
    }
}
