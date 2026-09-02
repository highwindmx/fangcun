using System;
using System.Runtime.InteropServices;

namespace Fangcun
{
    /// <summary>
    /// Win32 P/Invoke 声明：定位并操纵桌面 SysListView32。
    /// 桌面图标实际由 explorer.exe 持有，跨进程读写需 VM_*/VirtualAllocEx。
    /// </summary>
    internal static class NativeMethods
    {
        // ListView 消息
        public const int LVM_FIRST = 0x1000;
        public const int LVM_GETITEMCOUNT = LVM_FIRST + 4;       // 0x1004
        public const int LVM_GETITEMTEXTW = LVM_FIRST + 46;      // 0x102E
        public const int LVM_GETITEMPOSITION = LVM_FIRST + 16;   // 0x1010
        public const int LVM_SETITEMPOSITION = LVM_FIRST + 15;   // 0x100F

        public const uint LVIF_TEXT = 0x0001;

        // 进程/内存访问
        public const uint PROCESS_VM_OPERATION = 0x0008;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_VM_WRITE = 0x0020;
        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_RESERVE = 0x2000;
        public const uint MEM_RELEASE = 0x8000;
        public const uint PAGE_READWRITE = 0x04;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        // 桌面 reparent / 边界命中测试
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        // 父窗口查询 / 窗口有效性（G 类：Explorer 重启后父窗口失效需重挂）
        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        // 重设窗口层级/可见性（reparent 后强制刷新，避免双击 exe 无反应）
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_ASYNCWINDOWPOS = 0x4000;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;

        // 置底 z-order（沉底用）
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        // 置顶 z-order（reparent 后置于桌面图标层之上，呈现“浮于图标”的围栏效果）
        public static readonly IntPtr HWND_TOP = new IntPtr(0);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // 前台窗口查询（心跳判断用户是否正在操作围栏，避免抢占）
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        public const int SW_SHOW = 5;
        public const int SW_HIDE = 0;

        // 桌面壁纸路径（背景随桌面自适应）
        public const int SPI_GETDESKWALLPAPER = 0x0073;
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int SystemParametersInfo(int uiAction, int uiParam, System.Text.StringBuilder pvParam, int fWinIni);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public const int GWLP_EXSTYLE = -20;
        public const uint WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_LAYERED = 0x00080000;

        public const int GWL_STYLE = -16;
        // GWL_HWNDPARENT = 设置窗口的【所有者 owner】（注意不是 SetParent 的子窗关系）。
        // 把顶层窗 owner 设为 SHELLDLL_DefView，即可让窗口逃脱 Win+D“显示桌面”的最小化遍历，
        // 同时保持它是独立顶层窗（layered/AllowsTransparency 正常合成、不被改成子窗、坐标仍是屏幕坐标）。
        public const int GWL_HWNDPARENT = -8;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_CHILD = unchecked((int)0x40000000);
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPCHILDREN = 0x02000000;
        public const int WS_CLIPSIBLINGS = 0x04000000;

        // DWM 磨砂玻璃：reparent 到桌面后不能用分层透明(AllowsTransparency)，改用此实现半透明质感
        public const uint DWM_BB_ENABLE = 0x1;
        public const uint DWM_BB_BLURREGION = 0x2;
        public const uint DWM_BB_TRANSITIONONMAXIMIZED = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_BLURBEHIND
        {
            public uint dwFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fEnable;
            public IntPtr hRgnBlur;
            [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized;
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND pBlurBehind);

        // 整窗均匀半透明：仅设 WS_EX_LAYERED + LWA_ALPHA（不调用 UpdateLayeredWindow），
        // 窗口仍由 WPF 正常绘制，DWM 整窗按 bAlpha 合成。对桌面 WorkerW 的子窗口也有效（Win8+）。
        public const uint LWA_ALPHA = 0x00000002;
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // 64 位下设置所有者(GWL_HWNDPARENT)等指针型窗口长数据必须用 SetWindowLongPtr（IntPtr 版）
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetCapture(IntPtr hWnd);

        // 右键命中判定：WM_NCHITTEST 对标题栏返回 HTCAPTION 会让右键被系统吞掉（只弹系统菜单），
        // 需要判断右键是否被按下——按下则返回 HTCLIENT，让 WPF 弹自己的 ContextMenu。
        public const int VK_RBUTTON = 0x02;
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr GetDesktopWindow();

        // 读取窗口的所有者(owner)：GWL_HWNDPARENT 设为桌面 DefView 后，用 GetWindow(GW_OWNER) 验证是否生效
        public const int GW_OWNER = 4;
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        public const int WM_NCHITTEST = 0x84;
        // 光标：WM_SETCURSOR 里对 HTCAPTION(标题栏)设 SizeAll 移动光标——因为 HTCAPTION 属非客户区，
        // 系统不会自动给 move 光标，WPF 的 Cursor 属性在那里也不生效（只在 HTCLIENT 生效）。
        public const int WM_SETCURSOR = 0x0020;
        public const int IDC_SIZEALL = 32646;   // 四箭头移动光标
        public const int IDC_ARROW = 32512;
        public const int IDC_HAND = 32649;
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
        [DllImport("user32.dll")]
        public static extern IntPtr SetCursor(IntPtr hCursor);
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int WM_NCLBUTTONUP = 0x00A2;
        public const int WM_NCLBUTTONDBLCLK = 0x00A3;
        public const int HTCLIENT = 1;
        public const int HTCAPTION = 2;
        public const int HTLEFT = 10;
        public const int HTRIGHT = 11;
        public const int HTTOP = 12;
        public const int HTTOPLEFT = 13;
        public const int HTTOPRIGHT = 14;
        public const int HTBOTTOM = 15;
        public const int HTBOTTOMLEFT = 16;
        public const int HTBOTTOMRIGHT = 17;

        // 激活相关（桌面子窗口点击激活会让 explorer 异常重绘/隐藏，需拦截）
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;     // 点击不激活窗口（桌面 widget 标准做法）
        public const int MA_ACTIVATE = 1;
        public const int WM_ACTIVATE = 0x0006;
        public const int WA_INACTIVE = 0;
        public const int WM_ACTIVATEAPP = 0x001C;

        // 每窗口 DPI（Win10+），用于把 WPF 逻辑坐标与 Win32 物理像素统一，避免缩放鼠标与边界分离
        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        // 圆角区域（点击穿透）
        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        // ---------- UpdateLayeredWindow 自绘分层窗（openFence 同款：WS_CHILD + WS_EX_LAYERED + 自己推位图）----------
        // 用于把 WPF 渲染出的位图按每像素 alpha 推给系统合成，得到圆角透明 / 半透明毛玻璃围栏，
        // 同时窗口 reparent 到桌面（Win+D 不隐藏），彻底规避 AllowsTransparency 在跨进程子窗下不可见的坑。
        public const uint ULW_ALPHA = 0x00000002;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const uint BI_RGB = 0;
        public const uint DIB_RGB_COLORS = 0;
        public const uint WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
            out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;   // 负 = 自顶向下行序
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            // 32bpp BI_RGB 无需调色板，省略 bmiColors
        }

        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, int dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll")]
        public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, int dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, ref LVITEM lpBuffer, int nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] System.Text.StringBuilder lpBuffer, int nSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, ref POINT lpBuffer, int nSize, out int lpNumberOfBytesRead);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct LVITEM
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
            public int iIndent;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
