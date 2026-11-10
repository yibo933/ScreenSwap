using System.Threading;
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
    /// 以事务方式使系统自启状态与设置一致，返回 null 表示成功，否则为可直接展示的错误说明。
    /// 普通模式用注册表 Run 项；管理员模式用登录计划任务；两者互斥，避免开机启动两个实例。
    /// 流程：读取当前状态 → 计算目标状态 → 执行必要变更 → 验证结果 →
    /// 成功返回（由调用方保存设置）；失败则回滚已完成的变更，让系统回到修改前的状态。
    /// 计划任务变更放在最后（它可能触发 UAC 并被取消），这样最常见的失败路径只需回滚注册表，
    /// 不需要二次提权。状态已一致时不写入、不提权，可安全重复调用。
    /// </summary>
    public static string? Apply(bool enabled, bool asAdmin, bool allowElevation)
    {
        AutoStartState before = ReadState();
        var target = new AutoStartState(
            Run: enabled && !asAdmin,
            Task: enabled && asAdmin);

        if (before == target)
            return null;

        bool runChanged = false;
        bool taskChanged = false;
        string? failure = null;

        // 1) 注册表：低风险、可即时撤销，不需要提权
        if (before.Run != target.Run)
        {
            if (ApplyRun(target.Run))
                runChanged = true;
            else
                failure = "开机自启设置写入注册表失败";
        }

        // 2) 计划任务：高风险（UAC 可能被取消），放在最后
        if (failure is null && before.Task != target.Task)
        {
            string? error = AdminTaskService.Ensure(target.Task, ExePath, allowElevation);
            if (error is null)
                taskChanged = true;
            else
                failure = error;
        }

        // 3) 验证：确保两项系统状态都确实达到目标
       if (failure is null && WaitForState(target) != target)
            failure = "系统自启状态校验未通过：注册表 Run 项或计划任务未按预期生效";

        if (failure is null)
            return null;

         //失败：按与执行相反的顺序回滚已经完成的变更
        var rollbackErrors = new List<string>();
        if (taskChanged)
        {
            string? error = AdminTaskService.Ensure(before.Task, ExePath, allowElevation);
            if (error is not null)
                rollbackErrors.Add($"计划任务未能恢复：{error}");
        }
        if (runChanged && !ApplyRun(before.Run))
            rollbackErrors.Add("注册表 Run 项未能恢复");

        // 5) 复核回滚结果，并把实际状态写进错误说明，避免用户被界面误导
        bool anyChange = runChanged || taskChanged;
        AutoStartState rolled = WaitForState(before);
        string state = $"当前实际状态：注册表 Run 项={(rolled.Run ? "存在" : "不存在")}，" +
                       $"管理员计划任务={(rolled.Task ? "存在" : "不存在")}";

        //无论回滚是否成功，都把系统的真实状态一并报告，避免界面与实际情况脱节
        string prefix = $"{failure}\n{state}\n";
        if (rolled == before && rollbackErrors.Count == 0)
            return prefix + (anyChange
                ? "设置已回滚！"
                : "未对系统自启状态做出任何更改；设置不会保存");

       string detail = rollbackErrors.Count > 0 ? string.Join(" ", rollbackErrors) + " " : string.Empty;
        return prefix + $"已尝试回滚但未恢复：{detail}" +
               "请重新勾选一次，或以管理员权限运行后重试";
    }

    private readonly record struct AutoStartState(bool Run, bool Task);

    private static AutoStartState ReadState() =>
        new(IsEnabled(), AdminTaskService.TaskExists());

    /// <summary>任务计划程序的状态刷新可能略有延迟，短暂重试后再判定，避免误回滚。</summary>
    private static AutoStartState WaitForState(AutoStartState expected)
    {
        AutoStartState state = ReadState();
        for (int i = 0; i < 3 && state != expected; i++)
        {
            Thread.Sleep(150);
            state = ReadState();
        }
        return state;
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
