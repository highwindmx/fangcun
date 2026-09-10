using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Fangcun
{
    /// <summary>
    /// 文件名文本：支持多行省略号（WPF 原生 TextBlock 仅单行 CharacterEllipsis）。
    /// 通过 FormattedText 测量换行点，超出 MaxLines 时二分裁剪并在末尾补 "…"，
    /// 高度固定为"实际显示行数 × 行高"，使布局可预测，便于 ComputeCapacity 精确算容量。
    /// </summary>
    public class NameText : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(NameText),
                new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

        public static readonly DependencyProperty ForegroundProperty =
            DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(NameText),
                new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
        public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

        // 注意：不要从 TextElement.Font*Property.AddOwner —— AddOwner 继承的默认元数据在运行时会触发
        // "默认值类型与 FontFamily 属性的类型不匹配" 的类型初始化器异常。这里直接 Register 自有依赖属性并显式给出正确类型的默认值。
        public static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(nameof(FontFamily), typeof(FontFamily), typeof(NameText),
                new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(NameText),
                new FrameworkPropertyMetadata(11d,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

        public static readonly DependencyProperty FontWeightProperty =
            DependencyProperty.Register(nameof(FontWeight), typeof(FontWeight), typeof(NameText),
                new FrameworkPropertyMetadata(FontWeights.Normal,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment), typeof(NameText),
                new FrameworkPropertyMetadata(TextAlignment.Center, FrameworkPropertyMetadataOptions.AffectsRender));
        public TextAlignment TextAlignment { get => (TextAlignment)GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }

        public static readonly DependencyProperty MaxLinesProperty =
            DependencyProperty.Register(nameof(MaxLines), typeof(int), typeof(NameText),
                new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public int MaxLines { get => (int)GetValue(MaxLinesProperty); set => SetValue(MaxLinesProperty, value); }

        private FormattedText? _render;
        private double _lineH;

        private static int LineCountOf(FormattedText ft)
            => (int)Math.Ceiling(ft.Height / ft.LineHeight);

        private FormattedText MakeFt(string text, double maxWidth)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily ?? SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal),
                FontSize, Foreground, dpi.PixelsPerDip);
            ft.MaxTextWidth = maxWidth;
            ft.TextAlignment = TextAlignment;
            return ft;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            double maxW = constraint.Width;
            if (double.IsInfinity(maxW) || maxW <= 0) maxW = 1000; // 防御：无限宽时按内容宽度测量
            string text = Text ?? "";
            if (text.Length == 0)
            {
                _render = null;
                return new Size(0, 0);
            }

            var ft = MakeFt(text, maxW);
            _lineH = ft.LineHeight;
            int lines = LineCountOf(ft);

            if (lines > MaxLines)
            {
                // 二分找"可放进 MaxLines 行的最大前缀"，末尾补 "…"
                int lo = 0, hi = text.Length, best = 0;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (mid <= 0) { lo = 1; continue; }
                    var cft = MakeFt(text.Substring(0, mid) + "…", maxW);
                    if (LineCountOf(cft) <= MaxLines) { best = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                _render = best > 0 ? MakeFt(text.Substring(0, best) + "…", maxW) : MakeFt("…", maxW);
            }
            else
            {
                _render = ft;
            }

            double shown = Math.Min(MaxLines, Math.Max(1, LineCountOf(_render)));
            return new Size(maxW, shown * _lineH);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_render == null) return;
            dc.DrawText(_render, new Point(0, 0));
        }
    }
}
