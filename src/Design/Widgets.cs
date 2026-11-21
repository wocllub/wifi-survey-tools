// 공통 위젯 — 카드, 버튼, 선택기, 탭, 상태바 (기본 컨트롤 대체)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WifiSurvey.Design
{
    abstract class Widget : Control, IMotion
    {
        protected bool animating;
        public bool WantsFrame { get { return animating; } }

        protected Widget()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }
        public void StepMotion(float dt) { animating = Step(dt); }
        protected virtual bool Step(float dt) { return false; }
        protected void Poke()
        {
            if (!Motion.Enabled) { animating = false; Step(1.0f); Invalidate(); return; }
            Motion.Attach(this);
            animating = true;
        }
        protected void FillBg(Graphics g) { using (SolidBrush b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle); }
    }

    // ---- 카드: 둥근 표면 + 1px 테두리 + 선택적 상단 액센트 ----
    class Card : Widget
    {
        public string Title = "", Sub = "";
        public bool Accent;
        public bool Hoverable;
        public int Pad = 16;
        public Action<Graphics, Rectangle> Body;
        readonly Spring hover = new Spring(0);

        public Rectangle Content
        {
            get
            {
                int p = Theme.S(Pad);
                int top = Title.Length > 0 ? p + Theme.S(30) : p;
                return new Rectangle(p, top, Math.Max(1, Width - p * 2), Math.Max(1, Height - top - p));
            }
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Hoverable) { hover.To(1); Poke(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (Hoverable) { hover.To(0); Poke(); } }
        protected override bool Step(float dt)
        {
            hover.Step(dt);
            return !hover.Settled;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            FillBg(e.Graphics);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.Card(e.Graphics, r, Theme.S(12), hover.Value > 0.5 || Accent);
            if (Accent)
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(b, r.X + Theme.S(12), r.Y, Theme.S(24), Theme.S(2));
            if (Title.Length > 0)
            {
                int p = Theme.S(Pad);
                Theme.Text(e.Graphics, Title, Theme.LabelBold, Theme.Ink, new Rectangle(p, p - Theme.S(4), r.Width - p * 2, Theme.S(24)));
                if (Sub.Length > 0)
                    Theme.Text(e.Graphics, Sub, Theme.F(9f), Theme.TextMute, new Rectangle(p, p + Theme.S(14), r.Width - p * 2, Theme.S(18)));
            }
            PaintBody(e.Graphics);
        }
        protected virtual void PaintBody(Graphics g)
        {
            if (Body != null) Body(g, Content);
        }
    }

    // ---- 버튼 ----
    enum Btn { Primary, Ghost, Subtle, Danger }

    class UiButton : Widget
    {
        public string Label = "";
        public Glyph Glyph = Glyph.Wifi;
        public bool ShowGlyph = true;
        public Btn Kind = Btn.Ghost;
        public bool Active, Toggle;
        readonly Spring hover = new Spring(0), press = new Spring(0);

        public UiButton() { Cursor = Cursors.Hand; TabStop = true; }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); Poke(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0); press.To(0); Poke(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { press.To(1); Poke(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); press.To(0); Poke(); }
        protected override bool Step(float dt) { hover.Step(dt); press.Step(dt); return !hover.Settled || !press.Settled; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            if (!Enabled) { }
            bool on = Active || (Toggle && Active);
            int lift = (int)Math.Round(press.Value * Theme.S(2));
            Rectangle r = new Rectangle(0, lift, Width - 1, Height - 1 - lift);
            Color bg, fg, bd;
            if (Kind == Btn.Primary)
            {
                bg = Theme.Mix(Theme.Accent, Theme.AccentHi, hover.Value * 0.8);
                if (press.Value > 0.5) bg = Theme.AccentLo;
                fg = Color.White; bd = Color.Empty;
            }
            else if (Kind == Btn.Danger)
            {
                bg = Theme.Mix(Theme.Alpha(Theme.Lv1, 26), Theme.Alpha(Theme.Lv1, 46), hover.Value);
                fg = Theme.Lv1; bd = Theme.Alpha(Theme.Lv1, 70);
            }
            else if (Kind == Btn.Subtle)
            {
                bg = Theme.Mix(Theme.SurfaceHi, Theme.SurfaceTop, hover.Value);
                fg = on ? Theme.Ink : Theme.TextDim; bd = Theme.Border;
            }
            else
            {
                bg = Theme.Mix(Color.FromArgb(0, Theme.Surface), Theme.SurfaceHi, hover.Value);
                fg = on ? Theme.Accent : Theme.TextDim; bd = on ? Theme.Alpha(Theme.Accent, 120) : Theme.Mix(Color.FromArgb(0, Theme.Border), Theme.Border, hover.Value);
            }
            if (bd != Color.Empty) { Theme.Fill(g, r, Theme.S(8), bg == Color.Empty ? Theme.Surface : bg); Theme.Stroke(g, r, Theme.S(8), bd); }
            else Theme.Fill(g, r, Theme.S(8), bg);

            bool glyph = ShowGlyph;
            int isz = Theme.S(15);
            int tw = TextRenderer.MeasureText(g, Label, Theme.LabelBold, Size.Empty, TextFormatFlags.NoPadding).Width;
            // 아이콘 + 라벨 폭을 모두 포함해야 라벨이 잘리지 않는다.
            int total = (glyph && Label.Length > 0 ? isz + Theme.S(7) + tw : glyph ? isz : tw);
            int x = r.X + (r.Width - total) / 2;
            if (glyph)
            {
                Icons.Draw(g, Glyph, new RectangleF(x, r.Y + (r.Height - isz) / 2f, isz, isz), fg);
                x += isz + Theme.S(7);
            }
            if (Label.Length > 0)
                Theme.Text(g, Label, Theme.LabelBold, fg, new Rectangle(x, r.Y, tw + Theme.S(4), r.Height));
        }
    }

    // ---- 아이콘 레일 버튼 (음영 분석기 도구) ----
    class RailButton : Widget
    {
        public Glyph Glyph = Glyph.Wifi;
        public string Label = "", Tip = "";
        public bool Active;
        readonly Spring hover = new Spring(0);

        public RailButton() { Cursor = Cursors.Hand; }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); Poke(); Hint.Show(this, Tip.Length > 0 ? Tip : Label); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0); Poke(); Hint.Hide(); }
        protected override bool Step(float dt) { hover.Step(dt); return !hover.Settled; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            Rectangle r = new Rectangle(Theme.S(4), Theme.S(4), Width - Theme.S(8), Height - Theme.S(8));
            if (Active) Theme.Fill(g, r, Theme.S(10), Theme.AccentSoft);
            else if (hover.Value > 0.01) Theme.Fill(g, r, Theme.S(10), Theme.Alpha(Theme.SurfaceHi, (int)(255 * hover.Value)));
            Color fg = Active ? Theme.Accent : Theme.Mix(Theme.TextMute, Theme.Ink, hover.Value);
            int isz = Theme.S(22);
            Icons.Draw(g, Glyph, new RectangleF(r.X + (r.Width - isz) / 2f, r.Y + Theme.S(9), isz, isz), fg);
            Theme.Text(g, Label, Theme.F(8f), fg, new Rectangle(r.X, r.Y + r.Height - Theme.S(19), r.Width, Theme.S(15)), Theme.Align.Center);
            if (Active)
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, 0, r.Y + Theme.S(8), Theme.S(2), r.Height - Theme.S(16));
        }
    }

    // ---- 세그먼트 탭: 선택 표시가 미끄러진다 ----
    class Segmented : Widget
    {
        public readonly List<string> Items = new List<string>();
        public event EventHandler Changed;
        int selected;
        public int Selected
        {
            get { return selected; }
            set
            {
                int i = Math.Max(0, Math.Min(Items.Count - 1, value));
                if (i == selected && slide.Settled) return;
                selected = i;
                slide.Set(i);      // 코드로 지정할 때는 즉시
                Invalidate();
            }
        }
        readonly Spring slide = new Spring(0);
        int hot = -1;

        public Segmented() { Cursor = Cursors.Hand; }
        int SlotWidth { get { return Items.Count == 0 ? 1 : Math.Max(1, (Width - Theme.S(8)) / Items.Count); } }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = Slot(e.X);
            if (i != hot) { hot = i; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
        int Slot(int x)
        {
            int i = (x - Theme.S(4)) / SlotWidth;
            return i < 0 || i >= Items.Count ? -1 : i;
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = Slot(e.X);
            if (i < 0 || i == selected) return;
            selected = i;
            slide.To(i);          // 클릭할 때는 미끄러진다
            Poke();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
        protected override bool Step(float dt) { slide.Step(dt); return !slide.Settled; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.Fill(g, r, Theme.S(9), Theme.Surface);
            Theme.Stroke(g, r, Theme.S(9), Theme.Border);
            int sw = SlotWidth;
            Rectangle pill = new Rectangle(Theme.S(4) + (int)Math.Round(slide.Value * sw), Theme.S(4), sw, Height - Theme.S(8));
            Theme.Fill(g, pill, Theme.S(7), Theme.SurfaceTop);
            Theme.Stroke(g, pill, Theme.S(7), Theme.BorderStrong);
            for (int i = 0; i < Items.Count; i++)
            {
                Rectangle cell = new Rectangle(Theme.S(4) + i * sw, 0, sw, Height);
                Color c = i == selected ? Theme.Ink : (i == hot ? Theme.TextDim : Theme.TextMute);
                Theme.Text(g, Items[i], Theme.LabelBold, c, cell, Theme.Align.Center);
            }
        }
    }

    // ---- 콤보 선택기 (기본 ComboBox 대체) ----
    class Select : Widget
    {
        public readonly List<string> Items = new List<string>();
        int index;
        public int SelectedIndex
        {
            get { return index; }
            set
            {
                if (value < -1 || value >= Items.Count) return;
                bool diff = value != index;
                index = value;
                Invalidate();
                if (diff && Changed != null) Changed(this, EventArgs.Empty);
            }
        }
        public string SelectedItem { get { return index >= 0 && index < Items.Count ? Items[index] : ""; } }
        public event EventHandler Changed;
        readonly Spring hover = new Spring(0);
        DropDown drop;

        public Select() { Cursor = Cursors.Hand; }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); Poke(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0); Poke(); }
        protected override bool Step(float dt) { hover.Step(dt); return !hover.Settled; }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (drop != null && !drop.IsDisposed) { drop.Close(); drop = null; return; }
            drop = new DropDown(this);
            drop.ShowBelow(this);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.Fill(g, r, Theme.S(7), Theme.Mix(Theme.SurfaceHi, Theme.SurfaceTop, hover.Value));
            Theme.Stroke(g, r, Theme.S(7), hover.Value > 0.5 ? Theme.BorderStrong : Theme.Border);
            Theme.Text(g, SelectedItem, Theme.Body, index < 0 ? Theme.TextMute : Theme.Ink,
                new Rectangle(Theme.S(10), 0, Width - Theme.S(34), Height));
            Icons.Draw(g, Glyph.ChevronDown, new RectangleF(Width - Theme.S(24), (Height - Theme.S(12)) / 2f, Theme.S(12), Theme.S(12)), Theme.TextMute, 1.7f);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && drop != null && !drop.IsDisposed) drop.Close();
            base.Dispose(disposing);
        }

        // 바깥을 누르면 닫히는 목록 창
        class DropDown : Form
        {
            readonly Select owner;
            int hot = -1;
            public DropDown(Select o)
            {
                owner = o;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Theme.SurfaceTop;
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                Deactivate += delegate { Close(); };
            }
            public void ShowBelow(Select s)
            {
                int rowH = Theme.S(30);
                int h = Math.Min(Theme.S(300), Math.Max(rowH, owner.Items.Count * rowH + Theme.S(8)));
                Bounds = new Rectangle(s.PointToScreen(new Point(0, s.Height + Theme.S(4))), new Size(s.Width, h));
                Show();
                BringToFront();
                Capture = true;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Theme.SurfaceTop);
                Theme.Stroke(g, new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(8), Theme.BorderStrong);
                int rowH = Theme.S(30);
                for (int i = 0; i < owner.Items.Count; i++)
                {
                    Rectangle r = new Rectangle(Theme.S(4), Theme.S(4) + i * rowH, Width - Theme.S(8), rowH);
                    if (i == hot) Theme.Fill(g, r, Theme.S(6), Theme.SurfaceTop);
                    if (i == owner.SelectedIndex) Theme.Fill(g, r, Theme.S(6), Theme.AccentSoft);
                    Theme.Text(g, owner.Items[i], Theme.Body, i == owner.SelectedIndex ? Theme.Ink : Theme.TextDim,
                        new Rectangle(r.X + Theme.S(8), r.Y, r.Width - Theme.S(16), r.Height));
                    if (i != owner.Items.Count - 1 && i != hot && i != hot - 1)
                        using (Pen p = new Pen(Theme.Alpha(Theme.Border, 120)))
                            g.DrawLine(p, r.X + Theme.S(8), r.Bottom - 1, r.Right - Theme.S(8), r.Bottom - 1);
                }
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int i = (e.Y - Theme.S(4)) / Theme.S(30);
                if (i < 0 || i >= owner.Items.Count) i = -1;
                if (i != hot) { hot = i; Invalidate(); }
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                int i = (e.Y - Theme.S(4)) / Theme.S(30);
                if (i >= 0 && i < owner.Items.Count) owner.SelectedIndex = i;
                Close();
            }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
        }
    }

    // ---- 숫자 스테퍼 (기본 NumericUpDown 대체) ----
    class Stepper : Widget
    {
        public decimal Min = 0, Max = 100, Increment = 1;
        public int Decimals;
        public string Suffix = "";
        decimal val;
        public decimal Value
        {
            get { return val; }
            set
            {
                decimal v = Math.Max(Min, Math.Min(Max, value));
                bool diff = v != val;
                val = v;
                Invalidate();
                if (diff && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }
        public event EventHandler ValueChanged;
        readonly Spring hover = new Spring(0);
        TextBox edit;
        bool hoverUp, hoverDown;

        public Stepper() { Cursor = Cursors.Hand; }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); Poke(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0); hoverUp = hoverDown = false; Poke(); }
        protected override bool Step(float dt) { hover.Step(dt); return !hover.Settled; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool up = e.X > Width - Theme.S(24) && e.Y < Height / 2;
            bool down = e.X > Width - Theme.S(24) && e.Y >= Height / 2;
            if (up != hoverUp || down != hoverDown) { hoverUp = up; hoverDown = down; Invalidate(); }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.X > Width - Theme.S(24))
            {
                Value = val + (e.Y < Height / 2 ? Increment : -Increment);
                return;
            }
            BeginEdit();
        }
        void BeginEdit()
        {
            if (edit != null) return;
            edit = new TextBox { Text = val.ToString(Decimals == 0 ? "0" : "0." + new string('0', Decimals)), BorderStyle = BorderStyle.None, BackColor = Theme.SurfaceTop, ForeColor = Theme.Ink, Font = Theme.Body };
            edit.SetBounds(Theme.S(8), (Height - Theme.S(20)) / 2, Width - Theme.S(36), Theme.S(20));
            edit.KeyDown += delegate (object s, KeyEventArgs k)
            {
                if (k.KeyCode == Keys.Enter) { Commit(); k.SuppressKeyPress = true; }
                if (k.KeyCode == Keys.Escape) { EndEdit(); }
            };
            edit.LostFocus += delegate { Commit(); };
            Controls.Add(edit);
            edit.Focus();
            edit.SelectAll();
        }
        void Commit()
        {
            decimal v;
            if (decimal.TryParse(edit.Text, out v)) Value = v;
            EndEdit();
        }
        void EndEdit()
        {
            if (edit == null) return;
            TextBox t = edit; edit = null;
            Controls.Remove(t); t.Dispose();
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.Fill(g, r, Theme.S(7), Theme.Mix(Theme.SurfaceHi, Theme.SurfaceTop, hover.Value));
            Theme.Stroke(g, r, Theme.S(7), hover.Value > 0.5 ? Theme.BorderStrong : Theme.Border);
            if (edit == null)
                Theme.Mono(g, val.ToString(Decimals == 0 ? "0" : "0." + new string('0', Decimals)) + Suffix, Theme.Body, Theme.Ink,
                    new Rectangle(Theme.S(10), 0, Width - Theme.S(38), Height), Theme.Align.Left);
            float ax = Width - Theme.S(21), aw = Theme.S(11);
            using (Pen pen = new Pen(hoverUp ? Theme.Ink : Theme.TextMute, Math.Max(1f, Theme.SF(1f))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float cy = Height / 2f - Theme.S(7);
                g.DrawLines(pen, new PointF[] { new PointF(ax, cy + Theme.S(4)), new PointF(ax + aw / 2, cy), new PointF(ax + aw, cy + Theme.S(4)) });
                float dy = Height / 2f + Theme.S(7);
                pen.Color = hoverDown ? Theme.Ink : Theme.TextMute;
                g.DrawLines(pen, new PointF[] { new PointF(ax, dy - Theme.S(4)), new PointF(ax + aw / 2, dy), new PointF(ax + aw, dy - Theme.S(4)) });
            }
        }
    }

    // ---- 스위치 ----
    class Switch : Widget
    {
        public bool On;
        public event EventHandler Changed;
        readonly Spring pos = new Spring(0);
        public void Set(bool on) { On = on; pos.Set(on ? 1 : 0); Invalidate(); }
        public Switch() { Cursor = Cursors.Hand; }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            On = !On; pos.To(On ? 1 : 0); Poke();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
        protected override bool Step(float dt) { pos.Step(dt); return !pos.Settled; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            int h = Math.Min(Height - Theme.S(8), Theme.S(20));
            Rectangle r = new Rectangle(Width - Theme.S(44), (Height - h) / 2, Theme.S(38), h);
            Theme.Fill(g, r, h / 2, Theme.Mix(Theme.SurfaceTop, Theme.Accent, pos.Value));
            Theme.Stroke(g, r, h / 2, Theme.Mix(Theme.BorderStrong, Theme.Accent, pos.Value));
            int k = h - Theme.S(6);
            int kx = r.X + Theme.S(3) + (int)Math.Round(pos.Value * (r.Width - k - Theme.S(6)));
            using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.TextMute, Color.White, pos.Value)))
                g.FillEllipse(b, kx, r.Y + Theme.S(3), k, k);
        }
    }

    // ---- 상태바: 왼쪽 메시지 + 오른쪽 칩 ----
    class FootBar : Widget
    {
        public string Message = "";
        public bool Live;
        public string LiveText = "";
        public readonly List<string> Chips = new List<string>();
        Color tone = Theme.TextDim;
        DateTime noticeUntil = DateTime.MinValue;
        readonly Spring pulse = new Spring(0);
        bool pulsing;

        public FootBar() { Height = Theme.S(32); }
        public void Notice(string text, Color c, int ms)
        {
            Message = text; tone = c; noticeUntil = DateTime.Now.AddMilliseconds(ms);
            Invalidate();
        }
        public void SetPulse(bool on) { pulsing = on; if (on) Poke(); }
        protected override bool Step(float dt)
        {
            if (!pulsing) return false;
            double t = (DateTime.Now.TimeOfDay.TotalMilliseconds % 2400) / 2400.0;
            pulse.Set(0.5 + 0.5 * Math.Sin(t * Math.PI * 2));
            return true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, 0, 0, Width, 0);
            int x = Theme.P2;
            if (Live)
            {
                int dot = Theme.S(7);
                Theme.Glow(g, new PointF(x + dot / 2f, Height / 2f), dot * 2.4f, Theme.Lv5);
                using (SolidBrush b = new SolidBrush(Theme.Lv5))
                    g.FillEllipse(b, x, Height / 2f - dot / 2f, dot, dot);
                x += dot + Theme.S(7);
                Theme.CaptionText(g, LiveText, Theme.Lv5, new Rectangle(x, 0, Theme.S(86), Height));
                x += Theme.S(90);
            }
            Color c = DateTime.Now < noticeUntil ? tone : Theme.TextMute;
            Theme.Text(g, Message, Theme.F(9f), c, new Rectangle(x, 0, Math.Max(10, Width - x - RightWidth(g) - Theme.P2), Height));
            int rx = Width - Theme.P2;
            for (int i = Chips.Count - 1; i >= 0; i--)
            {
                int w = TextRenderer.MeasureText(g, Chips[i], Theme.F(8.5f), Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(14);
                rx -= w;
                Rectangle r = new Rectangle(rx, Theme.S(6), w, Height - Theme.S(12));
                Theme.Fill(g, r, Theme.S(6), Theme.Surface);
                Theme.Stroke(g, r, Theme.S(6), Theme.Border);
                Theme.Text(g, Chips[i], Theme.F(8.5f), Theme.TextDim, r, Theme.Align.Center);
                rx -= Theme.S(6);
            }
        }
        int RightWidth(Graphics g)
        {
            int w = Theme.P2;
            for (int i = 0; i < Chips.Count; i++) w += TextRenderer.MeasureText(g, Chips[i], Theme.F(8.5f), Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(20);
            return w;
        }
    }

    // ---- 검색 상자: 비어 있을 때 안내 문구를 직접 그린다 ----
    class SearchBox : TextBox
    {
        public string Placeholder = "";

        public SearchBox()
        {
            BorderStyle = BorderStyle.None;
            BackColor = Theme.SurfaceHi;
            ForeColor = Theme.Ink;
            Font = Theme.Body;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F && Placeholder.Length > 0 && Text.Length == 0)   // WM_PAINT
            {
                using (Graphics g = CreateGraphics())
                    TextRenderer.DrawText(g, Placeholder, Font, new Rectangle(Theme.S(3), 0, Width - Theme.S(6), Height), Theme.TextMute,
                        TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // ---- 툴팁: 직접 그리는 어두운 말풍선 ----
    static class Hint
    {
        static Form win;
        static Label text;
        static Timer delay;

        static Control pendingAnchor;
        static string pendingText;

        public static void Show(Control anchor, string s)
        {
            if (s == null || s.Length == 0) return;
            if (delay == null)
            {
                delay = new Timer { Interval = 420 };
                delay.Tick += delegate
                {
                    delay.Stop();
                    if (pendingAnchor != null && pendingText != null) Pop(pendingAnchor, pendingText);
                };
            }
            delay.Stop();
            pendingAnchor = anchor;
            pendingText = s;
            delay.Start();
        }

        public static void Hide()
        {
            if (delay != null) delay.Stop();
            if (win != null) { win.Hide(); }
        }
        static void Pop(Control anchor, string s)
        {
            if (win == null)
            {
                win = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, BackColor = Theme.SurfaceTop };
                text = new Label { AutoSize = true, BackColor = Theme.SurfaceTop, ForeColor = Theme.Ink, Font = Theme.F(9f), Padding = new Padding(Theme.S(8), Theme.S(5), Theme.S(8), Theme.S(5)) };
                win.Controls.Add(text);
                win.Paint += delegate (object o, PaintEventArgs e) { Theme.Stroke(e.Graphics, new Rectangle(0, 0, win.Width - 1, win.Height - 1), Theme.S(6), Theme.BorderStrong); };
            }
            text.Text = s;
            win.Size = text.PreferredSize;
            if (win.Width < Theme.S(60)) win.Width = Theme.S(60);
            Point p = anchor.PointToScreen(new Point(anchor.Width + Theme.S(8), (anchor.Height - win.Height) / 2));
            win.Location = p;
            win.Show(anchor.FindForm());
            win.BringToFront();
        }
    }
}
