using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Fangcun
{
    public partial class App : System.Windows.Application
    {
        public static AppConfig Config { get; private set; } = new();

        private NotifyIcon? _tray;
        private static Mutex? _mutex;
        private static bool _ownsMutex;

        // 所有存活围栏窗口实例（用于托盘"隐藏/显示所有围栏"）
        private static readonly List<FenceWindow> _windows = new();
        private static bool _allHidden;
        public static void RegisterWindow(FenceWindow w) { lock (_windows) if (!_windows.Contains(w)) _windows.Add(w); }
        public static void UnregisterWindow(FenceWindow w) { lock (_windows) _windows.Remove(w); }
        private static FenceWindow[] SnapshotWindows() { lock (_windows) return _windows.ToArray(); }

        // 托盘"隐藏/显示所有围栏"：点击一次全部隐藏，再点全部显示；文本随数量/状态刷新
        private static void UpdateToggleAllItem(ToolStripMenuItem item)
        {
            int n = SnapshotWindows().Length;
            item.Enabled = n > 0;
            item.Text = _allHidden ? $"显示所有围栏（{n}）" : $"隐藏所有围栏（{n}）";
            item.Checked = _allHidden;
        }

        private static void ToggleAllFences()
        {
            var wins = SnapshotWindows();
            if (wins.Length == 0) return;
            _allHidden = !_allHidden;
            foreach (var w in wins)
            {
                try { w.SetDesktopVisible(!_allHidden); } catch { }
            }
            Log(_allHidden ? "已隐藏所有围栏" : "已显示所有围栏");
        }

        public App()
        {
            // 确保 WinForms(NotifyIcon 托盘) 的高 DPI 与视觉样式已初始化。
            // 不初始化时，publish 单文件下托盘的内部窗口可能静默创建失败 → 托盘“消失”。
            try
            {
                System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
                System.Windows.Forms.Application.EnableVisualStyles();
            }
            catch { }

            // 任何未处理异常都弹窗提示，避免“双击没反应”却无任何线索
            DispatcherUnhandledException += (_, ex) => ShowError(ex.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
                ShowError(ex.ExceptionObject as Exception);
            System.Windows.Forms.Application.ThreadException += (_, ex) => ShowError(ex.Exception);
        }

        private static void ShowError(Exception? ex)
        {
            try
            {
                System.Windows.MessageBox.Show(ex?.ToString() ?? "未知错误（无异常对象）",
                    "方寸 运行错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // 方寸主界面改为 HwndSource 宿主（不再是 WPF Window），Application.Windows 为空，
            // 必须用显式退出，否则 OnLastWindowClose 会在启动后立即关闭进程。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 单实例守卫：反复双击 exe 会启动多个进程，每个进程都加载并叠加显示全部围栏，
            // 表现为“不停产生新围栏”。已有实例在运行时，新进程直接退出，不再重复创建窗口/污染 config。
            _mutex = new Mutex(true, "FangcunApp_SingleInstance", out bool createdNew);
            _ownsMutex = createdNew;
            if (!createdNew)
            {
                Shutdown();
                return;
            }
            try
            {
                base.OnStartup(e);
                Config = Persistence.Load();
                if (Config.Fences.Count == 0)
                    Config.Fences.Add(new Fence { Title = "我的围栏", X = 200, Y = 200 });

                int i = 0;
                foreach (var fence in Config.Fences)
                {
                    EnsureOnScreen(fence, i++);
                    new FenceWindow(fence).Show();
                }

                SetupTray();
            }
            catch (Exception ex)
            {
                ShowError(ex);
                Shutdown();
            }
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

        private void SetupTray()
        {
            try
            {
                Icon icon;
                try { icon = MakeTrayIcon(); }
                catch (Exception ex)
                {
                    Log($"MakeTrayIcon 失败({ex.GetType().Name}: {ex.Message})，改用系统图标兜底");
                    icon = new Icon(SystemIcons.Application, 16, 16);
                }
                _tray = new NotifyIcon
                {
                    Icon = icon,
                    Text = "方寸",
                    Visible = true
                };
                var menu = new ContextMenuStrip();
                var autoStart = new ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = IsAutoStart() };
                autoStart.CheckedChanged += (_, _) => SetAutoStart(autoStart.Checked);
                menu.Items.Add(autoStart);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("新建围栏", null, (_, _) => NewFence());

                // 隐藏/显示所有围栏（文本随围栏数量与当前状态刷新，见 menu.Opening）
                var toggleAll = new ToolStripMenuItem("隐藏所有围栏");
                menu.Opening += (_, _) => UpdateToggleAllItem(toggleAll);
                toggleAll.Click += (_, _) => ToggleAllFences();
                menu.Items.Add(toggleAll);

                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出方寸", null, (_, _) => Shutdown());
                _tray.ContextMenuStrip = menu;
                _tray.DoubleClick += (_, _) => NewFence();
                Log("托盘已创建并可见");
            }
            catch (Exception ex)
            {
                // 托盘失败不让整个程序退出（围栏已显示），仅记录，便于下次诊断
                Log($"SetupTray 失败: {ex}");
            }
        }

        // 开机自启：写入 HKCU\...\Run（无需管理员），值为本进程 exe 路径
        private static bool IsAutoStart()
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return k?.GetValue("Fangcun") != null; }
            catch { return false; }
        }

        private static void SetAutoStart(bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)!;
                if (on)
                {
                    var exe = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exe)) k.SetValue("Fangcun", $"\"{exe}\"");
                }
                else k.DeleteValue("Fangcun", false);
            }
            catch { }
        }

        // 灰色圆角方形 + 白色“寸”字
        private static Icon MakeTrayIcon()
        {
            const int size = 64;
            using var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(Color.FromArgb(0x66, 0x66, 0x66));
            int pad = 6, r = 14;
            var rect = new Rectangle(pad, pad, size - pad * 2, size - pad * 2);
            using var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, r, r, 180, 90);
            path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
            path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);

            using var font = new Font("Microsoft YaHei", 34, System.Drawing.FontStyle.Bold);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("寸", font, Brushes.White, new RectangleF(0, 0, size, size), sf);

            return Icon.FromHandle(bmp.GetHicon());
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            if (_ownsMutex) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }

        public static void Save() => Persistence.Save(Config);

        // 防止历史版本把围栏坐标/尺寸写成离屏或异常值（如 reparent 失败期间写回的 -2106 / 106x63），
        // 导致窗口跑到屏幕外看不见。围栏 reparent 到【主屏】WorkerW，只能落在主屏工作区内，
        // 因此用【主屏工作区】判定，而非虚拟屏幕（虚拟屏幕在多显示器下含负坐标，会把离屏/污染坐标误判为“在屏内”）。
        private static void EnsureOnScreen(Fence fence, int index)
        {
            var wa = SystemParameters.WorkArea; // 主显示器工作区（任务栏外）
            bool off = fence.X + fence.Width <= wa.Left || fence.X >= wa.Right ||
                       fence.Y + fence.Height <= wa.Top || fence.Y >= wa.Bottom;
            if (off)
            {
                // 完全离屏（含负坐标污染）→ 归位到主屏左上，依次错开
                fence.X = wa.Left + 40 + index * 28;
                fence.Y = wa.Top + 40 + index * 28;
            }
            else
            {
                // 半出屏 → 夹取回工作区内
                if (fence.X < wa.Left) fence.X = wa.Left + 8;
                if (fence.Y < wa.Top) fence.Y = wa.Top + 8;
                if (fence.X + fence.Width > wa.Right) fence.X = wa.Right - fence.Width - 8;
                if (fence.Y + fence.Height > wa.Bottom) fence.Y = wa.Bottom - fence.Height - 8;
            }
            if (fence.Width < 120) fence.Width = 240;
            if (fence.Height < 80) fence.Height = 320;
        }

        public static void NewFence()
        {
            var fence = new Fence { Title = "新围栏", X = 320, Y = 320 };
            Config.Fences.Add(fence);
            new FenceWindow(fence).Show();
            Save();
        }
    }
}
