using System.Windows.Interop;
using ScreenSwap.Models;
using ScreenSwap.Native;

namespace ScreenSwap.Services;

/// <summary>注册全局热键，并通过主窗口的消息钩子接收 WM_HOTKEY / WM_DISPLAYCHANGE。</summary>
internal sealed class HotkeyManager : IDisposable
{
    public const int IdToggle = 1;
    public const int IdFirst = 2;
    public const int IdSecond = 3;

    private readonly IntPtr _hwnd;
    private readonly HwndSourceHook _hook;
    private readonly Dictionary<int, HotkeyBinding> _registered = new();

    public event Action<int>? HotkeyPressed;
    public event Action? DisplayChanged;

    public HotkeyManager(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _hook = WndProc;
        HwndSource.FromHwnd(hwnd)?.AddHook(_hook);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WinApi.WM_HOTKEY)
        {
            handled = true;
            HotkeyPressed?.Invoke(wParam.ToInt32());
        }
        else if (msg == WinApi.WM_DISPLAYCHANGE)
        {
            DisplayChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    /// <summary>按当前设置重新注册全部热键，返回注册失败的说明列表。</summary>
    public List<string> Reload(AppSettings settings)
    {
        UnregisterAll();
        var errors = new List<string>();

        Register(IdToggle, "切换主显示器", settings.TogglePrimary, errors);
        Register(IdFirst, "显示器 1 设为主", settings.PrimaryFirst, errors);
        Register(IdSecond, "显示器 2 设为主", settings.PrimarySecond, errors);

        return errors;
    }

    private void Register(int id, string label, HotkeyBinding binding, List<string> errors)
    {
        if (!binding.Enabled)
            return;

        foreach (var existing in _registered.Values)
        {
            if (existing.Modifiers == binding.Modifiers && existing.Key == binding.Key)
            {
                errors.Add($"「{label}」快捷键与其他设置重复，已忽略。");
                return;
            }
        }

        if (!WinApi.RegisterHotKey(_hwnd, id, binding.Modifiers | WinApi.MOD_NOREPEAT, binding.Key))
        {
            errors.Add($"「{label}」的 {HotkeyText.Describe(binding.Modifiers, binding.Key)} 注册失败，可能已被其他程序占用。");
            return;
        }

        _registered[id] = binding.Clone();
    }

    public int RegisteredCount => _registered.Count;

    public void UnregisterAll()
    {
        foreach (int id in _registered.Keys.ToList())
            WinApi.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        HwndSource.FromHwnd(_hwnd)?.RemoveHook(_hook);
    }
}
