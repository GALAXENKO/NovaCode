using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace NovaCode.UI
{
    /// <summary>Цвета и шрифты. Тёмная тема в духе OpenCode.</summary>
    public static class Theme
    {
        public static Color Bg = Color.FromArgb(16, 16, 19);
        public static Color Panel = Color.FromArgb(23, 23, 27);
        public static Color PanelAlt = Color.FromArgb(30, 30, 35);
        public static Color Border = Color.FromArgb(46, 46, 54);
        public static Color Text = Color.FromArgb(228, 228, 232);
        public static Color Dim = Color.FromArgb(132, 132, 144);
        public static Color Faint = Color.FromArgb(92, 92, 102);
        public static Color Accent = Color.FromArgb(126, 166, 250);
        public static Color Assistant = Color.FromArgb(216, 216, 222);
        public static Color Tool = Color.FromArgb(186, 162, 255);
        public static Color ToolBg = Color.FromArgb(27, 26, 34);
        public static Color CodeBg = Color.FromArgb(24, 24, 28);
        public static Color Success = Color.FromArgb(126, 231, 135);
        public static Color Error = Color.FromArgb(255, 112, 112);
        public static Color Warn = Color.FromArgb(255, 186, 110);
        public static Color InputBg = Color.FromArgb(28, 28, 33);

        public static Font Mono;
        public static Font MonoBold;
        public static Font MonoSelf;
        public static Font Ui;
        public static Font UiBold;
        public static Font UiSmall;
        public static Font Title;

        private static string _monoName = "Courier New";
        private static int _size = 10;

        public static void Init(int fontSize)
        {
            _size = fontSize < 8 ? 8 : (fontSize > 16 ? 16 : fontSize);
            _monoName = PickMono();

            Mono = new Font(_monoName, _size, FontStyle.Regular, GraphicsUnit.Point);
            MonoBold = new Font(_monoName, _size, FontStyle.Bold, GraphicsUnit.Point);
            MonoSelf = new Font(_monoName, _size - 1, FontStyle.Regular, GraphicsUnit.Point);
            Ui = new Font("Tahoma", 8.25f, FontStyle.Regular, GraphicsUnit.Point);
            UiBold = new Font("Tahoma", 8.25f, FontStyle.Bold, GraphicsUnit.Point);
            UiSmall = new Font("Tahoma", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
            Title = new Font("Tahoma", 11f, FontStyle.Bold, GraphicsUnit.Point);
        }

        /// <summary>Consolas есть с Vista, Lucida Console — на XP. Courier New есть всегда.</summary>
        private static string PickMono()
        {
            string[] candidates = { "Consolas", "Lucida Console", "Courier New" };
            try
            {
                using (var installed = new InstalledFontCollection())
                {
                    foreach (string candidate in candidates)
                    {
                        foreach (FontFamily family in installed.Families)
                        {
                            if (string.Equals(family.Name, candidate, StringComparison.OrdinalIgnoreCase))
                                return candidate;
                        }
                    }
                }
            }
            catch { }
            return "Courier New";
        }

        public static Font MonoOf(float delta)
        {
            float size = _size + delta;
            if (size < 6) size = 6;
            return new Font(_monoName, size, FontStyle.Regular, GraphicsUnit.Point);
        }
    }

    /// <summary>Плоская кнопка без «объёмного» вида XP-темы.</summary>
    public sealed class FlatButton : Control
    {
        private bool _hover;
        private bool _pressed;
        public Color AccentColor = Theme.Border;
        public bool Primary;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Ui;
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
            Height = 26;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

            Color back;
            if (Primary)
                back = _pressed ? Color.FromArgb(70, 96, 150) : (_hover ? Color.FromArgb(60, 86, 140) : Color.FromArgb(48, 70, 118));
            else
                back = _pressed ? Theme.PanelAlt : (_hover ? Color.FromArgb(38, 38, 44) : Theme.Panel);

            using (var brush = new SolidBrush(back))
                g.FillRectangle(brush, ClientRectangle);

            using (var pen = new Pen(_hover ? Theme.Border : Theme.PanelAlt))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            TextRenderer.DrawText(g, Text, Font, ClientRectangle,
                Enabled ? ForeColor : Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Панель с рамкой — используется для карточки ввода.</summary>
    public sealed class BorderedPanel : Panel
    {
        public Color BorderColor = Theme.Border;

        public BorderedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.InputBg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(BorderColor))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
