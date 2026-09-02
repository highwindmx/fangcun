using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace Fangcun
{
    // "随桌面自适应"：采样桌面壁纸平均色，生成半透明背景写入 Style.BgColor（#AARRGGBB）。
    // 优先按围栏在主屏的屏幕矩形映射到壁纸对应区域取平均；围栏不在主屏（多屏）则退回整图平均。
    // 壁纸拉伸模式差异会影响精确映射，但对"半透明整体色调"观感影响可忽略，故取近似足够。
    public static class WallpaperTint
    {
        // 采样用的半透明背景 alpha（AARRGGBB 的 AA）。默认 0xCC ≈ 80% 不透明，色感清晰又不遮桌面
        private const byte Alpha = 0xCC;

        // 读取当前桌面壁纸文件路径：注册表 WallPaper 为主，SystemParametersInfo 兜底。
        public static string? GetWallpaperPath()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                var p = k?.GetValue("WallPaper") as string;
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
            catch { }
            try
            {
                var sb = new System.Text.StringBuilder(512);
                NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0);
                string s = sb.ToString();
                if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5);
                if (!string.IsNullOrEmpty(s) && File.Exists(s)) return s;
            }
            catch { }
            return null;
        }

        // 计算半透明背景色字符串（#AARRGGBB）；找不到壁纸返回 null（不覆盖用户颜色）。
        // fenceOnPrimary：围栏屏幕矩形（相对主屏原点 0,0，WPF Left/Top 即屏幕坐标）。
        public static string? Compute(System.Windows.Rect? fenceOnPrimary)
            => SampleToHex(fenceOnPrimary, darken: false, Alpha);

        // 栏底色：与背景同源，但压暗一点，使标题条在背景上仍可分辨。
        public static string? ComputeBar(System.Windows.Rect? fenceOnPrimary)
            => SampleToHex(fenceOnPrimary, darken: true, Alpha);

        // 判断围栏背景底层壁纸是否偏暗（感知亮度 Y=0.299R+0.587G+0.114B < 128 → 深色）。
        // 供"随桌面自适应"开启时自动把条目/标题字体切成白(深底)或黑(浅底)，保证可读。
        // 找不到壁纸返回 null（调用方保持当前字色不动）。
        public static bool? ComputeIsDark(System.Windows.Rect? fenceOnPrimary)
        {
            try
            {
                var path = GetWallpaperPath();
                if (path == null) return null;
                using var bmp = new Bitmap(path);
                var r = SampleRegion(bmp, fenceOnPrimary);
                if (r.A == 0) return null;
                double y = 0.299 * r.R + 0.587 * r.G + 0.114 * r.B;
                return y < 128.0;
            }
            catch { return null; }
        }

        private static string? SampleToHex(System.Windows.Rect? fenceOnPrimary, bool darken, byte alpha)
        {
            try
            {
                var path = GetWallpaperPath();
                if (path == null) return null;
                using var bmp = new Bitmap(path);
                var r = SampleRegion(bmp, fenceOnPrimary);
                if (r.A == 0) return null;
                int rr = r.R, gg = r.G, bb = r.B;
                if (darken) // 压暗约 30%
                {
                    rr = (int)(rr * 0.7); gg = (int)(gg * 0.7); bb = (int)(bb * 0.7);
                }
                return "#" + alpha.ToString("X2") + rr.ToString("X2") + gg.ToString("X2") + bb.ToString("X2");
            }
            catch { return null; }
        }

        private static Color SampleRegion(Bitmap bmp, System.Windows.Rect? fenceOnPrimary)
        {
            int bw = bmp.Width, bh = bmp.Height;
            if (bw <= 0 || bh <= 0) return Color.Transparent;

            // 壁纸铺满主屏全屏 → 用主屏全屏尺寸做比例映射
            double pw = SystemParameters.PrimaryScreenWidth, ph = SystemParameters.PrimaryScreenHeight;
            int x0 = 0, y0 = 0, x1 = bw, y1 = bh;

            if (fenceOnPrimary.HasValue && pw > 0 && ph > 0)
            {
                var f = fenceOnPrimary.Value;
                // 仅当围栏与主屏交叠才做区域映射，否则整图平均
                if (f.X < pw && f.Y < ph && f.X + f.Width > 0 && f.Y + f.Height > 0)
                {
                    double sx = bw / pw, sy = bh / ph;
                    double fx0 = Math.Max(0, f.X), fy0 = Math.Max(0, f.Y);
                    double fx1 = Math.Min(pw, f.X + f.Width), fy1 = Math.Min(ph, f.Y + f.Height);
                    int a = (int)Math.Floor(fx0 * sx), b = (int)Math.Floor(fy0 * sy);
                    int c = (int)Math.Ceiling(fx1 * sx), d = (int)Math.Ceiling(fy1 * sy);
                    x0 = Math.Clamp(a, 0, bw - 1);
                    y0 = Math.Clamp(b, 0, bh - 1);
                    x1 = Math.Clamp(c, 1, bw);
                    y1 = Math.Clamp(d, 1, bh);
                    if (x1 <= x0 || y1 <= y0) { x0 = 0; y0 = 0; x1 = bw; y1 = bh; }
                }
            }

            int w = x1 - x0, h = y1 - y0;
            var rect = new System.Drawing.Rectangle(x0, y0, w, h);
            byte[]? buf = null;
            BitmapData? data = null;
            try
            {
                data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                int stride = data.Stride;
                buf = new byte[stride * h];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);

                long sr = 0, sg = 0, sb = 0, n = 0;
                int total = w * h;
                int step = Math.Max(1, total / 20000); // 抽样，至多 ~2 万像素
                int idx = 0;
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++, idx++)
                    {
                        if (idx % step != 0) continue;
                        int p = row + x * 3;
                        sb += buf[p];      // B
                        sg += buf[p + 1];  // G
                        sr += buf[p + 2];  // R
                        n++;
                    }
                }
                if (n == 0) return Color.Transparent;
                return Color.FromArgb((int)(sr / n), (int)(sg / n), (int)(sb / n));
            }
            catch { return Color.Transparent; }
            finally { if (data != null) bmp.UnlockBits(data); }
        }
    }
}
