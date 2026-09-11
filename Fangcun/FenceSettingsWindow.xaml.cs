using System.Collections.Generic;
using System.Windows;
using System.Windows.Forms; // ColorDialog (WinForms)
using Color = System.Windows.Media.Color;

namespace Fangcun
{
    public partial class FenceSettingsWindow : Window
    {
        private readonly Fence _fence;
        private readonly FenceWindow? _owner;   // 围栏窗口，用于回调预设应用 / 退出自适应
        private readonly string? _focus;        // "custom"=进入配置窗后聚焦自定义配色区
        private bool _syncUiBusy;               // 闸门：Loaded 赋初值 / 配置窗关闭回写时不触发 SelectionChanged 误处理

        public FenceSettingsWindow(Fence fence, FenceWindow? owner = null, string? focus = null)
        {
            _fence = fence;
            _owner = owner;
            _focus = focus;
            DataContext = this;
            Fonts = new List<string>
            {
                "Microsoft YaHei", "Microsoft YaHei UI", "Segoe UI", "SimSun",
                "SimHei", "NSimSun", "KaiTi", "Arial", "Tahoma"
            };
            InitializeComponent();
            _fence.Style.PropertyChanged += (_, _) => App.Save();
            // FenceWindow 现为 Border 宿主（非 Window），无法作为 Owner 传入；
            // 设置项的改动经由双向绑定即时反映到 HwndSource 视觉树并自动重绘，这里仅确保持久化。
            Closed += (_, _) => App.Save();
            Loaded += (_, _) =>
            {
                // 「自定义」入口：把配色区滚入视野（该窗不再含预设区，预设在右键「预设主题」子菜单）。
                if (_focus == "custom") GroupCustom.BringIntoView();
                // 初始选中：用 _syncUiBusy 闸门避免 Loaded 赋初值触发 SelectionChanged（否则进入设置窗会误弹配置窗）
                _syncUiBusy = true;
                // 层叠模式下拉框初值：按当前围栏模式选中（枚举顺序与下拉项顺序一致：Bottom/Top/Window）
                LayerCombo.SelectedIndex = (int)_fence.LayerMode;
                // 同步模式下拉框初值（Manual=0 / Watch=1）
                SyncCombo.SelectedIndex = (int)_fence.SyncMode;
                _syncUiBusy = false;
            };
        }

        // 同步模式切换：手动直接生效；监视则弹出配置窗输入地址+后缀，确认后启动监视，取消则回退原模式。
        private void SyncCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_syncUiBusy) return;
            if (SyncCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem ci || ci.Tag is not string tag) return;
            if (!System.Enum.TryParse<SyncMode>(tag, out var m)) return;
            if (m == SyncMode.Manual)
            {
                if (_fence.SyncMode == SyncMode.Manual) return;
                _fence.SyncMode = SyncMode.Manual;
                _owner?.ApplySyncMode();   // 关闭监视，保留现有条目转为手动管理
                App.Save();
            }
            else
            {
                OpenWatchConfig();
            }
        }

        private void SyncConfig_Click(object sender, RoutedEventArgs e) => OpenWatchConfig();

        private void OpenWatchConfig()
        {
            _syncUiBusy = true;
            try
            {
                var dlg = new MonitorConfigWindow(_fence) { Owner = this };
                if (dlg.ShowDialog() == true)
                {
                    _fence.SyncMode = SyncMode.Watch;
                    _owner?.ApplySyncMode();
                    App.Save();
                }
                // 把下拉框对齐实际模式：确认→监视；取消→维持原模式（多为手动）
                SyncCombo.SelectedIndex = (int)_fence.SyncMode;
            }
            finally { _syncUiBusy = false; }
        }

        // 层叠模式切换：写回模型 → 通知围栏窗口即时应用（owner / Topmost / 不激活 / z 序）→ 持久化。
        private void LayerCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (LayerCombo.SelectedIndex < 0) return;
            var mode = (LayerMode)LayerCombo.SelectedIndex;
            if (_fence.LayerMode != mode)
            {
                _fence.LayerMode = mode;
                _owner?.ApplyLayerMode();
                App.Save();
            }
        }

        public FenceStyle Cfg => _fence.Style;
        public List<string> Fonts { get; }

        // 手动改自定义配色（文本失去焦点）：退出自适应，使手填色不被后续壁纸重算覆盖。
        // 右键「预设主题」子菜单会据 ResolveTheme 把「自定义」项打勾，反映当前为自定义状态。
        private void CustomColor_LostFocus(object sender, RoutedEventArgs e)
            => _owner?.ExitAdaptive();

        private void PickTitleColor_Click(object sender, RoutedEventArgs e)
        {
            _owner?.ExitAdaptive(); // 手动选色即视为自定义，退出自适应
            var c = PickColor(_fence.Style.TitleColor);
            if (c != null) { _fence.Style.TitleColor = c; App.Save(); }
        }

        private void PickTitleBarColor_Click(object sender, RoutedEventArgs e)
        {
            _owner?.ExitAdaptive();
            var c = PickColor(_fence.Style.TitleBarColor);
            if (c != null) { _fence.Style.TitleBarColor = c; App.Save(); }
        }

        private void PickItemColor_Click(object sender, RoutedEventArgs e)
        {
            _owner?.ExitAdaptive();
            var c = PickColor(_fence.Style.ItemColor);
            if (c != null) { _fence.Style.ItemColor = c; App.Save(); }
        }

        private void PickBgColor_Click(object sender, RoutedEventArgs e)
        {
            _owner?.ExitAdaptive();
            // BgColor 形如 #AARRGGBB：取 RGB 进拾色器，写回时保留原 alpha
            string cur = _fence.Style.BgColor;
            string rgb = cur.Length >= 9 ? "#" + cur.Substring(3) : (cur.Length == 7 ? cur : "#000000");
            using var dlg = new ColorDialog();
            try { dlg.Color = System.Drawing.ColorTranslator.FromHtml(rgb); } catch { }
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var col = dlg.Color;
                string alpha = cur.Length >= 9 ? cur.Substring(1, 2) : "26";
                _fence.Style.BgColor = "#" + alpha + col.R.ToString("X2") + col.G.ToString("X2") + col.B.ToString("X2");
                App.Save();
            }
        }

        private static string? PickColor(string current)
        {
            using var dlg = new ColorDialog();
            try { dlg.Color = System.Drawing.ColorTranslator.FromHtml(current); } catch { }
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var col = dlg.Color;
                return "#" + col.R.ToString("X2") + col.G.ToString("X2") + col.B.ToString("X2");
            }
            return null;
        }

        private void Done_Click(object sender, RoutedEventArgs e)
        {
            App.Save();
            Close();
        }
    }
}
