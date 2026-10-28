using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fangcun.Xichuang.Native;

namespace Fangcun.Xichuang;

/// <summary>栖窗侧栏（单例）：左/右/上/下停靠，顶端固定宽度 tab 切换多个命名栖窗。
/// 一个侧栏一个任务栏图标；最小化侧栏联动最小化活动栖窗的窗口（3a）。</summary>
public partial class SidebarWindow : Window
{
    /// <summary>tab 固定宽度：220 厚度（内容区约 202）下正好并排三个，超出横向滚动而不是换行堆叠。</summary>
    private const double TabWidth = 64;

    private DispatcherTimer? _hoverTimer;
    private nint _hoverHwnd;

    /// <summary>活性心跳：周期性核对槽位还在不在，让「窗口被关掉 → 清单立刻灰显」不再依赖用户点击。</summary>
    private DispatcherTimer? _aliveTimer;

    // 拖拽起点（用于区分「单击加入」与「拖动收集」）
    private Point _dragOrigin;
    private bool _dragOriginValid;

    public SidebarWindow()
    {
        InitializeComponent();
        SyncTabs();
        RefreshActiveList();
        StartAliveWatch();
    }

    // ---------- 活性心跳 ----------
    private void StartAliveWatch()
    {
        _aliveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _aliveTimer.Tick -= OnAliveTick;
        _aliveTimer.Tick += OnAliveTick;
        _aliveTimer.Start();
    }

    private void OnAliveTick(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) return;

        var inst = Launcher.ActiveInstance;
        if (inst is null || inst.Wm.Slots.Count == 0) return;

        // 只做「死句柄 → 已关闭」这一件事，槽位自己会把通知发给界面。
        // 认领窗口（TryReviveAll）与标题同步都留在 RefreshActiveList 里、由用户操作触发：
        // 每 600 ms 抢一次窗口会触发 Reflow，把用户正在点/刚打开的窗口挪到布局位上，
        // 表现就是「窗口突然自己动了、点不住」。
        if (inst.Wm.SyncAlive()) Launcher.SaveNow();
    }

    // ---------- 定位：按配置停靠边 + 显示器 ----------
    public void ApplyPosition() => PositionToConfig();

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _collapsed = false;
        Launcher.SidebarCollapsed = false;
        PositionToConfig();
        // 首次显示保持完整态（托盘「显示栖窗」要能看见内容）；之后鼠标移开再由定时器折叠
        _autoHide = XichuangConfig.Current.AutoHide;
    }

    private void PositionToConfig()
    {
        var cfg = XichuangConfig.Current;
        var work = WindowManager.WorkAreaPhysical(cfg.SidebarMonitor);
        double scale = Win32.ScaleForMonitor(Win32.MonitorByIndex(cfg.SidebarMonitor));
        int t = (int)(cfg.SidebarWidth * scale);  // 展开厚度（物理像素）
        int rail = (int)(RailThickness * scale);  // 折叠细条厚度（物理像素）
        bool collapsed = _collapsed;

        switch (cfg.Edge)
        {
            case DockEdge.Left:
                Left = work.Left / scale; Top = work.Top / scale;
                Width = collapsed ? RailThickness : cfg.SidebarWidth; Height = work.Height / scale;
                RootBorder.CornerRadius = new CornerRadius(0, 12, 12, 0);
                break;
            case DockEdge.Right:
                Left = (collapsed ? work.Right - rail : work.Right - t) / scale; Top = work.Top / scale;
                Width = collapsed ? RailThickness : cfg.SidebarWidth; Height = work.Height / scale;
                RootBorder.CornerRadius = new CornerRadius(12, 0, 0, 12);
                break;
            case DockEdge.Top:
                Left = work.Left / scale; Top = work.Top / scale;
                Width = work.Width / scale; Height = collapsed ? RailThickness : cfg.SidebarWidth;
                RootBorder.CornerRadius = new CornerRadius(0, 0, 12, 12);
                break;
            case DockEdge.Bottom:
                Left = work.Left / scale;
                Top = (collapsed ? work.Bottom - rail : work.Bottom - t) / scale;
                Width = work.Width / scale; Height = collapsed ? RailThickness : cfg.SidebarWidth;
                RootBorder.CornerRadius = new CornerRadius(12, 12, 0, 0);
                break;
        }

        // 折叠态只留贴边细条；展开态恢复完整内容
        ContentRoot.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        RootBorder.ToolTip = collapsed ? "鼠标移入展开栖窗" : null;
    }

    // ---------- 自动隐藏：折叠为贴边细条，鼠标移入展开 ----------
    /// <summary>折叠后保留的贴边细条厚度（逻辑像素）。太细不好点，太厚占地方。</summary>
    private const double RailThickness = 8;

    private bool _autoHide;
    private bool _collapsed;
    private DispatcherTimer? _collapseTimer;

    /// <summary>按当前配置应用/取消自动隐藏。设置保存后调用，立即生效，无需重开栖窗。</summary>
    public void ApplyAutoHide()
    {
        _autoHide = XichuangConfig.Current.AutoHide;
        _collapseTimer?.Stop();

        if (!_autoHide)
        {
            Expand();
            return;
        }
        // 鼠标不在侧栏上就立刻折起（在设置窗点保存时正是如此）；在侧栏上则等移开再折
        if (!IsMouseOver) Collapse();
    }

    /// <summary>从折叠态恢复为完整侧栏（托盘「显示栖窗」等外部唤出入口使用）。</summary>
    public void ExpandFromAutoHide() => Expand();

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_autoHide) return;
        _collapseTimer?.Stop();
        if (_collapsed) Expand();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_autoHide || _collapsed) return;
        if (AddPopup.IsOpen) return;   // 加窗弹层开着时先不折，避免误收

        _collapseTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _collapseTimer.Stop();
        _collapseTimer.Tick -= OnCollapseTick;
        _collapseTimer.Tick += OnCollapseTick;
        _collapseTimer.Start();
    }

    private void OnCollapseTick(object? sender, EventArgs e)
    {
        _collapseTimer?.Stop();
        if (!_autoHide || IsMouseOver || AddPopup.IsOpen) return;
        Collapse();
    }

    private void Collapse()
    {
        if (_collapsed) return;
        _collapsed = true;
        Launcher.SidebarCollapsed = true;
        PositionToConfig();
        // 可选：折叠后把排布区扩到整个工作区（左右停靠即横向铺满全屏）
        if (XichuangConfig.Current.ExpandOnAutoHide)
            Launcher.ActiveInstance?.Wm.Reflow();
    }

    private void Expand()
    {
        Launcher.SidebarCollapsed = false;
        if (!_collapsed) return;
        _collapsed = false;
        PositionToConfig();
        if (XichuangConfig.Current.ExpandOnAutoHide)
            Launcher.ActiveInstance?.Wm.Reflow();
    }

    // ---------- 联动：最小化/恢复 ----------
    private void OnStateChanged(object? sender, EventArgs e)
    {
        var inst = Launcher.ActiveInstance;
        if (inst is null) return;
        if (WindowState == WindowState.Minimized) inst.MinimizeAll();
        else if (WindowState == WindowState.Normal) { inst.RestoreAll(); inst.Wm.Reflow(); }
    }

    // ---------- tab 条 ----------
    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        Launcher.NewInstance();
        // 新 tab 在最右，滚到末尾保证看得见
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => TabScroll.ScrollToHorizontalOffset(TabScroll.ScrollableWidth)));
    }

    /// <summary>tab 超出可用宽度时，用滚轮横向滚动（不显示丑滚动条）。</summary>
    private void TabScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    public void SyncTabs()
    {
        TabPanel.Children.Clear();
        foreach (var kv in Launcher.Instances)
        {
            string name = kv.Key;
            bool active = name == Launcher.ActiveName;

            var txt = new TextBlock
            {
                Text = name,
                Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xBC)),
                FontSize = 11.5,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var border = new Border
            {
                Width = TabWidth,
                Height = 26,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 2, 0),
                Padding = new Thickness(5, 0, 5, 0),
                Background = active
                    ? new SolidColorBrush(Color.FromRgb(0x3A, 0x63, 0xA8))
                    : new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand,
                ToolTip = name,
                Child = txt
            };

            border.MouseEnter += (_, _) =>
            {
                if (!active) border.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            };
            border.MouseLeave += (_, _) =>
            {
                border.Background = active
                    ? new SolidColorBrush(Color.FromRgb(0x3A, 0x63, 0xA8))
                    : new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            };
            border.MouseLeftButtonDown += (_, _) => Launcher.SetActive(name);
            border.ContextMenu = BuildTabMenu(border, name, txt);

            TabPanel.Children.Add(border);
        }
    }

    /// <summary>tab 右键菜单：重命名 / 删除（不再往 tab 里塞 ✕，省出宽度给名字）。</summary>
    private ContextMenu BuildTabMenu(Border border, string name, TextBlock txt)
    {
        var cm = new ContextMenu();
        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) => BeginRename(border, name, txt);
        var del = new MenuItem { Header = "删除此栖窗" };
        del.Click += (_, _) => Launcher.DeleteInstance(name);
        cm.Items.Add(rename);
        cm.Items.Add(new Separator());
        cm.Items.Add(del);
        return cm;
    }

    private void BeginRename(Border border, string oldName, TextBlock txt)
    {
        var tb = new TextBox
        {
            Text = oldName,
            Width = TabWidth - 10,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x24)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 1, 2, 1),
            CaretBrush = Brushes.White
        };
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Launcher.RenameInstance(oldName, tb.Text);
            else if (e.Key == Key.Escape) SyncTabs();
        };
        tb.LostFocus += (_, _) => SyncTabs();
        border.Child = tb;
        tb.Focus();
        tb.SelectAll();
    }

    // ---------- 活动实例列表 ----------
    public void RefreshActiveList()
    {
        var inst = Launcher.ActiveInstance;
        if (inst is not null)
        {
            inst.Wm.SyncAlive();                                   // 死句柄 → 标为「已关闭」，槽位保留
            inst.Wm.TryReviveAll(Launcher.Occupied(inst.Name));    // 用户自己重开的窗口 → 自动认领回槽位

            foreach (var s in inst.Wm.Slots)
            {
                if (!s.Alive) continue;
                string t = Win32.GetTitle(s.Hwnd);
                if (!string.IsNullOrWhiteSpace(t)) s.Title = t;
                s.Icon = Win32.GetWindowIcon(s.Hwnd) ?? s.Icon;    // 关闭后保留最后图标，灰显时有辨识度
            }
        }

        // 重建 ItemsSource 会重置滚动位置——记住再恢复，滚动列表时不跳回顶部
        double scroll = ListScroll.VerticalOffset;
        List.ItemsSource = null;
        List.ItemsSource = inst?.Wm.Slots;
        if (scroll > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => ListScroll.ScrollToVerticalOffset(scroll)));

        TitleText.Text = inst is null ? "栖窗" : "栖窗 · " + inst.Name;
        HighlightLayout();
    }

    private WindowManager? ActiveWm => Launcher.ActiveInstance?.Wm;

    // ---------- 加窗：选单 + 拖放 + 全部加入 ----------
    private void AddWindow_Click(object sender, RoutedEventArgs e)
    {
        if (Launcher.ActiveInstance is null) return;
        RefreshAddList();
        AddPopup.IsOpen = !AddPopup.IsOpen;
    }

    /// <summary>计算「还没被加入本栖窗、也没被其它栖窗占用」的候选窗口。</summary>
    private List<WindowInfo> BuildEligible()
    {
        var inst = Launcher.ActiveInstance;
        if (inst is null) return new List<WindowInfo>();

        var occupied = Launcher.Occupied(Launcher.ActiveName);
        return WindowEnumerator.Enumerate()
            .Where(w => !inst.Wm.Contains(w.Handle) && !occupied.Contains(w.Handle))
            .Select(w => new WindowInfo(w.Handle, w.Pid, w.ProcessName, w.Title)
            {
                ClassName = w.ClassName,
                Icon = Win32.GetWindowIcon(w.Handle)
            })
            .ToList();
    }

    private void RefreshAddList()
    {
        var eligible = BuildEligible();
        AddList.ItemsSource = eligible;
        AddCountText.Text = eligible.Count == 0 ? "没有可加入的窗口" : $"可加入的窗口（{eligible.Count}）";
    }

    private void AddItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { DataContext: WindowInfo wi })
        {
            AddWindowToActive(wi.Handle);
            RefreshAddList();
        }
    }

    private void AddItem_Down(object sender, MouseButtonEventArgs e)
    {
        // 记录按下点：只有拖动超过系统阈值才起拖，否则保持「点击即加入」的可靠手感
        _dragOrigin = e.GetPosition(this);
        _dragOriginValid = true;
    }

    private void AddItem_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_dragOriginValid) return;

        var p = e.GetPosition(this);
        var o = _dragOrigin;
        bool far = Math.Abs(p.X - o.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(p.Y - o.Y) > SystemParameters.MinimumVerticalDragDistance;
        if (!far) return;

        _dragOriginValid = false; // 本次拖动只触发一次
        if (sender is Border { DataContext: WindowInfo wi })
            DragDrop.DoDragDrop((DependencyObject)sender, wi, DragDropEffects.Copy);
    }

    private void AddAll_Click(object sender, RoutedEventArgs e)
    {
        var inst = Launcher.ActiveInstance;
        if (inst is null) return;
        var eligible = AddList.ItemsSource as List<WindowInfo> ?? BuildEligible();

        // 批量加入：先全部塞进槽位，最后只 Reflow 一次 —— 否则每加一个就重摆全部窗口，屏幕会狂闪
        bool any = false;
        foreach (var w in eligible)
        {
            if (!Win32.IsWindow(w.Handle) || inst.Wm.Contains(w.Handle)) continue;
            var info = WindowEnumerator.Describe(w.Handle);
            if (info is not null) inst.Wm.Slots.Add(WindowManager.FromWindow(info));
            else inst.Wm.Slots.Add(new WindowSlot
            {
                Hwnd = w.Handle, Title = w.Title, ClassName = w.ClassName, ProcessName = w.ProcessName
            });
            any = true;
        }

        inst.HasCollected = true;
        if (any) inst.Wm.Reflow();
        RefreshActiveList();
        RefreshAddList();
        AddPopup.IsOpen = false;
        Launcher.SaveNow();
    }

    private void List_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(WindowInfo))) e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(WindowInfo)) is WindowInfo wi)
        {
            AddWindowToActive(wi.Handle);
            RefreshAddList();
        }
    }

    /// <summary>把指定窗口加入「活动栖窗」并按当前布局排布（手动收集，不越界）。</summary>
    private void AddWindowToActive(nint h)
    {
        var inst = Launcher.ActiveInstance;
        if (inst is null) return;
        if (!inst.Wm.Add(h)) return;
        inst.HasCollected = true;
        RefreshActiveList();
        Launcher.SaveNow();
    }

    private void Row_Enter(object sender, MouseEventArgs e)
    {
        // 已关闭 / 打开中的行没有可聚焦的窗口，不做悬停
        if (sender is not Border { DataContext: WindowSlot slot } || !slot.Alive)
        {
            _hoverTimer?.Stop();
            return;
        }
        _hoverHwnd = slot.Hwnd;
        if (XichuangConfig.Current.Trigger == TriggerMode.Hover && ActiveWm is not null)
        {
            _hoverTimer ??= new DispatcherTimer();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(XichuangConfig.Current.HoverDelayMs);
            _hoverTimer.Tick -= OnHoverTick;
            _hoverTimer.Tick += OnHoverTick;
            _hoverTimer.Start();
        }
    }

    private void Row_Leave(object sender, MouseEventArgs e) => _hoverTimer?.Stop();

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        if (sender is not Border { DataContext: WindowSlot slot }) return;
        var inst = Launcher.ActiveInstance;
        if (inst is null) return;

        if (!slot.Alive) { inst.ReopenSlot(slot); return; }   // 已关闭 → 点一下重新打开
        if (XichuangConfig.Current.Trigger == TriggerMode.Click) inst.Wm.Focus(slot.Hwnd);
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        _hoverTimer?.Stop();
        if (_hoverHwnd != nint.Zero) ActiveWm?.Focus(_hoverHwnd);
    }

    /// <summary>行尾按钮：活窗口 = ✕ 移出；已关闭 = ↻ 重新打开。
    /// 已关闭的行**永远不会**因为「查不到线索」被自动删掉——先提示，去掉与否交给用户。</summary>
    private void RowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WindowSlot slot }) return;
        var inst = Launcher.ActiveInstance;
        if (inst is null) return;

        if (slot.Alive)
        {
            inst.Wm.Remove(slot.Hwnd);
        }
        else
        {
            if (!inst.ReopenSlot(slot))
            {
                System.Windows.MessageBox.Show(
                    $"「{slot.Display}」没有可用的重开线索（既不知道源文件，也查不到 {slot.ProcessName}.exe）。\n\n" +
                    "可以手动打开它 —— 打开后这一行会自动复活；也可以右键这一行把它从清单移出。",
                    "栖窗", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            if (AddPopup.IsOpen) RefreshAddList();
            return;
        }

        RefreshActiveList();
        if (AddPopup.IsOpen) RefreshAddList();
        Launcher.SaveNow();
    }

    /// <summary>从右键菜单的 PlacementTarget（被右键的那一行）取槽位。</summary>
    private static WindowSlot? SlotOfMenu(object sender)
    {
        if (sender is not MenuItem mi) return null;
        return mi.Parent is ContextMenu { PlacementTarget: FrameworkElement fe } ? fe.DataContext as WindowSlot : null;
    }

    /// <summary>右键「重新打开」——已关闭行专用入口，等价于点行尾的 ↻。</summary>
    private void RowMenu_Reopen(object sender, RoutedEventArgs e)
    {
        var slot = SlotOfMenu(sender);
        var inst = Launcher.ActiveInstance;
        if (slot is null || inst is null) return;

        if (slot.Alive) { inst.Wm.Focus(slot.Hwnd); return; }

        if (!inst.ReopenSlot(slot))
        {
            System.Windows.MessageBox.Show(
                $"「{slot.Display}」没有可用的重开线索（既不知道源文件，也查不到 {slot.ProcessName}.exe）。\n\n" +
                "手动打开它，这一行会自动复活。",
                "栖窗", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        RefreshActiveList();
    }

    /// <summary>右键「从清单移出」——明确的用户意图，这才允许把槽位删掉。</summary>
    private void RowMenu_Forget(object sender, RoutedEventArgs e)
    {
        var slot = SlotOfMenu(sender);
        var inst = Launcher.ActiveInstance;
        if (slot is null || inst is null) return;

        inst.Wm.Forget(slot);
        RefreshActiveList();
        if (AddPopup.IsOpen) RefreshAddList();
        Launcher.SaveNow();
    }

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        var inst = Launcher.ActiveInstance;
        if (inst is null) return;
        if (sender is not ToggleButton { Tag: string tag } || !Enum.TryParse<LayoutKind>(tag, out var lk)) return;
        inst.Wm.Layout = lk;
        inst.Wm.Reflow();
        HighlightLayout();

        var ws = XichuangConfig.Current.Workspaces.FirstOrDefault(w => w.Name == inst.Name);
        if (ws is null) { ws = new WorkspaceConfig { Name = inst.Name, Layout = lk }; XichuangConfig.Current.Workspaces.Add(ws); }
        else ws.Layout = lk;
        XichuangConfig.Current.Save();
        Launcher.SaveNow();
    }

    private void HighlightLayout()
    {
        var lk = Launcher.ActiveInstance?.Wm.Layout ?? LayoutKind.Single;
        foreach (var b in LayoutGrid.Children.OfType<ToggleButton>())
            b.IsChecked = b.Tag is string tag && Enum.TryParse<LayoutKind>(tag, out var lk2) && lk2 == lk;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => Launcher.ShowSettings();

    private void Hide_Click(object sender, RoutedEventArgs e) => Launcher.HideSidebar();
}
