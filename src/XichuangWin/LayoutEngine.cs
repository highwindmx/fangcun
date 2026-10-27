using System.Collections.Generic;
using System.Runtime.InteropServices;
using XiChuang.Native;

namespace XichuangWin;

/// <summary>把一批窗口按布局铺到 stage 矩形内。纯移动/缩放真实窗口，绝不 SetParent 嵌入。</summary>
public static class LayoutEngine
{
    /// <summary>计算 count 个窗口在 stage 内的各自矩形。</summary>
    public static List<Win32.RECT> Compute(Win32.RECT stage, LayoutKind layout, int count)
    {
        var rects = new List<Win32.RECT>();
        if (count <= 0) return rects;

        int x = stage.Left, y = stage.Top, w = stage.Width, h = stage.Height;

        switch (layout)
        {
            case LayoutKind.Single:
                // 全部覆盖在同一区域，靠悬停/点击置顶切换
                for (int i = 0; i < count; i++)
                    rects.Add(new Win32.RECT(x, y, x + w, y + h));
                break;

            case LayoutKind.HSplit: // 左右并列（列）
                {
                    int cw = w / count;
                    for (int i = 0; i < count; i++)
                    {
                        int left = x + i * cw;
                        int right = (i == count - 1) ? x + w : left + cw; // 末列吃掉余数，避免缝隙
                        rects.Add(new Win32.RECT(left, y, right, y + h));
                    }
                }
                break;

            case LayoutKind.VSplit: // 上下堆叠（行）
                {
                    int ch = h / count;
                    for (int i = 0; i < count; i++)
                    {
                        int top = y + i * ch;
                        int bottom = (i == count - 1) ? y + h : top + ch;
                        rects.Add(new Win32.RECT(x, top, x + w, bottom));
                    }
                }
                break;

            case LayoutKind.Grid:
                {
                    int cols = (int)System.Math.Ceiling(System.Math.Sqrt(count));
                    int rows = (int)System.Math.Ceiling((double)count / cols);
                    int gw = w / cols, gh = h / rows;
                    for (int i = 0; i < count; i++)
                    {
                        int r = i / cols, c = i % cols;
                        int right = (c == cols - 1) ? x + w : x + (c + 1) * gw;
                        int bottom = (r == rows - 1) ? y + h : y + (r + 1) * gh;
                        rects.Add(new Win32.RECT(x + c * gw, y + r * gh, right, bottom));
                    }
                }
                break;
        }
        return rects;
    }

    /// <summary>把窗口按布局铺开，并返回每个窗口被分配的矩形（供悬停时校正用）。</summary>
    public static List<Win32.RECT> Apply(IEnumerable<nint> hwnds, Win32.RECT stage, LayoutKind layout)
    {
        var list = new List<nint>(hwnds);
        var rects = Compute(stage, layout, list.Count);
        for (int i = 0; i < list.Count; i++)
            PlaceToRect(list[i], rects[i]);
        return rects;
    }

    /// <summary>原子地把窗口放到目标矩形，并解除其最大化/最小化状态。
    /// 用 SetWindowPlacement 一次性设置「还原 + 目标正常矩形」，避免先 SW_RESTORE 再 SetWindowPos
    /// 导致的「跳回窗口记忆中的旧位置/旧尺寸」。
    /// 关键：必须清掉 flags 里的 WPF_RESTORETOMAXIMIZED，否则窗口下次被还原会重新弹回最大化。</summary>
    public static void PlaceToRect(nint h, Win32.RECT r)
    {
        var wp = new Win32.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
        if (Win32.GetWindowPlacement(h, ref wp))
        {
            wp.flags = 0;                                                       // 清 WPF_RESTORETOMAXIMIZED
            wp.showCmd = (uint)Win32.SW_RESTORE;                                // 还原（解除最大化/最小化）
            wp.rcNormalPosition = new Win32.RECT(r.Left, r.Top, r.Right, r.Bottom);
            if (Win32.SetWindowPlacement(h, ref wp) && RectMatches(h, r))
            {
                Win32.ClearTopmost(h);
                return;
            }
        }
        // 兜底：SetWindowPlacement 不被支持，或坐标系换算后落点不对（高 DPI 副屏常见）→ 用物理像素直接摆
        Win32.ShowWindow(h, Win32.SW_RESTORE);
        Win32.SetWindowPos(h, Win32.HWND_TOP, r.Left, r.Top, r.Width, r.Height,
            Win32.SWP_NOZORDER | Win32.SWP_SHOWWINDOW);
        Win32.ClearTopmost(h);
    }

    /// <summary>实际外框是否已落在目标矩形（容差 4px，兼容各家自绘边框的取整差异）。</summary>
    public static bool RectMatches(nint h, Win32.RECT want)
    {
        if (!Win32.GetWindowRect(h, out var cur)) return false;
        return System.Math.Abs(cur.Left - want.Left) <= 4
            && System.Math.Abs(cur.Top - want.Top) <= 4
            && System.Math.Abs(cur.Width - want.Width) <= 4
            && System.Math.Abs(cur.Height - want.Height) <= 4;
    }
}
