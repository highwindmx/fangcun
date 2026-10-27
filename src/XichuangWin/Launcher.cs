using System.Collections.Generic;
using System.Linq;
using XiChuang.Native;

namespace XichuangWin;

/// <summary>栖窗统一入口，由方寸（同一进程）调用。
/// 架构：单例 SidebarWindow（顶端 tab 切换）+ 多个命名栖窗实例共存、互不越界。
/// 持久化：任一变更即写配置快照（名字 + 布局 + 成员窗口身份）；启动时按身份匹配活窗口恢复。</summary>
public static class Launcher
{
    private static SettingsWindow? _settings;
    private static readonly Dictionary<string, XichuangInstance> _instances = new();
    private static SidebarWindow? _sidebar;
    public static string ActiveName { get; private set; } = "";

    public static IReadOnlyDictionary<string, XichuangInstance> Instances => _instances;
    public static XichuangInstance? ActiveInstance => _instances.TryGetValue(ActiveName, out var i) ? i : null;

    /// <summary>方寸启动完成后调用一次：只加载配置。真正的恢复延后到「第一次打开侧栏」——
    /// 因为开机自启时目标程序可能还没起来，此刻匹配不到任何窗口。</summary>
    public static void Initialize()
    {
        XichuangConfig.EnsureLoaded();
        _restorePending = XichuangConfig.Current.AutoRestore;
    }

    private static bool _restorePending;

    /// <summary>首次打开侧栏前，把上次的栖窗按窗口身份恢复出来（只建实例，不动窗口位置）。</summary>
    private static void EnsureRestored()
    {
        if (!_restorePending) return;
        _restorePending = false;
        RestoreSaved();   // 已存在的同名栖窗会被跳过，不会覆盖当前操作
    }

    /// <summary>返回已被其它栖窗占用的窗口句柄集合（指定 name 除外），用于多实例互不越界。</summary>
    public static HashSet<nint> Occupied(string exceptName)
    {
        var set = new HashSet<nint>();
        foreach (var kv in _instances)
            if (kv.Key != exceptName && kv.Value.Wm is not null)
                foreach (var h in kv.Value.Wm.LiveHandles()) set.Add(h);
        return set;
    }

    private static void EnsureSidebar()
    {
        if (_sidebar is null)
        {
            _sidebar = new SidebarWindow();
            _sidebar.Closed += (_, _) => _sidebar = null;
        }
        // 最小化只是「收起」：再显示时先恢复窗口状态（会触发联动 RestoreAll），
        // 否则 Show() 对最小化窗口无效，看起来像点了没反应。
        if (_sidebar.WindowState == System.Windows.WindowState.Minimized)
            _sidebar.WindowState = System.Windows.WindowState.Normal;
        else if (!_sidebar.IsVisible) _sidebar.Show();
    }

    private static bool SidebarShown()
        => _sidebar is not null
           && _sidebar.IsVisible
           && _sidebar.WindowState != System.Windows.WindowState.Minimized;

    /// <summary>侧栏当前是否真的露在屏幕上（最小化/未创建都算 false）。
    /// 实例用它判断「能不能移动窗口」——侧栏收起时若还 Reflow，会把刚最小化的窗口又弹出来。</summary>
    public static bool SidebarVisible => SidebarShown();

    private static XichuangInstance GetOrCreate(string name)
    {
        if (!_instances.TryGetValue(name, out var inst))
        {
            inst = new XichuangInstance(name);
            _instances[name] = inst;
        }
        return inst;
    }

    /// <summary>托盘「栖窗（集）」：把当前所有「未归入其它栖窗」的窗口一次性收进一个新建工作区并显示。
    /// 已存在的栖窗原样保留，互不越界（它们的窗口不会被抢走）。</summary>
    public static void GatherIntoNew()
    {
        EnsureRestored();
        var name = NextName();
        var inst = GetOrCreate(name);
        inst.HasCollected = true;      // 新建即视为已处理，避免 Collect 之外的路径再触发通用归集
        EnsureSidebar();
        SetActive(name);               // 先激活：Collect→Gather→Reflow 才会真的摆放窗口
        inst.Collect(Occupied(name));  // Gather 内部会清空本实例槽位并重排
        SaveNow();
    }

    /// <summary>托盘「栖窗（复）」：按上次保存的配置恢复栖窗并重新显示。
    /// 已存在的同名栖窗不覆盖（避免冲掉当前操作），只补回缺失的；
    /// 恢复后只把「活动栖窗」的窗口摆回布局位置（非活动栖窗保持收起，避免互相压叠）。</summary>
    public static void RestorePrevious()
    {
        _restorePending = false;       // 手动恢复过，取消「首次打开侧栏」的延迟恢复
        RestoreSaved();

        if (_instances.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "还没有保存过的栖窗配置。\n先用「栖窗（集）」或方寸围栏建一个，之后会自动记住。",
                "栖窗", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        ShowSidebar();
    }

    /// <summary>显示侧栏（不归集新窗口），并把活动栖窗的窗口摆到布局位置。</summary>
    public static void ShowSidebar()
    {
        EnsureRestored();
        EnsureSidebar();
        if (string.IsNullOrEmpty(ActiveName) && _instances.Count > 0)
            ActiveName = _instances.Keys.First();

        if (ActiveInstance is { } inst)
        {
            inst.RestoreAll();
            inst.Wm.Reflow();
        }
        _sidebar?.SyncTabs();
        _sidebar?.RefreshActiveList();
    }

    /// <summary>激活/创建命名栖窗（方寸围栏入口）。
    /// paths 非空 → 每次都「打开这些文件/文件夹 + 只归集这次打开的窗口」（点围栏就是这个语义，
    /// 所以不能因为「这个栖窗已经存在」就跳过）；paths 为空 → 首次才做通用归集。</summary>
    public static void ActivateWorkspace(string name, IEnumerable<string>? paths = null)
    {
        EnsureRestored();
        var inst = GetOrCreate(name);
        EnsureSidebar();
        SetActive(name);

        if (paths is not null)
        {
            inst.GatherFromPaths(paths);   // 每次点围栏都重新打开并归集
        }
        else if (!inst.HasCollected)
        {
            inst.Collect(Occupied(name));
        }

        inst.HasCollected = true;
        SaveNow();
    }

    /// <summary>下一个不重复的自动名（栖窗 1、栖窗 2…）。</summary>
    private static string NextName()
    {
        int n = 1;
        while (_instances.ContainsKey($"栖窗 {n}")) n++;
        return $"栖窗 {n}";
    }

    /// <summary>新建一个自动命名的空栖窗（侧栏 tab 条的「＋」入口）。
    /// 新建后保持为空，不自动归集任何窗口——由用户通过侧栏「加窗 ▾」手动收集（#5）。</summary>
    public static void NewInstance()
    {
        var name = NextName();
        var inst = GetOrCreate(name);
        inst.HasCollected = true; // 标记已处理：新建即空，不触发自动归集
        EnsureSidebar();
        SetActive(name);
        SaveNow();
    }

    /// <summary>切换活动栖窗：旧实例收起（最小化其窗口），新实例恢复并排布。</summary>
    public static void SetActive(string name)
    {
        if (!_instances.ContainsKey(name)) return;
        if (ActiveName != name)
        {
            if (ActiveName != "" && _instances.TryGetValue(ActiveName, out var prev))
            {
                // 不在这里 StopLive：上一个栖窗可能还在等「打开得很慢」的窗口，
                // 中断捕获就会丢窗口。后台实例只记账不搬窗口，所以留着捕获是安全的。
                prev.MinimizeAll();
            }
            ActiveName = name;
            var cur = _instances[name];
            cur.RestoreAll();
            cur.Wm.Reflow();
            cur.Wm.RaiseAll(); // 切 tab 时把这批窗口提到普通层级最前（不 topmost，不影响之后新开的窗口）
        }
        _sidebar?.SyncTabs();
        _sidebar?.RefreshActiveList();
    }

    /// <summary>重命名栖窗（tab 右键）。重名或与旧名相同则忽略。</summary>
    public static void RenameInstance(string oldName, string newName)
    {
        newName = (newName ?? "").Trim();
        if (string.IsNullOrEmpty(newName) || newName == oldName || _instances.ContainsKey(newName)) return;
        if (!_instances.TryGetValue(oldName, out var inst)) return;
        _instances.Remove(oldName);
        inst.Name = newName;
        inst.Wm.WorkspaceName = newName;
        _instances[newName] = inst;
        if (ActiveName == oldName) ActiveName = newName;
        _sidebar?.SyncTabs();
        SaveNow();
    }

    /// <summary>删除栖窗：释放其窗口管理（恢复正常浮动，不关闭窗口本身），并切换活动实例。</summary>
    public static void DeleteInstance(string name)
    {
        if (!_instances.TryGetValue(name, out var inst)) return;
        inst.StopLive();
        inst.RestoreAll(); // 释放：窗口恢复正常浮动
        foreach (var h in inst.Wm.LiveHandles()) if (Win32.IsWindow(h)) Win32.ClearTopmost(h);
        _instances.Remove(name);

        if (ActiveName == name)
        {
            ActiveName = _instances.Keys.FirstOrDefault() ?? "";
            if (ActiveName != "" && _instances.TryGetValue(ActiveName, out var cur))
            {
                cur.RestoreAll();
                cur.Wm.Reflow();
            }
        }
        if (_instances.Count == 0) HideSidebar();
        _sidebar?.SyncTabs();
        _sidebar?.RefreshActiveList();
        SaveNow();
    }

    /// <summary>隐藏侧栏（最小化）：联动最小化活动实例的窗口。</summary>
    public static void HideSidebar()
    {
        if (_sidebar is not null) _sidebar.WindowState = System.Windows.WindowState.Minimized;
    }

    /// <summary>设置变更后重新定位侧栏并重新排布活动实例窗口。</summary>
    public static void Relayout()
    {
        _sidebar?.ApplyPosition();
        ActiveInstance?.Wm.Reflow();
    }

    /// <summary>刷新侧栏中活动实例的窗口列表（供实例实时归集回调）。</summary>
    public static void RefreshActiveList() => _sidebar?.RefreshActiveList();

    /// <summary>打开设置窗。</summary>
    public static void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        else if (_settings.IsVisible) _settings.Activate();
        else _settings.Show();
    }

    // ---------------- 持久化（#7）----------------

    /// <summary>把当前所有栖窗（名字 + 布局 + 成员窗口身份 + 重开线索）写入配置。任一变更后调用。
    /// 已关闭的槽位也保存 —— 否则下次启动就丢了「可点击重开」那一行。</summary>
    public static void SaveNow()
    {
        var cfg = XichuangConfig.Current;
        var saved = new List<SavedInstance>();
        foreach (var inst in _instances.Values)
        {
            if (string.IsNullOrWhiteSpace(inst.Name)) continue;
            var si = new SavedInstance { Name = inst.Name, Layout = inst.Wm.Layout };

            foreach (var slot in inst.Wm.Slots)
            {
                if (slot.Alive)
                {
                    var info = WindowEnumerator.Describe(slot.Hwnd);
                    if (info is not null)
                    {
                        slot.ProcessName = info.ProcessName;
                        slot.ClassName = info.ClassName;
                        slot.Title = info.Title;
                    }
                }
                if (string.IsNullOrWhiteSpace(slot.ProcessName) && string.IsNullOrWhiteSpace(slot.SourcePath)) continue;

                si.Windows.Add(new SavedWindow
                {
                    Process = slot.ProcessName,
                    Class = slot.ClassName,
                    Title = slot.Title,
                    SourcePath = slot.SourcePath
                });
            }
            saved.Add(si);
        }
        cfg.SavedInstances = saved;
        cfg.Save();
    }

    /// <summary>按上次快照恢复栖窗：只认窗口身份，不主动移动窗口；
    /// 真正的铺排发生在用户打开侧栏（ShowSidebar）时，避免开机就搅动桌面。</summary>
    private static void RestoreSaved()
    {
        var cfg = XichuangConfig.Current;
        if (cfg.SavedInstances is null || cfg.SavedInstances.Count == 0) return;

        var live = WindowEnumerator.Enumerate().ToArray();
        // 已被现有栖窗占用的窗口不能再分给恢复出来的实例（否则一个窗口会同时属于两个栖窗）
        var used = new HashSet<nint>(Occupied(""));

        foreach (var s in cfg.SavedInstances)
        {
            if (string.IsNullOrWhiteSpace(s.Name) || _instances.ContainsKey(s.Name)) continue;
            var inst = new XichuangInstance(s.Name) { HasCollected = true };
            inst.Wm.Layout = s.Layout;

            // 同一进程多窗口时，标题可能已变（换文档/新标签页），所以按「进程+类」先粗匹配，
            // 再用标题优先命中，最后才退到「该进程任意窗口」。
            foreach (var sw in s.Windows)
            {
                nint h = MatchLive(live, sw, used);
                var slot = new WindowSlot
                {
                    SourcePath = sw.SourcePath,
                    ProcessName = sw.Process,
                    ClassName = sw.Class,
                    Title = sw.Title
                };

                if (h != nint.Zero)
                {
                    var info = WindowEnumerator.Describe(h);
                    if (info is not null) WindowManager.Fill(slot, info, sw.SourcePath);
                    else slot.Hwnd = h;
                    used.Add(h);
                }
                // 没匹配到活窗口 → 保留成「已关闭」槽位（Hwnd=0）：清单里灰显，点击可重开。
                inst.Wm.Slots.Add(slot);
            }
            _instances[s.Name] = inst;
        }

        if (string.IsNullOrEmpty(ActiveName) && _instances.Count > 0)
            ActiveName = _instances.Keys.First();
    }

    private static nint MatchLive(WindowInfo[] live, SavedWindow sw, HashSet<nint> used)
    {
        // 1) 进程 + 类 + 标题全等
        foreach (var w in live)
            if (!used.Contains(w.Handle)
                && Eq(w.ProcessName, sw.Process) && Eq(w.ClassName, sw.Class) && Eq(w.Title, sw.Title))
                return w.Handle;

        // 2) 进程 + 类（标题变了）
        foreach (var w in live)
            if (!used.Contains(w.Handle)
                && Eq(w.ProcessName, sw.Process) && Eq(w.ClassName, sw.Class))
                return w.Handle;

        // 3) 仅有进程名（类名在某些 app 升级后会变）
        foreach (var w in live)
            if (!used.Contains(w.Handle) && Eq(w.ProcessName, sw.Process))
                return w.Handle;

        return nint.Zero;
    }

    private static bool Eq(string a, string b)
        => !string.IsNullOrEmpty(b) && string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>退出时清理所有栖窗。</summary>
    public static void Shutdown()
    {
        try { SaveNow(); } catch { }
        foreach (var inst in _instances.Values) inst.StopLive();
        _instances.Clear();
        ActiveName = "";
        _settings?.Close();
        _settings = null;
        _sidebar?.Close();
        _sidebar = null;
    }
}
