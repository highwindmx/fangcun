using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using XiChuang.Native;

namespace XichuangWin;

/// <summary>栖窗核心：归集窗口、按布局排布（移动/缩放）、悬停聚焦、移除。
/// 所有操作基于真实窗口的 SetWindowPos/ShowWindow，不嵌入、不 SetParent。
/// 数据模型是「槽位」List&lt;WindowSlot&gt;：活窗口是一个槽位，窗口被关掉后槽位仍保留（灰显，可点击重开）。</summary>
public class WindowManager
{
    /// <summary>成员槽位（活窗口 + 已关闭）。顺序即布局顺序。</summary>
    public List<WindowSlot> Slots { get; } = new();

    public LayoutKind Layout { get; set; } = LayoutKind.Single;
    public string WorkspaceName { get; set; } = "默认";

    /// <summary>每个窗口上次被分配的目标矩形。悬停聚焦时用它纠正「被切走后漂移/被最大化」的窗口。</summary>
    private readonly Dictionary<nint, Win32.RECT> _assigned = new();
    private nint _lastFocused;

    // ---------------- 槽位查询 ----------------

    /// <summary>当前活着的窗口句柄（按槽位顺序）。</summary>
    public List<nint> LiveHandles()
    {
        var list = new List<nint>(Slots.Count);
        foreach (var s in Slots) if (s.Alive) list.Add(s.Hwnd);
        return list;
    }

    public bool Contains(nint h)
    {
        foreach (var s in Slots) if (s.Alive && s.Hwnd == h) return true;
        return false;
    }

    public WindowSlot? SlotOf(nint h)
    {
        foreach (var s in Slots) if (s.Alive && s.Hwnd == h) return s;
        return null;
    }

    // ---------------- 槽位构造 / 回填 ----------------

    /// <summary>由窗口信息构造槽位（顺手记下 exe 路径作为重开线索）。</summary>
    public static WindowSlot FromWindow(WindowInfo w, string? sourcePath = null) => new()
    {
        Hwnd = w.Handle,
        ProcessName = w.ProcessName,
        ClassName = w.ClassName,
        Title = w.Title,
        SourcePath = sourcePath,
        ExePath = ExePathOf(w.Pid)
    };

    /// <summary>把一个活窗口的数据回填到既有槽位（重开成功后用）。</summary>
    public static void Fill(WindowSlot slot, WindowInfo w, string? sourcePath = null)
    {
        slot.Hwnd = w.Handle;
        slot.ProcessName = w.ProcessName;
        slot.ClassName = w.ClassName;
        slot.Title = w.Title;
        if (!string.IsNullOrWhiteSpace(sourcePath)) slot.SourcePath = sourcePath;
        slot.ExePath = ExePathOf(w.Pid) ?? slot.ExePath;
        slot.Pending = false;
    }

    /// <summary>进程 exe 完整路径（失败返回 null，权限不足/进程已退出都可能失败）。</summary>
    public static string? ExePathOf(uint pid)
    {
        if (pid == 0) return null;
        try { return Process.GetProcessById((int)pid).MainModule?.FileName; }
        catch { return null; }
    }

    // ---------------- 归集 ----------------

    /// <summary>枚举当前可归集的窗口（已自动排除本进程/外壳窗口）。</summary>
    public List<WindowInfo> EnumerateEligible() => WindowEnumerator.Enumerate();

    /// <summary>通用归集：把未被 exclude 占用的合格窗口全部纳入（会清空原有槽位）。</summary>
    public void Gather(HashSet<nint>? exclude = null)
    {
        Slots.Clear();
        foreach (var w in WindowEnumerator.Enumerate())
            if (exclude is null || !exclude.Contains(w.Handle))
                Slots.Add(FromWindow(w));
        Reflow();
    }

    /// <summary>加入单个窗口（手动收集），返回是否真的加入。</summary>
    public bool Add(nint h, string? sourcePath = null)
    {
        if (!Win32.IsWindow(h) || Contains(h)) return false;
        var info = WindowEnumerator.Describe(h);
        if (info is not null)
        {
            Slots.Add(FromWindow(info, sourcePath));
        }
        else
        {
            Win32.GetWindowThreadProcessId(h, out uint pid);
            Slots.Add(new WindowSlot
            {
                Hwnd = h,
                Title = Win32.GetTitle(h),
                ClassName = Win32.GetClass(h),
                SourcePath = sourcePath,
                ExePath = ExePathOf(pid)
            });
        }
        Reflow();
        return true;
    }

    /// <summary>把已销毁的窗口标为「已关闭」（Hwnd 归零、槽位保留），而不是直接删掉——
    /// 这样清单里仍有一行，可点击用 SourcePath / ExePath 重新打开。</summary>
    public void SyncAlive()
    {
        foreach (var s in Slots)
        {
            if (s.Hwnd == nint.Zero || Win32.IsWindow(s.Hwnd)) continue;
            _assigned.Remove(s.Hwnd);
            s.Hwnd = nint.Zero;
            s.Pending = false;
        }
    }

    /// <summary>尝试把「已关闭」的槽位与用户自己重新打开的窗口对上（标题 → 进程+类 → 进程）。
    /// occupied 为其它栖窗占用的句柄，避免抢别人的窗口。</summary>
    public bool TryReviveAll(HashSet<nint>? occupied = null)
    {
        if (!Slots.Any(s => !s.Alive && !s.Pending)) return false;

        var live = WindowEnumerator.Enumerate()
            .Where(w => (occupied is null || !occupied.Contains(w.Handle)) && !Contains(w.Handle))
            .ToList();
        if (live.Count == 0) return false;

        bool changed = false;
        foreach (var s in Slots)
        {
            if (s.Alive || s.Pending) continue;
            var hit = MatchBySlot(live, s);
            if (hit is null) continue;
            Fill(s, hit, s.SourcePath);
            live.Remove(hit);
            changed = true;
        }
        if (changed) Reflow();
        return changed;
    }

    /// <summary>按槽位记录的身份找活窗口。只做严格匹配（标题全等+同进程，或标题为空时类名+同进程），
    /// 否则容易把「同一个程序的另一个窗口」错误认领，把用户刚开的窗口挪走。</summary>
    private static WindowInfo? MatchBySlot(List<WindowInfo> live, WindowSlot s)
    {
        const StringComparison IC = StringComparison.OrdinalIgnoreCase;

        if (!string.IsNullOrWhiteSpace(s.Title))
        {
            foreach (var w in live)
                if (string.Equals(w.Title, s.Title, IC) && Eq(w.ProcessName, s.ProcessName)) return w;
            return null;
        }

        if (!string.IsNullOrWhiteSpace(s.ClassName))
            foreach (var w in live)
                if (string.Equals(w.ClassName, s.ClassName, IC) && Eq(w.ProcessName, s.ProcessName)) return w;

        return null;

        bool Eq(string a, string b)
            => !string.IsNullOrWhiteSpace(b) && string.Equals(a, b, IC);
    }

    // ---------------- 排布 / 聚焦 / 移除 ----------------

    /// <summary>按当前布局重排活窗口，并记住每个窗口应处的位置。</summary>
    public void Reflow()
    {
        SyncAlive();
        var live = LiveHandles();
        if (live.Count == 0) { _assigned.Clear(); return; }

        var rects = LayoutEngine.Apply(live, StageRect(), Layout);
        _assigned.Clear();
        for (int i = 0; i < live.Count && i < rects.Count; i++)
            _assigned[live[i]] = rects[i];
    }

    /// <summary>窗口被分配的布局矩形（无记录则为 null）。</summary>
    public Win32.RECT? AssignedRect(nint h) => _assigned.TryGetValue(h, out var r) ? r : null;

    /// <summary>聚焦某个窗口：先把它校正回布局矩形（若被最大化/已漂移），再置顶 + 前台。
    /// 不再对可见窗口盲目 SW_RESTORE —— 那会让它跳回记忆中的旧位置/旧尺寸（跳到副屏、变最大化）。</summary>
    public void Focus(nint h)
    {
        if (!Win32.IsWindow(h)) { SyncAlive(); _assigned.Remove(h); return; }

        if (XichuangConfig.Current.MinMode && _lastFocused != nint.Zero && _lastFocused != h && Win32.IsWindow(_lastFocused))
            Win32.ShowWindow(_lastFocused, Win32.SW_MINIMIZE);

        int style = Win32.GetWindowLong(h, Win32.GWL_STYLE);
        bool minimized = (style & Win32.WS_MINIMIZE) != 0;
        bool maximized = (style & Win32.WS_MAXIMIZE) != 0;

        if (_assigned.TryGetValue(h, out var want))
        {
            // 用户把这个窗口最大化过 / 被别的窗口挤走后尺寸变了 → 重新摆回布局矩形，
            // 这样「悬停离开再悬停回来」位置与尺寸始终一致（#2）。
            if (maximized || !LayoutEngine.RectMatches(h, want))
                LayoutEngine.PlaceToRect(h, want);
        }
        else if (minimized || maximized)
        {
            Win32.ShowWindow(h, Win32.SW_RESTORE);
        }

        // 置顶：HWND_TOP 是 0（不是 -1！-1 是 TOPMOST，会把窗口钉在最上层，压死新开程序）。
        // 带 NOACTIVATE 是为了让「激活」只由下面的 Activate 统一负责，行为可预测。
        Win32.SetWindowPos(h, Win32.HWND_TOP, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW | Win32.SWP_NOACTIVATE);
        Win32.ClearTopmost(h);

        // 稳健置前：三级 fallback（附前台线程 → 附目标线程 + SetFocus → SwitchToThisWindow）。
        // 只 AttachThreadInput 前台线程是**不够**的：输入队列虽共享，但键盘焦点并未转给目标窗口，
        // 于是仍会出现「任务栏闪一下、窗口没到前台」的现象。
        if (!Win32.Activate(h))
        {
            // 前台锁实在绕不过去（最常见原因：目标程序以管理员身份运行，而本程序是普通权限，
            // 受 UIPI 限制）。此时任何用户态手段都无解，至少保证窗口可见地浮到最前。
            Win32.FlashToFront(h);
        }

        _lastFocused = h;
    }

    /// <summary>把本栖窗的活窗口按顺序提到普通层级最前（不激活、不 topmost）。
    /// 仅用于「切换 tab」这种明确操作——不清 topmost 也不抢前台，所以不会压住之后新开的程序。</summary>
    public void RaiseAll()
    {
        foreach (var h in LiveHandles())
            if (Win32.IsWindow(h))
                Win32.SetWindowPos(h, Win32.HWND_TOP, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>把窗口移出当前栖窗：解除 topmost（恢复普通层级），连同槽位一起移除。</summary>
    public void Remove(nint h)
    {
        if (Win32.IsWindow(h)) Win32.ClearTopmost(h);
        Slots.RemoveAll(s => s.Alive && s.Hwnd == h);
        _assigned.Remove(h);
        Reflow();
    }

    /// <summary>移除某个槽位（含已关闭的槽位——用户主动"不要这一项了"）。</summary>
    public void Forget(WindowSlot slot)
    {
        if (slot.Alive && Win32.IsWindow(slot.Hwnd)) Win32.ClearTopmost(slot.Hwnd);
        _assigned.Remove(slot.Hwnd);
        Slots.Remove(slot);
        Reflow();
    }

    // ---------------- 几何 ----------------

    /// <summary>排布区（stage，物理像素）：在所选显示器工作区内，按停靠边让出侧栏厚度。
    /// 窗口最终被铺到这个矩形里——所以它必然位于侧栏的「内侧」，而不会盖住侧栏。</summary>
    public static Win32.RECT StageRect()
    {
        var cfg = XichuangConfig.Current;
        var hMon = Win32.MonitorByIndex(cfg.SidebarMonitor);
        var mi = new Win32.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(hMon, ref mi);
        var work = mi.rcWork;
        double scale = Win32.ScaleForMonitor(hMon);
        int t = (int)(cfg.SidebarWidth * scale); // 侧栏厚度（物理像素）

        switch (cfg.Edge)
        {
            case DockEdge.Left: work.Left += t; break;
            case DockEdge.Right: work.Right -= t; break;
            case DockEdge.Top: work.Top += t; break;
            case DockEdge.Bottom: work.Bottom -= t; break;
        }
        return work;
    }

    /// <summary>所选显示器的完整工作区（物理像素），供侧栏自身定位用。</summary>
    public static Win32.RECT WorkAreaPhysical() => WorkAreaPhysical(XichuangConfig.Current.SidebarMonitor);

    public static Win32.RECT WorkAreaPhysical(int monitorIndex)
    {
        var hMon = Win32.MonitorByIndex(monitorIndex);
        var mi = new Win32.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(hMon, ref mi);
        return mi.rcWork;
    }
}
