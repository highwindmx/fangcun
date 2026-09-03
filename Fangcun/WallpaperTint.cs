using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace Fangcun
{
    // "随桌面自适应"：采样桌面壁纸在围栏区域的【明暗】，生成一块**中性半透明玻璃**写入 Style.BgColor（#AARRGGBB）。
    //
    // 为什么是"玻璃"而不是"壁纸自己的颜色"？（2026-09-02 修正）
    //   旧实现把围栏下方壁纸区域的平均色以高 alpha 填回背景。围栏恰好压在这块壁纸上，
    //   同色(约80%)叠自己 ≈ 同色 → 观感就像一块不透明色板，看不到透明度。
    //   正解：背景取【中性明暗玻璃】（深背景→深玻璃、浅背景→浅玻璃，RGB 与壁纸自身色无关），
    //   alpha 保持在 ~60%（保留 ~40% 透出桌面），于是围栏下的桌面/图标能透过它透出 → 透明感可辨；
    //   又因玻璃明暗跟随壁纸，字体自动黑/白仍保证可读。此即"半透明玻璃"观感。
    //
    // 优先按围栏在主屏的屏幕矩形映射到壁纸对应区域取平均亮度；围栏不在主屏（多屏）则退回整图平均。
    // 壁纸拉伸模式差异会影响精确映射，但对"明暗"观感影响可忽略，故取近似足够。
    public static class WallpaperTint
    {
        // 玻璃体 alpha（AARRGGBB 的 AA）：~60% → 桌面透出 ~40%，透明感清晰又不抢内容。
        private const byte BodyAlpha = 0x99;
        // 栏底 alpha 略高(约69%)且更暗，使标题条在玻璃体上仍可分出一条 header。
        private const byte BarAlpha = 0xB0;
        // 玻璃明暗两端：最浅(亮壁纸)玻璃 ~238，最深(暗壁纸)玻璃 ~24。RGB 中性，不带壁纸自身色相。
        private const int GlassLight = 238, GlassDark = 24;

        // 玻璃色值按区域感知亮度连续映射：亮壁纸→浅玻璃、暗壁纸→深玻璃（与壁纸自身明暗一致，围栏才像"贴"在桌面上）。
        // 旧实现误用 darkFrac=1-lum 再配 GlassLight 端点，把亮壁纸算成深玻璃、暗壁纸算成浅玻璃 → 自适应恒显深色（已修正）。
        // lum=1 亮壁纸→玻璃值趋 GlassLight；lum=0 暗壁纸→趋 GlassDark；中间平滑过渡避免跳变。
        private static int GlassValue(double lum)
        {
            double t = Math.Clamp(lum, 0.0, 1.0); // 0=暗壁纸 … 1=亮壁纸
            t = Math.Pow(t, 0.9);                  // 轻微曲线：中亮壁纸不至于过早发暗
            return (int)Math.Round(GlassDark + (GlassLight - GlassDark) * t);
        }

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

        // 壁纸变更签名：路径 + 文件最后写入时间（UTC ticks）。用于心跳轮询检测壁纸是否被替换
        // （部分第三方动态壁纸/幻灯片不会触发 SystemEvents.UserPreferenceChanged，只能靠签名比对兜底）。
        // 返回 null 表示当前无可用壁纸文件。
        public static string? GetWallpaperSignature()
        {
            var p = GetWallpaperPath();
            if (p == null) return null;
            try { return p + "|" + File.GetLastWriteTimeUtc(p).Ticks; }
            catch { return p; }
        }

        // 计算半透明【玻璃】背景色（#AARRGGBB）；找不到壁纸返回 null（不覆盖用户颜色）。
        // fenceOnPrimary：围栏屏幕矩形（相对主屏原点 0,0，WPF Left/Top 即屏幕坐标）。
        public static string? Compute(System.Windows.Rect? fenceOnPrimary)
            => GlassHex(fenceOnPrimary, isBar: false);

        // 栏底玻璃：与背景同源（明暗跟随壁纸），但略暗、alpha 略高，使标题条在背景上仍可分辨。
        public static string? ComputeBar(System.Windows.Rect? fenceOnPrimary)
            => GlassHex(fenceOnPrimary, isBar: true);

        // 字体颜色按【玻璃灰度】判黑/白（而非原始壁纸亮度）。原因：字体实际叠在中性玻璃上，
        // 而栏底玻璃=v*0.82 比背景玻璃更暗，若按整块区域原始亮度统一判字色，会出现
        // "整块偏亮判黑字、但栏底被压暗后黑字落在暗栏底看不清"的问题。故：
        //   背景玻璃灰度 v → 条目字色（v<128 深底→白字，否则黑字）
        //   栏底玻璃灰度 v*0.82 → 标题/按钮字色（更暗的栏底单独判，深栏底必出白字）
        // 找不到壁纸返回 null（调用方保持当前字色不动）。
        public static (string BodyInk, string BarInk)? ComputeInk(System.Windows.Rect? fenceOnPrimary)
        {
            var luma = SampleLuma(fenceOnPrimary);
            if (luma == null) return null;
            int v = GlassValue(luma.Value);
            int vb = (int)(v * 0.82); // 栏底玻璃比背景更暗（与 ComputeBar 同源）
            return (v < 128 ? "#FFFFFF" : "#000000", vb < 128 ? "#FFFFFF" : "#000000");
        }

        // 区域平均感知亮度（归一化 0-1），供 ComputeInk 判定字色。复用 SampleRegion。
        private static double? SampleLuma(System.Windows.Rect? fenceOnPrimary)
        {
            try
            {
                var path = GetWallpaperPath();
                if (path == null) return null;
                using var bmp = new Bitmap(path);
                var r = SampleRegion(bmp, fenceOnPrimary);
                if (r.A == 0) return null;
                return (0.299 * r.R + 0.587 * r.G + 0.114 * r.B) / 255.0;
            }
            catch { return null; }
        }

        // 中性玻璃：取区域感知亮度 → GlassValue → 等值 RGB（无壁纸色相），配 BodyAlpha/BarAlpha。
        private static string? GlassHex(System.Windows.Rect? fenceOnPrimary, bool isBar)
        {
            try
            {
                var path = GetWallpaperPath();
                if (path == null) return null;
                using var bmp = new Bitmap(path);
                var r = SampleRegion(bmp, fenceOnPrimary);
                if (r.A == 0) return null;
                double lum = (0.299 * r.R + 0.587 * r.G + 0.114 * r.B) / 255.0;
                int v = GlassValue(lum);
                if (isBar) v = (int)(v * 0.82);            // 栏底更暗一条
                byte alpha = isBar ? BarAlpha : BodyAlpha;
                return "#" + alpha.ToString("X2") + v.ToString("X2") + v.ToString("X2") + v.ToString("X2");
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
