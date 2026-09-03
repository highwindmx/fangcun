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
            };
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
