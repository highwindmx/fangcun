using System;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace Fangcun
{
    // 监视模式配置弹窗：输入被监视的文件夹地址与后缀筛选。
    // 确定后把值回写到 Fence（WatchPath / WatchFilter），由调用方决定何时启动监视。
    public partial class MonitorConfigWindow : Window
    {
        private readonly Fence _fence;

        public MonitorConfigWindow(Fence fence)
        {
            InitializeComponent();
            _fence = fence;
            TxtPath.Text = fence.WatchPath;
            TxtFilter.Text = fence.WatchFilter;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择要监视的文件夹",
                UseDescriptionForTitle = true,
                SelectedPath = TxtPath.Text,
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                TxtPath.Text = dlg.SelectedPath;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var path = TxtPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                System.Windows.MessageBox.Show("请输入有效的文件夹地址。", "监视设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _fence.WatchPath = path;
            _fence.WatchFilter = TxtFilter.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
