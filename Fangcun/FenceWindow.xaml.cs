using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Fangcun
{
    // 围栏主窗口。架构（2026-09-02 定稿，已发布，本版修正“HwndSource 子窗不分层”导致不可见）：
    // 顶层 WPF Window + AllowsTransparency=True → WPF 在创建时就带上 WS_EX_LAYERED，
    // 圆角(CornerRadius)+半透明(BgColor=#AARRGGBB)由 WPF 原生 per-pixel alpha 合成，无黑底/黑矩形问题。
    // OnSourceInitialized 里 SetParent 到桌面 WorkerW/Progman → 成为桌面子窗 → Win+D 不隐藏。
    // 缩放交回 Windows 原生：WM_NCHITTEST 对边界返回 HT*，系统拖动窗口四边缩放（无手动像素换算，根除“鼠标边界分离”）。
    // 移动：标题栏区返回 HTCAPTION 由系统原生拖动；双击标题进入重命名。
    public partial class FenceWindow : Window
    {
        private readonly Fence _fence;
        private FenceItem? _dragItem;
        private Point _dragStart;
        private bool _ellipsisExpanded;
        private double _normalHeight;
        private ObservableCollection<FenceItem>? _display;

        // 心跳：reparent 态下检测桌面父窗口（Explorer 重启）失效则重挂
        private readonly DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromSeconds(3) };

        private IntPtr _hwnd;
        private IntPtr _desktopHost;   // 桌面 owner（SHELLDLL_DefView），设 owner 后窗口仍顶层、坐标不变
        private bool _reparented;      // = 是否已成功设 owner（Win+D 免疫）
        private static bool _sent052C;

        private string? _manualBg;          // 随桌面自适应关闭时要还原的手动背景色
        private string? _manualTitleBar;    // 随桌面自适应关闭时要还原的手动栏底色
        private string? _manualItemColor;   // 随桌面自适应关闭时要还原的手动条目字体色
        private string? _manualTitleColor;  // 随桌面自适应关闭时要还原的手动标题/按钮字色
        private bool _tintApplied;          // 是否已把 BgColor/TitleBarColor/字色覆盖为壁纸自适应值
        private bool _closed;

        // 壁纸轮询：记录上次壁纸签名（路径|时间戳），心跳比对变化时重算自适应（兜底第三方换壁纸不触发系统事件）
        private string? _lastTintSig;
        // 背景/栏底原始绑定（构造后捕获），变色淡入时临时解绑再于动画结束复原
        private Binding? _bgBinding, _barBinding;
        // 拖动/缩放 debounce：自适应开启时，移动或缩放围栏后延迟 200ms 重算自适应（避免拖动每帧都 LockBits 采样壁纸卡顿）
        private readonly DispatcherTimer _tintDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };

        // ---------- 主题预设（浅色/深色），值与"背景样式/栏底/字色"全套匹配 ----------
        // "主题"菜单按"当前 Style 颜色==哪套预设"决定勾选；都不是 → 判为自定义。
        private static readonly (string Bg, string Bar, string Ink) DarkPreset = ("#80000000", "#33000000", "#FFFFFF");
        private static readonly (string Bg, string Bar, string Ink) LightPreset = ("#99FFFFFF", "#C0FFFFFF", "#000000");

        public FenceWindow(Fence fence)
        {
            _fence = fence;
            DataContext = _fence;
            InitializeComponent();

            // 捕获背景/栏底原始绑定，供变色淡入时临时解绑、动画结束复原（保持与 Style.* 的双向绑定）
            _bgBinding = BindingOperations.GetBinding(RootBorder, BackgroundProperty);
            _barBinding = BindingOperations.GetBinding(TitleBar, BackgroundProperty);

            // 顶层窗口直接用 WPF 逻辑坐标定位（WPF 自动处理 DPI），无需手动像素换算
            Left = _fence.X;
            Top = _fence.Y;
            Width = _fence.Width;
            Height = _fence.Height;

            _normalHeight = _fence.Collapsed ? 320 : _fence.Height;
            Scroller.Visibility = _fence.Collapsed ? Visibility.Collapsed : Visibility.Visible;
            _fence.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Fence.Collapsed))
                    Scroller.Visibility = _fence.Collapsed ? Visibility.Collapsed : Visibility.Visible;
                else if (e.PropertyName == nameof(Fence.Overflow))
                    ApplyOverflowMode();
            };
            ApplyOverflowMode();

            // 随桌面自适应：勾选/取消时实时覆盖或还原背景；监听系统壁纸变更自动刷新
            _fence.Style.PropertyChanged += Style_PropertyChanged;
            SystemEvents.UserPreferenceChanged += OnUserPrefChanged;
            if (_fence.Style.UseWallpaperTint) ApplyWallpaperTint();

            Loaded += (_, _) =>
            {
                _titleBarHeight = Math.Max(26, TitleBar.ActualHeight);
                ApplyClip();
            };
            SizeChanged += (_, _) =>
            {
                _titleBarHeight = Math.Max(26, TitleBar.ActualHeight);
                _fence.Width = ActualWidth;
                _fence.Height = ActualHeight;
                ApplyClip();
                // 省略模式：容量随拖放后尺寸变化，需重算显示。
                // 展开态若新尺寸已容不下全部条目，则收回省略，避免条目溢出圆角。
                if (_fence.Overflow == OverflowMode.Ellipsis)
                {
                    if (_ellipsisExpanded && _fence.Items.Count > ComputeCapacity()) _ellipsisExpanded = false;
                    RebuildDisplay();
                }
                Save();
                // 自适应开启时，缩放围栏后延迟重算，使背景随所在桌面区域变化（缺陷2）
                if (_fence.Style.UseWallpaperTint) { _tintDebounce.Stop(); _tintDebounce.Start(); }
            };
            LocationChanged += (_, _) =>
            {
                // owner 模式下窗口仍是顶层，Left/Top 恒为屏幕坐标，写回无污染风险
                _fence.X = Left;
                _fence.Y = Top;
                Save();
                // 自适应开启时，移动围栏后延迟重算，使背景随所在桌面区域变化（缺陷2）
                if (_fence.Style.UseWallpaperTint) { _tintDebounce.Stop(); _tintDebounce.Start(); }
            };

            _heartbeat.Tick += Heartbeat_Tick;
            _heartbeat.Start();

            // 拖动/缩放后重算自适应：停止操作 200ms 后再算一次，使背景随围栏所在桌面区域变化（缺陷2）
            _tintDebounce.Tick += (_, _) =>
            {
                _tintDebounce.Stop();
                if (!_closed && _fence.Style.UseWallpaperTint) ApplyWallpaperTint();
            };
            Log($"FenceWindow ctor: '{_fence.Title}' at ({_fence.X},{_fence.Y}) {_fence.Width}x{_fence.Height}");
            App.RegisterWindow(this); // 供托盘"隐藏/显示所有围栏"
        }

        private static readonly object _logLock = new();
        private static void Log(string msg)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fangcun");
                Directory.CreateDirectory(dir);
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
                lock (_logLock) File.AppendAllText(Path.Combine(dir, "fangcun.log"), line + Environment.NewLine);
            }
            catch { }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd).AddHook(HwndHook);
            ReparentToDesktop();
        }

        // ---------- 桌面常驻：把窗口 owner 设为桌面 SHELLDLL_DefView（Win+D 不隐藏） ----------
        // 关键认知（2026-09-02 14:05→本版）：之前一路用 SetParent(把窗变 WS_CHILD 子窗)去贴桌面，
        // 在本机反复失败/不可见：①贴 Progman → `reparented=False`（AllowsTransparency 分层窗跨进程
        // SetParent 不生效，坐标被改却不显示→不可见）；②只贴可见 WorkerW → 本机常常找不到可见含
        // DefView 的 WorkerW → 回退顶层 → Win+D 仍隐藏。
        // 正解（winsoft666 逃逸 Win+D 三法之"方式二"）：**不改父窗，只把窗口的【所有者 owner】设为
        // SHELLDLL_DefView**（`SetWindowLongPtr(hwnd, GWL_HWNDPARENT, defView)`）。窗口仍是独立顶层窗：
        //   - layered/AllowsTransparency 正常合成（不再被改成子窗 → 无黑底/不可见）；
        //   - 坐标仍是屏幕坐标（不引入父相对负坐标污染）；
        //   - owner 归属桌面 → Win+D"显示桌面"的最小化遍历视其为桌面一部分 → **不隐藏**。
        // 该方法与 SetParent 完全不同：SetParent 建子窗关系（WS_CHILD），GWL_HWNDPARENT 建 owner 关系（顶层+owned）。
        private void ReparentToDesktop()
        {
            try
            {
                var defView = FindDefView();
                if (defView == IntPtr.Zero)
                {
                    Log("Reparent: 未找到 SHELLDLL_DefView 桌面宿主，保持独立顶层窗口（Win+D 不免疫）");
                    _reparented = false;
                    return;
                }
                _desktopHost = defView;
                // 设 owner（不是 SetParent 子窗）。窗口保持顶层，坐标/分层不受影响。
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_HWNDPARENT, defView);
                _reparented = NativeMethods.GetWindow(_hwnd, NativeMethods.GW_OWNER) == defView;
                int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWLP_EXSTYLE);
                Log($"Reparent(owner): defView={defView} owned={_reparented} layered={(ex & NativeMethods.WS_EX_LAYERED) != 0} at({_fence.X},{_fence.Y}) {_fence.Width}x{_fence.Height}");
            }
            catch (Exception ex)
            {
                Log($"Reparent 异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string ClassName(IntPtr h)
        {
            try
            {
                var s = new System.Text.StringBuilder(256);
                NativeMethods.GetClassName(h, s, s.Capacity);
                return s.ToString();
            }
            catch { return "?"; }
        }

        // 定位桌面 SHELLDLL_DefView 窗口（桌面图标容器）。顺序：
        //   1) Progman 的直接子窗；
        //   2) 先给 Progman 发 0x052C 让 explorer 生成承载 DefView 的 WorkerW（异步，短重试），
        //      再枚举所有顶层窗找含 SHELLDLL_DefView 的（WorkerW 可见性不做硬要求，找到即用）。
        // 找不到才返回 IntPtr.Zero（保持独立顶层）。
        private IntPtr FindDefView()
        {
            var progman = NativeMethods.FindWindow("Progman", null);

            IntPtr direct = IntPtr.Zero;
            if (progman != IntPtr.Zero)
                direct = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (direct != IntPtr.Zero) return direct;

            if (!_sent052C && progman != IntPtr.Zero)
            {
                NativeMethods.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
                _sent052C = true;
            }

            for (int attempt = 0; attempt < 4; attempt++)
            {
                // 直接在 DefView 所在 WorkerW/其他宿主里找
                IntPtr found = IntPtr.Zero;
                IntPtr top = IntPtr.Zero;
                NativeMethods.EnumWindows((hwnd, _) =>
                {
                    // 只查 WorkerW / Progman 这两类桌面壳窗口内部
                    var cls = new System.Text.StringBuilder(256);
                    NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
                    if (cls.ToString() != "WorkerW") return true;
                    var def = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (def != IntPtr.Zero) { found = def; return false; }
                    return true;
                }, IntPtr.Zero);
                if (found != IntPtr.Zero) return found;

                if (progman != IntPtr.Zero)
                {
                    direct = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (direct != IntPtr.Zero) return direct;
                }
                System.Threading.Thread.Sleep(80);
            }
            return IntPtr.Zero;
        }

        private void Heartbeat_Tick(object? sender, EventArgs e)
        {
            if (_hwnd == IntPtr.Zero) return;
            try
            {
                // Explorer 重启/桌面刷新后 owner 失效 → 重新设 owner
                bool ownerGone = _reparented && (_desktopHost == IntPtr.Zero ||
                    !NativeMethods.IsWindow(_desktopHost) ||
                    NativeMethods.GetWindow(_hwnd, NativeMethods.GW_OWNER) != _desktopHost);
                if (ownerGone)
                {
                    Log("Heartbeat: 桌面 owner 失效，重新设 owner");
                    _reparented = false;
                    ReparentToDesktop();
                }

                // 壁纸变更轮询：仅自适应开启时，比对壁纸签名（路径|时间戳），变了就重算（兜底第三方换壁纸不触发系统事件）
                if (_fence.Style.UseWallpaperTint)
                {
                    var sig = WallpaperTint.GetWallpaperSignature();
                    if (sig != null && sig != _lastTintSig)
                    {
                        _lastTintSig = sig;
                        ApplyWallpaperTint();
                    }
                }
            }
            catch { }
        }

        // ---------- WndProc 钩子：原生缩放 / 原生移动 / 双击重命名 ----------
        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_NCHITTEST)
            {
                int x = (short)(lParam.ToInt32() & 0xFFFF);
                int y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
                Point p = PointFromScreen(new Point(x, y)); // 窗口逻辑坐标
                double m = 6, w = ActualWidth, h = ActualHeight;

                // 重命名编辑态：标题区交回 WPF（让 TextBox/按钮可点击），仅四边仍可缩放
                bool editing = TitleEdit.Visibility == Visibility.Visible;
                bool left = p.X <= m, right = p.X >= w - m, top = p.Y <= m, bottom = p.Y >= h - m;
                if (top && left) { handled = true; return new IntPtr(NativeMethods.HTTOPLEFT); }
                if (top && right) { handled = true; return new IntPtr(NativeMethods.HTTOPRIGHT); }
                if (bottom && left) { handled = true; return new IntPtr(NativeMethods.HTBOTTOMLEFT); }
                if (bottom && right) { handled = true; return new IntPtr(NativeMethods.HTBOTTOMRIGHT); }
                if (left) { handled = true; return new IntPtr(NativeMethods.HTLEFT); }
                if (right) { handled = true; return new IntPtr(NativeMethods.HTRIGHT); }
                if (top) { handled = true; return new IntPtr(NativeMethods.HTTOP); }
                if (bottom) { handled = true; return new IntPtr(NativeMethods.HTBOTTOM); }

                // 标题栏区域：左键返回 HTCAPTION 由系统原生拖动；
                // 右键被按下则返回 HTCLIENT（否则 HTCAPTION 会让右键被系统吞掉、只弹系统菜单，导致 ContextMenu 只在非 HTCAPTION 区能开）。
                // 编辑态也交回 WPF（避免抢 TextBox/按钮点击）。
                if (!editing && p.Y <= _titleBarHeight)
                {
                    // 右侧按钮条（… 菜单 / ▾ 折叠）区域交回 WPF（可点击）
                    Rect strip = BtnStrip.TransformToVisual(this)
                        .TransformBounds(new Rect(0, 0, BtnStrip.ActualWidth, BtnStrip.ActualHeight));
                    bool rbtn = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON) & 0x8000) != 0;
                    if (!strip.Contains(p) && !rbtn)
                    {
                        handled = true;
                        return new IntPtr(NativeMethods.HTCAPTION);
                    }
                }
                handled = true;
                return new IntPtr(NativeMethods.HTCLIENT);
            }
            if (msg == NativeMethods.WM_SETCURSOR)
            {
                // lParam 低 16 位 = 命中测试结果(HT*)，高 16 位 = 触发的鼠标消息
                int ht = (short)(lParam.ToInt32() & 0xFFFF);
                // 标题栏(HTCAPTION)悬停 → 显示移动光标(四箭头)。系统不自动给，需手动 SetCursor。
                // 缩放边角/按钮(HTCLIENT)则交回默认处理：边角显示缩放光标、按钮走 WPF 自身 Cursor。
                if (ht == NativeMethods.HTCAPTION)
                {
                    NativeMethods.SetCursor(NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_SIZEALL));
                    handled = true;
                    return new IntPtr(1);
                }
                return IntPtr.Zero; // 其余交系统/WPF（缩放光标、按钮 Hand、文本 I 型等）
            }
            if (msg == NativeMethods.WM_NCLBUTTONDBLCLK)
            {
                // wParam 为命中测试值；标题栏双击 → 重命名
                int ht = wParam.ToInt32();
                if (ht == NativeMethods.HTCAPTION)
                {
                    StartRename();
                    handled = true;
                    return IntPtr.Zero;
                }
            }
            return IntPtr.Zero;
        }

        // ---------- 圆角裁剪：每像素 alpha 下靠 Clip 保证圆角外透明，无需 SetWindowRgn ----------
        private void ApplyClip()
        {
            try
            {
                RootBorder.Clip = new RectangleGeometry(
                    new Rect(0, 0, RootBorder.ActualWidth, RootBorder.ActualHeight), 12, 12);
            }
            catch { }
        }

        // 分层窗下透明度完全由 BgColor 的 #AARRGGBB 经 WPF per-pixel alpha 合成，改色后绑定自动刷新，无需手动重绘
        internal void RefreshBackground() { }

        // ---------- 随桌面自适应（中性半透明玻璃背景） ----------
        // 不把壁纸区域平均色直接填回背景（同色 80% 叠自己≈不透明色板，观感不透），
        // 而是按区域明暗生成中性玻璃(RGB 与壁纸色相无关)+~60% alpha → 桌面透出、透明可辨。
        // 栏底同源略暗；条目/标题字色按明暗切黑/白。见 WallpaperTint.cs 顶部注释。
        private void Style_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FenceStyle.UseWallpaperTint))
            {
                if (_fence.Style.UseWallpaperTint) ApplyWallpaperTint();
                else RestoreManualBg();
            }
        }

        private void OnUserPrefChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // 壁纸变更会触发 Desktop 分类的用户偏好变更
            if (e.Category == UserPreferenceCategory.Desktop && _fence.Style.UseWallpaperTint && !_closed)
                Dispatcher.BeginInvoke(() => ApplyWallpaperTint());
        }

        private void ApplyWallpaperTint()
        {
            if (!_fence.Style.UseWallpaperTint) return;
            try
            {
                if (!_tintApplied) // 记住用户手动色（背景 + 栏底 + 条目字 + 标题字）
                {
                    _manualBg = _fence.Style.BgColor;
                    _manualTitleBar = _fence.Style.TitleBarColor;
                    _manualItemColor = _fence.Style.ItemColor;
                    _manualTitleColor = _fence.Style.TitleColor;
                    _tintApplied = true;
                }
                // 围栏屏幕矩形（Left/Top 即屏幕坐标，主屏原点 0,0）。围栏 reparent 到桌面后可能为父相对负坐标，
                // 用当前屏幕位置即可；若在屏外 WallpaperTint.Compute 会自动退回整图平均。
                var rect = new Rect(Left, Top, ActualWidth, ActualHeight);
                var bg = WallpaperTint.Compute(rect);
                var bar = WallpaperTint.ComputeBar(rect);
                if (bg != null)
                {
                    FadeBackground(_bgBinding, RootBorder, BackgroundProperty, bg);
                    if (_fence.Style.BgColor != bg) _fence.Style.BgColor = bg; // 触发绑定（淡入结束复原后反映此值）
                }
                if (bar != null)
                {
                    FadeBackground(_barBinding, TitleBar, BackgroundProperty, bar);
                    if (_fence.Style.TitleBarColor != bar) _fence.Style.TitleBarColor = bar;
                }
                // 字体颜色按【玻璃灰度】判黑/白（而非原始壁纸亮度）：字体实际叠在玻璃上，
                // 栏底玻璃=v*0.82 比背景更暗，故标题字单独用栏底玻璃判定，深栏底必出白字，
                // 根除"整块偏亮判黑字、压暗栏底后看不清"的问题（缺陷1）。
                var ink = WallpaperTint.ComputeInk(rect);
                if (ink.HasValue)
                {
                    if (_fence.Style.ItemColor != ink.Value.BodyInk) _fence.Style.ItemColor = ink.Value.BodyInk;
                    if (_fence.Style.TitleColor != ink.Value.BarInk) _fence.Style.TitleColor = ink.Value.BarInk;
                }
                _lastTintSig = WallpaperTint.GetWallpaperSignature(); // 记录当前壁纸签名，供心跳轮询比对
                Save();
            }
            catch { }
        }

        // 变色淡入：临时解绑目标(dp) 的原绑定、以旧色起手的 SolidColorBrush 做 ~220ms ColorAnimation 到 newHex，
        // 动画结束复原原始绑定（继续跟随 Style.*）。newHex 无效或无可动画笔刷时直接复原绑定。
        private void FadeBackground(Binding? origBinding, DependencyObject target, DependencyProperty dp, string? newHex)
        {
            if (newHex == null || origBinding == null) return;
            Color newColor;
            try { newColor = (Color)ColorConverter.ConvertFromString(newHex); }
            catch { BindingOperations.SetBinding(target, dp, origBinding); return; }
            var oldBrush = target.GetValue(dp) as SolidColorBrush;
            if (oldBrush == null) { BindingOperations.SetBinding(target, dp, origBinding); return; }

            var animBrush = new SolidColorBrush(oldBrush.Color);
            BindingOperations.ClearBinding(target, dp);
            target.SetValue(dp, animBrush);
            var anim = new ColorAnimation(newColor, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop };
            anim.Completed += (_, _) => BindingOperations.SetBinding(target, dp, origBinding);
            animBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        private void RestoreManualBg()
        {
            if (_tintApplied)
            {
                _tintApplied = false;
                if (_manualBg != null && _fence.Style.BgColor != _manualBg)
                    _fence.Style.BgColor = _manualBg;
                if (_manualTitleBar != null && _fence.Style.TitleBarColor != _manualTitleBar)
                    _fence.Style.TitleBarColor = _manualTitleBar;
                if (_manualItemColor != null && _fence.Style.ItemColor != _manualItemColor)
                    _fence.Style.ItemColor = _manualItemColor;
                if (_manualTitleColor != null && _fence.Style.TitleColor != _manualTitleColor)
                    _fence.Style.TitleColor = _manualTitleColor;
                Save();
            }
        }

        // ---------- 溢出模式 ----------
        private void ApplyOverflowMode()
        {
            if (_fence.Overflow == OverflowMode.Ellipsis)
            {
                Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                _display ??= new ObservableCollection<FenceItem>();
                ItemsHost.ItemsSource = _display;
                RebuildDisplay();
            }
            else
            {
                Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                ItemsHost.ItemsSource = _fence.Items;
            }
        }

        private void RebuildDisplay()
        {
            if (_display == null) return;
            _display.Clear();
            int cap = ComputeCapacity();
            if (!_ellipsisExpanded && _fence.Items.Count > cap && cap > 1)
            {
                for (int i = 0; i < cap - 1; i++) _display.Add(_fence.Items[i]);
                int remaining = _fence.Items.Count - (cap - 1);
                _display.Add(new FenceItem { IsEllipsis = true, EllipsisCount = remaining, DisplayName = $"还有 {remaining} 项" });
            }
            else
            {
                foreach (var it in _fence.Items) _display.Add(it);
            }
        }

        private int ComputeCapacity()
        {
            int cols = (int)Math.Max(1, Math.Floor((_fence.Width - 12) / (76 + 8)));
            int rows = (int)Math.Max(1, Math.Floor((_fence.Height - 30 - 12) / (40 + 11 + 16)));
            return cols * rows;
        }

        // ---------- 条目增删/排序 ----------
        private void AddPaths(string[] paths)
        {
            foreach (var p in paths)
                _fence.Items.Add(new FenceItem { Path = p, DisplayName = Path.GetFileName(p) ?? p });
            Reindex();
            Save();
            if (_fence.Overflow == OverflowMode.Ellipsis) RebuildDisplay();
        }

        private void MoveItem(FenceItem dragged, FenceItem? target)
        {
            if (dragged == target || dragged.IsEllipsis) return;
            int oldIndex = _fence.Items.IndexOf(dragged);
            if (oldIndex < 0) return;
            int newIndex = target == null ? _fence.Items.Count - 1 : _fence.Items.IndexOf(target);
            if (newIndex < 0) newIndex = _fence.Items.Count - 1;
            _fence.Items.Move(oldIndex, newIndex);
            Reindex();
            Save();
            if (_fence.Overflow == OverflowMode.Ellipsis) RebuildDisplay();
        }

        private void Reindex() => _fence.Items.Select((it, i) => { it.Order = i; return it; }).ToList();

        // ---------- 拖拽 ----------
        private void Item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (((FrameworkElement)sender).DataContext is FenceItem item)
            {
                if (e.ClickCount == 2) { OpenItem(item); return; }
                _dragItem = item;
                _dragStart = e.GetPosition(this);
                ((FrameworkElement)sender).CaptureMouse();
            }
        }

        private void Item_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
            if ((e.GetPosition(this) - _dragStart).Length < 4) return;
            var item = _dragItem;
            _dragItem = null;
            ((FrameworkElement)sender).ReleaseMouseCapture();
            DragDrop.DoDragDrop(this, new DataObject("fenceItem", item), DragDropEffects.Move);
        }

        private void Item_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragItem != null)
            {
                _dragItem = null;
                ((FrameworkElement)sender).ReleaseMouseCapture();
            }
        }

        private void Item_Drop(object sender, DragEventArgs e) => HandleDrop(sender, e);
        private void ItemsHost_Drop(object sender, DragEventArgs e) => HandleDrop(sender, e);

        private void Item_DragEnter(object sender, DragEventArgs e) => SetDropEffect(e);
        private void Item_DragOver(object sender, DragEventArgs e) => SetDropEffect(e);
        private void Item_DragLeave(object sender, DragEventArgs e) { }
        private void ItemsHost_DragEnter(object sender, DragEventArgs e) => SetDropEffect(e);
        private void ItemsHost_DragOver(object sender, DragEventArgs e) => SetDropEffect(e);
        private void ItemsHost_DragLeave(object sender, DragEventArgs e) { }

        private void SetDropEffect(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            else if (e.Data.GetDataPresent("fenceItem"))
                e.Effects = DragDropEffects.Move;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void HandleDrop(object sender, DragEventArgs e)
        {
            var target = ((FrameworkElement)sender).DataContext as FenceItem;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                AddPaths((string[])e.Data.GetData(DataFormats.FileDrop)!);
                e.Handled = true;
            }
            else if (e.Data.GetDataPresent("fenceItem"))
            {
                MoveItem((FenceItem)e.Data.GetData("fenceItem")!, target);
                e.Handled = true;
            }
        }

        private void ItemsHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }

        private void OpenItem(FenceItem item)
        {
            if (item.IsEllipsis) { _ellipsisExpanded = true; RebuildDisplay(); return; }
            try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); } catch { }
        }

        // ---------- 条目右键菜单 ----------
        private FenceItem? MenuFenceItem(object sender)
            => ((FrameworkElement)sender).DataContext as FenceItem;

        private void ItemOpen_Click(object sender, RoutedEventArgs e)
        {
            var it = MenuFenceItem(sender);
            if (it == null) return;
            OpenItem(it);
        }

        private void ItemOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            var it = MenuFenceItem(sender);
            if (it == null || string.IsNullOrEmpty(it.Path)) return;
            try
            {
                string p = it.Path;
                if (Directory.Exists(p))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true });
                else if (File.Exists(p))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
                else
                {
                    var dir = Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(dir))
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                }
            }
            catch { }
        }

        private void ItemRemove_Click(object sender, RoutedEventArgs e)
        {
            var it = MenuFenceItem(sender);
            if (it == null) return;
            _fence.Items.Remove(it);
            Reindex();
            Save();
            if (_fence.Overflow == OverflowMode.Ellipsis) RebuildDisplay();
        }

        // ---------- 折叠 ----------
        private void BtnCollapse_Click(object sender, RoutedEventArgs e)
        {
            if (!_fence.Collapsed) { _normalHeight = _fence.Height; _fence.Collapsed = true; Height = 40; }
            else { _fence.Collapsed = false; Height = _normalHeight; }
            Save();
        }

        // ---------- “…” 菜单按钮：左键打开围栏菜单 ----------
        private void BtnMenu_Click(object sender, RoutedEventArgs e)
        {
            var cm = TitleBar.ContextMenu;
            if (cm == null) return;
            cm.PlacementTarget = BtnMenu;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        // ---------- 重命名（标题） ----------
        private void StartRename()
        {
            TitleEdit.Text = _fence.Title;
            TitleText.Visibility = Visibility.Collapsed;
            TitleEdit.Visibility = Visibility.Visible;
            TitleEdit.Focus();
            TitleEdit.SelectAll();
        }
        private void MenuRename_Click(object sender, RoutedEventArgs e) => StartRename();
        private void TitleEdit_LostFocus(object sender, RoutedEventArgs e) => CommitRename();
        private void TitleEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) CommitRename();
            else if (e.Key == Key.Escape) { TitleEdit.Visibility = Visibility.Collapsed; TitleText.Visibility = Visibility.Visible; }
        }
        private void CommitRename()
        {
            if (!string.IsNullOrWhiteSpace(TitleEdit.Text)) _fence.Title = TitleEdit.Text;
            TitleEdit.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            Save();
        }

        // ---------- 右键菜单（围栏） ----------
        // 「预设主题」子菜单：自适应/浅色/深色 立即应用；自定义→进入配置窗手动调色。
        // 配置窗里的预设选择会回调本类的 ApplyPresetTheme；手动改色会回调 ExitAdaptive（让自定义粘住）。
        private void MenuPreset_Click(object sender, RoutedEventArgs e)
        {
            if (((MenuItem)sender).Tag is string tag)
                ApplyPresetTheme(tag);
            UpdatePresetChecks();
        }
        private void MenuConfig_Click(object sender, RoutedEventArgs e)
            => new FenceSettingsWindow(_fence, this, "custom").ShowDialog();

        // 按当前主题给「预设主题」子菜单四项打勾（自适应/浅色/深色/自定义，互斥）。
        private void UpdatePresetChecks()
        {
            string th = ResolveTheme(_fence.Style);
            PresetAdaptive.IsChecked = th == "Adaptive";
            PresetLight.IsChecked = th == "Light";
            PresetDark.IsChecked = th == "Dark";
            PresetCustom.IsChecked = th == "Custom";
        }

        // 预设主题：浅色/深色=固定预设；自适应=跟随桌面壁纸明暗(UseWallpaperTint)；自定义=把当前样式固定下来、退出自适应。
        // 关键顺序：先清 _tintApplied/_manual*(使关自适应时 RestoreManualBg 直接 return、不还原旧色)，
        // 再关 UseWallpaperTint(触发 Style_PropertyChanged→RestoreManualBg，此时已 no-op)，最后按选择处理：
        //   Adaptive → 重新置 true 触发 Style_PropertyChanged→ApplyWallpaperTint 立刻套用；
        //   Light/Dark → 覆盖预设色（带淡入）；Custom → 不动颜色(保留用户当前样式)。
        // 同时供配置窗预设区回调使用。
        internal void ApplyPresetTheme(string tag)
        {
            _tintApplied = false;
            _manualBg = _manualTitleBar = _manualItemColor = _manualTitleColor = null;
            if (tag == "Adaptive") { _fence.Style.UseWallpaperTint = true; ApplyWallpaperTint(); } // 显式重算：即便已为 true（值不变不触发 PropertyChanged）也按当前壁纸/位置刷新
            else if (tag == "Light") { _fence.Style.UseWallpaperTint = false; ApplyThemeColors(LightPreset); }
            else if (tag == "Dark") { _fence.Style.UseWallpaperTint = false; ApplyThemeColors(DarkPreset); }
            // "Custom"：保持现状（自适应已关、颜色未动）
            Save();
        }

        // 手动改自定义配色时调用：退出自适应，使手填色不被后续壁纸重算覆盖（自定义=手动样式）。
        internal void ExitAdaptive()
        {
            if (!_fence.Style.UseWallpaperTint) return;
            _tintApplied = false;
            _manualBg = _manualTitleBar = _manualItemColor = _manualTitleColor = null;
            _fence.Style.UseWallpaperTint = false; // 触发 Style_PropertyChanged→RestoreManualBg(no-op，快照已清)
        }

        private void ApplyThemeColors((string Bg, string Bar, string Ink) p)
        {
            var s = _fence.Style;
            s.ItemColor = p.Ink;
            s.TitleColor = p.Ink;
            FadeBackground(_bgBinding, RootBorder, BackgroundProperty, p.Bg);
            FadeBackground(_barBinding, TitleBar, BackgroundProperty, p.Bar);
            if (s.BgColor != p.Bg) s.BgColor = p.Bg;
            if (s.TitleBarColor != p.Bar) s.TitleBarColor = p.Bar;
        }

        // 推导主题（静态版，供配置窗预设区初值）：UseWallpaperTint 开着→Adaptive；否则颜色==某套预设→Light/Dark；都不等→Custom。
        internal static string ResolveTheme(FenceStyle s)
        {
            if (s.UseWallpaperTint) return "Adaptive";
            bool eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            if (eq(s.BgColor, DarkPreset.Bg) && eq(s.TitleBarColor, DarkPreset.Bar)) return "Dark";
            if (eq(s.BgColor, LightPreset.Bg) && eq(s.TitleBarColor, LightPreset.Bar)) return "Light";
            return "Custom";
        }


        private void MenuOverflow_Click(object sender, RoutedEventArgs e)
        {
            if (((MenuItem)sender).Tag is string tag && Enum.TryParse<OverflowMode>(tag, out var m))
            { _fence.Overflow = m; Save(); }
        }
        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            App.Config.Fences.Remove(_fence);
            App.Save();
            Close();
        }
        private void MenuNewFence_Click(object sender, RoutedEventArgs e) => App.NewFence();

        // ---------- 重命名退出编辑模式：点击围栏内非编辑控件即提交 ----------
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TitleEdit.Visibility == Visibility.Visible &&
                !(e.OriginalSource is TextBox tb && tb == TitleEdit))
                CommitRename();
        }

        // 围栏菜单打开时：提交未完成的重命名，并按当前设置给"显示模式/溢出模式"打勾。
        // 注意必须用 ContextMenu 的 Opened 事件（不是 ContextMenuOpening——后者只在该元素作为宿主时触发，
        // 挂在 ContextMenu 自身元素上从不触发，导致勾选不更新/永不勾选）。
        private void TitleCtx_Opened(object? sender, RoutedEventArgs e)
        {
            if (TitleEdit.Visibility == Visibility.Visible) CommitRename();
            LayoutIcon.IsChecked = _fence.Style.ItemLayout == "Icon";
            LayoutList.IsChecked = _fence.Style.ItemLayout == "List";
            OverflowScroll.IsChecked = _fence.Overflow == OverflowMode.Scroll;
            OverflowEllipsis.IsChecked = _fence.Overflow == OverflowMode.Ellipsis;
            UpdatePresetChecks();
        }

        // ---------- 显示模式（图标/列表） ----------
        private void MenuLayout_Click(object sender, RoutedEventArgs e)
        {
            if (((MenuItem)sender).Tag is string tag)
            { _fence.Style.ItemLayout = tag; Save(); }
        }

        private double _titleBarHeight = 26;

        // 托盘"隐藏/显示所有围栏"：用 Win32 ShowWindow 直接显隐，避免 WPF Hide/Show 的副作用。
        // owner 模式下窗口仍是顶层、坐标系统不变，恢复显示无需重新定位（无需 SetWindowPos）。
        public void SetDesktopVisible(bool visible)
        {
            if (_hwnd == IntPtr.Zero)
            {
                if (visible) Show();
                return;
            }
            NativeMethods.ShowWindow(_hwnd, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        }

        public new void Close()
        {
            _heartbeat.Stop();
            _closed = true;
            _fence.Style.PropertyChanged -= Style_PropertyChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPrefChanged;
            App.UnregisterWindow(this);
            base.Close();
        }

        private void Save() => App.Save();
    }
}
