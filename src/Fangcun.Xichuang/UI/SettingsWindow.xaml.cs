using System.Windows;
using System.Windows.Controls;
using Fangcun.Xichuang.Native;

namespace Fangcun.Xichuang;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        var c = XichuangConfig.Current;
        TriggerCombo.SelectedIndex = c.Trigger == TriggerMode.Hover ? 0 : 1;
        DelayBox.Text = c.HoverDelayMs.ToString();
        WidthBox.Text = c.SidebarWidth.ToString();
        MinModeBox.IsChecked = c.MinMode;
        AutoHideBox.IsChecked = c.AutoHide;
        AutoRestoreBox.IsChecked = c.AutoRestore;

        // 停靠边
        EdgeCombo.SelectedIndex = c.Edge switch
        {
            DockEdge.Left => 0, DockEdge.Right => 1, DockEdge.Top => 2, _ => 3
        };

        // 显示器：按枚举数量生成（index 0 = 主屏，始终第一）
        int count = Win32.EnumMonitors().Count;
        if (count == 0) count = 1;
        for (int i = 0; i < count; i++)
            MonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = i == 0 ? "显示器 1（主屏）" : $"显示器 {i + 1}",
                Tag = i
            });
        MonitorCombo.SelectedIndex = System.Math.Max(0, System.Math.Min(c.SidebarMonitor, count - 1));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var c = XichuangConfig.Current;
        c.Trigger = TriggerCombo.SelectedIndex == 0 ? TriggerMode.Hover : TriggerMode.Click;
        if (int.TryParse(DelayBox.Text, out int d) && d >= 0) c.HoverDelayMs = d;
        if (int.TryParse(WidthBox.Text, out int w) && w is >= 120 and <= 560) c.SidebarWidth = w;

        if (EdgeCombo.SelectedItem is ComboBoxItem ei && ei.Tag is string et)
            c.Edge = System.Enum.Parse<DockEdge>(et);
        if (MonitorCombo.SelectedItem is ComboBoxItem mi && mi.Tag is int idx) c.SidebarMonitor = idx;

        c.MinMode = MinModeBox.IsChecked == true;
        c.AutoHide = AutoHideBox.IsChecked == true;
        c.AutoRestore = AutoRestoreBox.IsChecked == true;
        c.Save();

        // 停靠边/厚度/显示器变更：立即重新定位侧栏并重新排布活动实例窗口
        Launcher.Relayout();

        MessageBox.Show("已保存。停靠边/显示器已即时生效；其它项下次打开侧栏生效。", "栖窗");
        Close();
    }
}
