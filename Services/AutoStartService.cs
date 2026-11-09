using Microsoft.Win32;

namespace ScreenSwap.Services;

internal static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScreenSwap";

    private static string ExePath => AdminTaskService.ExePath;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 使系统状态与设置一致，返回 null 表示成功，否则为可直接展示的错误说明。
    /// 普通模式用注册表 Run 项；管理员模式用登录计划任务；两者互斥，避免开机启动两个实例。
    /// 状态已一致时不做任何写入，因此可以在每次设置变更时安全地重复调用。
    /// </summary>
    public static string? Apply(bool enabled, bool asAdmin, bool allowElevation)
    {
        bool wantRun = enabled && !asAdmin;
        bool wantTask = enabled && asAdmin;

        if (IsEnabled() != wantRun && !ApplyRun(wantRun))
            return "开机自启设置写入注册表失败。";

        if (AdminTaskService.TaskExists() != wantTask)
            return AdminTaskService.Ensure(wantTask, ExePath, allowElevation);

        return null;
    }

    /// <summary>只读的自启状态描述，用于界面展示。</summary>
    public static string Describe(bool enabled, bool asAdmin)
    {
        if (!enabled)
            return "开机自启：已关闭";

        return asAdmin
            ? AdminTaskService.TaskExists()
                ? "开机自启：管理员权限（计划任务已就绪）"
                : "开机自启：管理员权限（计划任务未创建）"
            : "开机自启：普通权限（注册表 Run 项）";
    }

    private static bool ApplyRun(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                           ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
                return false;

            if (enabled)
                key.SetValue(ValueName, $"\"{ExePath}\" --minimized");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
