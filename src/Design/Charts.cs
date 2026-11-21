// 차트와 표 — 계측기용 시각화 (스트림 그래프, 링, KPI, 채널 스펙트럼, 데이터 표)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WifiSurvey.Design
{
    // ---- 문자열/빈 상태 도우미 ----
    static class Deco
    {
        public static void Chip(Graphics g, Rectangle r, string text, Color tone, bool filled)
        {
            Theme.Fill(g, r, r.Height / 2, filled ? Theme.Alpha(tone, 34) : Theme.Surface);
            Theme.Stroke(g, r, r.Height / 2, filled ? Theme.Alpha(tone, 90) : Theme.Border);
            Theme.Text(g, text, Theme.F(8.5f, FontStyle.Bold), tone, r, Theme.Align.Center);
        }
        public static void Divider(Graphics g, int x, int y, int w) { using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, x, y, x + w, y); }
        public static void EmptyState(Graphics g, Rectangle r, Glyph icon, string title, string hint)
        {
            int isz = Theme.S(34);
            int cy = r.Y + r.Height / 2 - Theme.S(34);
            Icons.Draw(g, icon, new RectangleF(r.X + r.Width / 2f - isz / 2f, cy, isz, isz), Theme.Alpha(Theme.TextMute, 150), 1.6f);
            Theme.Text(g, title, Theme.Label, Theme.TextDim, new Rectangle(r.X, cy + isz + Theme.S(10), r.Width, Theme.S(22)), Theme.Align.Center);
            if (hint.Length > 0)
                Theme.Wrap(g, hint, Theme.F(9f), Theme.TextMute, new Rectangle(r.X + Theme.S(24), cy + isz + Theme.S(32), r.Width - Theme.S(48), Theme.S(40)));
        }
        public static void Bar(Graphics g, Rectangle r, double frac, Color tone, bool animated)
        {
            Theme.Fill(g, r, r.Height / 2, Theme.Alpha(Theme.TextMute, 34));
            int w = (int)Math.Round(r.Width * Math.Max(0, Math.Min(1, frac)));
            if (w > 0) Theme.Fill(g, new Rectangle(r.X, r.Y, Math.Max(r.Height, w), r.Height), r.Height / 2, tone);
        }
        public static string Short(double v, int dec)
        {
            return v.ToString("0." + new string('0', dec));
        }
    }

    // ---- KPI 카드: 캡션 + 숫자 + 단위 + 보조 설명 + 스파크라인 ----
    class Kpi : Widget
    {
        public string Caption = "", Unit = "", Sub = "";
        public Color Tone = Theme.Ink;
        public int Decimals;
        public bool ShowSpark = true;
        readonly Spring value = new Spring(double.NaN);
        readonly List<double> spark = new List<double>();
        double lo = double.MaxValue, hi = double.MinValue;
        double actual = double.NaN;

        public void Set(double v) { actual = v; if (double.IsNaN(value.Value)) value.Set(v); else value.To(v); Poke(); }
        public double Actual { get { return actual; } }
        public void Push(double v)
        {
            spark.Add(v);
            if (spark.Count > 48) spark.RemoveAt(0);
            if (spark.Count > 2)
            {
                lo = double.MaxValue; hi = double.MinValue;
                foreach (double d in spark) { if (double.IsNaN(d)) continue; lo = Math.Min(lo, d); hi = Math.Max(hi, d); }
            }
        }
        protected override bool Step(float dt) { value.Step(dt); return !value.Settled; }
        public void CopyTrendTo(Kpi other)
        {
            other.spark.Clear(); other.spark.AddRange(spark); other.lo = lo; other.hi = hi;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.Card(g, r, Theme.S(10), false);
            int p = Theme.S(12);
            Theme.CaptionText(g, Caption, Theme.TextMute, new Rectangle(p, p - Theme.S(2), r.Width - p * 2, Theme.S(14)));
            string text = double.IsNaN(value.Value) ? "—" : value.Value.ToString(Decimals == 0 ? "0" : "0." + new string('0', Decimals));
            Size u = TextRenderer.MeasureText(g, Unit, Theme.F(9.5f, FontStyle.Bold), Size.Empty, TextFormatFlags.NoPadding);
            Theme.Mono(g, text, Theme.F(24f, FontStyle.Bold), Tone, new Rectangle(p, p + Theme.S(12), r.Width - p * 2 - (Unit.Length > 0 ? u.Width + Theme.S(6) : 0), Theme.S(40)), Theme.Align.Left);
            if (Unit.Length > 0)
            {
                int tw = TextRenderer.MeasureText(g, text, Theme.F(24f, FontStyle.Bold), Size.Empty, TextFormatFlags.NoPadding).Width;
                Theme.Text(g, Unit, Theme.F(9.5f, FontStyle.Bold), Theme.Alpha(Tone, 190),
                    new Rectangle(p + Math.Min(tw, r.Width - p * 2 - u.Width) + Theme.S(4), p + Theme.S(30), u.Width + Theme.S(6), Theme.S(18)));
            }
            if (Sub.Length > 0)
                Theme.Text(g, Sub, Theme.F(8.5f), Theme.TextMute, new Rectangle(p, r.Bottom - p - Theme.S(16), r.Width - p * 2 - (ShowSpark ? Theme.S(56) : 0), Theme.S(16)));
            if (ShowSpark && spark.Count > 2)
            {
                Rectangle s = new Rectangle(r.Right - p - Theme.S(52), r.Bottom - p - Theme.S(22), Theme.S(52), Theme.S(22));
                DrawSpark(g, s);
            }
        }
        void DrawSpark(Graphics g, Rectangle s)
        {
            double span = Math.Max(1, hi - lo);
            PointF[] pts = new PointF[spark.Count];
            for (int i = 0; i < spark.Count; i++)
            {
                double v = double.IsNaN(spark[i]) ? hi : spark[i];
                pts[i] = new PointF(s.X + s.Width * i / (float)(spark.Count - 1), (float)(s.Bottom - (v - lo) / span * s.Height));
            }
            using (Pen p = new Pen(Theme.Alpha(Tone, 120), Math.Max(1f, Theme.SF(1.2f)))) { p.LineJoin = LineJoin.Round; g.DrawCurve(p, pts, 0.4f); }
        }
    }

    // ---- 링 게이지 ----
    class Ring : Widget
    {
        public string Caption = "", Center = "", Sub = "";
        public Color Tone = Theme.Accent;
        public double Target, Min, Max = 100;
        public bool Pulsing;
        readonly Spring pct = new Spring(0);
        double shown;

        public void Set(double v) { Target = v; pct.To(Math.Max(0, Math.Min(1, (v - Min) / Math.Max(0.0001, Max - Min)))); Poke(); }
        public void SnapTo(double v) { Target = v; pct.Set(Math.Max(0, Math.Min(1, (v - Min) / Math.Max(0.0001, Max - Min)))); Invalidate(); }
        protected override bool Step(float dt)
        {
            shown = pct.Step(dt);
            if (Pulsing) { Invalidate(); return true; }
            return !pct.Settled;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            int size = Math.Min(Width - Theme.S(16), Height - Theme.S(28));
            if (size < Theme.S(40)) return;
            RectangleF box = new RectangleF((Width - size) / 2f, Theme.S(6), size, size);
            float thick = Theme.SF(7f);
            using (Pen track = new Pen(Theme.Alpha(Theme.TextMute, 46), thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(track, box, 135, 270);
            double v = double.IsNaN(shown) ? 0 : shown;
            Color tone = Tone;
            if (Pulsing)
            {
                double t = (DateTime.Now.TimeOfDay.TotalMilliseconds % 1800) / 1800.0;
                Theme.Glow(g, new PointF(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2f, tone, 3);
                tone = Theme.Mix(tone, Color.White, 0.10 + 0.06 * Math.Sin(t * Math.PI * 2));
            }
            using (Pen arc = new Pen(tone, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                if (v > 0.002) g.DrawArc(arc, box, 135, (float)(270 * v));
            int cy = (int)(box.Y + box.Height / 2);
            Theme.Mono(g, Center, Theme.F(20f, FontStyle.Bold), Theme.Ink, new Rectangle(0, cy - Theme.S(22), Width, Theme.S(30)), Theme.Align.Center);
            if (Sub.Length > 0)
                Theme.Text(g, Sub, Theme.F(8.5f), Theme.TextMute, new Rectangle(0, cy + Theme.S(8), Width, Theme.S(16)), Theme.Align.Center);
            if (Caption.Length > 0)
                Theme.CaptionText(g, Caption, Theme.TextMute, new Rectangle(Theme.P2, Height - Theme.S(18), Width - Theme.P4, Theme.S(14)), Theme.Align.Center);
        }
    }

    // ---- 신호 막대 5단계 (애니메이션 + 측정 중 펄스) ----
    class LevelBars : Widget
    {
        public int Rssi = -100;
        public bool None = true;
        public bool Pulsing;
        readonly Spring[] h = { new Spring(0.25), new Spring(0.25), new Spring(0.25), new Spring(0.25), new Spring(0.25) };
        static readonly double[] Steps = { 0.26, 0.44, 0.62, 0.82, 1.0 };

        public void Set(int rssi, bool none)
        {
            Rssi = rssi; None = none;
            int bars = none ? 0 : rssi >= -50 ? 5 : rssi >= -60 ? 4 : rssi >= -70 ? 3 : rssi >= -80 ? 2 : 1;
            for (int i = 0; i < 5; i++) h[i].To(i < bars ? Steps[i] : 0.18);
            Poke();
        }
        protected override bool Step(float dt)
        {
            bool moving = false;
            for (int i = 0; i < 5; i++) { h[i].Step(dt); moving |= !h[i].Settled; }
            if (Pulsing) { Invalidate(); return true; }
            return moving;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            Color tone = None ? Theme.None : Theme.Tone(Rssi);
            if (Pulsing)
            {
                double t = (DateTime.Now.TimeOfDay.TotalMilliseconds % 1600) / 1600.0;
                float a = (float)(0.5 + 0.5 * Math.Sin(t * Math.PI * 2));
                Theme.Glow(g, new PointF(Width / 2f, Height - Theme.S(10)), Theme.S(16) + Theme.S(6) * a, tone, 4);
            }
            int bw = Theme.S(5), gap = Theme.S(4);
            int total = bw * 5 + gap * 4;
            int x = (Width - total) / 2, baseY = Height - Theme.S(4);
            for (int i = 0; i < 5; i++)
            {
                int bh = (int)((Height - Theme.S(8)) * h[i].Value);
                if (bh < Theme.S(3)) bh = Theme.S(3);
                Rectangle r = new Rectangle(x + i * (bw + gap), baseY - bh, bw, bh);
                Theme.Fill(g, r, Theme.S(2), h[i].Target > 0.3 ? tone : Theme.Alpha(Theme.TextMute, 40));
            }
        }
    }

    // ---- 실시간 스트림 그래프 ----
    class StreamGraph : Widget
    {
        public struct Pt { public double T, V; public Pt(double t, double v) { T = t; V = v; } }

        public double Floor = -100, Ceiling = -20;
        public double Threshold = -70;
        public string Unit = "dBm";
        public bool ShowThreshold = true;
        public readonly List<Pt> Data = new List<Pt>();
        public List<double[]> Ghost;   // {초, rssi} — 비교 세션(끝을 현재에 맞춰 겹친다)
        public string EmptyTitle = "표시할 데이터가 없습니다", EmptyHint = "";
        public int Window = 180;
        int hoverX = -1;
        readonly Spring dot = new Spring(0);

        // 마지막 값이 화면에 부드럽게 도착하도록 Y좌표를 스프링으로 따라간다
        float dotY = float.NaN;
        public void AddSample(double t, double v) { Data.Add(new Pt(t, v)); Trim(t); Poke(); }
        public void Clear() { Data.Clear(); Invalidate(); }
        void Trim(double now)
        {
            while (Data.Count > 1 && Data[0].T < now - Window - Window * 0.25) Data.RemoveAt(0);
        }
        protected override bool Step(float dt)
        {
            dot.Step(dt);
            return !dot.Settled || float.IsNaN(dotY);
        }
        public static float Y(double v, double floor, double ceil, Rectangle p)
        {
            double c = Math.Max(floor + 1, Math.Min(ceil, v));
            return (float)(p.Top + (ceil - c) / (ceil - floor) * p.Height);
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); hoverX = e.X; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverX = -1; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            Rectangle p = new Rectangle(Theme.S(40), Theme.S(8), Math.Max(10, Width - Theme.S(52)), Math.Max(10, Height - Theme.S(30)));
            using (Pen gp = new Pen(Theme.Alpha(Theme.Border, 170)))
            {
                for (int v = (int)Ceiling; v >= (int)Floor; v -= 10)
                {
                    float y = Y(v, Floor, Ceiling, p);
                    if (v % 20 == 0) { g.DrawLine(gp, p.Left, y, p.Right, y); Theme.Text(g, v.ToString(), Theme.F(8f), Theme.TextMute, new Rectangle(0, (int)y - Theme.S(9), Theme.S(34), Theme.S(16)), Theme.Align.Right); }
                }
            }
            if (ShowThreshold)
            {
                float ty = Y(Threshold, Floor, Ceiling, p);
                using (Pen tp = new Pen(Theme.Alpha(Theme.Lv2, 130)) { DashStyle = DashStyle.Dash, DashPattern = new float[] { 4f, 4f } })
                    g.DrawLine(tp, p.Left, ty, p.Right, ty);
                Theme.Text(g, "약전계 " + Threshold.ToString("0"), Theme.F(8f), Theme.Alpha(Theme.Lv2, 200), new Rectangle(p.Right - Theme.S(74), (int)ty - Theme.S(17), Theme.S(74), Theme.S(16)), Theme.Align.Right);
            }
            if (Data.Count < 2)
            {
                Deco.EmptyState(g, p, Glyph.Pulse, EmptyTitle, EmptyHint);
                return;
            }
            double now = Data[Data.Count - 1].T;
            double start = now - Window;
            float pxPerSec = p.Width / (float)Math.Max(1, Window);
            List<PointF> pts = new List<PointF>();
            List<PointF> ghostPts = null;
            for (int i = 0; i < Data.Count; i++)
            {
                if (double.IsNaN(Data[i].V)) { pts.Add(new PointF(float.NaN, float.NaN)); continue; }
                float x = p.Right - (float)(now - Data[i].T) * pxPerSec;
                if (x < p.Left - 4) continue;
                pts.Add(new PointF(x, Y(Data[i].V, Floor, Ceiling, p)));
            }
            // 구간별 선: NaN이면 끊는다
            List<List<PointF>> runs = Split(pts);
            Color tone = pts.Count > 0 ? Theme.Tone(Data[Data.Count - 1].V) : Theme.Accent;
            // 그라데이션 영역
            Rectangle area = new Rectangle(p.Left, p.Top, p.Width, p.Height);
            using (LinearGradientBrush fill = new LinearGradientBrush(area, Theme.Alpha(tone, 78), Theme.Alpha(tone, 4), LinearGradientMode.Vertical))
            {
                foreach (List<PointF> run in runs)
                {
                    if (run.Count < 2) continue;
                    List<PointF> poly = new List<PointF>(run);
                    poly.Add(new PointF(run[run.Count - 1].X, p.Bottom));
                    poly.Add(new PointF(run[0].X, p.Bottom));
                    g.FillPolygon(fill, poly.ToArray());
                }
            }
            foreach (List<PointF> run in runs)
            {
                if (run.Count < 2) continue;
                using (Pen line = new Pen(Theme.Mix(tone, Color.White, 0.15), Math.Max(1.4f, Theme.SF(1.8f))) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawCurve(line, run.ToArray(), 0.35f);
            }
            if (Ghost != null && Ghost.Count > 1)
            {
                ghostPts = new List<PointF>();
                double ghostEnd = Ghost[Ghost.Count - 1][0];
                foreach (double[] q in Ghost)
                {
                    float x = p.Right - (float)(ghostEnd - q[0]) * pxPerSec;
                    if (x < p.Left - 4 || double.IsNaN(q[1])) { ghostPts.Add(new PointF(float.NaN, float.NaN)); continue; }
                    ghostPts.Add(new PointF(x, Y(q[1], Floor, Ceiling, p)));
                }
                foreach (List<PointF> run in Split(ghostPts))
                {
                    if (run.Count < 2) continue;
                    using (Pen ghost = new Pen(Theme.Alpha(Theme.TextMute, 200), Math.Max(1f, Theme.SF(1.3f))) { DashStyle = DashStyle.Dash, DashPattern = new float[] { 5f, 4f }, LineJoin = LineJoin.Round })
                        g.DrawCurve(ghost, run.ToArray(), 0.35f);
                }
            }
            // 현재 값 점 + 빛무리
            PointF last = pts[pts.Count - 1];
            if (!float.IsNaN(last.X))
            {
                float ty = dotY;
                if (float.IsNaN(ty)) ty = last.Y;
                else ty = (float)(0.55 * ty + 0.45 * last.Y);
                dotY = ty;
                Theme.Glow(g, new PointF(p.Right, ty), Theme.S(18), tone, 4);
                using (SolidBrush b = new SolidBrush(tone)) g.FillEllipse(b, p.Right - Theme.S(4), ty - Theme.S(4), Theme.S(7), Theme.S(7));
            }
            // 시간 축
            for (int s = 0; s <= Window; s += Window / 3)
            {
                float x = p.Right - s * pxPerSec;
                if (x < p.Left) break;
                string label = s == 0 ? "지금" : s < 60 ? "-" + s + "초" : "-" + (s / 60) + "분";
                Theme.Text(g, label, Theme.F(8f), Theme.TextMute, new Rectangle((int)x - Theme.S(26), p.Bottom + Theme.S(4), Theme.S(52), Theme.S(16)), s == 0 ? Theme.Align.Right : Theme.Align.Center);
            }
            // 호버 십자선 + 값
            if (hoverX >= p.Left && hoverX <= p.Right && pts.Count > 2)
            {
                int best = -1; float bd = float.MaxValue;
                for (int i = 0; i < pts.Count; i++)
                {
                    if (float.IsNaN(pts[i].X)) continue;
                    float d = Math.Abs(pts[i].X - hoverX);
                    if (d < bd) { bd = d; best = i; }
                }
                if (best >= 0)
                {
                    using (Pen vp = new Pen(Theme.Alpha(Theme.Ink, 40)))
                    {
                        g.DrawLine(vp, hoverX, p.Top, hoverX, p.Bottom);
                        g.DrawLine(vp, p.Left, pts[best].Y, p.Right, pts[best].Y);
                    }
                    double v = double.NaN;
                    int idx = Math.Max(0, Math.Min(Data.Count - 1, Data.Count - pts.Count + best));
                    v = Data[idx].V;
                    string text = double.IsNaN(v) ? "—" : v.ToString("0") + " " + Unit;
                    int tw = TextRenderer.MeasureText(g, text, Theme.F(9f, FontStyle.Bold), Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(12);
                    Rectangle box = new Rectangle(Math.Min(p.Right - tw, Math.Max(p.Left, hoverX + Theme.S(8))), (int)pts[best].Y - Theme.S(24), tw, Theme.S(20));
                    Theme.Fill(g, box, Theme.S(5), Theme.SurfaceTop);
                    Theme.Stroke(g, box, Theme.S(5), Theme.BorderStrong);
                    Theme.Text(g, text, Theme.F(9f, FontStyle.Bold), Theme.Ink, box, Theme.Align.Center);
                }
            }
        }
        static List<List<PointF>> Split(List<PointF> pts)
        {
            List<List<PointF>> runs = new List<List<PointF>>();
            List<PointF> cur = new List<PointF>();
            foreach (PointF p in pts)
            {
                if (float.IsNaN(p.X)) { if (cur.Count > 0) { runs.Add(cur); cur = new List<PointF>(); } continue; }
                cur.Add(p);
            }
            if (cur.Count > 0) runs.Add(cur);
            return runs;
        }
    }

    // ---- 채널 스펙트럼 (점유 막대) ----
    class Spectrum : Widget
    {
        public string[] Labels = new string[0];
        public double[] Values = new double[0];
        public Color[] Tones = new Color[0];
        public int Highlight = -1;
        public string HighlightText = "";
        int hot = -1;

        public void Set(string[] labels, double[] values, Color[] tones, int highlight)
        {
            Labels = labels; Values = values; Tones = tones; Highlight = highlight;
            Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = Index(e.X);
            if (i != hot) { hot = i; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
        int Index(int x)
        {
            if (Labels.Length == 0) return -1;
            int p = Theme.S(10), w = Math.Max(1, Width - p * 2);
            int i = (x - p) * Labels.Length / w;
            return i < 0 || i >= Labels.Length ? -1 : i;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            if (Labels.Length == 0) { Deco.EmptyState(g, ClientRectangle, Glyph.Bars, "채널 데이터가 없습니다", "주변 네트워크를 스캔하면 채널 점유가 표시됩니다."); return; }
            Rectangle p = new Rectangle(Theme.S(10), Theme.S(14), Math.Max(10, Width - Theme.S(20)), Math.Max(10, Height - Theme.S(34)));
            double max = 0.0001;
            foreach (double v in Values) max = Math.Max(max, v);
            int bw = Math.Max(Theme.S(5), p.Width / Labels.Length - Theme.S(3));
            using (Pen basePen = new Pen(Theme.Border)) g.DrawLine(basePen, p.Left, p.Bottom, p.Right, p.Bottom);
            for (int i = 0; i < Labels.Length; i++)
            {
                int x = p.Left + (int)((long)p.Width * i / Labels.Length) + Theme.S(1);
                int h = (int)Math.Min(p.Height, Values[i] / max * p.Height);
                if (h < Theme.S(2)) h = Theme.S(2);
                bool hi = i == Highlight;
                Color tone = Tones.Length > i ? Tones[i] : Theme.Accent;
                Rectangle r = new Rectangle(x, p.Bottom - h, bw, h);
                Theme.Fill(g, r, Theme.S(3), hi ? tone : Theme.Alpha(tone, hot == i ? 230 : 170));
                if (hi && HighlightText.Length > 0)
                {
                    Theme.Glow(g, new PointF(r.X + r.Width / 2f, p.Bottom - h), Theme.S(14), tone, 3);
                    int tw = TextRenderer.MeasureText(g, HighlightText, Theme.F(8f, FontStyle.Bold), Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(10);
                    Rectangle tip = new Rectangle(Math.Min(p.Right - tw, Math.Max(0, r.X + r.Width / 2 - tw / 2)), p.Top - Theme.S(4), tw, Theme.S(16));
                    Theme.Fill(g, tip, Theme.S(4), Theme.Alpha(tone, 40));
                    Theme.Text(g, HighlightText, Theme.F(8f, FontStyle.Bold), tone, tip, Theme.Align.Center);
                }
                if (i == hot && !hi)
                {
                    string t = Values[i].ToString("0") + " AP";
                    int tw = TextRenderer.MeasureText(g, t, Theme.F(8f), Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(10);
                    Rectangle tip = new Rectangle(Math.Min(p.Right - tw, Math.Max(0, r.X + r.Width / 2 - tw / 2)), p.Top - Theme.S(4), tw, Theme.S(16));
                    Theme.Fill(g, tip, Theme.S(4), Theme.SurfaceTop);
                    Theme.Text(g, t, Theme.F(8f), Theme.TextDim, tip, Theme.Align.Center);
                }
            }
            int labelEvery = Math.Max(1, Labels.Length / Math.Max(1, p.Width / Theme.S(40)));
            for (int i = 0; i < Labels.Length; i += labelEvery)
            {
                int x = p.Left + (int)((long)p.Width * i / Labels.Length);
                Theme.Text(g, Labels[i], Theme.F(7.5f), i == Highlight ? Theme.Ink : Theme.TextMute, new Rectangle(x, p.Bottom + Theme.S(2), Theme.S(34), Theme.S(14)));
            }
        }
    }

    // ---- 데이터 표: 정렬 헤더, 호버, 선택, 신호 막대 ----
    class Column
    {
        public string Title = "";
        public int Width = 100;
        public Theme.Align Align = Theme.Align.Left;
        public bool Sortable = true;
    }
    class Row
    {
        public string[] Cells = new string[0];
        public Color[] Tones;
        public bool Emphasis;
        public object Tag;
        public double Bar = double.NaN;   // 0..1, 막대 열
        public int BarColumn = -1;
        public Color BarTone = Theme.Accent;
    }

    class DataTable : Widget
    {
        public readonly List<Column> Columns = new List<Column>();
        public readonly List<Row> Rows = new List<Row>();
        public int SortColumn = -1;
        public bool SortAscending = true;
        public string EmptyTitle = "항목이 없습니다", EmptyHint = "";
        public int RowHeight = 30;
        public event EventHandler SelectionChanged;
        public event EventHandler SortChanged;
        public event EventHandler Activated;

        int hover = -1, selected = -1, scroll;
        readonly Spring slide = new Spring(0);

        public int SelectedIndex
        {
            get { return selected; }
            set { if (value != selected) { selected = value; slide.To(value); Poke(); Invalidate(); if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); } }
        }
        public Row SelectedRow { get { return selected >= 0 && selected < Rows.Count ? Rows[selected] : null; } }
        public void ResetScroll() { scroll = 0; Invalidate(); }

        int HeaderH { get { return Theme.S(30); } }
        int VisibleRows { get { return Math.Max(1, (Height - HeaderH) / Theme.S(RowHeight)); } }
        int MaxScroll { get { return Math.Max(0, Rows.Count - VisibleRows); } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = RowAt(e.Y);
            if (i != hover) { hover = i; Invalidate(); }
            Theme.Cursor(this, i >= 0);
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Y < HeaderH)
            {
                int c = ColumnAt(e.X);
                if (c < 0 || !Columns[c].Sortable) return;
                if (c == SortColumn) SortAscending = !SortAscending; else { SortColumn = c; SortAscending = true; }
                Invalidate();
                if (SortChanged != null) SortChanged(this, EventArgs.Empty);
                return;
            }
            int i = RowAt(e.Y);
            if (i >= 0) SelectedIndex = i;
            else SelectedIndex = -1;
            if (e.Clicks > 1 && i >= 0 && Activated != null) Activated(this, EventArgs.Empty);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - Math.Sign(e.Delta) * 3));
            Invalidate();
        }
        protected override bool Step(float dt) { slide.Step(dt); return !slide.Settled; }

        int ColumnAt(int x)
        {
            int cx = Theme.S(10);
            for (int i = 0; i < Columns.Count; i++) { if (x >= cx && x < cx + Theme.S(Columns[i].Width)) return i; cx += Theme.S(Columns[i].Width); }
            return -1;
        }
        int RowAt(int y)
        {
            if (y < HeaderH) return -1;
            int i = (y - HeaderH) / Theme.S(RowHeight) + scroll;
            return i < Rows.Count ? i : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            if (Rows.Count == 0) { Deco.EmptyState(g, ClientRectangle, Glyph.Search, EmptyTitle, EmptyHint); return; }
            // 헤더
            int cx = Theme.S(10);
            for (int i = 0; i < Columns.Count; i++)
            {
                Column c = Columns[i];
                Rectangle hr = new Rectangle(cx, 0, Theme.S(c.Width), HeaderH);
                bool sorted = i == SortColumn;
                Theme.CaptionText(g, c.Title, sorted ? Theme.Ink : Theme.TextMute, new Rectangle(hr.X, Theme.S(2), hr.Width - Theme.S(6), Theme.S(20)), c.Align);
                if (sorted)
                {
                    int ax = c.Align == Theme.Align.Right ? hr.X + Theme.S(2) : hr.X + Math.Min(hr.Width - Theme.S(10), TextRenderer.MeasureText(g, c.Title, Theme.Caption, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(8));
                    using (Pen p = new Pen(Theme.Accent, Math.Max(1f, Theme.SF(1.4f))) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        float y = HeaderH / 2f;
                        if (SortAscending) g.DrawLines(p, new PointF[] { new PointF(ax, y + Theme.S(2)), new PointF(ax + Theme.S(4), y - Theme.S(3)), new PointF(ax + Theme.S(8), y + Theme.S(2)) });
                        else g.DrawLines(p, new PointF[] { new PointF(ax, y - Theme.S(3)), new PointF(ax + Theme.S(4), y + Theme.S(2)), new PointF(ax + Theme.S(8), y - Theme.S(3)) });
                    }
                }
                cx += Theme.S(c.Width);
            }
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, Theme.S(10), HeaderH - 1, Width - Theme.S(10), HeaderH - 1);

            int rh = Theme.S(RowHeight);
            int top = HeaderH, bottom = Height;
            g.SetClip(new Rectangle(0, HeaderH, Width, bottom - HeaderH));
            for (int i = scroll; i < Rows.Count && top < bottom; i++)
            {
                Rectangle rr = new Rectangle(0, top, Width, rh);
                bool sel = i == selected, hov = i == hover;
                if (sel) Theme.Fill(g, new Rectangle(Theme.S(4), rr.Y + Theme.S(1), Width - Theme.S(8), rh - Theme.S(2)), Theme.S(6), Theme.AccentSoft);
                else if (hov) Theme.Fill(g, new Rectangle(Theme.S(4), rr.Y + Theme.S(1), Width - Theme.S(8), rh - Theme.S(2)), Theme.S(6), Theme.SurfaceHi);
                if (sel)
                    using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, Theme.S(1), rr.Y + Theme.S(6), Theme.S(3), rh - Theme.S(12));
                Row row = Rows[i];
                int x = Theme.S(10);
                for (int c = 0; c < Columns.Count; c++)
                {
                    Column col = Columns[c];
                    Rectangle cell = new Rectangle(x, rr.Y, Theme.S(col.Width), rh);
                    if (c == row.BarColumn && !double.IsNaN(row.Bar))
                    {
                        Rectangle bar = new Rectangle(cell.X, rr.Y + Theme.S(10), Math.Min(Theme.S(col.Width) - Theme.S(24), Theme.S(96)), Theme.S(8));
                        Deco.Bar(g, bar, row.Bar, row.BarTone, false);
                    }
                    Color tone = row.Tones != null && c < row.Tones.Length && row.Tones[c] != Color.Empty ? row.Tones[c] : (row.Emphasis ? Theme.Ink : Theme.TextDim);
                    string text = c < row.Cells.Length ? row.Cells[c] : "";
                    if (c == 0 && row.Emphasis)
                    {
                        using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, cell.X, rr.Y + rh / 2 - Theme.S(2), Theme.S(4), Theme.S(4));
                        cell = new Rectangle(cell.X + Theme.S(10), cell.Y, cell.Width - Theme.S(10), cell.Height);
                    }
                    Theme.Text(g, text, row.Emphasis ? Theme.LabelBold : Theme.Body, tone, cell, col.Align);
                    x += Theme.S(col.Width);
                }
                using (Pen p = new Pen(Theme.Alpha(Theme.Border, 120)))
                    g.DrawLine(p, Theme.S(10), rr.Bottom - 1, Width - Theme.S(10), rr.Bottom - 1);
                top += rh;
            }
            g.ResetClip();
            if (Rows.Count > VisibleRows)
            {
                int trackH = Height - HeaderH, barH = Math.Max(Theme.S(24), trackH * VisibleRows / Rows.Count);
                int y = HeaderH + (int)((float)scroll / Math.Max(1, MaxScroll) * (trackH - barH));
                Theme.Fill(g, new Rectangle(Width - Theme.S(5), y, Theme.S(3), barH), Theme.S(2), Theme.BorderStrong);
            }
        }
    }
}
