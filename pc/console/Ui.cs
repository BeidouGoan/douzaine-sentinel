// ===========================================================================
//  Ui.cs — 工位哨兵 PC 端自绘控件库
//
//  ★ 为什么不用 WinForms 原生控件：
//    1) 原生 CheckBox/Button 在深色主题下样式不可控（系统蓝底、默认虚线焦点框）
//    2) 圆角、状态光晕、图标这些设计细节原生控件做不到
//    3) 自绘后可以保证深浅色、对比度、DPI 缩放完全一致
//
//  ★ 所有控件遵循同一套规范：
//    - 悬停态：SurfaceVariant → SurfaceHover
//    - 焦点态：2px 品牌色内描边（键盘可达，WCAG 2.4.7）
//    - 禁用态：前景降到 OnSurfaceMuted，背景保持
//    - 圆角统一 6px（控件）/ 10px（卡片）
// ===========================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SentinelConsole
{
    // -----------------------------------------------------------------------
    //  绘制辅助
    // -----------------------------------------------------------------------
    internal static class Gfx
    {
        /// <summary>生成圆角矩形路径。</summary>
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); return p; }
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius,
            int topLeft, int topRight, int bottomRight, int bottomLeft)
        {
            var p = new GraphicsPath();
            // ★ 半径为 0 的拐角：AddArc 会收到 0 尺寸矩形，GDI+ 在部分环境
            //   抛“参数无效”。退化为直线到该拐角点（0 尺寸圆弧的起点正是
            //   矩形角），路径仍连续、视觉是直角。
            AddCorner(p, r.X, r.Y, topLeft * 2, topLeft * 2, 180, 90);
            AddCorner(p, r.Right - topRight * 2, r.Y, topRight * 2, topRight * 2, 270, 90);
            AddCorner(p, r.Right - bottomRight * 2, r.Bottom - bottomRight * 2, bottomRight * 2, bottomRight * 2, 0, 90);
            AddCorner(p, r.X, r.Bottom - bottomLeft * 2, bottomLeft * 2, bottomLeft * 2, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static void AddCorner(GraphicsPath p, int x, int y, int w, int h,
                                      int startAngle, int sweepAngle)
        {
            if (w <= 0 || h <= 0)
                p.AddLine(x, y, x, y);   // 0 尺寸圆弧 → 退化为拐角点
            else
                p.AddArc(x, y, w, h, startAngle, sweepAngle);
        }

        /// <summary>
        /// 带光晕的状态点。
        /// 光晕用三层递减透明度的同心圆模拟，模拟 CSS 的 box-shadow。
        /// </summary>
        public static void GlowDot(Graphics g, Point center, int radius,
                                   Color core, Color glow)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 3; i >= 1; i--)
            {
                int r = radius + i * 5;
                using (var br = new SolidBrush(Color.FromArgb(28, glow)))
                using (var path = RoundRect(
                    new Rectangle(center.X - r, center.Y - r, r * 2, r * 2), r))
                    g.FillPath(br, path);
            }
            using (var br = new SolidBrush(core))
            using (var path = RoundRect(
                new Rectangle(center.X - radius, center.Y - radius, radius * 2, radius * 2), radius))
                g.FillPath(br, path);
        }
    }

    // -----------------------------------------------------------------------
    //  卡片容器
    // -----------------------------------------------------------------------
    internal class Card : Panel
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public bool ShowTitle { get; set; }

        /// <summary>
        /// 卡片内容容器。
        ///
        /// ★ 为什么内容要用 TableLayoutPanel 而不是直接往 Card 里塞控件：
        ///   之前用「Anchor + DisplayRectangle 手动定位」，踩了两个坑：
        ///     1) Anchor 在控件加入父容器【之前】设置时，Width 会算错
        ///        （实测分段控件 Width=596，真实可用只有 398，
        ///          第三段画到卡片外被裁掉）
        ///     2) DisplayRectangle 在 OnLayout 时刻宽度还是 0，控件直接消失
        ///   改成 TableLayoutPanel + Dock=Fill 后，宽度由布局系统保证，
        ///   不再需要手动算坐标。
        /// </summary>
        public TableLayoutPanel Content { get; private set; }

        private int _pad = Theme.Space4;

        public Card()
        {
            ShowTitle = true;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            Padding = new Padding(_pad, ShowTitle ? 40 : _pad, _pad, _pad);

            Content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 1,
                BackColor = Theme.Surface,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            Content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(Content);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var path = Gfx.RoundRect(
                new Rectangle(0, 0, Width - 1, Height - 1), Theme.RadiusCard))
            {
                g.DrawPath(new Pen(Theme.Outline), path);
            }

            if (!ShowTitle || string.IsNullOrEmpty(Title)) return;

            var titleFont = new Font(Theme.FontFamily, 9f, FontStyle.Bold);
            TextRenderer.DrawText(g, Title, titleFont,
                new Rectangle(_pad, Theme.Space3, Width - _pad * 2, 20),
                Theme.OnSurfaceMuted,
                TextFormatFlags.Left | TextFormatFlags.NoPadding);

            using (var pen = new Pen(Theme.Outline))
            {
                g.DrawLine(pen, _pad, Theme.Space3 + 20, Width - _pad, Theme.Space3 + 20);
            }
        }
    }

    // -----------------------------------------------------------------------
    //  扁平按钮
    // -----------------------------------------------------------------------
    internal class FlatButton : Control
    {
        public enum Kind { Primary, Ghost, Warning }

        public Kind ButtonKind { get; set; }
        public int Radius { get; set; }
        private bool _hover, _down;

        public FlatButton()
        {
            ButtonKind = Kind.Ghost;
            Radius = Theme.RadiusControl;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ForeColor = Theme.OnSurface;
            Font = new Font(Theme.FontFamily, 9f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            Height = 34;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);

            Color bg, fg;
            switch (ButtonKind)
            {
                case Kind.Primary:
                    bg = _down ? Theme.PrimaryHover : (_hover ? Theme.PrimaryHover : Theme.Primary);
                    fg = Theme.OnPrimary;
                    break;
                case Kind.Warning:
                    bg = _hover ? Color.FromArgb(38, 251, 191, 36) : Color.FromArgb(24, 251, 191, 36);
                    fg = Theme.Uncertain;
                    break;
                default:
                    bg = _hover ? Theme.SurfaceHover : Theme.SurfaceVariant;
                    fg = Theme.OnSurface;
                    break;
            }
            if (!Enabled) { fg = Theme.OnSurfaceMuted; }

            using (var path = Gfx.RoundRect(r, Radius))
            using (var br = new SolidBrush(bg))
                g.FillPath(br, path);

            // 焦点环（键盘可达）
            if (Focused)
            {
                using (var path = Gfx.RoundRect(r, Radius))
                using (var pen = new Pen(Theme.BrandBlue, 2f))
                    g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, Text, Font, r, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    // -----------------------------------------------------------------------
    //  方框勾选框（自绘，可键盘操作）
    // -----------------------------------------------------------------------
    internal class FlatCheck : Control
    {
        private bool _checked;

        /// <summary>选中状态。</summary>
        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        /// <summary>选中状态变化时触发。</summary>
        public event EventHandler CheckedChanged;

        public FlatCheck()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ForeColor = Theme.OnSurface;
            Font = new Font(Theme.FontFamily, 9f);
            Cursor = Cursors.Hand;
            Height = 26;
            // ★ Margin=0：Dock=Fill 时占满整个单元格，
            //   默认 Margin(3,3,3,3) 会让控件矮 6px，文字被裁掉上半截。
            Margin = new Padding(0);
            TabStop = true;
        }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space) { Checked = !Checked; return true; }
            return base.IsInputKey(keyData);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);

            int box = 18;
            int by = (Height - box) / 2;
            var br = new Rectangle(0, by, box, box);

            using (var path = Gfx.RoundRect(br, 4))
            {
                if (Checked)
                {
                    using (var b = new SolidBrush(Theme.Primary)) g.FillPath(b, path);
                }
                else
                {
                    using (var b = new SolidBrush(Theme.SurfaceVariant)) g.FillPath(b, path);
                    using (var p = new Pen(Theme.OutlineStrong)) g.DrawPath(p, path);
                }
            }

            if (Checked)
            {
                using (var p = new Pen(Color.White, 2f))
                {
                    p.StartCap = p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawLines(p, new[]
                    {
                        new PointF(br.X + 4, br.Y + 9),
                        new PointF(br.X + 7.5f, br.Y + 12.5f),
                        new PointF(br.X + 13.5f, br.Y + 5.5f)
                    });
                }
            }

            if (Focused)
            {
                using (var path = Gfx.RoundRect(new Rectangle(-2, by - 2, box + 4, box + 4), 6))
                using (var p = new Pen(Theme.BrandBlue, 2f))
                    g.DrawPath(p, path);
            }

            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(box + 10, 0, Width - box - 10, Height),
                Enabled ? ForeColor : Theme.OnSurfaceMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    // -----------------------------------------------------------------------
    //  单选组（触发条件用）
    // -----------------------------------------------------------------------
    internal class SegmentedControl : Control
    {
        private string[] _items = new string[0];
        private int _selected;

        public string[] Items
        {
            get { return _items; }
            set { _items = value ?? new string[0]; Height = 32; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set { _selected = Math.Max(0, Math.Min(value, _items.Length - 1)); Invalidate(); }
        }

        public event EventHandler SelectedChanged;

        public SegmentedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Height = 32;
        }

        private int SegWidth { get { return _items.Length == 0 ? 0 : Width / _items.Length; } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_items.Length == 0) return;
            int i = e.X / Math.Max(1, SegWidth);
            if (i >= 0 && i < _items.Length && i != _selected)
            {
                _selected = i;
                Invalidate();
                var h = SelectedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);

            if (_items.Length == 0) return;
            int sw = SegWidth;

            // 底槽
            using (var path = Gfx.RoundRect(
                new Rectangle(0, 0, Width - 1, Height - 1), Theme.RadiusControl))
            using (var b = new SolidBrush(Theme.SurfaceVariant))
                g.FillPath(b, path);

            for (int i = 0; i < _items.Length; i++)
            {
                var r = new Rectangle(i * sw + 2, 2, sw - 4, Height - 5);
                if (i == _selected)
                {
                    using (var path = Gfx.RoundRect(r, Theme.RadiusControl - 1))
                    using (var b = new SolidBrush(Theme.Primary))
                        g.FillPath(b, path);
                }

                TextRenderer.DrawText(g, _items[i], Font, r,
                    i == _selected ? Theme.OnPrimary : Theme.OnSurfaceVariant,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // -----------------------------------------------------------------------
    //  带下划线的数值输入（比 NumericUpDown 更可控）
    // -----------------------------------------------------------------------
    internal class NumberField : Control
    {
        private readonly TextBox _box;

        public int Value
        {
            get
            {
                int v;
                return int.TryParse(_box.Text, out v) ? v : 0;
            }
            set { _box.Text = value.ToString(); }
        }

        public int Minimum { get; set; }
        public int Maximum { get; set; }

        public event EventHandler ValueChanged;

        public NumberField()
        {
SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            Minimum = 0;
            Maximum = 999;
            Height = 30;
            Width = 84;
            _box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.SurfaceVariant,
                ForeColor = Theme.OnSurface,
                Font = new Font(Theme.FontMono, 10f),
                TextAlign = HorizontalAlignment.Center,
                Location = new Point(0, 6),
                Width = 84
            };
            _box.TextChanged += (s, e) =>
            {
                int v = Value;
                if (v < Minimum) { _box.Text = Minimum.ToString(); return; }
                if (v > Maximum) { _box.Text = Maximum.ToString(); return; }
                var h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            };
            Controls.Add(_box);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);
            using (var path = Gfx.RoundRect(
                new Rectangle(0, 0, Width - 1, Height - 1), Theme.RadiusControl))
            using (var p = new Pen(Theme.Outline))
                g.DrawPath(p, path);
        }

        protected override void OnGotFocus(EventArgs e) { _box.Focus(); base.OnGotFocus(e); }
    }

    // -----------------------------------------------------------------------
    //  单行文本输入（命令输入框）
    // -----------------------------------------------------------------------
    internal class LineField : Control
    {
        private readonly TextBox _box;

        public string Text2 { get { return _box.Text; } set { _box.Text = value; } }

        public string Placeholder { get; set; }

        public LineField()
        {
SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            Placeholder = "";
            Height = 32;
            _box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.SurfaceVariant,
                ForeColor = Theme.OnSurface,
                Font = new Font(Theme.FontMono, 9.5f),
                Location = new Point(10, 7),
                // ★ 宽度跟着父容器走，否则长命令会被裁
                //   （之前固定宽度，长命令只剩尾部几个字符）
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Resize += (s, e) => _box.Width = Math.Max(20, Width - 20);
            // ★ 必须转发成控件自己的 TextChanged：
            //   外部订阅的是 LineField.TextChanged（继承自 Control），
            //   光订阅内部 TextBox 的话，外面永远收不到——
            //   表现就是「改了执行命令/手机地址，保存按钮不出 * 号」。
            _box.TextChanged += (s, e) => { Invalidate(); OnTextChanged(EventArgs.Empty); };
            Controls.Add(_box);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);
            using (var path = Gfx.RoundRect(
                new Rectangle(0, 0, Width - 1, Height - 1), Theme.RadiusControl))
            using (var p = new Pen(Theme.Outline))
                g.DrawPath(p, path);

            if (_box.Text.Length == 0 && !string.IsNullOrEmpty(Placeholder))
            {
                TextRenderer.DrawText(g, Placeholder, new Font(Theme.FontMono, 9.5f),
                    new Rectangle(10, 7, Width - 20, 20),
                    Theme.OnSurfaceMuted,
                    TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }
        }

        protected override void OnGotFocus(EventArgs e) { _box.Focus(); base.OnGotFocus(e); }
    }

    // -----------------------------------------------------------------------
    //  提示条（说明文字独立成条，不与控件混排）
    // -----------------------------------------------------------------------
    internal class HintBar : Control
    {
        public string Text2 { get; set; }
        public Color Accent { get; set; }
        public bool Warn { get; set; }

        public HintBar()
        {
            Text2 = "";
            Accent = Theme.OnSurfaceMuted;
            Height = 52;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);

            var bg = Warn ? Color.FromArgb(22, 251, 191, 36) : Theme.SurfaceVariant;
            var fg = Warn ? Theme.Uncertain : Theme.OnSurfaceVariant;

            using (var path = Gfx.RoundRect(
                new Rectangle(0, 0, Width - 1, Height - 1), Theme.RadiusControl))
            using (var b = new SolidBrush(bg))
                g.FillPath(b, path);

            // 左侧强调条
            using (var b = new SolidBrush(Accent))
                g.FillRectangle(b, 0, 0, 3, Height - 1);

            TextRenderer.DrawText(g, Text2,
                new Font(Theme.FontFamily, 8.5f),
                new Rectangle(12, 6, Width - 22, Height - 12),
                fg,
                TextFormatFlags.Left | TextFormatFlags.WordBreak |
                TextFormatFlags.NoPadding);
        }
    }
}
