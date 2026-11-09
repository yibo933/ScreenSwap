using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using ScreenSwap.Models;
using ScreenSwap.Native;
using ScreenSwap.Services;
using WinForms = System.Windows.Forms;

namespace ScreenSwap;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    /// <summary>全局共享的设置对象（界面修改后立即生效）。</summary>
    public static AppSettings Settings { get; private set; } = new();

    internal static SettingsService SettingsStore { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 提权辅助进程：只负责创建/删除开机自启计划任务。
        // 必须在单实例互斥体之前处理，否则提权实例会被「程序已在运行」拦下。
        if (e.Args.Length >= 2 &&
            string.Equals(e.Args[0], "--admin-task", StringComparison.OrdinalIgnoreCase))
        {
            int exitCode = AdminTaskService.RunElevatedRequest(e.Args[1]);
            Shutdown(exitCode);
            return;
        }

        // 只读诊断模式：不创建窗口、不注册热键，输出 CCD 结构与当前显示拓扑后退出
        if (e.Args.Any(a => string.Equals(a, "--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            var report = new StringBuilder();
            report.AppendLine("ScreenSwap 诊断报告  " + DateTime.Now).AppendLine();
            var monitors = DisplayService.GetMonitors();
            report.AppendLine($"显示器数量：{monitors.Count}");
            foreach (var m in monitors)
                report.AppendLine($"  [{m.Index + 1}] {m.FriendlyName} / {m.DeviceName} / {m.Resolution} / 位置 {m.Position} / 主显示器={m.IsPrimary}");
            report.AppendLine().AppendLine(DisplayConfigApi.Diagnose(monitors));

            string reportPath = Path.Combine(Path.GetTempPath(), "ScreenSwap_diagnostics.txt");
            File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
            Shutdown();
            return;
        }

        // 以管理员权限重启时，新实例会在 UAC 通过后立刻启动，
        // 此时旧实例可能还持有互斥体，短暂重试等它退出。
        bool restart = e.Args.Any(a => string.Equals(a, "--restart", StringComparison.OrdinalIgnoreCase));
        Mutex? mutex = null;
        bool createdNew = false;
        for (int attempt = 0; attempt < (restart ? 25 : 1); attempt++)
        {
            mutex = new Mutex(initiallyOwned: true, @"Local\ScreenSwap.SingleInstance", out createdNew);
            if (createdNew)
                break;
            mutex.Dispose();
            if (!restart)
                break;
            Thread.Sleep(100);
        }
        _singleInstanceMutex = createdNew ? mutex : null;

        if (!createdNew)
        {
            WinForms.MessageBox.Show(
                "ScreenSwap 已在后台运行。\n请点击系统托盘中的图标打开设置或退出程序。",
                "ScreenSwap",
                WinForms.MessageBoxButtons.OK,
                WinForms.MessageBoxIcon.Information);
            Shutdown();
            return;
        }

        Settings = SettingsStore.Load();

        bool startHidden = Settings.StartMinimized ||
                           e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow(Settings);
        MainWindow = window;

        if (!startHidden)
            window.Show();
        // 隐藏启动时，窗口句柄已在 MainWindow 构造函数中通过 EnsureHandle 创建，
        // 因此全局热键与托盘依旧可用。
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch
        {
            // 进程退出时忽略互斥体清理异常
        }
        base.OnExit(e);
    }
}
