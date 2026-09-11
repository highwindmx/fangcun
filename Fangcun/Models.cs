using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Fangcun
{
    // 溢出模式：滚动（默认，可纵向滚动） / 省略（不塞占位格：隐藏滚动条，超出部分由右下角折线角标显示被裁数量，点击角标向下撑大显示全部）
    public enum OverflowMode { Scroll, Ellipsis }

    // 层叠模式（围栏相对其他窗口的层级）：
    // 置底 Bottom = 贴桌面（owner=桌面 SHELLDLL_DefView，Win+D 不隐藏，且沉到普通窗口之下；默认，等同当前行为）
    // 置顶 Top    = 在置底基础上叠加 WS_EX_TOPMOST，始终置于所有非置顶窗口之上，同时仍 Win+D 免疫
    // 窗口 Window = 解除桌面 owner，去掉不激活/置顶，成为独立顶层窗口：随普通窗口 z 序、可聚焦、Win+D 会最小化、可 Alt+Tab
    public enum LayerMode { Bottom, Top, Window }

    // 同步模式（围栏内容来源）：
    // 手动 Manual = 当前默认状态，条目由用户自由增删/拖拽管理。
    // 监视 Watch  = 监视某文件夹，按后缀筛选后把该文件夹内的文件（新增/删除/重命名）实时同步进围栏，
    //               围栏成为该文件夹的“活镜像”（双击打开仍指向真实文件/快捷方式）。
    public enum SyncMode { Manual, Watch }

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
        private bool _isSelected;
        [JsonIgnore]
        public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); } }
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

        private LayerMode _layer = LayerMode.Bottom;
        public LayerMode LayerMode { get => _layer; set { if (_layer != value) { _layer = value; OnPropertyChanged(); } } }

        private SyncMode _sync = SyncMode.Manual;
        public SyncMode SyncMode { get => _sync; set { if (_sync != value) { _sync = value; OnPropertyChanged(); } } }

        // 监视模式：被监视的文件夹路径（地址）
        public string WatchPath { get; set; } = "";
        // 监视模式：后缀筛选（如 "txt" / "*.txt;*.png" / 留空=全部），分号或逗号分隔，忽略大小写与前后空白
        public string WatchFilter { get; set; } = "";

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
