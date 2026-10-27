using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Fangcun.Xichuang.Native;

namespace Fangcun.Xichuang;

/// <summary>
/// 栖窗中的一个成员槽位。
/// 活窗口：Hwnd 有效，可聚焦 / 参与排布；
/// 窗口被关闭：Hwnd 归零但槽位保留（清单里灰显 + 角标），点击可用
/// SourcePath（围栏打开的文件，最准）或 ExePath（程序本体）重新打开。
/// 实现 INotifyPropertyChanged 是为了让「窗口刚被关掉 / 重开成功」能立刻反映到清单，
/// 而不必整表重建（重建会闪屏并冲掉滚动位置）。
/// </summary>
public class WindowSlot : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private nint _hwnd;

    /// <summary>窗口句柄；0 = 已关闭 / 尚未出现。</summary>
    public nint Hwnd
    {
        get => _hwnd;
        set
        {
            if (_hwnd == value) return;
            _hwnd = value;
            Raise(nameof(Hwnd));
            Raise(nameof(Alive));       // 存活态由句柄派生，必须一并通知，否则行样式不会变
            Raise(nameof(CanReopen));
        }
    }

    public string ProcessName { get; set; } = "";
    public string ClassName { get; set; } = "";

    private string _title = "";

    /// <summary>窗口标题快照。关闭后保留最后标题，用于显示与重开后匹配。</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            Raise(nameof(Title));
            Raise(nameof(Display));
        }
    }

    /// <summary>围栏打开的原始文件 / 文件夹路径 —— 最可靠的重开线索（能精确回到原文档）。</summary>
    public string? SourcePath { get; set; }

    /// <summary>进程 exe 完整路径 —— 次优线索（重开程序本体，但不一定回到原文档）。</summary>
    public string? ExePath { get; set; }

    private bool _pending;

    /// <summary>已登记但窗口还没出现（刚点围栏、程序正在启动）→ 灰显「打开中…」。</summary>
    public bool Pending
    {
        get => _pending;
        set
        {
            if (_pending == value) return;
            _pending = value;
            Raise(nameof(Pending));
        }
    }

    private ImageSource? _icon;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            Raise(nameof(Icon));
        }
    }

    public bool Alive => Hwnd != nint.Zero && Win32.IsWindow(Hwnd);

    /// <summary>显示名：优先窗口标题；关闭/待开时退到文件名，再退到进程名。</summary>
    public string Display
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title;
            if (!string.IsNullOrWhiteSpace(SourcePath))
            {
                string name = Path.GetFileName(SourcePath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            return ProcessName;
        }
    }

    /// <summary>是否有可用于重开的线索。</summary>
    public bool CanReopen => !Alive && (PathExists(SourcePath) || PathExists(ExePath));

    /// <summary>用已有线索重新打开。返回是否真的发出了打开请求。</summary>
    public bool TryReopen()
    {
        try
        {
            if (PathExists(SourcePath))
            {
                Process.Start(new ProcessStartInfo(SourcePath!) { UseShellExecute = true });
                return true;
            }
            if (PathExists(ExePath))
            {
                Process.Start(new ProcessStartInfo(ExePath!) { UseShellExecute = true });
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool PathExists(string? p)
        => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p));
}
