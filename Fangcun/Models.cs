using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Fangcun
{
    // 溢出模式：滚动（默认） / 省略号（超出显示"还有 N 项"更多按钮）
    public enum OverflowMode { Scroll, Ellipsis }

    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
    }

    public class FenceItem : ViewModelBase
    {
        public string Path { get; set; } = "";
        [JsonIgnore] public ImageSource? IconSource => IconService.GetIcon(Path);
        public string DisplayName { get; set; } = "";
        public int Order { get; set; }
        public bool IsEllipsis { get; set; }
        [JsonIgnore] public int EllipsisCount { get; set; }
    }

    public class FenceStyle : ViewModelBase
    {
        private string _titleFontFamily = "Microsoft YaHei";
        public string TitleFontFamily { get => _titleFontFamily; set { _titleFontFamily = value; OnPropertyChanged(); } }

        private double _titleFontSize = 14;
        public double TitleFontSize { get => _titleFontSize; set { _titleFontSize = value; OnPropertyChanged(); } }

        private string _titleColor = "#FFFFFF";
        public string TitleColor { get => _titleColor; set { _titleColor = value; OnPropertyChanged(); } }

        // 标题栏底色（#AARRGGBB）。默认半透明深色横条，与围栏主体区分
        private string _titleBarColor = "#33000000";
        public string TitleBarColor { get => _titleBarColor; set { _titleBarColor = value; OnPropertyChanged(); } }

        // Left / Center / Right
        private string _titleAlign = "Left";
        public string TitleAlign { get => _titleAlign; set { _titleAlign = value; OnPropertyChanged(); } }

        private string _itemFontFamily = "Microsoft YaHei";
        public string ItemFontFamily { get => _itemFontFamily; set { _itemFontFamily = value; OnPropertyChanged(); } }

        private double _itemFontSize = 11;
        public double ItemFontSize { get => _itemFontSize; set { _itemFontSize = value; OnPropertyChanged(); } }

        private string _itemColor = "#FFFFFF";
        public string ItemColor { get => _itemColor; set { _itemColor = value; OnPropertyChanged(); } }

        private bool _showItemName = true;
        public bool ShowItemName { get => _showItemName; set { _showItemName = value; OnPropertyChanged(); } }

        // Icon / List
        private string _itemLayout = "Icon";
        public string ItemLayout { get => _itemLayout; set { _itemLayout = value; OnPropertyChanged(); } }

        // 围栏背景色（#AARRGGBB），手动模式使用
        private string _bgColor = "#80000000";
        public string BgColor { get => _bgColor; set { _bgColor = value; OnPropertyChanged(); } }

        // 背景随桌面壁纸自适应（采样壁纸在围栏区域的平均色作半透明背景）
        private bool _useWallpaperTint;
        public bool UseWallpaperTint { get => _useWallpaperTint; set { _useWallpaperTint = value; OnPropertyChanged(); } }
    }

    public class Fence : ViewModelBase
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        private string _title = "新围栏";
        public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }

        private double _x = 200;
        public double X { get => _x; set { _x = value; OnPropertyChanged(); } }

        private double _y = 200;
        public double Y { get => _y; set { _y = value; OnPropertyChanged(); } }

        private double _width = 240;
        public double Width { get => _width; set { _width = value; OnPropertyChanged(); } }

        private double _height = 320;
        public double Height { get => _height; set { _height = value; OnPropertyChanged(); } }

        private bool _collapsed;
        public bool Collapsed { get => _collapsed; set { _collapsed = value; OnPropertyChanged(); } }

        private OverflowMode _overflow = OverflowMode.Ellipsis;
        public OverflowMode Overflow { get => _overflow; set { _overflow = value; OnPropertyChanged(); } }

        public int Monitor { get; set; } = 0;

        private FenceStyle _style = new();
        public FenceStyle Style { get => _style; set { _style = value; OnPropertyChanged(); } }

        private ObservableCollection<FenceItem> _items = new();
        public ObservableCollection<FenceItem> Items { get => _items; set { _items = value; OnPropertyChanged(); } }
    }

    public class AppConfig
    {
        public ObservableCollection<Fence> Fences { get; set; } = new();
    }
}
