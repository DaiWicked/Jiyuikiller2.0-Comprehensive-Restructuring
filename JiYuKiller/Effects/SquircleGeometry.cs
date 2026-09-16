using System;
using System.Windows;
using System.Windows.Media;

namespace JiYuKiller.Effects
{
    /// <summary>
    /// 超椭圆圆角（squircle / iOS 连续性圆角）几何生成。
    ///
    /// 算法与常数取自参考实现 COUI 的 <c>SquirclePath.kt</c>（Apache-2.0）：
    /// <code>
    ///   tile   = cornerRadius * 1.2819        // 圆角区尺寸（比圆弧占用更大范围）
    ///   handle = tile * (1 - 0.643)           // 三次贝塞尔控制柄长度
    /// </code>
    /// 与普通圆角矩形相比，它在"直线段 → 拐角"处的曲率是连续的，
    /// 视觉上更顺、更接近苹果那套圆角；普通圆角会有明显的"曲率突变"。
    ///
    /// <paramref name="squircle"/> 传 false 时退化为普通圆角矩形，
    /// 用于设置项里让用户自行对照/回退，无需重新编译。
    /// </summary>
    public static class SquircleGeometry
    {
        /// <summary>圆角区尺寸相对半径的倍数（COUI 默认 1.2819）</summary>
        public const double Extension = 1.2819;

        /// <summary>贝塞尔控制柄比例（COUI 里 SQUIRCLE_CONTROL = 0.643）</summary>
        public const double Control = 0.643;

        /// <summary>
        /// 生成一个圆角矩形/超椭圆的几何。
        /// 返回对象已 Freeze，可安全共享（同时给 Clip 与 Path.Data 用，保证完全对齐）。
        /// </summary>
        public static Geometry Create(double width, double height, double cornerRadius, bool squircle, double extension = Extension)
        {
            if (width <= 0 || height <= 0)
            {
                return Geometry.Empty;
            }

            double cap = Math.Min(width, height) * 0.5;
            double radius = Math.Min(Math.Max(0.0, cornerRadius), cap);

            if (radius <= 0.5)
            {
                var plain = new RectangleGeometry(new Rect(0, 0, width, height));
                plain.Freeze();
                return plain;
            }

            if (!squircle)
            {
                var round = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
                round.Freeze();
                return round;
            }

            // tile 不能超过短边的一半，否则上下/左右两段角会互相重叠
            // extension 越大，拐角区越大、曲线越"软"（COUI 允许 1.0~2.0）
            double ext = Math.Min(Math.Max(extension, 1.0), 2.0);
            double tile = Math.Min(radius * ext, cap);
            double handle = tile * (1.0 - Control);
            double w = width, h = height;

            var figure = new PathFigure
            {
                StartPoint = new Point(tile, 0),
                IsClosed = true,
                IsFilled = true
            };

            // 顺时针：上边 → 右上角 → 右边 → 右下角 → 下边 → 左下角 → 左边 → 左上角
            figure.Segments.Add(new LineSegment(new Point(w - tile, 0), true));
            figure.Segments.Add(new BezierSegment(
                new Point(w - handle, 0), new Point(w, handle), new Point(w, tile), true));

            figure.Segments.Add(new LineSegment(new Point(w, h - tile), true));
            figure.Segments.Add(new BezierSegment(
                new Point(w, h - handle), new Point(w - handle, h), new Point(w - tile, h), true));

            figure.Segments.Add(new LineSegment(new Point(tile, h), true));
            figure.Segments.Add(new BezierSegment(
                new Point(handle, h), new Point(0, h - handle), new Point(0, h - tile), true));

            figure.Segments.Add(new LineSegment(new Point(0, tile), true));
            figure.Segments.Add(new BezierSegment(
                new Point(0, handle), new Point(handle, 0), new Point(tile, 0), true));

            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            geo.Freeze();
            return geo;
        }
    }
}
