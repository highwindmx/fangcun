using Fangcun.Xichuang.Native;

namespace Fangcun.Xichuang;

/// <summary>进程级 DPI 缩放因子（基于系统 DPI）。方寸已设 PerMonitorV2，WPF 走逻辑像素，
/// 而 Win32 的 SetWindowPos 用物理像素——二者通过此因子换算。</summary>
public static class DpiHelper
{
    public static double Scale => Win32.GetDpiForSystem() / 96.0;
}
