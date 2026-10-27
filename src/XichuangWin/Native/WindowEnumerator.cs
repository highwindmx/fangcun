using System.Diagnostics;
using System.Windows.Media;

namespace XiChuang.Native;

public sealed record WindowInfo(IntPtr Handle, uint Pid, string ProcessName, string Title)
{
    /// <summary>窗口类名。用于持久化匹配与外壳窗口过滤（句柄重启后会变，类名+进程名才是稳定身份）。</summary>
    public string ClassName { get; init; } = "";

    public ImageSource? Icon { get; init; }

    // 图标已能区分软件，故只显示窗口标题；标题为空时回退到进程名，不再带分隔符/句柄
    public string Display => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;
}

/// <summary>
/// 枚举所有可收纳的顶层窗口。
/// 判定「顶层」用 WS_CHILD 位，而不是 GetParent()==0 —— 后者对有 owner 的无边框窗口
/// （飞书、微信这类自绘标题栏的 Popup 风格主窗）会返回非零，导致它们被整批误杀。
/// </summary>
internal static class WindowEnumerator
{
    /// <summary>桌面 / 任务栏 / 开始菜单等「外壳」窗口类名：不是用户的应用窗口，不进清单。</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",                          // 桌面（标题正是 "Program Manager"）
        "WorkerW",                          // 桌面壁纸/图标层
        "Shell_TrayWnd",                    // 主任务栏
        "Shell_SecondaryTrayWnd",           // 副屏任务栏
        "NotifyIconOverflowWindow",         // 托盘溢出
        "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow",     // 开始菜单/搜索
        "MultitaskingViewFrame",
        "ForegroundStaging",
        "TaskListThumbnailWnd",             // 任务栏缩略图预览
        "DV2ControlHost",
        "SysShadow",
        "Default IME",
        "MSCTFIME UI",
    };

    public static List<WindowInfo> Enumerate()
    {
        var list = new List<WindowInfo>();
        uint selfPid = (uint)Environment.ProcessId;

        Win32.EnumWindows((hWnd, _) =>
        {
            var info = Describe(hWnd, selfPid);
            if (info is not null) list.Add(info);
            return true;
        }, IntPtr.Zero);

        return list.OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
                   .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    /// <summary>把一个 HWND 解释为可收纳的 WindowInfo；不合格返回 null。自校验用，供「加窗」与持久化复用。</summary>
    public static WindowInfo? Describe(IntPtr hWnd)
        => Describe(hWnd, (uint)Environment.ProcessId);

    public static WindowInfo? Describe(IntPtr hWnd, uint selfPid)
    {
        if (hWnd == IntPtr.Zero || !Win32.IsWindow(hWnd)) return null;
        if (!Win32.IsWindowVisible(hWnd)) return null;

        // 只收顶层窗口：WS_CHILD 为 0 即顶层（有 owner 的 Popup 窗口仍是顶层）
        int style = Win32.GetWindowLong(hWnd, Win32.GWL_STYLE);
        if ((style & Win32.WS_CHILD) != 0) return null;

        string cls = Win32.GetClass(hWnd);
        if (cls.Length == 0 || ShellClasses.Contains(cls)) return null;

        // 被 DWM 隐藏的窗口（UWP 挂起、非当前虚拟桌面、已 cloaked）
        if (Win32.DwmGetWindowAttribute(hWnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return null;

        Win32.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0 || pid == selfPid) return null;

        var title = Win32.GetTitle(hWnd);

        // 无标题窗口只在「任务栏可见（WS_EX_APPWINDOW）」时才收，避免各种辅助/隐藏窗口混进来
        if (string.IsNullOrWhiteSpace(title))
        {
            int ex = Win32.GetWindowLong(hWnd, Win32.GWL_EXSTYLE);
            if ((ex & Win32.WS_EX_APPWINDOW) == 0) return null;
        }

        string procName = "?";
        try { procName = Process.GetProcessById((int)pid).ProcessName; }
        catch { /* 进程已退出 */ }

        return new WindowInfo(hWnd, pid, procName, title.Length > 60 ? title[..60] + "…" : title)
        {
            ClassName = cls
        };
    }

    public static WindowInfo? FindByProcess(IEnumerable<WindowInfo> windows, string[] processNames)
    {
        foreach (var name in processNames)
        {
            var hit = windows.FirstOrDefault(w =>
                string.Equals(w.ProcessName, name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }
}
