using System.Text.Json.Serialization;

namespace ScreenSwap.Models;

/// <summary>一组全局快捷键。Modifiers 使用 MOD_* 值，Key 使用虚拟键码 (VK_*)。</summary>
public sealed class HotkeyBinding
{
    public uint Modifiers { get; set; }
    public uint Key { get; set; }

    [JsonIgnore]
    public bool Enabled => Key != 0;

    public HotkeyBinding Clone() => new() { Modifiers = Modifiers, Key = Key };

    public static HotkeyBinding Empty() => new();
}

public sealed class AppSettings
{
    /// <summary>切换主显示器（默认 Ctrl + Alt + S）。</summary>
    public HotkeyBinding TogglePrimary { get; set; } = new()
    {
        Modifiers = 0x0002 | 0x0001, // MOD_CONTROL | MOD_ALT
        Key = 0x53                   // 'S'
    };

    /// <summary>把按位置排序后的第 1 台显示器设为主显示器，默认未设置。</summary>
    public HotkeyBinding PrimaryFirst { get; set; } = new();

    /// <summary>把按位置排序后的第 2 台显示器设为主显示器，默认未设置。</summary>
    public HotkeyBinding PrimarySecond { get; set; } = new();

    /// <summary>切换主显示器时，同时把两屏上的桌面窗口互换。</summary>
    public bool SwapWindows { get; set; } = true;

    /// <summary>开机自动启动并隐藏到托盘。</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>启动时不显示设置窗口。</summary>
    public bool StartMinimized { get; set; }

    /// <summary>显示托盘气泡提醒。</summary>
    public bool ShowNotifications { get; set; } = true;
}
