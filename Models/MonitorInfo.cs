namespace ScreenSwap.Models;

/// <summary>一台物理显示器在虚拟桌面中的信息。Index 按 X、Y 坐标从左到右排序（0 / 1）。</summary>
internal sealed record MonitorInfo(
    int Index,
    string DeviceName,
    string FriendlyName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary)
{
    public string Resolution => $"{Width} × {Height}";
    public string Position => $"({Left}, {Top})";
}
