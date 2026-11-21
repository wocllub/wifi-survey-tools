// 디자인 토큰과 공통 드로잉 — 두 프로그램이 함께 쓰는 한 곳의 테마 정의
// 빌드: build.bat (C# 5 문법만 사용한다. Windows 기본 csc.exe 호환)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace WifiSurvey.Design
{
    // ---- 색 토큰: 다크 차콜 + 단일 액센트 ----
    static class Theme
    {
        public static float K = 1f;
        public static int S(int v) { return (int)Math.Round(v * K); }
        public static float SF(float v) { return v * K; }

        // 표면
        public static readonly Color Bg = Color.FromArgb(11, 14, 19);
        public static readonly Color Surface = Color.FromArgb(18, 22, 29);
        public static readonly Color SurfaceHi = Color.FromArgb(23, 29, 38);
        public static readonly Color SurfaceTop = Color.FromArgb(29, 36, 47);
        public static readonly Color Border = Color.FromArgb(35, 42, 53);
        public static readonly Color BorderStrong = Color.FromArgb(46, 55, 68);
        public static readonly Color Scrim = Color.FromArgb(8, 10, 14);

        // 글자
        public static readonly Color Ink = Color.FromArgb(233, 238, 246);
        public static readonly Color TextDim = Color.FromArgb(152, 164, 181);
        public static readonly Color TextMute = Color.FromArgb(102, 113, 127);

        // 단일 액센트
        public static readonly Color Accent = Color.FromArgb(76, 141, 255);
        public static readonly Color AccentHi = Color.FromArgb(111, 166, 255);
        public static readonly Color AccentLo = Color.FromArgb(54, 112, 216);
        public static readonly Color AccentSoft = Color.FromArgb(30, 50, 84);

        // 신호 5단계 (도메인 의미색: 액센트와 별개)
        public static readonly Color Lv5 = Color.FromArgb(17, 201, 160);
        public static readonly Color Lv4 = Color.FromArgb(95, 209, 106);
        public static readonly Color Lv3 = Color.FromArgb(242, 198, 61);
        public static readonly Color Lv2 = Color.FromArgb(255, 154, 61);
        public static readonly Color Lv1 = Color.FromArgb(255, 93, 108);
        public static readonly Color None = Color.FromArgb(72, 82, 96);
        public static readonly Color Empty = Color.Empty;

        // 8pt 그리드
        public const int U = 4;      // 기본 단위 (4px) — 4/8/12/16/24/32/48만 사용
        public static int P1 { get { return S(4); } }
        public static int P2 { get { return S(8); } }
        public static int P3 { get { return S(12); } }
        public static int P4 { get { return S(16); } }
        public static int P6 { get { return S(24); } }
        public static int P8 { get { return S(32); } }

        // ---- 글꼴: 5단계로 축소 ----
        const string Family = "Segoe UI";
        static readonly Dictionary<int, Font> cache = new Dictionary<int, Font>();
        static readonly Dictionary<int, Font> monoCache = new Dictionary<int, Font>();

        public static Font F(float pt, FontStyle st = FontStyle.Regular)
        {
            int key = (int)Math.Round(pt * 4) * 8 + (int)st;
            Font f;
            if (cache.TryGetValue(key, out f)) return f;
            f = new Font(Family, pt, st, GraphicsUnit.Point);
            cache[key] = f;
            return f;
        }
        public static Font Caption { get { return F(8.5f, FontStyle.Bold); } }   // 캡션 (대문자 + 자간)
        public static Font Body { get { return F(9.5f); } }
        public static Font Label { get { return F(10.5f); } }
        public static Font LabelBold { get { return F(10.5f, FontStyle.Bold); } }
        public static Font Title { get { return F(13f, FontStyle.Bold); } }
        public static Font Metric { get { return F(20f, FontStyle.Bold); } }
        public static Font Hero { get { return F(40f, FontStyle.Bold); } }

        public static Color Tone(double rssi)
        {
            if (rssi >= -50) return Lv5;
            if (rssi >= -60) return Lv4;
            if (rssi >= -70) return Lv3;
            if (rssi >= -80) return Lv2;
            return Lv1;
        }
        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            // 알파도 함께 보간한다. 3-인자 FromArgb는 알파를 255로 만들어
            // 반투명 색(예: 위험 버튼 배경)이 불투명하게 칠해지던 문제가 있었다.
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }
        public static Color Alpha(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c); }

        // ---- 도형 ----
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        public static void Fill(Graphics g, Rectangle r, int radius, Color c)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }
        public static void Stroke(Graphics g, Rectangle r, int radius, Color c, float w = 1f)
        {
            Rectangle rr = new Rectangle(r.X, r.Y, Math.Max(1, r.Width - 1), Math.Max(1, r.Height - 1));
            using (GraphicsPath p = Round(rr, radius))
            using (Pen pen = new Pen(c, w)) g.DrawPath(pen, p);
        }
        // 위에서 아래로 아주 옅게 밝아지는 카드(유리 느낌)
        public static void Card(Graphics g, Rectangle r, int radius, bool lifted)
        {
            Fill(g, r, radius, lifted ? SurfaceHi : Surface);
            Rectangle inner = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, Math.Max(2, r.Height / 2));
            using (GraphicsPath p = Round(r, radius))
            using (LinearGradientBrush b = new LinearGradientBrush(inner, Color.FromArgb(lifted ? 14 : 9, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                g.FillPath(b, p);
            Stroke(g, r, radius, lifted ? BorderStrong : Border);
        }
        // 부드러운 빛무리 (PathGradientBrush는 느려서 원형 알파를 겹쳐 쓴다)
        public static void Glow(Graphics g, PointF center, float radius, Color c, int layers = 5)
        {
            for (int i = layers; i >= 1; i--)
            {
                float rr = radius * i / layers;
                int a = (int)(34.0 / i);
                using (SolidBrush b = new SolidBrush(Alpha(c, a)))
                    g.FillEllipse(b, center.X - rr, center.Y - rr, rr * 2, rr * 2);
            }
        }

        // ---- 텍스트 ----
        public enum Align { Left, Center, Right }

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, Align align = Align.Left)
        {
            TextFormatFlags fl = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;
            if (align == Align.Right) fl |= TextFormatFlags.Right;
            else if (align == Align.Center) fl |= TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(g, s, f, r, c, fl);
        }
        public static void Wrap(Graphics g, string s, Font f, Color c, Rectangle r)
        {
            TextRenderer.DrawText(g, s, f, r, c, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
        }
        // 캡션: 대문자 + 자간. 글자 폭은 캐시해서 매 프레임 측정하지 않는다.
        static readonly Dictionary<string, int[]> captionWidths = new Dictionary<string, int[]>();
        static readonly int[] emptyWidths = new int[0];

        static int[] CaptionWidths(Graphics g, string t, Font f)
        {
            string key = t + "|" + f.Size.ToString("0.0");
            int[] w;
            if (captionWidths.TryGetValue(key, out w)) return w;
            if (captionWidths.Count > 400) captionWidths.Clear();
            w = new int[t.Length];
            for (int i = 0; i < t.Length; i++)
                w[i] = TextRenderer.MeasureText(g, t[i].ToString(), f, Size.Empty, TextFormatFlags.NoPadding).Width;
            captionWidths[key] = w;
            return w;
        }

        public static void CaptionText(Graphics g, string s, Color c, Rectangle r, Align align = Align.Left)
        {
            Font f = Caption;
            string t = s.ToUpperInvariant();
            int space = S(1);
            float total = 0;
            int[] w = t.Length == 0 ? emptyWidths : CaptionWidths(g, t, f);
            for (int i = 0; i < t.Length; i++)
                total += w[i] + (i < t.Length - 1 ? space : 0);
            float x = align == Align.Right ? r.Right - total : align == Align.Center ? r.X + (r.Width - total) / 2f : r.X;
            for (int i = 0; i < t.Length; i++)
            {
                TextRenderer.DrawText(g, t[i].ToString(), f, new Rectangle((int)x, r.Y, w[i] + S(2), r.Height), c,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                x += w[i] + space;
            }
        }

        // 고정폭 숫자: 계측기에서 값이 흔들리지 않게 자리를 고정한다
        static readonly Dictionary<int, int> digitW = new Dictionary<int, int>();
        public static void Mono(Graphics g, string s, Font f, Color c, Rectangle r, Align align = Align.Right)
        {
            int key = (int)Math.Round(f.Size * 4) * 8 + (int)f.Style;
            int dw;
            if (!digitW.TryGetValue(key, out dw))
            {
                dw = 0;
                for (int d = 0; d < 10; d++) dw = Math.Max(dw, TextRenderer.MeasureText(g, d.ToString(), f, Size.Empty, TextFormatFlags.NoPadding).Width);
                digitW[key] = dw;
            }
            float total = 0;
            for (int i = 0; i < s.Length; i++)
                total += s[i] >= '0' && s[i] <= '9' ? dw : TextRenderer.MeasureText(g, s[i].ToString(), f, Size.Empty, TextFormatFlags.NoPadding).Width;
            float x = align == Align.Right ? r.Right - total : align == Align.Center ? r.X + (r.Width - total) / 2f : r.X;
            for (int i = 0; i < s.Length; i++)
            {
                string ch = s[i].ToString();
                bool digit = s[i] >= '0' && s[i] <= '9';
                int cw = digit ? dw : TextRenderer.MeasureText(g, ch, f, Size.Empty, TextFormatFlags.NoPadding).Width;
                Rectangle cell = new Rectangle((int)x, r.Y, digit ? dw : cw + S(2), r.Height);
                TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter;
                if (!digit) { fl = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix; cell.X -= S(1); }
                TextRenderer.DrawText(g, ch, f, cell, c, fl);
                x += cw;
            }
        }

        public static void Track(Graphics g, string s, Font f, Color c, Rectangle r, int tracking, Align align)
        {
            Size[] w = new Size[s.Length];
            float total = 0;
            for (int i = 0; i < s.Length; i++)
            {
                w[i] = TextRenderer.MeasureText(g, s[i].ToString(), f, Size.Empty, TextFormatFlags.NoPadding);
                total += w[i].Width + tracking;
            }
            float x = align == Align.Right ? r.Right - total : align == Align.Center ? r.X + (r.Width - total) / 2f : r.X;
            for (int i = 0; i < s.Length; i++)
            {
                TextRenderer.DrawText(g, s[i].ToString(), f, new Rectangle((int)x, r.Y, w[i].Width + 4, r.Height), c,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                x += w[i].Width + tracking;
            }
        }

        public static void Cursor(Control c, bool hand)
        {
            Cursor want = hand ? Cursors.Hand : Cursors.Default;
            if (c.Cursor != want) c.Cursor = want;
        }
    }

    // ---- 이징 ----
    static class Ease
    {
        public static double OutCubic(double t) { t = 1 - Math.Max(0, Math.Min(1, t)); return 1 - t * t * t; }
        public static double InOutCubic(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        }
        public static double OutBack(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            const double c = 1.70158;
            return 1 + (c + 1) * Math.Pow(t - 1, 3) + c * Math.Pow(t - 1, 2);
        }
    }

    // ---- 스프링: 값이 연속으로 들어와도 끊기지 않는 보간 ----
    class Spring
    {
        public double Value, Target, Velocity = 0;
        public double Stiffness = 180, Damping = 22;
        public bool Settled
        {
            get
            {
                if (double.IsNaN(Target)) return true;
                return Math.Abs(Target - Value) < 0.0008 && Math.Abs(Velocity) < 0.008;
            }
        }

        public Spring(double value) { Value = value; Target = value; }
        public void To(double target) { Target = target; }
        public void Set(double v) { Value = Target = v; Velocity = 0; }
        public double Step(double dt)
        {
            if (dt <= 0) return Value;
            if (double.IsNaN(Target) || dt > 0.9) { Value = Target; Velocity = 0; return Value; }
            if (dt > 0.1) dt = 0.1;
            int steps = (int)Math.Ceiling(dt / 0.008);
            double h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                double acc = (Target - Value) * Stiffness - Velocity * Damping;
                Velocity += acc * h;
                Value += Velocity * h;
            }
            if (Settled) Value = Target;
            return Value;
        }
    }

    interface IMotion
    {
        void StepMotion(float dt);
        bool WantsFrame { get; }
    }

    // 하나의 60fps 타이머가 애니메이션 중인 컨트롤만 다시 그린다
    static class Motion
    {
        static readonly List<IMotion> items = new List<IMotion>();
        static readonly Timer timer = new Timer { Interval = 15 };
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static double last;
        public static bool Enabled = true;
        public static float Fps;

        public static void Attach(IMotion m)
        {
            if (!items.Contains(m)) items.Add(m);
            if (Enabled && !timer.Enabled) { last = clock.Elapsed.TotalSeconds; timer.Start(); }
        }
        public static void Detach(IMotion m) { items.Remove(m); }
        public static void InvalidateAll() { foreach (IMotion m in items.ToArray()) { Control c = m as Control; if (c != null) c.Invalidate(); } }

        static Motion()
        {
            timer.Tick += delegate
            {
                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(0.1, now - last);
                last = now;
                if (dt <= 0) return;
                Fps = (float)(0.9 * Fps + 0.1 * (1.0 / dt));
                IMotion[] snap = items.ToArray();
                foreach (IMotion m in snap)
                {
                    try
                    {
                        m.StepMotion(dt);
                        if (m.WantsFrame)
                        {
                            Control c = m as Control;
                            if (c != null && c.IsHandleCreated && c.Visible) c.Invalidate();
                        }
                    }
                    catch (ObjectDisposedException) { items.Remove(m); }
                }
            };
        }
    }

    // ---- 세로 스택 패널: 8pt 그리드로 자동 배치 ----
    class Stack : Panel
    {
        readonly List<Control> items = new List<Control>();
        public int Gap = 8;

        public Stack()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            AutoScroll = true;
            Padding = Padding.Empty;
        }
        public T Add<T>(T c, int height) where T : Control
        {
            c.Height = Theme.S(height);
            c.Margin = Padding.Empty;
            Controls.Add(c);
            items.Add(c);
            Arrange();
            return c;
        }
        public void Arrange()
        {
            int y = Theme.P2;
            int right = Theme.P4 + (VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
            foreach (Control c in items)
            {
                if (!c.Visible) continue;
                c.Left = Theme.P2;
                c.Top = y;
                c.Width = Math.Max(1, ClientSize.Width - right);
                y += c.Height + Theme.S(Gap);
            }
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Arrange(); }
    }
}
