using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenSwap.Models;
using ScreenSwap.Native;
using ScreenSwap.Services;

namespace ScreenSwap.Controls;

/// <summary>点击后直接按下组合键即可完成设置的输入框。</summary>
public sealed class HotkeyBox : TextBox
{
    private HotkeyBinding _value = HotkeyBinding.Empty();

    public event EventHandler? ValueChanged;
    public event Action<string>? Hint;

    public HotkeyBinding Value
    {
        get => _value;
        set
        {
            _value = value ?? HotkeyBinding.Empty();
            RefreshText();
        }
    }

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        IsTabStop = true;
        Cursor = Cursors.IBeam;
        ToolTip = "点击后按下想要的组合键；Backspace / Delete 可清除";
        RefreshText();
    }

    private void RefreshText()
    {
        Text = HotkeyText.Describe(_value.Modifiers, _value.Key);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.Back or Key.Delete)
        {
            SetValue(HotkeyBinding.Empty());
            return;
        }

        if (key == Key.Escape)
        {
            RefreshText();
            return;
        }

        // 只按下修饰键时先等待用户按下主键
        if (IsModifier(key))
            return;

        if ((key is Key.Tab or Key.Enter or Key.Space) && Keyboard.Modifiers == ModifierKeys.None)
        {
            // 保留 Tab / Enter 的常规导航行为
            e.Handled = false;
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0)
            return;

        uint modifiers = 0;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) modifiers |= WinApi.MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Alt)) modifiers |= WinApi.MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Shift)) modifiers |= WinApi.MOD_SHIFT;
        if (mods.HasFlag(ModifierKeys.Windows)) modifiers |= WinApi.MOD_WIN;

        bool isFunctionKey = vk >= 0x70 && vk <= 0x87 || vk == 0x2C;
        if (modifiers == 0 && !isFunctionKey)
        {
            Hint?.Invoke("请至少带上一个修饰键（Ctrl / Alt / Shift / Win），避免占用普通按键");
            return;
        }

        SetValue(new HotkeyBinding { Modifiers = modifiers, Key = (uint)vk });
    }

    /// <summary>清除当前快捷键（会触发 ValueChanged）。</summary>
    public void ClearHotkey() => SetValue(HotkeyBinding.Empty());

    private void SetValue(HotkeyBinding binding)
    {
        _value = binding;
        RefreshText();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.System or Key.None;
}
