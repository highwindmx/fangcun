using System.Windows;

namespace Fangcun
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void Enumerate_Click(object sender, RoutedEventArgs e)
        {
            var icons = DesktopIconEnumerator.Enumerate();
            IconList.ItemsSource = icons;
            Title = $"方寸 - 原型（共 {icons.Count} 个图标）";
        }
    }
}
