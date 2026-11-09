using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace ScreenSwap.Services;

/// <summary>
/// 管理「登录时以最高权限启动」的计划任务，以及创建/删除任务所需的提权辅助进程。
/// 注册表 Run 项由资源管理器以普通令牌拉起，无法获得管理员权限，
/// 因此管理员模式必须改由任务计划程序的 LogonTrigger + HighestAvailable 实现。
/// </summary>
internal static class AdminTaskService
{
    public const string TaskName = "ScreenSwap Admin AutoStart";

    private static readonly bool IsElevatedCache = DetectElevation();

    /// <summary>当前进程是否拥有管理员令牌。</summary>
    public static bool IsElevated => IsElevatedCache;

    /// <summary>主程序可执行文件的绝对路径。</summary>
    public static string ExePath =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>查询计划任务是否存在（只依据退出码，不解析本地化输出）。</summary>
    public static bool TaskExists()
    {
        var (code, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    /// <summary>
    /// 创建或删除计划任务。当前进程已提权时直接执行；否则重新启动自身并请求 UAC 提权。
    /// 返回 null 表示成功，否则为可直接展示给用户的错误说明。
    /// </summary>
    public static string? Ensure(bool install, string exePath, bool allowElevation)
    {
        if (IsElevated)
            return install ? Install(exePath) : Uninstall();

        if (!allowElevation)
            return "修改管理员自启计划任务需要授权，请在选项中重新勾选一次并确认 UAC 提示。";

        string requestPath = Path.Combine(
            Path.GetTempPath(), $"ScreenSwap_admin_task_{Environment.ProcessId}.txt");
        string resultPath = requestPath + ".result";

        try
        {
            File.WriteAllLines(
                requestPath,
                new[] { $"install={(install ? 1 : 0)}", $"exe={exePath}" },
                Encoding.UTF8);

            var info = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--admin-task \"{requestPath}\"",
                WorkingDirectory = Path.GetDirectoryName(ExePath) ?? string.Empty
            };

            using var process = Process.Start(info);
            if (process is null)
                return "无法启动权限提升进程。";

            process.WaitForExit();

            string result = File.Exists(resultPath)
                ? File.ReadAllText(resultPath, Encoding.UTF8).Trim()
                : string.Empty;
            if (result == "OK")
                return null;
            return string.IsNullOrWhiteSpace(result)
                ? "计划任务修改未完成，请确认已在 UAC 提示中允许操作。"
                : result;
        }
        catch (Win32Exception)
        {
            // 用户在 UAC 提示中点了「否」
            return "已取消 UAC 授权，管理员自启设置未更改。";
        }
        catch (Exception ex)
        {
            return $"权限提升失败：{ex.Message}";
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    /// <summary>
    /// 提权辅助进程的入口：读取请求、执行计划任务操作、把结果写回 .result 文件。
    /// 返回进程退出码：0 成功，1 操作失败，2 请求无效。
    /// </summary>
    public static int RunElevatedRequest(string requestPath)
    {
        string resultPath = requestPath + ".result";
        try
        {
            if (!IsElevated)
            {
                File.WriteAllText(resultPath, "权限提升未生效，计划任务未修改。", Encoding.UTF8);
                return 2;
            }

            bool install = false;
            string? exePath = null;
            foreach (string line in File.ReadAllLines(requestPath, Encoding.UTF8))
            {
                int sep = line.IndexOf('=');
                if (sep < 1)
                    continue;
                string key = line[..sep];
                string value = line[(sep + 1)..];
                if (key == "install") install = value == "1";
                else if (key == "exe") exePath = value;
            }

            if (install && string.IsNullOrWhiteSpace(exePath))
            {
                File.WriteAllText(resultPath, "请求中缺少主程序路径。", Encoding.UTF8);
                return 2;
            }

            string? error = install ? Install(exePath!) : Uninstall();
            File.WriteAllText(resultPath, error ?? "OK", Encoding.UTF8);
            return error is null ? 0 : 1;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(resultPath, $"操作失败：{ex.Message}", Encoding.UTF8);
            }
            catch
            {
                // 结果文件不可写时，父进程会给出通用提示
            }
            return 2;
        }
    }

    /// <summary>以管理员权限重新启动一个实例；返回 null 表示已成功拉起新进程。</summary>
    public static string? RestartAsAdmin()
    {
        if (IsElevated)
            return "当前已以管理员权限运行，无需重启。";

        try
        {
            var info = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                // --restart 让新实例在单实例互斥体冲突时短暂重试，等待旧实例退出
                Arguments = "--restart",
                WorkingDirectory = Path.GetDirectoryName(ExePath) ?? string.Empty
            };
            Process.Start(info);
            return null;
        }
        catch (Win32Exception)
        {
            return "已取消 UAC 授权，程序继续以普通权限运行。";
        }
        catch (Exception ex)
        {
            return $"重启失败：{ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 计划任务

    private static string? Install(string exePath)
    {
        string xmlPath = Path.Combine(
            Path.GetTempPath(), $"ScreenSwap_task_{Environment.ProcessId}.xml");
        try
        {
            // schtasks /XML 在部分系统上要求 UTF-16 编码
            File.WriteAllText(xmlPath, BuildTaskXml(exePath), Encoding.Unicode);
            var (code, _) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            return code == 0
                ? null
                : $"创建计划任务失败（schtasks 退出码 {code}）。";
        }
        catch (Exception ex)
        {
            return $"创建计划任务失败：{ex.Message}";
        }
        finally
        {
            TryDelete(xmlPath);
        }
    }

    private static string? Uninstall()
    {
        if (!TaskExists())
            return null;

        var (code, _) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        return code == 0
            ? null
            : $"删除计划任务失败（schtasks 退出码 {code}）。";
    }

    private static string BuildTaskXml(string exePath)
    {
        // InteractiveToken + 不指定 UserId：任务以「当前登录用户」身份运行，
        // HighestAvailable 会取该用户的最高令牌，因此对管理员账户无需每次弹 UAC。
        // 也不依赖提权进程自身的账户，避免标准用户借管理员凭据提权后绑定到错误账户。
        string command = SecurityElement.Escape(exePath);
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>ScreenSwap 登录后以最高权限自动启动</Description>
    <Author>ScreenSwap</Author>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <Arguments>--minimized</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static (int Code, string Text) RunSchtasks(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(info);
            if (process is null)
                return (-1, string.Empty);

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 临时文件清理失败不影响主流程
        }
    }
}
