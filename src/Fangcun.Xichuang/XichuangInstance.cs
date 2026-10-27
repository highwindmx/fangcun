using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Fangcun.Xichuang.Native;

namespace Fangcun.Xichuang;

/// <summary>
/// 一个栖窗实例：独立命名、独立槽位集合（多实例互不越界）。
/// 侧栏最小化 → 其窗口整体最小化；侧栏恢复 → 其窗口整体还原（3a）。
/// 侧栏 UI 已改为「单窗口 + 顶部 tab」，本类不持有 SidebarWindow。
/// </summary>
public class XichuangInstance
{
    public string Name { get; set; }
    public WindowManager Wm { get; }
    public bool HasCollected { get; set; }

    private readonly Dispatcher _dispatcher;

    // ---- 通用实时归集（Collect 使用）----
    private DispatcherTimer? _liveTimer;
    private DateTime _liveUntil;
    private HashSet<uint>? _livePids;

    // ---- 打开文件 / 重开窗口的捕获器 ----
    private DispatcherTimer? _capTimer;
    private Dictionary<nint, WindowInfo>? _capSnap;   // 打开前的窗口快照（hwnd → 信息）
    private HashSet<uint> _capBeforePids = new();      // 打开前「已有可见窗口的进程」
    private List<string> _capPaths = new();            // 本次打开的目标路径（命中后移除）
    private List<WindowSlot> _capPending = new();      // 预登记、等待窗口出现的槽位
    private WindowSlot? _capRevive;                    // 非空 = 回填这个已关闭槽位
    private DateTime _capStart;
    private DateTime _capUntil;

    public XichuangInstance(string name)
    {
        Name = name;
        Wm = new WindowManager { WorkspaceName = name };
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>通用归集：纳入未被 exclude 占用的合格窗口，并开启一段实时归集（捕获随后出现的窗口）。</summary>
    public void Collect(HashSet<nint>? exclude = null)
    {
        Wm.Gather(exclude);
        BeginLiveCapture(null, TimeSpan.FromSeconds(10));
    }

    /// <summary>侧栏最小化联动：本实例窗口整体最小化（3a）。</summary>
    public void MinimizeAll()
    {
        foreach (var h in Wm.LiveHandles())
            if (Win32.IsWindow(h)) Win32.ShowWindow(h, Win32.SW_MINIMIZE);
    }

    /// <summary>侧栏恢复联动：只把「确实处于最小化」的窗口摆回它的布局矩形。
    /// 不动 z 序、不动正常显示的窗口 —— 否则会把新打开的程序压在下面（#8）。</summary>
    public void RestoreAll()
    {
        foreach (var h in Wm.LiveHandles())
        {
            if (!Win32.IsWindow(h)) continue;
            int style = Win32.GetWindowLong(h, Win32.GWL_STYLE);
            if ((style & Win32.WS_MINIMIZE) == 0) continue;

            var want = Wm.AssignedRect(h);
            if (want is Win32.RECT r) LayoutEngine.PlaceToRect(h, r);
            else Win32.ShowWindow(h, Win32.SW_RESTORE);
        }
    }

    // ================= 围栏归集（3b）=================

    /// <summary>
    /// 用默认应用打开一批文件/文件夹，并把「这次因打开动作而出现/变化的窗口」归集进来，绝不越界。
    ///
    /// 为什么不再按 pid 归集：ShellExecute 打开文件时，Process.Start 常常返回 null
    /// （文件交给了已运行的实例，如已开着的 Word/Acrobat），或返回一个立即退出的壳进程，
    /// 于是 pid 集合为空 → 过滤条件把所有窗口都挡掉，表现为「侧栏出来了但一个窗口都没纳入」。
    ///
    /// 现在改为「打开前快照 + 打开后差分」：
    ///   ① 打开后新出现的窗口，且（该进程之前没有任何可见窗口，或标题含目标文件名）→ 收；
    ///   ② 打开前就有、但标题变了的窗口（Word 单窗口模式打开新文档），且标题含目标文件名 → 收。
    /// </summary>
    public void GatherFromPaths(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        if (list.Count == 0) return;

        // ① 打开前快照
        var snap = Snapshot();

        // ② 预登记「打开中…」槽位：程序启动慢时清单里也有即时反馈，命中后回填。
        //    已经开着的不重复登记；已关闭的复用原槽位（避免连点围栏堆出一堆重复行）。
        var pending = new List<WindowSlot>();
        foreach (var p in list)
        {
            if (Wm.Slots.Any(s => s.Alive && Same(s.SourcePath, p))) continue;

            var dead = Wm.Slots.FirstOrDefault(s => !s.Alive && Same(s.SourcePath, p));
            if (dead is not null)
            {
                dead.Pending = true;
                pending.Add(dead);
                continue;
            }

            string name = Path.GetFileNameWithoutExtension(p);
            if (string.IsNullOrWhiteSpace(name))
                name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var slot = new WindowSlot
            {
                SourcePath = p,
                Title = string.IsNullOrWhiteSpace(name) ? p : name,
                Pending = true
            };
            Wm.Slots.Add(slot);
            pending.Add(slot);
        }
        Launcher.RefreshActiveList();

        // ③ 用默认应用打开
        bool anyOpened = false;
        foreach (var p in list)
        {
            try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); anyOpened = true; }
            catch { /* 路径不存在/无关联程序：跳过 */ }
        }

        if (!anyOpened)
        {
            // 一个都没打开成功（路径失效/无关联程序）：把这批预登记转成「已关闭」，留给用户判断
            foreach (var s in pending) s.Pending = false;
            Launcher.RefreshActiveList();
            return;
        }

        // ④ 启动捕获器（15 秒窗口，覆盖冷启动很慢的程序）
        BeginCapture(snap, list, pending, null, TimeSpan.FromSeconds(15));
    }

    /// <summary>重新打开一个已关闭的槽位，并在窗口出现后回填该槽位（不新增行）。</summary>
    public void ReopenSlot(WindowSlot slot)
    {
        if (slot.Alive) return;
        var snap = Snapshot();
        if (!slot.TryReopen()) return;

        slot.Pending = true;
        Launcher.RefreshActiveList();

        var paths = string.IsNullOrWhiteSpace(slot.SourcePath)
            ? new List<string>()
            : new List<string> { slot.SourcePath! };
        BeginCapture(snap, paths, new List<WindowSlot>(), slot, TimeSpan.FromSeconds(15));
    }

    private static Dictionary<nint, WindowInfo> Snapshot()
    {
        var snap = new Dictionary<nint, WindowInfo>();
        foreach (var w in WindowEnumerator.Enumerate()) snap[w.Handle] = w;
        return snap;
    }

    /// <summary>本实例是否就是侧栏上正在显示的那个栖窗（且侧栏没有收起）。
    /// 只有这种情况下才允许移动窗口 —— 后台实例或收起的侧栏若也 Reflow，会把已最小化的窗口弹出来。</summary>
    private bool IsActive
        => string.Equals(Launcher.ActiveName, Name, StringComparison.Ordinal) && Launcher.SidebarVisible;

    // ================= 捕获器 =================

    private void BeginCapture(Dictionary<nint, WindowInfo> snap, List<string> paths,
                              List<WindowSlot> pending, WindowSlot? revive, TimeSpan duration)
    {
        if (_dispatcher.HasShutdownStarted) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => BeginCapture(snap, paths, pending, revive, duration));
            return;
        }

        _capSnap = snap;
        _capBeforePids = new HashSet<uint>(snap.Values.Select(w => w.Pid));
        _capPaths = new List<string>(paths);
        _capPending = pending;
        _capRevive = revive;
        _capStart = DateTime.UtcNow;
        _capUntil = _capStart + duration;

        _capTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _capTimer.Tick -= OnCaptureTick;
        _capTimer.Tick += OnCaptureTick;
        if (!_capTimer.IsEnabled) _capTimer.Start();
    }

    private void OnCaptureTick(object? sender, EventArgs e)
    {
        // 用当前快照判断「哪些窗口是打开动作之后新冒出来的」
        var occupied = Launcher.Occupied(Name);
        bool changed = false;

        foreach (var w in WindowEnumerator.Enumerate())
        {
            if (occupied.Contains(w.Handle) || Wm.Contains(w.Handle)) continue;

            // 标准对话框（打开/另存为/属性…）不是成员窗口，别把它当成一条行挪来挪去
            if (string.Equals(w.ClassName, "#32770", StringComparison.OrdinalIgnoreCase)) continue;

            // 注意：不能写成 `known = _capSnap is not null && TryGetValue(..., out var oldInfo)`
            // 再用三目取 oldInfo —— 短路时 oldInfo 未赋值，编译器报 CS0165。显式分支最稳。
            WindowInfo? old = null;
            bool known = false;
            if (_capSnap is not null && _capSnap.TryGetValue(w.Handle, out var oldInfo))
            {
                known = true;
                old = oldInfo;
            }
            string? hitPath = PathHit(w.Title, _capPaths);

            bool accept;
            if (_capRevive is not null)
            {
                // 复活模式：只要「新出现」或「标题变了」，且能对上原槽位
                bool fresh = !known || !string.Equals(old?.Title, w.Title, StringComparison.Ordinal);
                accept = fresh && (MatchesRevive(w, _capRevive) || hitPath is not null);
            }
            else if (!known)
            {
                // 打开前不存在 → 新进程 或 标题命中目标文件名。
                // 待开槽位都填满之后只认标题命中，否则这十几秒里用户随手开的程序也会被卷进来（越界）。
                bool freshPid = !_capBeforePids.Contains(w.Pid);
                accept = hitPath is not null || (freshPid && _capPending.Count > 0);
            }
            else
            {
                // 打开前就有这个窗口 → 两种情况都算本次目标：
                //   ① 标题变了且命中目标文件名（Word 复用窗口换文档）；
                //   ② 标题命中、且待开槽位里还有这个路径 —— 说明用户先手动开了这份文档，现在点围栏要把它拢进来。
                bool titleChanged = !string.Equals(old?.Title, w.Title, StringComparison.Ordinal);
                bool stillWanted = hitPath is not null && _capPending.Any(s => Same(s.SourcePath, hitPath));
                accept = hitPath is not null && (titleChanged || stillWanted);
            }

            if (!accept) continue;

            if (_capRevive is not null)
            {
                WindowManager.Fill(_capRevive, w, hitPath);
                _capRevive = null;
            }
            else
            {
                // 优先回填「路径一致」的预登记槽位，否则取第一个待开槽位
                var target = hitPath is not null
                    ? _capPending.FirstOrDefault(s => Same(s.SourcePath, hitPath))
                    : _capPending.FirstOrDefault();

                if (target is not null)
                {
                    WindowManager.Fill(target, w, hitPath ?? target.SourcePath);
                    _capPending.Remove(target);
                }
                else
                {
                    Wm.Slots.Add(WindowManager.FromWindow(w, hitPath));
                }
            }

            changed = true;
        }

        if (changed)
        {
            // 后台实例只记账不搬窗口：真正铺排等用户切到它（SetActive）时再做
            if (IsActive)
            {
                Wm.Reflow();
                Launcher.RefreshActiveList();
            }
            else
            {
                Launcher.SaveNow();
            }
        }

        // 至少观察 2.5 秒（一个路径可能产生多个窗口，别在第一帧就收工）；
        // 之后待开槽位都填满即可结束，剩下没出现的窗口交给「已关闭」槽位由用户点击重开。
        bool minElapsed = DateTime.UtcNow - _capStart >= TimeSpan.FromSeconds(2.5);
        bool done = DateTime.UtcNow >= _capUntil
                    || (minElapsed && _capRevive is null && _capPending.Count == 0);
        if (done) FinishCapture(changed);
    }

    private void FinishCapture(bool changed)
    {
        _capTimer?.Stop();

        // 仍未匹配到的预登记槽位：标为「已关闭」保留下来（可点击重开，也可从清单里移出），
        // 而不是直接删掉——否则程序启动特别慢时用户会看到「什么都没有」。
        foreach (var s in _capPending) s.Pending = false;
        _capPending.Clear();
        _capRevive = null;
        _capPaths.Clear();
        _capSnap = null;
        _capBeforePids.Clear();

        Launcher.RefreshActiveList();
        if (changed) Launcher.SaveNow();
    }

    /// <summary>标题里是否含某个目标的文件名（不含扩展名）。返回命中的那个路径。</summary>
    private static string? PathHit(string title, List<string> paths)
    {
        if (string.IsNullOrWhiteSpace(title) || paths.Count == 0) return null;
        foreach (var p in paths)
        {
            string name = Path.GetFileNameWithoutExtension(p);
            if (name.Length < 2)
                name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (name.Length >= 2 && title.Contains(name, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    /// <summary>新窗口是否就是那个已关闭槽位对应的程序。</summary>
    private static bool MatchesRevive(WindowInfo w, WindowSlot slot)
    {
        if (!string.IsNullOrWhiteSpace(slot.ClassName)
            && string.Equals(w.ClassName, slot.ClassName, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(slot.ProcessName)
               && string.Equals(w.ProcessName, slot.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ================= 通用实时归集 =================

    /// <summary>实时归集：周期性扫描合格窗口，把新出现的加入本实例并重新排布。
    /// pids 为 null 表示捕获所有未被占用的窗口；否则仅捕获指定进程产生的窗口。
    /// 注意：空集合视为「不限」，否则会被误当成「一个都不许」把归集整个屏蔽掉。
    /// DispatcherTimer 必须在 UI 线程创建，故非 UI 线程调用时自动 marshal 过去。</summary>
    public void BeginLiveCapture(HashSet<uint>? pids, TimeSpan duration)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => BeginLiveCapture(pids, duration));
            return;
        }
        _livePids = pids is { Count: > 0 } ? pids : null;
        _liveUntil = DateTime.UtcNow + duration;
        _liveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _liveTimer.Tick -= OnLiveTick;
        _liveTimer.Tick += OnLiveTick;
        if (!_liveTimer.IsEnabled) _liveTimer.Start();
    }

    public void StopLive()
    {
        _liveTimer?.Stop();
        _livePids = null;

        _capTimer?.Stop();
        _capPending.Clear();
        _capPaths.Clear();
        _capRevive = null;
        _capSnap = null;
        _capBeforePids.Clear();
    }

    private void OnLiveTick(object? sender, EventArgs e)
    {
        var occupied = Launcher.Occupied(Name);
        int before = Wm.Slots.Count;

        foreach (var w in WindowEnumerator.Enumerate())
        {
            if (Wm.Contains(w.Handle) || occupied.Contains(w.Handle)) continue;
            if (_livePids is not null && !_livePids.Contains(w.Pid)) continue;
            Wm.Slots.Add(WindowManager.FromWindow(w));
        }

        // 只在真的收进窗口时才刷新 —— 以前每 400ms 无条件重建列表，
        // 会让清单一直闪、滚动位置被重置，点都点不准。
        if (Wm.Slots.Count != before)
        {
            if (IsActive)
            {
                Wm.Reflow();
                Launcher.RefreshActiveList();
            }
            else
            {
                Launcher.SaveNow();
            }
        }

        if (DateTime.UtcNow >= _liveUntil)
        {
            _liveTimer?.Stop();
            if (Wm.Slots.Count != before) Launcher.SaveNow();
        }
    }
}
