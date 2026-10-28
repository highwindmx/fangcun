using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fangcun.Xichuang;

/// <summary>悬停/点击触发模式。</summary>
public enum TriggerMode { Hover, Click }

/// <summary>分屏布局：单窗覆盖 / 左右(列) / 上下(行) / 网格。</summary>
public enum LayoutKind { Single, HSplit, VSplit, Grid }

/// <summary>侧栏停靠边：左 / 右 / 上 / 下。</summary>
public enum DockEdge { Left, Right, Top, Bottom }

public class WorkspaceConfig
{
    public string Name { get; set; } = "默认";
    public LayoutKind Layout { get; set; } = LayoutKind.Single;
}

/// <summary>持久化的窗口身份：句柄重启后会变，所以用「进程名 + 类名 + 标题」三元组匹配。
/// 类名比标题稳定（标题会随文档/页面变化），标题用于同进程多窗口时区分。</summary>
public class SavedWindow
{
    public string Process { get; set; } = "";
    public string Class { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>围栏打开的文件/文件夹路径。有了它，窗口关掉后仍能在清单里点击重新打开（最准的线索）。</summary>
    public string? SourcePath { get; set; }

    /// <summary>程序 exe 完整路径。手动「加窗」进来的窗口没有 SourcePath，
    /// 全靠它作为重开线索；不存的话，重启后这些槽位就点不开了（点了只会被当作移出）。</summary>
    public string? ExePath { get; set; }
}

/// <summary>一个栖窗的持久化快照。</summary>
public class SavedInstance
{
    public string Name { get; set; } = "";
    public LayoutKind Layout { get; set; } = LayoutKind.Single;
    public List<SavedWindow> Windows { get; set; } = new();
}

/// <summary>栖窗配置：全局偏好 + 工作区列表 + 栖窗快照。独立持久化到 %LOCALAPPDATA%\Xichuang，不触碰方寸 config。</summary>
public class XichuangConfig
{
    public static XichuangConfig Current { get; private set; } = new();

    /// <summary>列表项触发方式：悬停 or 单击。</summary>
    public TriggerMode Trigger { get; set; } = TriggerMode.Hover;

    /// <summary>悬停延迟（毫秒）。</summary>
    public int HoverDelayMs { get; set; } = 350;

    /// <summary>最小化模式：true=只显示聚焦窗口、其余最小化；false=覆盖式（窗口都可见并排布）。</summary>
    public bool MinMode { get; set; } = false;

    /// <summary>侧栏厚度（逻辑像素，96 DPI 基准）：左/右停靠时为宽度，上/下停靠时为高度。
    /// 默认 220 —— 刚好放下三个固定宽度的 tab。</summary>
    public int SidebarWidth { get; set; } = 220;

    /// <summary>侧栏停靠边：左 / 右 / 上 / 下。默认左。</summary>
    public DockEdge Edge { get; set; } = DockEdge.Left;

    /// <summary>侧栏所在显示器序号：0 = 主屏（始终排第一），1/2… = 副屏。</summary>
    public int SidebarMonitor { get; set; } = 0;

    /// <summary>侧栏自动隐藏（鼠标离开后收起）。v1 占位，默认关。</summary>
    public bool AutoHide { get; set; } = false;

    /// <summary>自动隐藏折叠后，把窗口排布区扩到整个工作区（左/右停靠时即横向铺满全屏）。</summary>
    public bool ExpandOnAutoHide { get; set; } = false;

    /// <summary>启动时是否按上次快照恢复栖窗（只认窗口身份，不主动移动窗口，打开侧栏时才铺排）。</summary>
    public bool AutoRestore { get; set; } = true;

    public List<WorkspaceConfig> Workspaces { get; set; } = new() { new WorkspaceConfig() };

    /// <summary>上次退出时的栖窗快照（名字 + 布局 + 成员窗口身份）。</summary>
    public List<SavedInstance> SavedInstances { get; set; } = new();

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xichuang", "xichuang.json");

    public static void EnsureLoaded()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<XichuangConfig>(File.ReadAllText(FilePath), Options());
                if (cfg is not null) Current = cfg;
            }
        }
        catch { }
        if (Current.Workspaces.Count == 0) Current.Workspaces.Add(new WorkspaceConfig());
        Current.SavedInstances ??= new List<SavedInstance>();
        // 兼容旧配置：200 是老版本默认值（不是用户主动选的），迁移到新默认 220
        if (Current.SidebarWidth is < 160 or 200) Current.SidebarWidth = 220;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options()));
        }
        catch { }
    }

    private static JsonSerializerOptions Options() => new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
