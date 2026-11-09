using System.Runtime.InteropServices;
using System.Text;
using ScreenSwap.Models;

namespace ScreenSwap.Native;

/// <summary>
/// 通过 CCD API (QueryDisplayConfig / SetDisplayConfig) 设置主显示器。
/// Windows 规定虚拟桌面坐标 (0,0) 所在的显示器即为主显示器，
/// 因此把所有 source mode 的坐标整体平移，让目标显示器落到 (0,0)，
/// 即可在保持物理左右/上下排列不变的前提下切换主显示器。
/// </summary>
internal static class DisplayConfigApi
{
    private const string User32 = "user32.dll";

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    private const uint SDC_VALIDATE = 0x00000040;
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    private const uint SDC_ALLOW_CHANGES = 0x00000400;
    private const int ERROR_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)]
        public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public POINTL position;
    }

    // 原生 union 的最大成员为 DISPLAYCONFIG_TARGET_MODE (48 字节)。
    // 只映射我们需要的 source mode；其余字节在 P/Invoke 往返中原样保留。
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct DISPLAYCONFIG_MODE_INFO_UNION
    {
        [FieldOffset(0)]
        public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
    }

    [DllImport(User32)]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport(User32)]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport(User32)]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DISPLAYCONFIG_PATH_INFO[] pathArray,
        uint numModeInfoArrayElements,
        [In] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        uint flags);

    /// <summary>
    /// 把 <paramref name="target"/> 设为主显示器。
    /// <paramref name="validateOnly"/> 为 true 时只让系统校验配置，不实际改变桌面。
    /// </summary>
    internal static bool SetPrimary(MonitorInfo target, int targetIndex, out string error, bool validateOnly = false)
    {
        error = string.Empty;

        int rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
        if (rc != ERROR_SUCCESS)
        {
            error = $"读取显示配置缓冲区失败 (0x{rc:X8})";
            return false;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (rc != ERROR_SUCCESS)
        {
            error = $"读取当前显示配置失败 (0x{rc:X8})";
            return false;
        }

        Array.Resize(ref paths, (int)pathCount);
        Array.Resize(ref modes, (int)modeCount);

        var sources = new List<(int modeIdx, POINTL pos)>();
        for (int i = 0; i < paths.Length; i++)
        {
            if ((paths[i].flags & DISPLAYCONFIG_PATH_ACTIVE) == 0)
                continue;

            uint idx = paths[i].sourceInfo.modeInfoIdx;
            if (idx >= modes.Length || modes[idx].infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                continue;

            sources.Add(((int)idx, modes[idx].modeInfo.sourceMode.position));
        }

        if (sources.Count < 2)
        {
            error = "未检测到两个独立工作的显示器输出（可能处于复制模式），已拒绝切换。";
            return false;
        }

        POINTL delta;
        int exact = sources.FindIndex(s => s.pos.x == target.Left && s.pos.y == target.Top);
        if (exact >= 0)
        {
            delta = sources[exact].pos;
        }
        else
        {
            // 某些显卡驱动下 CCD 坐标与 EnumDisplayMonitors 坐标不完全一致，
            // 退化为按从左到右的顺序取第 N 个输出。
            var ordered = sources.OrderBy(s => s.pos.x).ThenBy(s => s.pos.y).ToList();
            delta = ordered[Math.Clamp(targetIndex, 0, ordered.Count - 1)].pos;
        }

        foreach (var s in sources)
        {
            var mode = modes[s.modeIdx];
            var sourceMode = mode.modeInfo.sourceMode;
            sourceMode.position = new POINTL
            {
                x = sourceMode.position.x - delta.x,
                y = sourceMode.position.y - delta.y
            };
            mode.modeInfo.sourceMode = sourceMode;
            modes[s.modeIdx] = mode;
        }

        uint flags = SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES;
        flags |= validateOnly
            ? SDC_VALIDATE
            : SDC_APPLY | SDC_SAVE_TO_DATABASE;

        rc = SetDisplayConfig(
            (uint)paths.Length, paths,
            (uint)modes.Length, modes,
            flags);

        if (rc != ERROR_SUCCESS)
        {
            error = validateOnly
                ? $"配置校验未通过 (0x{rc:X8})"
                : $"应用显示配置失败 (0x{rc:X8})，可尝试在「Windows 显示设置」中确认扩展模式。";
            return false;
        }

        return true;
    }

    /// <summary>只读诊断：检查 CCD 结构体布局与当前显示拓扑，并让系统校验一次切换配置（不实际应用）。</summary>
    internal static string Diagnose(IReadOnlyList<MonitorInfo> monitors)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DISPLAYCONFIG_PATH_INFO 大小 = {Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>()} 字节（原生应为 72）");
        sb.AppendLine($"DISPLAYCONFIG_MODE_INFO 大小 = {Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>()} 字节（原生应为 64）");

        int rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
        sb.AppendLine($"GetDisplayConfigBufferSizes -> {rc}，paths={pathCount}，modes={modeCount}");
        if (rc != ERROR_SUCCESS)
            return sb.ToString();

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        sb.AppendLine($"QueryDisplayConfig -> {rc}，paths={pathCount}，modes={modeCount}");
        if (rc != ERROR_SUCCESS)
            return sb.ToString();

        for (int i = 0; i < pathCount; i++)
        {
            bool active = (paths[i].flags & DISPLAYCONFIG_PATH_ACTIVE) != 0;
            sb.AppendLine($"  path[{i}] active={active}, flags=0x{paths[i].flags:X8}, " +
                          $"sourceId={paths[i].sourceInfo.id}, sourceModeIdx={paths[i].sourceInfo.modeInfoIdx}");
        }

        for (int i = 0; i < modeCount; i++)
        {
            if (modes[i].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            {
                var s = modes[i].modeInfo.sourceMode;
                sb.AppendLine($"  mode[{i}] SOURCE {s.width}x{s.height} @ ({s.position.x}, {s.position.y})");
            }
            else
            {
                sb.AppendLine($"  mode[{i}] type={modes[i].infoType}");
            }
        }

        sb.AppendLine();
        var target = monitors.FirstOrDefault(m => !m.IsPrimary);
        if (monitors.Count == 2 && target is not null)
        {
            bool valid = SetPrimary(target, target.Index, out string error, validateOnly: true);
            sb.AppendLine($"[只读校验] 把「{target.FriendlyName}」设为主显示器的配置：{(valid ? "系统校验通过（未实际应用）" : error)}");
        }
        else
        {
            sb.AppendLine($"[只读校验] 跳过：当前不是 2 台显示器（{monitors.Count} 台）。");
        }

        return sb.ToString();
    }
}
