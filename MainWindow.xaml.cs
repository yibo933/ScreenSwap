using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Brush = System.Windows.Media.Brush;
using System.Windows.Media;
using ScreenSwap.Models;
using ScreenSwap.Native;
using ScreenSwap.Services;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ScreenSwap;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly WinForms.NotifyIcon _tray;
    private readonly HotkeyManager _hotkeys;
    private readonly SwapService _swap;

    private WinForms.ToolStripMenuItem? _miSwapWindows;
    private WinForms.ToolStripMenuItem? _miAutoStart;

    private bool _initialized;
    private bool _suppressEvents;
    private bool _allowClose;
    private bool _adminBusy;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        _tray = CreateTrayIcon();

        // 即便启动时隐藏窗口，也需要一个真实 HWND 才能注册全局热键
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();

        _hotkeys = new HotkeyManager(hwnd);
        _swap = new SwapService(_settings);

        _swap.Message += OnSwapMessage;
        _hotkeys.HotkeyPressed += OnHotkey;
        _hotkeys.DisplayChanged += OnDisplayChanged;

        HotkeyToggle.ValueChanged += Hotkey_Changed;

        //多余的功能
        /*
        HotkeyFirst.ValueChanged += Hotkey_Changed;
        HotkeySecond.ValueChanged += Hotkey_Changed;
        HotkeyToggle.Hint += OnHotkeyHint;
        HotkeyFirst.Hint += OnHotkeyHint;
        HotkeySecond.Hint += OnHotkeyHint;
        */

        
        ConfigPathText.Text = $"设置文件：{SettingsService.SettingsPath}";

        LoadSettingsToUi();
        ApplySettings(persist: false);
        UpdateAdminUi();

        _initialized = true;
        RefreshMonitors();
    }

    // ---------------------------------------------------------------- 设置

    private void LoadSettingsToUi()
    {
        _suppressEvents = true;
        HotkeyToggle.Value = _settings.TogglePrimary.Clone();

        //多余的功能,可自行编写
        //HotkeyFirst.Value = _settings.PrimaryFirst.Clone();
        //HotkeySecond.Value = _settings.PrimarySecond.Clone();
        ChkSwapWindows.IsChecked = _settings.SwapWindows;
        ChkAutoStart.IsChecked = _settings.StartWithWindows;
        ChkAdminStartup.IsChecked = _settings.StartAsAdministrator;
        ChkStartMinimized.IsChecked = _settings.StartMinimized;
       // ChkNotify.IsChecked = _settings.ShowNotifications;
        _suppressEvents = false;
    }

    private void ApplySettings(bool persist = true)
    {
        if (persist)
            App.SettingsStore.Save(_settings);

        var errors = _hotkeys.Reload(_settings);

        if (_miSwapWindows is not null) _miSwapWindows.Checked = _settings.SwapWindows;
        if (_miAutoStart is not null) _miAutoStart.Checked = _settings.StartWithWindows;

        HotkeyStatus.Text = errors.Count == 0
            ? $"已启用 {_hotkeys.RegisteredCount} 个全局热键，窗口隐藏后依然生效"
            : string.Join(" ", errors);
        HotkeyStatus.Foreground = (Brush)(errors.Count == 0
            ? FindResource("MutedBrush")
            : FindResource("WarnBrush"));
    }

    private void Hotkey_Changed(object? sender, EventArgs e)
    {
        if (_suppressEvents || !_initialized)
            return;

        _settings.TogglePrimary = HotkeyToggle.Value.Clone();
        //_settings.PrimaryFirst = HotkeyFirst.Value.Clone();
        //_settings.PrimarySecond = HotkeySecond.Value.Clone();
        ApplySettings();
        SetStatus("快捷键已更新并立即生效", false);
    }

    private void OnHotkeyHint(string text) => SetStatus(text, true);

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !_initialized)
            return;

        bool autoStartChanged = ReferenceEquals(sender, ChkAutoStart);
        bool previousAutoStart = _settings.StartWithWindows;
        bool previousAsAdmin = _settings.StartAsAdministrator;

        _settings.SwapWindows = ChkSwapWindows.IsChecked == true;
        _settings.StartWithWindows = ChkAutoStart.IsChecked == true;
        _settings.StartMinimized = ChkStartMinimized.IsChecked == true;
       // _settings.ShowNotifications = ChkNotify.IsChecked == true;
        string? error = autoStartChanged ? ApplyAutoStart(allowElevation: true) : null;

        // 只有系统状态变更成功后才落盘；失败时 AutoStartService 已回滚系统状态，这里恢复设置
        ApplySettings(persist: error is null);
        if (error is not null)
        {
            _settings.StartWithWindows = previousAutoStart;
            _settings.StartAsAdministrator = previousAsAdmin;
            App.SettingsStore.Save(_settings);
            SyncOptionCheckboxes();
            Notify(error, true);
        }

        UpdateAdminUi();
        SetStatus(error ?? "设置已保存", error is not null);
    }

    /// <summary>管理员自启开关：创建 / 删除计划任务需要一次 UAC 授权。</summary>
    private void AdminOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !_initialized || _adminBusy)
            return;

        bool previous = _settings.StartAsAdministrator;
        _settings.StartAsAdministrator = ChkAdminStartup.IsChecked == true;

        string? error = ApplyAutoStart(allowElevation: true);

        // 系统变更成功才保存设置；失败时 AutoStartService 已回滚系统状态，这里恢复设置与界面
        ApplySettings(persist: error is null);
        if (error is not null)
        {
            _settings.StartAsAdministrator = previous;
            App.SettingsStore.Save(_settings);
            _suppressEvents = true;
            ChkAdminStartup.IsChecked = previous;
            _suppressEvents = false;
            Notify(error, true);
        }

        UpdateAdminUi();
        SetStatus(error ?? (_settings.StartAsAdministrator
                      ? "已启用管理员权限开机自启动"
                      : "已恢复为普通权限开机自启动"),
                  error is not null);
    }

    /// <summary>把设置同步到系统；AutoStartService.Apply 是幂等的，状态一致时不会提权。</summary>
    private string? ApplyAutoStart(bool allowElevation)
    {
        if (_adminBusy)
            return null;

        _adminBusy = true;
        try
        {
            return AutoStartService.Apply(
                _settings.StartWithWindows,
                _settings.StartAsAdministrator,
                allowElevation);
        }
        finally
        {
            _adminBusy = false;
        }
    }

    private void UpdateAdminUi()
    {
        ChkAdminStartup.IsEnabled = ChkAutoStart.IsChecked == true;

        bool elevated = AdminTaskService.IsElevated;
        BtnRestartAsAdmin.IsEnabled = !elevated;
        BtnRestartAsAdmin.Content = elevated ? "已以管理员权限运行" : "以管理员权限重新启动";
        //AdminStatusText.Text = (elevated ? "当前：管理员权限" : "当前：普通权限")
                              // + " · " + AutoStartService.Describe(
                                  // _settings.StartWithWindows,
                                  // _settings.StartAsAdministrator);
    }

    private void BtnRestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        string? error = AdminTaskService.RestartAsAdmin();
        if (error is not null)
        {
            SetStatus(error, true);
            return;
        }
        // 新实例已拉起，退出当前实例；互斥体冲突由 --restart 的重试逻辑处理
        ExitApplication();
    }

    // ---------------------------------------------------------------- 托盘

    private WinForms.NotifyIcon CreateTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开界面", null, (_, _) => ShowMainWindow());
        menu.Items.Add("立即切换主显示器", null, (_, _) => DoSwap(SwapMode.Toggle));
        //menu.Items.Add(new WinForms.ToolStripSeparator());

       /* _miSwapWindows = new WinForms.ToolStripMenuItem("同时调换桌面窗口") { CheckOnClick = true };
        _miSwapWindows.Click += (_, _) =>
        {
            _settings.SwapWindows = _miSwapWindows.Checked;
            SyncOptionCheckboxes();
            ApplySettings();
        };
        menu.Items.Add(_miSwapWindows);

        _miAutoStart = new WinForms.ToolStripMenuItem("开机自动启动") { CheckOnClick = true };
        _miAutoStart.Click += (_, _) =>
        {
            _settings.StartWithWindows = _miAutoStart.Checked;
            SyncOptionCheckboxes();
            ApplySettings();
            string? error = ApplyAutoStart(allowElevation: true);
            UpdateAdminUi();
            if (error is not null)
                Notify(error, true);
            SetStatus(error ?? "设置已保存。", error is not null);
        };
        */
        //menu.Items.Add(_miAutoStart);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出 ScreenSwap", null, (_, _) => ExitApplication());

        var notify = new WinForms.NotifyIcon
        {
            Icon = CreateTrayIconImage(),
            Text = "ScreenSwap · 双屏主显示器快捷切换",
            ContextMenuStrip = menu,
            Visible = true
        };
        notify.DoubleClick += (_, _) => ShowMainWindow();
        notify.BalloonTipClicked += (_, _) => ShowMainWindow();
        return notify;
    }

    private void SyncOptionCheckboxes()
    {
        _suppressEvents = true;
        ChkSwapWindows.IsChecked = _settings.SwapWindows;
        ChkAutoStart.IsChecked = _settings.StartWithWindows;
        ChkAdminStartup.IsChecked = _settings.StartAsAdministrator;
        _suppressEvents = false;
    }

    private static Drawing.Icon CreateTrayIconImage()
    {
        using var bmp = new Drawing.Bitmap(64, 64);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(Drawing.Color.Transparent);
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var blue = new Drawing.SolidBrush(Drawing.Color.FromArgb(91, 140, 255)))
                g.FillRectangle(blue, 4, 10, 28, 20);
            using (var orange = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 169, 77)))
                g.FillRectangle(orange, 32, 34, 28, 20);
            using var pen = new Drawing.Pen(Drawing.Color.White, 3);
            pen.EndCap = Drawing.Drawing2D.LineCap.ArrowAnchor;
            g.DrawLine(pen, 30, 20, 34, 40);
        }

        IntPtr handle = bmp.GetHicon();
        var original = Drawing.Icon.FromHandle(handle);
        var icon = (Drawing.Icon)original.Clone();
        original.Dispose();
        WinApi.DestroyIcon(handle);
        return icon;
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        WinApi.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        RefreshMonitors();
    }

    private void HideToTray()
    {
        Hide();
        Notify("已隐藏到系统托盘，快捷键继续在后台生效", false);
    }

    private void ExitApplication()
    {
        _allowClose = true;
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Icon?.Dispose();
        _tray.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            Notify("已隐藏到系统托盘，快捷键继续在后台生效", false);
            return;
        }
        base.OnClosing(e);
    }

    // ---------------------------------------------------------------- 切换

    private void OnHotkey(int id)
    {
        SwapMode mode = id switch
        {
            HotkeyManager.IdFirst => SwapMode.First,
            HotkeyManager.IdSecond => SwapMode.Second,
            _ => SwapMode.Toggle
        };
        DoSwap(mode);
    }

    private void DoSwap(SwapMode mode)
    {
        _swap.Execute(mode);
        RefreshMonitors();
    }

    private void OnSwapMessage(string text, bool warning) => Notify(text, warning);

    private void OnDisplayChanged()
    {
        if (_initialized)
            RefreshMonitors();
    }

    private void Notify(string text, bool warning)
    {
        SetStatus(text, warning);
        if (!_settings.ShowNotifications)
            return;

        try
        {
            _tray.ShowBalloonTip(
                3500,
                warning ? "ScreenSwap · 操作被拒绝" : "ScreenSwap",
                text,
                warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
        }
        catch
        {
            // 某些系统策略下气泡不可用，状态栏仍会显示消息
        }
    }

    private void SetStatus(string text, bool warning)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)(warning
            ? FindResource("WarnBrush")
            : FindResource("FgBrush"));
    }

    // ---------------------------------------------------------------- 显示器卡片

    private void RefreshMonitors()
    {
        if (!_initialized)
            return;

        var monitors = DisplayService.GetMonitors();
        MonitorPanel.Children.Clear();

        var accent = (Brush)FindResource("AccentBrush");
        var accent2 = (Brush)FindResource("Accent2Brush");
        var muted = (Brush)FindResource("MutedBrush");
        var fg = (Brush)FindResource("FgBrush");
        var cardBg = (Brush)FindResource("CardBrush");
        var line = (Brush)FindResource("LineBrush");

        foreach (var m in monitors)
        {
            var card = new Border
            {
                Width = 320,
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 12, 0),
                Background = cardBg,
                BorderBrush = m.IsPrimary ? accent : line,
                BorderThickness = new Thickness(m.IsPrimary ? 2 : 1),
                CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Top
            };

            var panel = new StackPanel();
            panel.Children.Add(MakeText($"显示器 {m.Index + 1}", 11.5, muted));
            panel.Children.Add(MakeText(m.FriendlyName, 15, fg, bold: true));
            panel.Children.Add(MakeText($"分辨率  {m.Resolution}", 12, muted));
            panel.Children.Add(MakeText($"位置  {m.Position}   ·   {m.DeviceName}", 11, muted));
            panel.Children.Add(MakeText(
                m.IsPrimary ? "● 当前主显示器" : "○ 扩展显示器",
                12,
                m.IsPrimary ? accent : accent2,
                bold: m.IsPrimary));

            int index = m.Index;
            var button = new Button
            {
                Content = m.IsPrimary ? "当前主显示器" : "设为主显示器",
                Height = 34,
                Padding = new Thickness(16, 2, 16, 2),
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = monitors.Count == 2 && !m.IsPrimary
            };
            button.Click += (_, _) => DoSwap(index == 0 ? SwapMode.First : SwapMode.Second);
            panel.Children.Add(button);

            card.Child = panel;
            MonitorPanel.Children.Add(card);
        }

        if (monitors.Count == 0)
            MonitorPanel.Children.Add(MakeText("未检测到任何显示器", 13, muted));

        if (monitors.Count != 2)
        {
            MonitorPanel.Children.Add(new Border
            {
                Width = 320,
                Padding = new Thickness(16),
                Background = cardBg,
                BorderBrush = (Brush)FindResource("WarnBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = MakeText(
                    $"当前检测到 {monitors.Count} 台显示器。\n须连接 2 台显示器，切换功能才会执行",
                    12, (Brush)FindResource("WarnBrush"))
            });
        }

        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        StatusBadge.Text = monitors.Count == 2
            ? "● 已就绪 · 2 台显示器"
            : $"● 不可用 · {monitors.Count} 台显示器";
        StatusBadge.Foreground = (Brush)(monitors.Count == 2
            ? FindResource("OkBrush")
            : FindResource("WarnBrush"));

        MonitorSummary.Text = primary is null
            ? $"共 {monitors.Count} 台显示器"
            : $"共 {monitors.Count} 台显示器 · 当前主显示器：{primary.FriendlyName}";
        _tray.Text = primary is null
            ? "ScreenSwap"
            : $"ScreenSwap · 主显示器：{primary.FriendlyName}";
    }

    private static TextBlock MakeText(string text, double size, Brush color, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 5)
    };

    // ---------------------------------------------------------------- 按钮事件

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 点击标题栏上的按钮时不要启动窗口拖动，否则会吞掉 Click 事件
        if (e.OriginalSource is DependencyObject d && FindVisualParent<Button>(d) is not null)
            return;
        try
        {
            DragMove();
        }
        catch
        {
            // 窗口未处于正常状态时忽略
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshMonitors();
        SetStatus("显示器列表已刷新", false);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        ApplySettings();
        SetStatus("设置已保存", false);
    }

    private void BtnSwapNow_Click(object sender, RoutedEventArgs e) => DoSwap(SwapMode.Toggle);

    private void BtnHide_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void BtnCloseWindow_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void BtnClearToggle_Click(object sender, RoutedEventArgs e) => HotkeyToggle.ClearHotkey();


    //多余的功能
    //private void BtnClearFirst_Click(object sender, RoutedEventArgs e) => HotkeyFirst.ClearHotkey();
    //private void BtnClearSecond_Click(object sender, RoutedEventArgs e) => HotkeySecond.ClearHotkey();
}
