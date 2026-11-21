// 선 아이콘 세트 — 외부 리소스 없이 코드로 그린다 (24×24 기준, 선 두께 균일)
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace WifiSurvey.Design
{
    enum Glyph
    {
        Wifi, Scan, Record, Stop, Play, Pause, Settings, Export, Folder, Search,
        ChevronDown, ChevronRight, Plus, Trash, Layers, Wall, Area, Ruler, Target, Ap,
        Grid, Undo, Check, Alert, Info, Bars, Pulse, Close, Clock, Sliders, Bolt, Compare, Minus
    }

    static class Icons
    {
        public static void Draw(Graphics g, Glyph icon, RectangleF box, Color color, float weight = 1.9f)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(box.Width, box.Height) / 24f;
            float ox = box.X + (box.Width - 24 * s) / 2f, oy = box.Y + (box.Height - 24 * s) / 2f;
            using (Pen p = new Pen(color, Math.Max(1f, weight * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (SolidBrush b = new SolidBrush(color))
            {
                switch (icon)
                {
                    case Glyph.Wifi:
                        p.Width = Math.Max(1f, 1.9f * s);
                        g.DrawArc(p, ox + 4 * s, oy + 5 * s, 16 * s, 16 * s, 200, 140);
                        g.DrawArc(p, ox + 7.5f * s, oy + 8.5f * s, 9 * s, 9 * s, 200, 140);
                        g.FillEllipse(b, ox + 10.6f * s, oy + 14.6f * s, 2.8f * s, 2.8f * s);
                        break;
                    case Glyph.Scan:
                        g.DrawArc(p, ox + 3 * s, oy + 3 * s, 18 * s, 18 * s, 40, 100);
                        g.DrawLine(p, ox + 19.5f * s, oy + 4.5f * s, ox + 19.5f * s, oy + 9 * s);
                        g.DrawLine(p, ox + 19.5f * s, oy + 4.5f * s, ox + 15 * s, oy + 4.5f * s);
                        g.FillEllipse(b, ox + 9 * s, oy + 9 * s, 6 * s, 6 * s);
                        break;
                    case Glyph.Record:
                        g.FillEllipse(b, ox + 6 * s, oy + 6 * s, 12 * s, 12 * s);
                        break;
                    case Glyph.Stop:
                        g.FillRectangle(b, ox + 6.5f * s, oy + 6.5f * s, 11 * s, 11 * s);
                        break;
                    case Glyph.Play:
                        PointF[] tri = { new PointF(ox + 8 * s, oy + 5 * s), new PointF(ox + 19 * s, oy + 12 * s), new PointF(ox + 8 * s, oy + 19 * s) };
                        g.FillPolygon(b, tri);
                        break;
                    case Glyph.Pause:
                        g.FillRectangle(b, ox + 7 * s, oy + 5.5f * s, 3.6f * s, 13 * s);
                        g.FillRectangle(b, ox + 13.4f * s, oy + 5.5f * s, 3.6f * s, 13 * s);
                        break;
                    case Glyph.Settings:
                        g.DrawEllipse(p, ox + 8.5f * s, oy + 8.5f * s, 7 * s, 7 * s);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            g.DrawLine(p, ox + (float)(12 + Math.Cos(a) * 7.4) * s, oy + (float)(12 + Math.Sin(a) * 7.4) * s,
                                          ox + (float)(12 + Math.Cos(a) * 9.6) * s, oy + (float)(12 + Math.Sin(a) * 9.6) * s);
                        }
                        break;
                    case Glyph.Sliders:
                        g.DrawLine(p, ox + 4 * s, oy + 8 * s, ox + 20 * s, oy + 8 * s);
                        g.DrawLine(p, ox + 4 * s, oy + 16 * s, ox + 20 * s, oy + 16 * s);
                        g.FillEllipse(b, ox + 7 * s, oy + 6 * s, 4 * s, 4 * s);
                        g.FillEllipse(b, ox + 14 * s, oy + 14 * s, 4 * s, 4 * s);
                        break;
                    case Glyph.Export:
                        g.DrawLine(p, ox + 12 * s, oy + 4 * s, ox + 12 * s, oy + 14 * s);
                        g.DrawLine(p, ox + 8 * s, oy + 10.5f * s, ox + 12 * s, oy + 14.5f * s);
                        g.DrawLine(p, ox + 16 * s, oy + 10.5f * s, ox + 12 * s, oy + 14.5f * s);
                        g.DrawLine(p, ox + 5 * s, oy + 19 * s, ox + 19 * s, oy + 19 * s);
                        break;
                    case Glyph.Folder:
                        g.DrawPath(p, Box(new PointF(ox + 3.5f * s, oy + 6 * s), 17 * s, 12 * s, 2 * s * 1.4f));
                        g.DrawLine(p, ox + 4 * s, oy + 9 * s, ox + 20 * s, oy + 9 * s);
                        break;
                    case Glyph.Search:
                        g.DrawEllipse(p, ox + 4 * s, oy + 4 * s, 12 * s, 12 * s);
                        g.DrawLine(p, ox + 15.5f * s, oy + 15.5f * s, ox + 20 * s, oy + 20 * s);
                        break;
                    case Glyph.ChevronDown:
                        g.DrawLine(p, ox + 7 * s, oy + 10 * s, ox + 12 * s, oy + 15 * s);
                        g.DrawLine(p, ox + 17 * s, oy + 10 * s, ox + 12 * s, oy + 15 * s);
                        break;
                    case Glyph.ChevronRight:
                        g.DrawLine(p, ox + 10 * s, oy + 7 * s, ox + 15 * s, oy + 12 * s);
                        g.DrawLine(p, ox + 15 * s, oy + 12 * s, ox + 10 * s, oy + 17 * s);
                        break;
                    case Glyph.Plus:
                        g.DrawLine(p, ox + 12 * s, oy + 5 * s, ox + 12 * s, oy + 19 * s);
                        g.DrawLine(p, ox + 5 * s, oy + 12 * s, ox + 19 * s, oy + 12 * s);
                        break;
                    case Glyph.Minus:
                        g.DrawLine(p, ox + 5 * s, oy + 12 * s, ox + 19 * s, oy + 12 * s);
                        break;
                    case Glyph.Trash:
                        g.DrawLine(p, ox + 4.5f * s, oy + 7 * s, ox + 19.5f * s, oy + 7 * s);
                        g.DrawLine(p, ox + 9.5f * s, oy + 7 * s, ox + 10.5f * s, oy + 4.5f * s);
                        g.DrawLine(p, ox + 14.5f * s, oy + 7 * s, ox + 13.5f * s, oy + 4.5f * s);
                        g.DrawLine(p, ox + 10.5f * s, oy + 4.5f * s, ox + 13.5f * s, oy + 4.5f * s);
                        g.DrawLine(p, ox + 6 * s, oy + 7 * s, ox + 7 * s, oy + 20 * s);
                        g.DrawLine(p, ox + 18 * s, oy + 7 * s, ox + 17 * s, oy + 20 * s);
                        g.DrawLine(p, ox + 7 * s, oy + 20 * s, ox + 17 * s, oy + 20 * s);
                        break;
                    case Glyph.Layers:
                        g.DrawLine(p, ox + 12 * s, oy + 4 * s, ox + 20 * s, oy + 8.5f * s);
                        g.DrawLine(p, ox + 20 * s, oy + 8.5f * s, ox + 12 * s, oy + 13 * s);
                        g.DrawLine(p, ox + 12 * s, oy + 13 * s, ox + 4 * s, oy + 8.5f * s);
                        g.DrawLine(p, ox + 4 * s, oy + 8.5f * s, ox + 12 * s, oy + 4 * s);
                        g.DrawLine(p, ox + 4 * s, oy + 13 * s, ox + 12 * s, oy + 17.5f * s);
                        g.DrawLine(p, ox + 12 * s, oy + 17.5f * s, ox + 20 * s, oy + 13 * s);
                        break;
                    case Glyph.Wall:
                        g.DrawRectangle(p, ox + 3.5f * s, oy + 5.5f * s, 17 * s, 13 * s);
                        g.DrawLine(p, ox + 3.5f * s, oy + 10 * s, ox + 20.5f * s, oy + 10 * s);
                        g.DrawLine(p, ox + 3.5f * s, oy + 14.5f * s, ox + 20.5f * s, oy + 14.5f * s);
                        g.DrawLine(p, ox + 9 * s, oy + 5.5f * s, ox + 9 * s, oy + 10 * s);
                        g.DrawLine(p, ox + 15 * s, oy + 10 * s, ox + 15 * s, oy + 14.5f * s);
                        g.DrawLine(p, ox + 9 * s, oy + 14.5f * s, ox + 9 * s, oy + 18.5f * s);
                        break;
                    case Glyph.Area:
                        using (Pen dash = new Pen(color, Math.Max(1f, 1.6f * s)) { DashStyle = DashStyle.Dash, DashPattern = new float[] { 3f, 2.4f } })
                            g.DrawRectangle(dash, ox + 4 * s, oy + 5 * s, 16 * s, 14 * s);
                        break;
                    case Glyph.Ruler:
                        g.DrawRectangle(p, ox + 3 * s, oy + 8 * s, 18 * s, 8 * s);
                        for (int i = 0; i < 4; i++)
                        {
                            float x = ox + (6 + i * 4) * s;
                            g.DrawLine(p, x, oy + 8 * s, x, oy + (i % 2 == 0 ? 12f : 11f) * s);
                        }
                        break;
                    case Glyph.Target:
                        g.DrawEllipse(p, ox + 4 * s, oy + 4 * s, 16 * s, 16 * s);
                        g.DrawEllipse(p, ox + 9 * s, oy + 9 * s, 6 * s, 6 * s);
                        g.DrawLine(p, ox + 12 * s, oy + 2 * s, ox + 12 * s, oy + 5 * s);
                        g.DrawLine(p, ox + 12 * s, oy + 19 * s, ox + 12 * s, oy + 22 * s);
                        g.DrawLine(p, ox + 2 * s, oy + 12 * s, ox + 5 * s, oy + 12 * s);
                        g.DrawLine(p, ox + 19 * s, oy + 12 * s, ox + 22 * s, oy + 12 * s);
                        break;
                    case Glyph.Ap:
                        g.DrawRectangle(p, ox + 4 * s, oy + 14 * s, 16 * s, 6 * s);
                        g.DrawArc(p, ox + 7 * s, oy + 4 * s, 10 * s, 10 * s, 200, 140);
                        g.DrawArc(p, ox + 9.5f * s, oy + 6.5f * s, 5 * s, 5 * s, 200, 140);
                        g.DrawLine(p, ox + 12 * s, oy + 14 * s, ox + 12 * s, oy + 10 * s);
                        break;
                    case Glyph.Grid:
                        g.DrawRectangle(p, ox + 4 * s, oy + 4 * s, 16 * s, 16 * s);
                        g.DrawLine(p, ox + 12 * s, oy + 4 * s, ox + 12 * s, oy + 20 * s);
                        g.DrawLine(p, ox + 4 * s, oy + 12 * s, ox + 20 * s, oy + 12 * s);
                        break;
                    case Glyph.Undo:
                        g.DrawArc(p, ox + 4 * s, oy + 6 * s, 16 * s, 14 * s, 30, 250);
                        g.DrawLine(p, ox + 4 * s, oy + 6 * s, ox + 4.5f * s, oy + 11.5f * s);
                        g.DrawLine(p, ox + 4 * s, oy + 6 * s, ox + 9.5f * s, oy + 7 * s);
                        break;
                    case Glyph.Check:
                        g.DrawLine(p, ox + 5 * s, oy + 12.5f * s, ox + 10 * s, oy + 17.5f * s);
                        g.DrawLine(p, ox + 10 * s, oy + 17.5f * s, ox + 19 * s, oy + 7 * s);
                        break;
                    case Glyph.Alert:
                        g.DrawPolygon(p, new PointF[] { new PointF(ox + 12 * s, oy + 4 * s), new PointF(ox + 21 * s, oy + 20 * s), new PointF(ox + 3 * s, oy + 20 * s) });
                        g.DrawLine(p, ox + 12 * s, oy + 10 * s, ox + 12 * s, oy + 15 * s);
                        g.FillEllipse(b, ox + 11.1f * s, oy + 16.6f * s, 1.9f * s, 1.9f * s);
                        break;
                    case Glyph.Info:
                        g.DrawEllipse(p, ox + 4 * s, oy + 4 * s, 16 * s, 16 * s);
                        g.DrawLine(p, ox + 12 * s, oy + 11 * s, ox + 12 * s, oy + 16.5f * s);
                        g.FillEllipse(b, ox + 11.1f * s, oy + 7 * s, 1.9f * s, 1.9f * s);
                        break;
                    case Glyph.Bars:
                        g.DrawLine(p, ox + 5 * s, oy + 19.5f * s, ox + 19.5f * s, oy + 19.5f * s);
                        g.FillRectangle(b, ox + 6 * s, oy + 12 * s, 3.4f * s, 7 * s);
                        g.FillRectangle(b, ox + 11.5f * s, oy + 7 * s, 3.4f * s, 12 * s);
                        g.FillRectangle(b, ox + 17 * s, oy + 15 * s, 0.1f * s, 4 * s);
                        break;
                    case Glyph.Pulse:
                        g.DrawLines(p, new PointF[] {
                            new PointF(ox + 3 * s, oy + 13 * s), new PointF(ox + 7.5f * s, oy + 13 * s),
                            new PointF(ox + 10 * s, oy + 6 * s), new PointF(ox + 13 * s, oy + 18 * s),
                            new PointF(ox + 15.5f * s, oy + 11 * s), new PointF(ox + 21 * s, oy + 11 * s) });
                        break;
                    case Glyph.Close:
                        g.DrawLine(p, ox + 6 * s, oy + 6 * s, ox + 18 * s, oy + 18 * s);
                        g.DrawLine(p, ox + 18 * s, oy + 6 * s, ox + 6 * s, oy + 18 * s);
                        break;
                    case Glyph.Clock:
                        g.DrawEllipse(p, ox + 4 * s, oy + 4 * s, 16 * s, 16 * s);
                        g.DrawLine(p, ox + 12 * s, oy + 7.5f * s, ox + 12 * s, oy + 12.5f * s);
                        g.DrawLine(p, ox + 12 * s, oy + 12.5f * s, ox + 15.5f * s, oy + 14.5f * s);
                        break;
                    case Glyph.Bolt:
                        g.DrawPolygon(p, new PointF[] { new PointF(ox + 13 * s, oy + 3 * s), new PointF(ox + 6 * s, oy + 13.5f * s), new PointF(ox + 11.5f * s, oy + 13.5f * s), new PointF(ox + 10 * s, oy + 21 * s), new PointF(ox + 18 * s, oy + 10 * s), new PointF(ox + 12.5f * s, oy + 10 * s) });
                        break;
                    case Glyph.Compare:
                        g.DrawLine(p, ox + 12 * s, oy + 4 * s, ox + 12 * s, oy + 20 * s);
                        g.DrawRectangle(p, ox + 3 * s, oy + 8 * s, 6 * s, 8 * s);
                        g.DrawRectangle(p, ox + 15 * s, oy + 5 * s, 6 * s, 12 * s);
                        break;
                }
            }
            g.SmoothingMode = old;
        }

        static GraphicsPath Box(PointF p, float w, float h, float r)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = r * 2;
            gp.AddArc(p.X, p.Y, d, d, 180, 90);
            gp.AddArc(p.X + w - d, p.Y, d, d, 270, 90);
            gp.AddArc(p.X + w - d, p.Y + h - d, d, d, 0, 90);
            gp.AddArc(p.X, p.Y + h - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }
    }
}
