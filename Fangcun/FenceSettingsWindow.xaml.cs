using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms; // ColorDialog (WinForms)
using Color = System.Windows.Media.Color;

namespace Fangcun
{
    public partial class FenceSettingsWindow : Window
    {
        private readonly Fence _fence;

        public FenceSettingsWindow(Fence fence)
        {
            _fence = fence;
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
        }

        public FenceStyle Cfg => _fence.Style;
        public List<string> Fonts { get; }

        private void PickTitleColor_Click(object sender, RoutedEventArgs e)
        {
            var c = PickColor(_fence.Style.TitleColor);
            if (c != null) { _fence.Style.TitleColor = c; App.Save(); }
        }

        private void PickTitleBarColor_Click(object sender, RoutedEventArgs e)
        {
            var c = PickColor(_fence.Style.TitleBarColor);
            if (c != null) { _fence.Style.TitleBarColor = c; App.Save(); }
        }

        private void PickItemColor_Click(object sender, RoutedEventArgs e)
        {
            var c = PickColor(_fence.Style.ItemColor);
            if (c != null) { _fence.Style.ItemColor = c; App.Save(); }
        }

        private void PickBgColor_Click(object sender, RoutedEventArgs e)
        {
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
