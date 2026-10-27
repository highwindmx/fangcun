using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Fangcun.Xichuang.Native;

/// <summary>
/// 栖窗所需的 Win32 P/Invoke 声明（M0 阶段只覆盖窗口发现 + 嵌入 + 释放）。
/// </summary>
public static class Win32
{
    // ---- Window long 索引 ----
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    // ---- 窗口样式 ----
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CLIPSIBLINGS = 0x04000000;
    public const int WS_CLIPCHILDREN = 0x02000000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_DLGFRAME = 0x00400000;
    public const int WS_BORDER = 0x00800000;
    public const int WS_MAXIMIZE = 0x01000000;
    public const int WS_MINIMIZE = 0x20000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // WINDOWPLACEMENT.flags 位定义：位 1 表示「(从最小化)还原时应还原为最大化」。
    // 归集时若不清掉它，窗口之后被还原就会弹回最大化 —— 表现为「位置/尺寸又不对了」。
    public const uint WPF_RESTORETOMAXIMIZED = 0x0002;

    // ---- SetWindowPos flags ----
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;

    // ---- 消息 ----
    public const uint WM_SIZE = 0x0005;
    public const uint WM_WINDOWPOSCHANGED = 0x0047;

    // ---- ShowWindow ----
    public const int SW_SHOW = 5;
    public const int SW_HIDE = 0;
    public const int SW_MINIMIZE = 2;

    // ---- Z 序插入位置（务必与 Win32 SDK 一致！）----
    // HWND_TOP = 0，HWND_BOTTOM = 1，HWND_TOPMOST = -1，HWND_NOTOPMOST = -2。
    // 历史 bug：曾把 HWND_TOP 写成 (IntPtr)(-1)，那其实是 HWND_TOPMOST ——
    // 于是每次「置前」都把被归集的窗口变成总在最前，新打开的程序窗口全被压在下面。
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_BOTTOM = (IntPtr)1;
    public static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);
    public static readonly IntPtr HWND_NOTOPMOST = (IntPtr)(-2);

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;

    // ---- DWM ----
    public const uint DWMWA_CLOAKED = 14;

    // ---- 显示器 / DPI ----
    public const uint MONITOR_DEFAULTTONULL = 0;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(int x, int y, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern int GetDpiForSystem();

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>枚举所有显示器的工作区（物理像素），主屏强制排在第一位（index 0）。</summary>
    public static List<(IntPtr Handle, RECT Work)> EnumMonitors()
    {
        var all = new List<(IntPtr Handle, RECT Work)>();
        MonitorEnumProc proc = (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi)) all.Add((hMon, mi.rcWork));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);

        // 主屏优先：MonitorFromPoint(0,0) 即主屏
        var primary = MonitorFromPoint(0, 0, MONITOR_DEFAULTTOPRIMARY);
        var ordered = new List<(IntPtr, RECT)>();
        var p = all.FirstOrDefault(m => m.Handle == primary);
        if (p.Handle != IntPtr.Zero) ordered.Add(p);
        foreach (var m in all) if (m.Handle != primary) ordered.Add(m);
        return ordered;
    }

    /// <summary>按配置序号取显示器句柄（0 = 主屏）；越界/无显示器时回退主屏。</summary>
    public static IntPtr MonitorByIndex(int index)
    {
        var mons = EnumMonitors();
        if (mons.Count == 0) return MonitorFromPoint(0, 0, MONITOR_DEFAULTTOPRIMARY);
        index = Math.Max(0, Math.Min(index, mons.Count - 1));
        return mons[index].Handle;
    }

    /// <summary>取指定显示器的 DPI 缩放因子（物理/96）。失败回退系统 DPI。</summary>
    public static double ScaleForMonitor(IntPtr hMon)
    {
        if (GetDpiForMonitor(hMon, 0, out uint x, out uint _) == 0 && x > 0) return x / 96.0;
        return GetDpiForSystem() / 96.0;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    /// <summary>窗口是否「已挂起」（不响应消息）。心跳里读标题前先问一下，
    /// 避免 GetWindowText 卡在无响应的窗口上把侧栏也拖住。</summary>
    [DllImport("user32.dll")]
    public static extern bool IsHungAppWindow(IntPtr hWnd);

    // ---- 取进程 exe 路径 ----
    // 比 Process.MainModule 可靠：只需 QUERY_LIMITED_INFORMATION 权限，
    // 对提权进程、跨位数进程也能拿到路径（MainModule 常见 AccessDenied）。

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
        StringBuilder lpExeName, ref uint lpdwSize);

    /// <summary>进程主模块路径；拿不到返回 null。</summary>
    public static string? ProcessImagePath(uint pid)
    {
        if (pid == 0) return null;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageNameW(h, 0, sb, ref size) && size > 0)
                    return sb.ToString(0, (int)size);
            }
            finally { CloseHandle(h); }
        }
        return null;
    }

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint dwFlags);

    // ---- 前台 / 置前 ----
    public const int SW_RESTORE = 9;
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    /// <summary>未文档化但仍由 user32 导出；SetForegroundWindow 被前台锁拒绝时是标准兜底（Alt+Tab 同一路径）。</summary>
    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    /// <summary>
    /// 稳健地把窗口提到前台（逐级绕过 Windows 前台锁）。
    ///
    /// 系统规则：调用进程不是前台进程时，SetForegroundWindow 会被**静默拒绝**——
    /// 表现就是目标窗口只在任务栏闪一下、并不真正激活。这不是 bug，是 Windows 故意限制。
    /// 逐级尝试：
    ///   ① 经典绕过：本线程 AttachThreadInput 到「当前前台窗口所在线程」→ BringWindowToTop + SetForegroundWindow；
    ///   ② 若仍失败则 AttachThreadInput 到「目标窗口所在线程」→ SetActiveWindow + SetFocus + SetForegroundWindow
    ///      （SetFocus 只对已附加的线程队列生效，这一步才是真正把键盘焦点交过去）；
    ///   ③ 仍失败则 SwitchToThisWindow（系统内部强制切换，Alt+Tab 同款）。
    /// 返回是否最终真正拿到前台。拿不到通常意味着目标程序以更高权限运行（UIPI 限制），
    /// 此时任何用户态手段都无解，只能同权限运行。
    /// </summary>
    public static bool Activate(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;

        // 最小化窗口 SetForegroundWindow 无效，必须先还原
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        if (GetForegroundWindow() == hWnd) return true;

        uint myTid = GetCurrentThreadId();

        // ① 附加到当前前台线程：让本线程被视为「前台线程」的一部分
        IntPtr fg = GetForegroundWindow();
        uint fgTid = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        bool attachedFg = fgTid != 0 && fgTid != myTid && AttachThreadInput(myTid, fgTid, true);
        try
        {
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attachedFg) AttachThreadInput(myTid, fgTid, false);
        }
        if (GetForegroundWindow() == hWnd) return true;

        // ② 附加到目标窗口线程：SetActiveWindow / SetFocus 只对「已附加」的输入队列生效
        uint tTid = GetWindowThreadProcessId(hWnd, out _);
        bool attachedT = tTid != 0 && tTid != myTid && AttachThreadInput(myTid, tTid, true);
        try
        {
            BringWindowToTop(hWnd);
            SetActiveWindow(hWnd);
            SetFocus(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attachedT) AttachThreadInput(myTid, tTid, false);
        }
        if (GetForegroundWindow() == hWnd) return true;

        // ③ 系统内部强制切换（未文档化的正统兜底）
        SwitchToThisWindow(hWnd, true);
        return GetForegroundWindow() == hWnd;
    }

    /// <summary>
    /// 视觉兜底：把窗口顶到最前（不改变前台、不抢焦点）。
    /// 前台锁实在绕不过去时，至少保证用户「看得见」这个窗口，而不是只在任务栏闪一下。
    /// 手法是借道 topmost 层再立刻撤销——比单纯 HWND_TOP 更能压过其它 topmost 窗口。
    /// </summary>
    public static void FlashToFront(IntPtr hWnd)
    {
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // ---- 取窗口图标 ----
    public const uint WM_GETICON = 0x007F;
    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;
    public const int ICON_SMALL2 = 2;
    public const int GCLP_HICONSM = -34;
    public const int GCLP_HICON = -14;
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtr", SetLastError = true)]
    public static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    /// <summary>从 HWND 取真实软件图标（优先窗口自带，回退到类图标）；失败返回 null。不负责 DestroyIcon。</summary>
    public static System.Windows.Media.ImageSource? GetWindowIcon(IntPtr hWnd)
    {
        IntPtr hIcon = SendMessage(hWnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero);
        if (hIcon == IntPtr.Zero) hIcon = SendMessage(hWnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);
        if (hIcon == IntPtr.Zero) hIcon = SendMessage(hWnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
        if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hWnd, GCLP_HICONSM);
        if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hWnd, GCLP_HICON);
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hIcon, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        catch { return null; }
    }

    public static string GetTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        return GetWindowText(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>取窗口类名（Progman/WorkerW/Shell_TrayWnd 等外壳窗口靠它识别）。</summary>
    public static string GetClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameW(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>窗口当前是否带 WS_EX_TOPMOST（总在最前）。</summary>
    public static bool IsTopmost(IntPtr hWnd)
        => (GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    /// <summary>解除「总在最前」，让窗口回到普通层级（新开的程序才能盖在它上面）。</summary>
    public static void ClearTopmost(IntPtr hWnd)
    {
        if (!IsTopmost(hWnd)) return;
        SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
