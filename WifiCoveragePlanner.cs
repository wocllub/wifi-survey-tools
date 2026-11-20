// Wi-Fi 음영지역 분석기 - 2D 도면 AP 배치, 신호 예측, 현장 실측
// 빌드: build.bat (Windows 기본 포함 .NET Framework 4.x의 csc.exe 사용)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WifiCoveragePlanner
{
    // ---- 프로젝트 데이터 (단위: 미터) ----
    [DataContract] public class ApItem
    {
        [DataMember] public string Vendor = "", Model = "", Name = "";
        [DataMember] public double X, Y, Height = 3;
        [DataMember] public double Tx24 = 15, Tx5 = 18, Tx6 = 18, Gain24 = 4, Gain5 = 5, Gain6 = 5;
        [DataMember] public bool Has6;
    }
    [DataContract] public class WallItem
    {
        [DataMember] public double X1, Y1, X2, Y2;
        [DataMember] public int Material;
    }
    [DataContract] public class Reading
    {
        [DataMember] public string Ssid = "", Bssid = "";
        [DataMember] public int Band, Rssi;
    }
    [DataContract] public class SurveyPoint
    {
        [DataMember] public double X, Y;
        [DataMember] public string Time = "";
        [DataMember] public List<Reading> Readings = new List<Reading>();
    }
    [DataContract] public class Project
    {
        [DataMember] public string PlanImageBase64;
        [DataMember] public double PixelsPerMeter = 20, PlanW = 40, PlanH = 25;
        [DataMember] public bool ScaleSet = true, HasArea;
        [DataMember] public double AreaX, AreaY, AreaW, AreaH;
        [DataMember] public double Threshold = -67, Exponent = 2.6;
        [DataMember] public int Band = 1, View;
        [DataMember] public string SurveySsid = "";
        [DataMember] public List<ApItem> Aps = new List<ApItem>();
        [DataMember] public List<WallItem> Walls = new List<WallItem>();
        [DataMember] public List<SurveyPoint> Points = new List<SurveyPoint>();
    }

    static class Json
    {
        public static byte[] Bytes(Project p)
        {
            using (MemoryStream m = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(Project)).WriteObject(m, p);
                return m.ToArray();
            }
        }
        public static Project Parse(byte[] b)
        {
            using (MemoryStream m = new MemoryStream(b))
                return (Project)new DataContractJsonSerializer(typeof(Project)).ReadObject(m);
        }
        public static Project Clone(Project p) { return Parse(Bytes(p)); }
    }

    // ---- AP 제품 목록 ----
    public class ApModel
    {
        public string Vendor, Name, Gen;
        public double G24, G5, G6;
        public bool Verified;
        public bool Has6 { get { return !double.IsNaN(G6); } }
        public ApModel(string vendor, string name, string gen, double g24, double g5, double g6, bool verified)
        { Vendor = vendor; Name = name; Gen = gen; G24 = g24; G5 = g5; G6 = g6; Verified = verified; }
    }

    static class Catalog
    {
        const double None = double.NaN;
        // Verified=true: 제조사 문서에서 안테나 이득을 확인한 모델. false: 참고값이므로 데이터시트 확인 필요.
        public static readonly ApModel[] All =
        {
            new ApModel("Cisco", "CW9176I", "Wi-Fi 7", 4, 5, 5, false),
            new ApModel("Cisco", "CW9166I", "Wi-Fi 6E", 3, 5, 4, false),
            new ApModel("Cisco", "C9136I", "Wi-Fi 6E", 4, 6, 6, false),
            new ApModel("Cisco", "C9130AXI", "Wi-Fi 6", 4, 5, None, false),
            new ApModel("Cisco", "C9120AXI", "Wi-Fi 6", 4, 5, None, false),
            new ApModel("HPE Aruba", "AP-735", "Wi-Fi 7", 4, 6, 6, false),
            new ApModel("HPE Aruba", "AP-655", "Wi-Fi 6E", 4.8, 5.3, 5.4, false),
            new ApModel("HPE Aruba", "AP-635", "Wi-Fi 6E", 4.6, 7.0, 6.3, true),
            new ApModel("HPE Aruba", "AP-515", "Wi-Fi 6", 4.2, 7.5, None, true),
            new ApModel("Juniper Mist", "AP47", "Wi-Fi 7", 4, 6, 6, true),
            new ApModel("Juniper Mist", "AP45", "Wi-Fi 6E", 3, 5, 5, true),
            new ApModel("Juniper Mist", "AP34", "Wi-Fi 6E", 4, 6, 6, true),
            new ApModel("Juniper Mist", "AP43", "Wi-Fi 6", 4, 6, None, true),
            new ApModel("Juniper Mist", "AP32", "Wi-Fi 6", 5, 6, None, true),
            new ApModel("Grandstream", "GWN7672", "Wi-Fi 7", 3.5, 5.5, 5.5, true),
            new ApModel("Grandstream", "GWN7672L", "Wi-Fi 7", 3.5, 5.5, 5.5, false),
            new ApModel("Grandstream", "GWN7670", "Wi-Fi 7", 4, 5, None, false),
            new ApModel("Grandstream", "GWN7664", "Wi-Fi 6", 3, 4, None, true),
            new ApModel("Grandstream", "GWN7664ELR", "Wi-Fi 6", 5.5, 6.5, None, true),
            new ApModel("Grandstream", "GWN7660", "Wi-Fi 6", 3, 4, None, false),
            new ApModel("H3C", "WA7638", "Wi-Fi 7", 4, 5, 5, false),
            new ApModel("H3C", "WA6638", "Wi-Fi 6", 4.8, 5.3, None, true),
            new ApModel("H3C", "WA6628", "Wi-Fi 6", 3, 4, None, false),
            new ApModel("기타", "일반 AP (직접 입력)", "-", 4, 5, 5, false),
        };
        public static string[] Vendors { get { return All.Select(m => m.Vendor).Distinct().ToArray(); } }
        public static Color VendorColor(string v)
        {
            switch (v)
            {
                case "Cisco": return Color.FromArgb(0, 92, 170);
                case "HPE Aruba": return Color.FromArgb(214, 96, 16);
                case "Juniper Mist": return Color.FromArgb(0, 128, 120);
                case "Grandstream": return Color.FromArgb(120, 70, 180);
                case "H3C": return Color.FromArgb(150, 110, 0);
                default: return Color.FromArgb(90, 96, 108);
            }
        }
        public static string VendorMark(string v)
        {
            switch (v) { case "Cisco": return "C"; case "HPE Aruba": return "A"; case "Juniper Mist": return "M"; case "Grandstream": return "G"; case "H3C": return "H"; default: return "AP"; }
        }
    }

    class Material
    {
        public string Name; public double[] Loss; public Color Color;
        public Material(string n, double a, double b, double c, Color col) { Name = n; Loss = new[] { a, b, c }; Color = col; }
        // 대역별(2.4/5/6 GHz) 통과 손실 dB. 일반적인 설계 추정치.
        public static readonly Material[] All =
        {
            new Material("석고보드", 3, 4, 5, Color.FromArgb(120, 130, 145)),
            new Material("유리", 2, 4, 5, Color.FromArgb(40, 150, 200)),
            new Material("목재 문", 3, 4, 5, Color.FromArgb(150, 100, 50)),
            new Material("벽돌", 6, 10, 12, Color.FromArgb(170, 80, 50)),
            new Material("콘크리트", 12, 20, 25, Color.FromArgb(50, 55, 65)),
            new Material("금속 / 엘리베이터", 25, 30, 35, Color.FromArgb(120, 40, 140)),
        };
    }

    // ---- 신호 계산 ----
    public class HeatResult
    {
        public int Cols, Rows; public double Cell;
        public float[] V;
        public Bitmap Image;
        public List<RectangleF> DeadRuns = new List<RectangleF>();
        public int Analyzed, Covered, Dead;
        public double CoveragePct { get { return Analyzed == 0 ? 0 : 100.0 * Covered / Analyzed; } }
        public double DeadM2 { get { return Dead * Cell * Cell; } }
        public double AnalyzedM2 { get { return Analyzed * Cell * Cell; } }
    }

    static class Rf
    {
        public static readonly double[] FreqMhz = { 2437, 5500, 6500 };
        public static readonly string[] BandNames = { "2.4 GHz", "5 GHz", "6 GHz" };
        public const double ClientHeight = 1.0, IdwRadius = 8.0, NoSignal = -100;

        public static bool Crosses(double ax, double ay, double bx, double by, WallItem w)
        {
            double d1 = Cross(w.X1, w.Y1, w.X2, w.Y2, ax, ay), d2 = Cross(w.X1, w.Y1, w.X2, w.Y2, bx, by);
            if (Math.Abs(d2) < 1e-9) return false; // 벽 위의 지점은 벽을 통과한 것으로 보지 않는다
            double d3 = Cross(ax, ay, bx, by, w.X1, w.Y1), d4 = Cross(ax, ay, bx, by, w.X2, w.Y2);
            return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0));
        }
        static double Cross(double ax, double ay, double bx, double by, double cx, double cy)
        { return (bx - ax) * (cy - ay) - (by - ay) * (cx - ax); }

        public static double FromAp(Project p, ApItem ap, double x, double y, int band)
        {
            if (band == 2 && !ap.Has6) return double.NegativeInfinity;
            double tx = band == 0 ? ap.Tx24 : band == 1 ? ap.Tx5 : ap.Tx6;
            double gain = band == 0 ? ap.Gain24 : band == 1 ? ap.Gain5 : ap.Gain6;
            double dx = ap.X - x, dy = ap.Y - y, dz = ap.Height - ClientHeight;
            double d = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            double loss = 20 * Math.Log10(FreqMhz[band]) - 27.55 + 10 * p.Exponent * Math.Log10(d);
            foreach (WallItem w in p.Walls)
                if (Crosses(ap.X, ap.Y, x, y, w)) loss += Material.All[w.Material].Loss[band];
            return tx + gain - loss;
        }

        public static double Best(Project p, double x, double y, int band, out ApItem server)
        {
            double best = double.NegativeInfinity; server = null;
            foreach (ApItem ap in p.Aps)
            {
                double v = FromAp(p, ap, x, y, band);
                if (v > best) { best = v; server = ap; }
            }
            return best;
        }

        public static double PointValue(Project p, SurveyPoint pt)
        {
            IEnumerable<Reading> r = pt.Readings.Where(a => a.Band == p.Band);
            if (!string.IsNullOrEmpty(p.SurveySsid)) r = r.Where(a => a.Ssid == p.SurveySsid);
            int[] v = r.Select(a => a.Rssi).ToArray();
            return v.Length == 0 ? NoSignal : v.Max();
        }

        static bool InArea(Project p, double x, double y)
        {
            return !p.HasArea || (x >= p.AreaX && x <= p.AreaX + p.AreaW && y >= p.AreaY && y <= p.AreaY + p.AreaH);
        }

        // mode 0: 예측, 1: 실측(IDW 보간)
        public static HeatResult Compute(Project p, int mode)
        {
            double cell = Math.Max(0.05, Math.Max(p.PlanW, p.PlanH) / 300.0);
            int cols = Math.Max(1, (int)Math.Ceiling(p.PlanW / cell)), rows = Math.Max(1, (int)Math.Ceiling(p.PlanH / cell));
            HeatResult h = new HeatResult { Cols = cols, Rows = rows, Cell = cell, V = new float[cols * rows] };
            double[][] pts = p.Points.Select(a => new[] { a.X, a.Y, PointValue(p, a) }).ToArray();
            Parallel.For(0, rows, j =>
            {
                for (int i = 0; i < cols; i++)
                {
                    double x = (i + 0.5) * cell, y = (j + 0.5) * cell;
                    float v = float.NaN;
                    if (InArea(p, x, y))
                    {
                        if (mode == 0 && p.Aps.Count > 0)
                        {
                            ApItem s; double b = Best(p, x, y, p.Band, out s);
                            v = (float)Math.Max(NoSignal, b);
                        }
                        else if (mode == 1) v = Idw(pts, x, y);
                    }
                    h.V[j * cols + i] = v;
                }
            });
            Finish(h, p.Threshold);
            return h;
        }

        public static float Idw(double[][] pts, double x, double y)
        {
            double sum = 0, weight = 0;
            foreach (double[] a in pts)
            {
                double dx = a[0] - x, dy = a[1] - y, d2 = dx * dx + dy * dy;
                if (d2 > IdwRadius * IdwRadius) continue;
                if (d2 < 1e-4) return (float)a[2];
                double w = 1.0 / d2;
                sum += w * a[2]; weight += w;
            }
            return weight == 0 ? float.NaN : (float)(sum / weight);
        }

        static void Finish(HeatResult h, double thr)
        {
            int[] px = new int[h.V.Length];
            for (int j = 0; j < h.Rows; j++)
            {
                int runStart = -1;
                for (int i = 0; i <= h.Cols; i++)
                {
                    bool dead = false;
                    if (i < h.Cols)
                    {
                        float v = h.V[j * h.Cols + i];
                        if (!float.IsNaN(v))
                        {
                            h.Analyzed++;
                            dead = v < thr;
                            if (dead) h.Dead++; else h.Covered++;
                            px[j * h.Cols + i] = ColorOf(v, thr).ToArgb();
                        }
                    }
                    if (dead && runStart < 0) runStart = i;
                    if (!dead && runStart >= 0)
                    {
                        h.DeadRuns.Add(new RectangleF((float)(runStart * h.Cell), (float)(j * h.Cell), (float)((i - runStart) * h.Cell), (float)h.Cell));
                        runStart = -1;
                    }
                }
            }
            Bitmap bmp = new Bitmap(h.Cols, h.Rows, PixelFormat.Format32bppArgb);
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, h.Cols, h.Rows), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int j = 0; j < h.Rows; j++)
                Marshal.Copy(px, j * h.Cols, new IntPtr(d.Scan0.ToInt64() + j * d.Stride), h.Cols);
            bmp.UnlockBits(d);
            h.Image = bmp;
        }

        public static Color ColorOf(double v, double thr)
        {
            if (v < thr) return Color.FromArgb(165, 190, 28, 48);
            double t = Math.Max(0, Math.Min(1, (v - thr) / Math.Max(1, -45 - thr)));
            Color a = Color.FromArgb(245, 196, 40), b = Color.FromArgb(132, 192, 58), c = Color.FromArgb(12, 128, 84);
            Color x = t < 0.5 ? a : b, y = t < 0.5 ? b : c;
            double k = t < 0.5 ? t * 2 : (t - 0.5) * 2;
            return Color.FromArgb(145, (int)(x.R + (y.R - x.R) * k), (int)(x.G + (y.G - x.G) * k), (int)(x.B + (y.B - x.B) * k));
        }
    }

    // ---- 실제 Wi-Fi 측정 (Native Wifi API) ----
    class MeasureResult
    {
        public List<Reading> Readings = new List<Reading>();
        public string ConnectedSsid = "", Error = "";
        public int ConnectedBand = -1, ConnectedRssi;
    }

    static class WifiReader
    {
        [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint v, IntPtr r, out uint n, out IntPtr h);
        [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr h, IntPtr r);
        [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr h, IntPtr r, out IntPtr l);
        [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr h, ref Guid g, int op, IntPtr r, out int s, out IntPtr d, IntPtr t);
        [DllImport("wlanapi.dll")] static extern uint WlanGetNetworkBssList(IntPtr h, ref Guid g, IntPtr s, int t, bool e, IntPtr r, out IntPtr l);
        [DllImport("wlanapi.dll")] static extern uint WlanScan(IntPtr h, ref Guid g, IntPtr s, IntPtr i, IntPtr r);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

        static string Ssid(IntPtr p)
        {
            int len = Math.Max(0, Math.Min(32, Marshal.ReadInt32(p, 0)));
            byte[] b = new byte[len];
            Marshal.Copy(new IntPtr(p.ToInt64() + 4), b, 0, len);
            string s = Encoding.UTF8.GetString(b).Trim('\0');
            return s.Length == 0 ? "(숨겨진 네트워크)" : s;
        }
        static string Mac(IntPtr p)
        {
            byte[] b = new byte[6];
            Marshal.Copy(p, b, 0, 6);
            return BitConverter.ToString(b).Replace('-', ':');
        }

        public static MeasureResult Measure(int waitMs)
        {
            MeasureResult m = new MeasureResult();
            uint n; IntPtr h;
            uint rc = WlanOpenHandle(2, IntPtr.Zero, out n, out h);
            if (rc != 0) { m.Error = "Wi-Fi 서비스에 연결할 수 없습니다. (코드 " + rc + ")"; return m; }
            try
            {
                IntPtr list;
                if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0 || Marshal.ReadInt32(list, 0) == 0)
                { m.Error = "Wi-Fi 어댑터를 찾을 수 없습니다."; return m; }
                Guid id = Guid.Empty; int count = Marshal.ReadInt32(list, 0);
                for (int i = 0; i < count; i++)
                {
                    IntPtr it = new IntPtr(list.ToInt64() + 8 + i * 532);
                    byte[] g = new byte[16];
                    Marshal.Copy(it, g, 0, 16);
                    if (i == 0 || Marshal.ReadInt32(it, 528) == 1) id = new Guid(g);
                    if (Marshal.ReadInt32(it, 528) == 1) break;
                }
                WlanFreeMemory(list);

                WlanScan(h, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(waitMs);

                IntPtr bss;
                rc = WlanGetNetworkBssList(h, ref id, IntPtr.Zero, 3, false, IntPtr.Zero, out bss);
                if (rc != 0)
                {
                    m.Error = rc == 5 ? "Windows 설정 > 개인 정보 및 보안 > 위치에서 위치 서비스를 켜 주세요." : "주변 네트워크를 읽지 못했습니다. (코드 " + rc + ")";
                    return m;
                }
                int items = Marshal.ReadInt32(bss, 4);
                for (int i = 0; i < items; i++)
                {
                    IntPtr e = new IntPtr(bss.ToInt64() + 8 + i * 360);
                    int mhz = Marshal.ReadInt32(e, 92) / 1000;
                    m.Readings.Add(new Reading
                    {
                        Ssid = Ssid(e),
                        Bssid = Mac(new IntPtr(e.ToInt64() + 40)),
                        Rssi = Marshal.ReadInt32(e, 56),
                        Band = mhz < 3000 ? 0 : mhz < 5925 ? 1 : 2
                    });
                }
                WlanFreeMemory(bss);

                int size; IntPtr c;
                if (WlanQueryInterface(h, ref id, 7, IntPtr.Zero, out size, out c, IntPtr.Zero) == 0)
                {
                    if (Marshal.ReadInt32(c, 0) == 1)
                    {
                        m.ConnectedSsid = Ssid(new IntPtr(c.ToInt64() + 520));
                        string mac = Mac(new IntPtr(c.ToInt64() + 560));
                        Reading mine = m.Readings.FirstOrDefault(r => r.Bssid == mac);
                        IntPtr r2;
                        if (WlanQueryInterface(h, ref id, 0x10000102, IntPtr.Zero, out size, out r2, IntPtr.Zero) == 0)
                        {
                            m.ConnectedRssi = Marshal.ReadInt32(r2, 0);
                            WlanFreeMemory(r2);
                            if (mine != null) mine.Rssi = m.ConnectedRssi; // 연결 중인 AP는 최신 값으로 교체
                        }
                        if (mine != null) m.ConnectedBand = mine.Band;
                    }
                    WlanFreeMemory(c);
                }
            }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
            return m;
        }
    }

    // ---- 화면 그리기 ----
    static class Ui
    {
        public static float K = 1f;
        public static int S(int v) { return (int)Math.Round(v * K); }
        public static readonly Color Bg = Color.FromArgb(243, 245, 248), Ink = Color.FromArgb(28, 33, 41), Muted = Color.FromArgb(98, 107, 120), Accent = Color.FromArgb(0, 103, 192);
        public static Font F(float pt, FontStyle st = FontStyle.Regular) { return new Font("Malgun Gothic", pt, st); }
    }

    class View
    {
        public double Zoom = 20, PanX, PanY;
        public PointF P(double x, double y) { return new PointF((float)(PanX + x * Zoom), (float)(PanY + y * Zoom)); }
        public double WX(double sx) { return (sx - PanX) / Zoom; }
        public double WY(double sy) { return (sy - PanY) / Zoom; }
        public void Fit(Project p, int w, int h)
        {
            Zoom = Math.Max(0.5, Math.Min((w - 40.0) / p.PlanW, (h - 40.0) / p.PlanH));
            PanX = (w - p.PlanW * Zoom) / 2; PanY = (h - p.PlanH * Zoom) / 2;
        }
    }

    static class Scene
    {
        static readonly Font Small = Ui.F(8f), Mark = Ui.F(8.5f, FontStyle.Bold), Label = Ui.F(8.5f, FontStyle.Bold);

        public static void Draw(Graphics g, Project p, Bitmap plan, HeatResult heat, View v, object selected)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PointF o = v.P(0, 0);
            RectangleF pr = new RectangleF(o.X, o.Y, (float)(p.PlanW * v.Zoom), (float)(p.PlanH * v.Zoom));
            g.FillRectangle(Brushes.White, pr);
            if (plan != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(plan, pr);
            }
            else DrawGrid(g, p, v, pr);

            GraphicsState st = g.Save();
            g.SetClip(pr);
            if (heat != null && heat.Image != null && p.View != 2)
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(heat.Image, new RectangleF(o.X, o.Y, (float)(heat.Cols * heat.Cell * v.Zoom), (float)(heat.Rows * heat.Cell * v.Zoom)));
                g.PixelOffsetMode = PixelOffsetMode.Default;
                using (HatchBrush hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(120, 255, 255, 255), Color.Transparent))
                    foreach (RectangleF r in heat.DeadRuns)
                    {
                        PointF a = v.P(r.X, r.Y);
                        g.FillRectangle(hb, a.X, a.Y, (float)(r.Width * v.Zoom) + 0.5f, (float)(r.Height * v.Zoom) + 0.5f);
                    }
            }
            g.Restore(st);

            using (Pen b = new Pen(Color.FromArgb(150, 158, 170))) g.DrawRectangle(b, pr.X, pr.Y, pr.Width, pr.Height);

            if (p.HasArea)
                using (Pen dash = new Pen(Ui.Accent, 2f) { DashStyle = DashStyle.Dash })
                {
                    PointF a = v.P(p.AreaX, p.AreaY);
                    g.DrawRectangle(dash, a.X, a.Y, (float)(p.AreaW * v.Zoom), (float)(p.AreaH * v.Zoom));
                }

            foreach (WallItem w in p.Walls)
            {
                bool sel = ReferenceEquals(w, selected);
                using (Pen pen = new Pen(sel ? Ui.Accent : Material.All[w.Material].Color, Ui.S(sel ? 5 : 3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, v.P(w.X1, w.Y1), v.P(w.X2, w.Y2));
            }
            foreach (SurveyPoint s in p.Points) DrawPoint(g, p, s, v, ReferenceEquals(s, selected));
            foreach (ApItem a in p.Aps) DrawAp(g, a, v, ReferenceEquals(a, selected));
        }

        static void DrawGrid(Graphics g, Project p, View v, RectangleF pr)
        {
            double step = v.Zoom >= 12 ? 1 : 5;
            using (Pen thin = new Pen(Color.FromArgb(236, 239, 243)))
            using (Pen thick = new Pen(Color.FromArgb(218, 223, 230)))
            {
                for (double x = 0; x <= p.PlanW; x += step)
                {
                    PointF a = v.P(x, 0);
                    g.DrawLine(((int)x) % 5 == 0 ? thick : thin, a.X, pr.Top, a.X, pr.Bottom);
                }
                for (double y = 0; y <= p.PlanH; y += step)
                {
                    PointF a = v.P(0, y);
                    g.DrawLine(((int)y) % 5 == 0 ? thick : thin, pr.Left, a.Y, pr.Right, a.Y);
                }
            }
        }

        static void Halo(Graphics g, string s, Font f, Color c, float x, float y)
        {
            SizeF sz = g.MeasureString(s, f);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(225, 255, 255, 255)))
                g.FillRectangle(bg, x - sz.Width / 2, y, sz.Width, sz.Height - 1);
            using (SolidBrush b = new SolidBrush(c))
                g.DrawString(s, f, b, x - sz.Width / 2, y);
        }

        static void DrawAp(Graphics g, ApItem a, View v, bool selected)
        {
            PointF c = v.P(a.X, a.Y);
            float r = Ui.S(14);
            if (selected)
                using (Pen ring = new Pen(Ui.Accent, Ui.S(3))) g.DrawEllipse(ring, c.X - r - 4, c.Y - r - 4, (r + 4) * 2, (r + 4) * 2);
            using (SolidBrush b = new SolidBrush(Catalog.VendorColor(a.Vendor))) g.FillEllipse(b, c.X - r, c.Y - r, r * 2, r * 2);
            using (Pen w = new Pen(Color.White, Ui.S(2))) g.DrawEllipse(w, c.X - r, c.Y - r, r * 2, r * 2);
            string mark = Catalog.VendorMark(a.Vendor);
            SizeF sz = g.MeasureString(mark, Mark);
            g.DrawString(mark, Mark, Brushes.White, c.X - sz.Width / 2, c.Y - sz.Height / 2);
            Halo(g, a.Name, Label, Ui.Ink, c.X, c.Y + r + 3);
        }

        static void DrawPoint(Graphics g, Project p, SurveyPoint s, View v, bool selected)
        {
            PointF c = v.P(s.X, s.Y);
            double value = Rf.PointValue(p, s);
            float r = Ui.S(selected ? 7 : 5);
            Color col = value < p.Threshold ? Color.FromArgb(190, 28, 48) : Color.FromArgb(12, 118, 78);
            using (SolidBrush b = new SolidBrush(col)) g.FillEllipse(b, c.X - r, c.Y - r, r * 2, r * 2);
            using (Pen w = new Pen(selected ? Ui.Accent : Color.White, Ui.S(2))) g.DrawEllipse(w, c.X - r, c.Y - r, r * 2, r * 2);
            Halo(g, value <= Rf.NoSignal ? "신호 없음" : value.ToString("0"), Small, col, c.X, c.Y + r + 1);
        }

        public static void Legend(Graphics g, Project p, int x, int y)
        {
            int w = Ui.S(300), h = Ui.S(58);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(238, 255, 255, 255))) g.FillRectangle(bg, x, y, w, h);
            using (Pen b = new Pen(Color.FromArgb(205, 210, 218))) g.DrawRectangle(b, x, y, w, h);
            int barX = x + Ui.S(88), barY = y + Ui.S(10), barW = w - Ui.S(102), barH = Ui.S(14);
            for (int i = 0; i < barW; i++)
            {
                Color c = Rf.ColorOf(p.Threshold + (-45 - p.Threshold) * (double)i / barW, p.Threshold);
                using (Pen pen = new Pen(Color.FromArgb(255, c))) g.DrawLine(pen, barX + i, barY, barX + i, barY + barH);
            }
            Rectangle dead = new Rectangle(x + Ui.S(12), barY, Ui.S(64), barH);
            using (SolidBrush d = new SolidBrush(Color.FromArgb(190, 28, 48))) g.FillRectangle(d, dead);
            using (HatchBrush hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(130, 255, 255, 255), Color.Transparent)) g.FillRectangle(hb, dead);
            int ty = barY + barH + Ui.S(4);
            using (SolidBrush t = new SolidBrush(Ui.Ink))
            {
                g.DrawString("음영 " + p.Threshold.ToString("0") + " 미만", Small, t, x + Ui.S(8), ty);
                g.DrawString(p.Threshold.ToString("0"), Small, t, barX - Ui.S(2), ty);
                string top = "-45 dBm 이상";
                g.DrawString(top, Small, t, barX + barW - g.MeasureString(top, Small).Width + Ui.S(4), ty);
            }
        }
    }

    class PlanCanvas : Control
    {
        public PlanCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Color.FromArgb(226, 230, 236);
        }
    }

    enum Tool { Select, PlaceAp, Wall, Area, Scale, Survey }

    // ---- 메인 화면 ----
    class MainForm : Form
    {
        public Project P = new Project();
        public Bitmap Plan;
        public HeatResult Heat;
        public object Selected;
        public Tool ActiveTool = Tool.Select;
        public readonly View V = new View();
        public readonly PlanCanvas Canvas = new PlanCanvas();

        readonly List<byte[]> undo = new List<byte[]>();
        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer { Interval = 90 };
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly ToolStripStatusLabel cursorInfo = new ToolStripStatusLabel();
        readonly Dictionary<Tool, RadioButton> toolButtons = new Dictionary<Tool, RadioButton>();
        readonly ComboBox vendorBox = Combo(), wallBox = Combo(), viewBox = Combo(), bandBox = Combo(), envBox = Combo(), ssidBox = Combo();
        readonly ListBox modelList = new ListBox { IntegralHeight = false };
        readonly Label modelInfo = new Label { AutoSize = false }, hint = new Label { AutoSize = false, ForeColor = Ui.Muted };
        readonly Label statMain = new Label { AutoSize = false, Font = Ui.F(20f, FontStyle.Bold) }, statSub = new Label { AutoSize = false };
        readonly NumericUpDown thrBox = Nud(-90, -50, 0, 1);
        readonly Panel apPanel = new Panel();
        readonly TextBox apName = new TextBox();
        readonly Label apModel = new Label { AutoSize = false, ForeColor = Ui.Muted };
        readonly NumericUpDown apHeight = Nud(1, 15, 1, 0.5m), apTx24 = Nud(0, 30, 0, 1), apTx5 = Nud(0, 30, 0, 1), apTx6 = Nud(0, 30, 0, 1);

        static readonly double[] EnvValues = { 2.2, 2.6, 3.0, 3.4 };
        bool loading, dirty, measuring, dragging, panning, panMoved, areaDrag, hasFirst;
        double firstX, firstY, curX, curY;
        Point lastMouse;
        int heatVersion;
        string filePath;
        public bool Quiet;

        static ComboBox Combo() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; }
        static NumericUpDown Nud(decimal min, decimal max, int dec, decimal inc)
        { return new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = dec, Increment = inc }; }

        public MainForm()
        {
            Text = "Wi-Fi 음영지역 분석기";
            Font = Ui.F(9.5f);
            BackColor = Ui.Bg;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size(Math.Min(Ui.S(1000), wa.Width), Math.Min(Ui.S(680), wa.Height));
            Size = new Size(Math.Min(Ui.S(1400), wa.Width), Math.Min(Ui.S(900), wa.Height));

            Canvas.Dock = DockStyle.Fill;
            Controls.Add(Canvas);
            Controls.Add(BuildRight());
            Controls.Add(BuildLeft());
            StatusStrip strip = new StatusStrip();
            strip.Items.Add(status); strip.Items.Add(cursorInfo);
            Controls.Add(strip);
            MenuStrip menu = BuildMenu();
            Controls.Add(menu);
            MainMenuStrip = menu;

            Canvas.Paint += CanvasPaint;
            Canvas.MouseDown += CanvasDown;
            Canvas.MouseMove += CanvasMove;
            Canvas.MouseUp += CanvasUp;
            Canvas.MouseWheel += CanvasWheel;
            Canvas.Resize += delegate { if (!userMovedView) { V.Fit(P, Canvas.Width, Canvas.Height); Canvas.Invalidate(); } };
            debounce.Tick += delegate { debounce.Stop(); StartHeat(); };
            SetTool(Tool.Select);
            SyncViewControls();
            UpdateStats();
        }
        bool userMovedView;

        // ---- 화면 구성 ----
        FlowLayoutPanel Side(DockStyle dock, int width)
        {
            return new FlowLayoutPanel { Dock = dock, Width = Ui.S(width), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(Ui.S(10)), BackColor = Ui.Bg };
        }
        Control Fit(Control c, int width, int height = 0)
        {
            c.Width = Ui.S(width);
            if (height > 0) c.Height = Ui.S(height);
            c.Margin = new Padding(0, 0, 0, Ui.S(5));
            return c;
        }
        Label Head(string s, int width)
        {
            Label l = new Label { Text = s, Font = Ui.F(10f, FontStyle.Bold), ForeColor = Ui.Ink, AutoSize = false, TextAlign = ContentAlignment.BottomLeft };
            Fit(l, width, 28);
            return l;
        }

        Control BuildLeft()
        {
            const int W = 222;
            FlowLayoutPanel f = Side(DockStyle.Left, 256);
            f.Controls.Add(Head("작업 도구", W));
            AddTool(f, Tool.Select, "선택 / 이동", W);
            AddTool(f, Tool.PlaceAp, "AP 배치", W);
            AddTool(f, Tool.Wall, "벽 그리기", W);
            AddTool(f, Tool.Area, "분석 범위 지정", W);
            AddTool(f, Tool.Scale, "축척 설정", W);
            AddTool(f, Tool.Survey, "현장 실측", W);
            f.Controls.Add(Fit(hint, W, 66));

            f.Controls.Add(Head("AP 제품", W));
            vendorBox.Items.AddRange(Catalog.Vendors);
            vendorBox.SelectedIndexChanged += delegate { FillModels(); };
            f.Controls.Add(Fit(vendorBox, W));
            modelList.SelectedIndexChanged += delegate { ShowModel(); };
            modelList.Click += delegate { SetTool(Tool.PlaceAp); };
            f.Controls.Add(Fit(modelList, W, 112));
            f.Controls.Add(Fit(modelInfo, W, 84));

            f.Controls.Add(Head("벽 재질", W));
            foreach (Material m in Material.All) wallBox.Items.Add(m.Name + " (5GHz -" + m.Loss[1] + " dB)");
            wallBox.SelectedIndex = 0;
            wallBox.SelectedIndexChanged += delegate
            {
                WallItem w = Selected as WallItem;
                if (w != null && w.Material != wallBox.SelectedIndex) { Snap(); w.Material = wallBox.SelectedIndex; Changed(); }
            };
            f.Controls.Add(Fit(wallBox, W));
            vendorBox.SelectedIndex = 0;
            return f;
        }

        void AddTool(Control parent, Tool t, string text, int w)
        {
            RadioButton b = new RadioButton { Text = text, Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter, FlatStyle = FlatStyle.Standard };
            b.Click += delegate { SetTool(t); };
            toolButtons[t] = b;
            parent.Controls.Add(Fit(b, w, 32));
        }

        Control BuildRight()
        {
            const int W = 250;
            FlowLayoutPanel f = Side(DockStyle.Right, 284);
            f.Controls.Add(Head("분석 결과", W));
            statMain.Text = "-";
            f.Controls.Add(Fit(statMain, W, 44));
            f.Controls.Add(Fit(statSub, W, 88));

            f.Controls.Add(Head("표시 설정", W));
            viewBox.Items.AddRange(new object[] { "예측 신호", "실측 신호", "히트맵 끄기" });
            viewBox.SelectedIndexChanged += delegate { if (loading) return; P.View = viewBox.SelectedIndex; Changed(false); };
            AddField(f, "보기", viewBox, W);
            bandBox.Items.AddRange(Rf.BandNames);
            bandBox.SelectedIndexChanged += delegate { if (loading) return; P.Band = bandBox.SelectedIndex; Changed(false); };
            AddField(f, "주파수 대역", bandBox, W);
            thrBox.ValueChanged += delegate { if (loading) return; P.Threshold = (double)thrBox.Value; Changed(false); };
            AddField(f, "음영 기준 (dBm)", thrBox, W);
            envBox.Items.AddRange(new object[] { "개방형 공간", "일반 사무실", "밀집 공간", "창고 / 공장" });
            envBox.SelectedIndexChanged += delegate { if (loading) return; P.Exponent = EnvValues[envBox.SelectedIndex]; Changed(false); };
            AddField(f, "공간 유형", envBox, W);
            ssidBox.SelectedIndexChanged += delegate
            {
                if (loading) return;
                P.SurveySsid = ssidBox.SelectedIndex <= 0 ? "" : (string)ssidBox.SelectedItem; Changed(false);
            };
            AddField(f, "실측 SSID", ssidBox, W);

            apPanel.Width = Ui.S(W); apPanel.Height = Ui.S(270);
            apPanel.Margin = new Padding(0, Ui.S(8), 0, 0);
            int y = 0;
            Label h = Head("선택한 AP", W); h.Top = 0; apPanel.Controls.Add(h); y += 30;
            apModel.SetBounds(0, Ui.S(y), Ui.S(W), Ui.S(24)); apPanel.Controls.Add(apModel); y += 26;
            ApRow("이름", apName, ref y, W);
            ApRow("설치 높이 (m)", apHeight, ref y, W);
            ApRow("2.4 GHz 출력 (dBm)", apTx24, ref y, W);
            ApRow("5 GHz 출력 (dBm)", apTx5, ref y, W);
            ApRow("6 GHz 출력 (dBm)", apTx6, ref y, W);
            Button del = new Button { Text = "AP 삭제" };
            del.SetBounds(0, Ui.S(y + 2), Ui.S(W), Ui.S(32));
            del.Click += delegate { DeleteSelected(); };
            apPanel.Controls.Add(del);
            apName.TextChanged += delegate { ApEdited(); };
            foreach (NumericUpDown n in new[] { apHeight, apTx24, apTx5, apTx6 }) n.ValueChanged += delegate { ApEdited(); };
            f.Controls.Add(apPanel);
            return f;
        }

        void AddField(Control parent, string label, Control c, int W)
        {
            TableLayoutPanel t = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, Ui.S(4)), Width = Ui.S(W), Height = Ui.S(32) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(112)));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.Controls.Add(new Label { Text = label, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, 0, 0);
            c.Dock = DockStyle.Fill; c.Margin = new Padding(0, Ui.S(2), 0, 0);
            t.Controls.Add(c, 1, 0);
            parent.Controls.Add(t);
        }

        void ApRow(string label, Control c, ref int y, int W)
        {
            Label l = new Label { Text = label, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
            l.SetBounds(0, Ui.S(y), Ui.S(148), Ui.S(28));
            c.SetBounds(Ui.S(150), Ui.S(y), Ui.S(W - 150), Ui.S(28));
            apPanel.Controls.Add(l); apPanel.Controls.Add(c);
            y += 34;
        }

        MenuStrip BuildMenu()
        {
            MenuStrip m = new MenuStrip { BackColor = Color.White };
            ToolStripMenuItem file = new ToolStripMenuItem("파일");
            Item(file, "새 프로젝트", Keys.Control | Keys.N, delegate { if (ConfirmDiscard()) NewProject(new Project(), null); });
            Item(file, "도면 이미지 불러오기...", Keys.Control | Keys.I, delegate { OpenPlanDialog(); });
            Item(file, "예제 도면 열기", Keys.None, delegate { if (ConfirmDiscard()) NewProject(Demo.Build(), null); });
            file.DropDownItems.Add(new ToolStripSeparator());
            Item(file, "프로젝트 열기...", Keys.Control | Keys.O, delegate { OpenProjectDialog(); });
            Item(file, "프로젝트 저장", Keys.Control | Keys.S, delegate { SaveProjectDialog(false); });
            Item(file, "다른 이름으로 저장...", Keys.Control | Keys.Shift | Keys.S, delegate { SaveProjectDialog(true); });
            file.DropDownItems.Add(new ToolStripSeparator());
            Item(file, "분석 결과 이미지로 내보내기...", Keys.Control | Keys.E, delegate { ExportPngDialog(); });
            Item(file, "실측 데이터 CSV로 내보내기...", Keys.None, delegate { ExportCsvDialog(); });
            ToolStripMenuItem edit = new ToolStripMenuItem("편집");
            Item(edit, "실행 취소", Keys.Control | Keys.Z, delegate { Undo(); });
            Item(edit, "분석 범위 해제", Keys.None, delegate { Snap(); P.HasArea = false; Changed(); });
            Item(edit, "실측 지점 모두 지우기", Keys.None, delegate { Snap(); P.Points.Clear(); Selected = null; RefreshSsids(); Changed(); });
            ToolStripMenuItem view = new ToolStripMenuItem("보기");
            Item(view, "도면 전체 보기", Keys.Control | Keys.D0, delegate { userMovedView = false; V.Fit(P, Canvas.Width, Canvas.Height); Canvas.Invalidate(); });
            m.Items.Add(file); m.Items.Add(edit); m.Items.Add(view);
            return m;
        }
        static void Item(ToolStripMenuItem parent, string text, Keys keys, EventHandler h)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text, null, h);
            if (keys != Keys.None) i.ShortcutKeys = keys;
            parent.DropDownItems.Add(i);
        }

        void FillModels()
        {
            modelList.Items.Clear();
            foreach (ApModel m in Catalog.All.Where(a => a.Vendor == (string)vendorBox.SelectedItem)) modelList.Items.Add(m.Name);
            if (modelList.Items.Count > 0) modelList.SelectedIndex = 0;
        }
        ApModel CurrentModel()
        {
            return Catalog.All.FirstOrDefault(a => a.Vendor == (string)vendorBox.SelectedItem && a.Name == (string)modelList.SelectedItem);
        }
        void ShowModel()
        {
            ApModel m = CurrentModel();
            if (m == null) return;
            modelInfo.Text = m.Gen + "\n안테나 이득 " + m.G24 + " / " + m.G5 + " / " + (m.Has6 ? m.G6 + " dBi" : "없음 dBi").Replace("없음 dBi", "6GHz 없음")
                + "\n(2.4 / 5 / 6 GHz)\n" + (m.Verified ? "제조사 자료 기준" : "참고값: 데이터시트 확인 필요");
            modelInfo.ForeColor = m.Verified ? Ui.Muted : Color.FromArgb(166, 90, 0);
        }

        public void ShowStatus(string text) { status.Text = text; }

        public void SetTool(Tool t)
        {
            ActiveTool = t; hasFirst = false; areaDrag = false;
            foreach (KeyValuePair<Tool, RadioButton> kv in toolButtons) kv.Value.Checked = kv.Key == t;
            switch (t)
            {
                case Tool.Select: hint.Text = "AP를 끌어서 옮기고, 벽이나 지점을 눌러 선택합니다. Delete 키로 삭제합니다."; break;
                case Tool.PlaceAp: hint.Text = "아래에서 제품을 고른 뒤 도면을 눌러 AP를 놓습니다."; break;
                case Tool.Wall: hint.Text = "도면을 차례로 눌러 벽을 잇습니다. 오른쪽 클릭이나 Esc로 끝냅니다."; break;
                case Tool.Area: hint.Text = "건물 내부를 드래그해 지정합니다. 범위 안만 분석합니다."; break;
                case Tool.Scale: hint.Text = "길이를 아는 두 점을 차례로 누른 뒤 실제 거리를 입력합니다."; break;
                case Tool.Survey: hint.Text = "현재 서 있는 위치를 도면에서 누르면 약 4초간 실제 신호를 측정합니다."; break;
            }
            Canvas.Cursor = t == Tool.Select ? Cursors.Default : Cursors.Cross;
            Canvas.Invalidate();
        }

        // ---- 변경 관리 ----
        public void Snap()
        {
            undo.Add(Json.Bytes(P));
            if (undo.Count > 40) undo.RemoveAt(0);
        }
        public void Undo()
        {
            if (undo.Count == 0) return;
            P = Json.Parse(undo[undo.Count - 1]);
            undo.RemoveAt(undo.Count - 1);
            Selected = null; hasFirst = false;
            SyncViewControls(); RefreshSsids(); ShowSelection(); Changed();
        }
        void Changed(bool markDirty = true)
        {
            if (markDirty) dirty = true;
            debounce.Stop(); debounce.Start();
            Canvas.Invalidate();
        }

        void StartHeat()
        {
            int version = ++heatVersion;
            Project copy = Json.Clone(P);
            if (copy.View == 2) { SetHeat(null); return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                HeatResult h = Rf.Compute(copy, copy.View);
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke((Action)delegate { if (version == heatVersion) SetHeat(h); }); }
                catch (InvalidOperationException) { }
            });
        }
        public void ComputeNow() { debounce.Stop(); heatVersion++; SetHeat(P.View == 2 ? null : Rf.Compute(P, P.View)); }
        void SetHeat(HeatResult h)
        {
            if (Heat != null && Heat.Image != null) Heat.Image.Dispose();
            Heat = h; UpdateStats(); Canvas.Invalidate();
        }

        void UpdateStats()
        {
            StringBuilder s = new StringBuilder();
            if (Heat == null || Heat.Analyzed == 0)
            {
                statMain.Text = "-"; statMain.ForeColor = Ui.Ink;
                s.AppendLine(P.View == 1 ? "실측 지점이 아직 없습니다." : P.View == 0 ? "AP를 도면에 배치해 주세요." : "히트맵이 꺼져 있습니다.");
            }
            else
            {
                statMain.Text = "커버리지 " + Heat.CoveragePct.ToString("0.0") + "%";
                statMain.ForeColor = Heat.CoveragePct >= 95 ? Color.FromArgb(12, 118, 78) : Heat.CoveragePct >= 80 ? Color.FromArgb(166, 100, 0) : Color.FromArgb(190, 28, 48);
                s.AppendLine("음영 면적  " + Heat.DeadM2.ToString("0.0") + " m²");
                s.AppendLine((P.View == 1 ? "측정된 면적  " : "분석 면적  ") + Heat.AnalyzedM2.ToString("0.0") + " m²");
            }
            s.AppendLine("AP " + P.Aps.Count + "대 · 벽 " + P.Walls.Count + "개 · 실측 " + P.Points.Count + "곳");
            string err = PredictionError();
            if (err != null) s.AppendLine(err);
            if (!P.ScaleSet) s.AppendLine("축척이 아직 설정되지 않았습니다.");
            statSub.Text = s.ToString();
        }
        string PredictionError()
        {
            if (P.Aps.Count == 0) return null;
            List<double> diff = new List<double>();
            foreach (SurveyPoint pt in P.Points)
            {
                double m = Rf.PointValue(P, pt);
                if (m <= Rf.NoSignal) continue;
                ApItem s; double b = Rf.Best(P, pt.X, pt.Y, P.Band, out s);
                if (!double.IsInfinity(b)) diff.Add(Math.Abs(b - m));
            }
            return diff.Count == 0 ? null : "예측과 실측 차이  평균 " + diff.Average().ToString("0.0") + " dB";
        }

        void SyncViewControls()
        {
            loading = true;
            viewBox.SelectedIndex = P.View; bandBox.SelectedIndex = P.Band;
            thrBox.Value = (decimal)Math.Max(-90, Math.Min(-50, P.Threshold));
            int env = Array.FindIndex(EnvValues, e => Math.Abs(e - P.Exponent) < 0.01);
            envBox.SelectedIndex = env < 0 ? 1 : env;
            loading = false;
            RefreshSsids();
            ShowSelection();
        }
        void RefreshSsids()
        {
            loading = true;
            ssidBox.Items.Clear();
            ssidBox.Items.Add("(모든 SSID)");
            foreach (string s in P.Points.SelectMany(a => a.Readings).Select(r => r.Ssid).Distinct().OrderBy(a => a)) ssidBox.Items.Add(s);
            int i = ssidBox.Items.IndexOf(P.SurveySsid);
            ssidBox.SelectedIndex = i < 0 ? 0 : i;
            loading = false;
        }

        void ShowSelection()
        {
            ApItem a = Selected as ApItem;
            apPanel.Visible = a != null;
            WallItem w = Selected as WallItem;
            if (w != null) wallBox.SelectedIndex = w.Material;
            if (a == null) return;
            loading = true;
            apModel.Text = a.Vendor + " " + a.Model;
            apName.Text = a.Name;
            apHeight.Value = Clamp(apHeight, a.Height); apTx24.Value = Clamp(apTx24, a.Tx24); apTx5.Value = Clamp(apTx5, a.Tx5); apTx6.Value = Clamp(apTx6, a.Tx6);
            apTx6.Enabled = a.Has6;
            loading = false;
        }
        static decimal Clamp(NumericUpDown n, double v) { return Math.Max(n.Minimum, Math.Min(n.Maximum, (decimal)v)); }
        void ApEdited()
        {
            ApItem a = Selected as ApItem;
            if (loading || a == null) return;
            Snap();
            a.Name = apName.Text; a.Height = (double)apHeight.Value;
            a.Tx24 = (double)apTx24.Value; a.Tx5 = (double)apTx5.Value; a.Tx6 = (double)apTx6.Value;
            Changed();
        }

        public void DeleteSelected()
        {
            if (Selected == null) return;
            Snap();
            P.Aps.Remove(Selected as ApItem); P.Walls.Remove(Selected as WallItem); P.Points.Remove(Selected as SurveyPoint);
            Selected = null; ShowSelection(); RefreshSsids(); Changed();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { hasFirst = false; areaDrag = false; Canvas.Invalidate(); }
            if (e.KeyCode == Keys.Delete && ActiveControl == Canvas) { DeleteSelected(); e.Handled = true; }
        }

        // ---- 마우스 ----
        double ClampX(double x) { return Math.Max(0, Math.Min(P.PlanW, x)); }
        double ClampY(double y) { return Math.Max(0, Math.Min(P.PlanH, y)); }

        object HitTest(Point m)
        {
            foreach (ApItem a in Enumerable.Reverse(P.Aps))
            {
                PointF c = V.P(a.X, a.Y);
                if (Dist(m.X, m.Y, c.X, c.Y) <= Ui.S(18)) return a;
            }
            foreach (SurveyPoint s in Enumerable.Reverse(P.Points))
            {
                PointF c = V.P(s.X, s.Y);
                if (Dist(m.X, m.Y, c.X, c.Y) <= Ui.S(10)) return s;
            }
            foreach (WallItem w in Enumerable.Reverse(P.Walls))
            {
                PointF a = V.P(w.X1, w.Y1), b = V.P(w.X2, w.Y2);
                if (SegDist(m.X, m.Y, a.X, a.Y, b.X, b.Y) <= Ui.S(7)) return w;
            }
            return null;
        }
        static double Dist(double a, double b, double c, double d) { return Math.Sqrt((a - c) * (a - c) + (b - d) * (b - d)); }
        static double SegDist(double px, double py, double ax, double ay, double bx, double by)
        {
            double l = (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
            double t = l == 0 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * (bx - ax) + (py - ay) * (by - ay)) / l));
            return Dist(px, py, ax + t * (bx - ax), ay + t * (by - ay));
        }

        public void CanvasDown(object sender, MouseEventArgs e)
        {
            Canvas.Focus();
            lastMouse = e.Location;
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle) { panning = true; panMoved = false; return; }
            if (e.Button != MouseButtons.Left) return;
            double x = ClampX(V.WX(e.X)), y = ClampY(V.WY(e.Y));
            switch (ActiveTool)
            {
                case Tool.Select:
                    Selected = HitTest(e.Location);
                    if (Selected is ApItem) { Snap(); dragging = true; }
                    ShowSelection();
                    break;
                case Tool.PlaceAp: PlaceAp(x, y); break;
                case Tool.Wall:
                    if (hasFirst && Dist(firstX, firstY, x, y) > 0.05)
                    {
                        Snap();
                        P.Walls.Add(new WallItem { X1 = firstX, Y1 = firstY, X2 = x, Y2 = y, Material = wallBox.SelectedIndex });
                        Changed();
                    }
                    hasFirst = true; firstX = x; firstY = y;
                    break;
                case Tool.Area: areaDrag = true; firstX = curX = x; firstY = curY = y; break;
                case Tool.Scale:
                    if (!hasFirst) { hasFirst = true; firstX = x; firstY = y; }
                    else
                    {
                        hasFirst = false;
                        double measured = Dist(firstX, firstY, x, y);
                        if (measured < 0.01) break;
                        double real = AskNumber("축척 설정", "두 점 사이의 실제 거리 (m)", measured);
                        if (real > 0) { Snap(); ApplyScale(measured, real); }
                    }
                    break;
                case Tool.Survey: StartMeasure(x, y); break;
            }
            Canvas.Invalidate();
        }

        public void PlaceAp(double x, double y)
        {
            ApModel m = CurrentModel();
            if (m == null) return;
            Snap();
            int n = P.Aps.Count(a => a.Model == m.Name) + 1;
            ApItem ap = new ApItem
            {
                Vendor = m.Vendor, Model = m.Name, Name = m.Name + "-" + n, X = x, Y = y,
                Gain24 = m.G24, Gain5 = m.G5, Gain6 = m.Has6 ? m.G6 : 0, Has6 = m.Has6
            };
            P.Aps.Add(ap);
            Selected = ap; ShowSelection(); Changed();
        }

        public void ApplyScale(double measured, double real)
        {
            double f = real / measured;
            foreach (ApItem a in P.Aps) { a.X *= f; a.Y *= f; }
            foreach (WallItem w in P.Walls) { w.X1 *= f; w.Y1 *= f; w.X2 *= f; w.Y2 *= f; }
            foreach (SurveyPoint s in P.Points) { s.X *= f; s.Y *= f; }
            P.AreaX *= f; P.AreaY *= f; P.AreaW *= f; P.AreaH *= f;
            P.PlanW *= f; P.PlanH *= f; P.PixelsPerMeter /= f; P.ScaleSet = true;
            userMovedView = false; V.Fit(P, Canvas.Width, Canvas.Height);
            status.Text = "축척을 설정했습니다. 도면 크기 " + P.PlanW.ToString("0.0") + " × " + P.PlanH.ToString("0.0") + " m";
            Changed();
        }

        public void CanvasMove(object sender, MouseEventArgs e)
        {
            curX = ClampX(V.WX(e.X)); curY = ClampY(V.WY(e.Y));
            if (panning)
            {
                V.PanX += e.X - lastMouse.X; V.PanY += e.Y - lastMouse.Y;
                if (e.X != lastMouse.X || e.Y != lastMouse.Y) { panMoved = true; userMovedView = true; }
                lastMouse = e.Location;
                Canvas.Invalidate();
            }
            else if (dragging)
            {
                ApItem a = Selected as ApItem;
                if (a != null) { a.X = curX; a.Y = curY; Changed(); }
            }
            else if (hasFirst || areaDrag) Canvas.Invalidate();

            ApItem server; double b = Rf.Best(P, curX, curY, P.Band, out server);
            cursorInfo.Text = curX.ToString("0.0") + ", " + curY.ToString("0.0") + " m"
                + (server != null ? "   예측 " + b.ToString("0") + " dBm (" + server.Name + ")" : "");
        }

        public void CanvasUp(object sender, MouseEventArgs e)
        {
            if (panning)
            {
                panning = false;
                if (e.Button == MouseButtons.Right && !panMoved) { hasFirst = false; Canvas.Invalidate(); }
                return;
            }
            dragging = false;
            if (areaDrag)
            {
                areaDrag = false;
                double x = ClampX(V.WX(e.X)), y = ClampY(V.WY(e.Y));
                double w = Math.Abs(x - firstX), h = Math.Abs(y - firstY);
                if (w > 0.5 && h > 0.5)
                {
                    Snap();
                    P.HasArea = true; P.AreaX = Math.Min(x, firstX); P.AreaY = Math.Min(y, firstY); P.AreaW = w; P.AreaH = h;
                    Changed();
                }
                Canvas.Invalidate();
            }
        }

        void CanvasWheel(object sender, MouseEventArgs e)
        {
            double f = e.Delta > 0 ? 1.15 : 1 / 1.15, wx = V.WX(e.X), wy = V.WY(e.Y);
            V.Zoom = Math.Max(1, Math.Min(400, V.Zoom * f));
            V.PanX = e.X - wx * V.Zoom; V.PanY = e.Y - wy * V.Zoom;
            userMovedView = true;
            Canvas.Invalidate();
        }

        void CanvasPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Scene.Draw(g, P, Plan, Heat, V, Selected);
            if (hasFirst && (ActiveTool == Tool.Wall || ActiveTool == Tool.Scale))
                using (Pen pen = new Pen(Ui.Accent, Ui.S(2)) { DashStyle = DashStyle.Dash })
                {
                    g.DrawLine(pen, V.P(firstX, firstY), V.P(curX, curY));
                    PointF m = V.P(curX, curY);
                    g.DrawString(Dist(firstX, firstY, curX, curY).ToString("0.0") + " m", Font, Brushes.Black, m.X + 10, m.Y + 8);
                }
            if (areaDrag)
                using (Pen pen = new Pen(Ui.Accent, Ui.S(2)) { DashStyle = DashStyle.Dash })
                {
                    PointF a = V.P(Math.Min(firstX, curX), Math.Min(firstY, curY));
                    g.DrawRectangle(pen, a.X, a.Y, (float)(Math.Abs(curX - firstX) * V.Zoom), (float)(Math.Abs(curY - firstY) * V.Zoom));
                }
            if (P.View != 2) Scene.Legend(g, P, Ui.S(12), Canvas.Height - Ui.S(70));
        }

        // ---- 현장 실측 ----
        void StartMeasure(double x, double y)
        {
            if (measuring) return;
            measuring = true;
            status.Text = "측정 중입니다. 그 자리에서 약 4초만 기다려 주세요...";
            Canvas.Cursor = Cursors.WaitCursor;
            ThreadPool.QueueUserWorkItem(delegate
            {
                MeasureResult m;
                try { m = WifiReader.Measure(4000); }
                catch (Exception ex) { m = new MeasureResult { Error = ex.Message }; }
                try { BeginInvoke((Action)delegate { FinishMeasure(x, y, m); }); }
                catch (InvalidOperationException) { }
            });
        }

        public void FinishMeasure(double x, double y, MeasureResult m)
        {
            measuring = false;
            Canvas.Cursor = ActiveTool == Tool.Select ? Cursors.Default : Cursors.Cross;
            if (m.Error.Length > 0) { status.Text = "측정 실패: " + m.Error; return; }
            Snap();
            bool first = P.Points.Count == 0;
            P.Points.Add(new SurveyPoint { X = x, Y = y, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Readings = m.Readings });
            if (first)
            {
                if (m.ConnectedSsid.Length > 0) P.SurveySsid = m.ConnectedSsid;
                if (m.ConnectedBand >= 0) P.Band = m.ConnectedBand;
                P.View = 1;
                SyncViewControls();
            }
            RefreshSsids();
            status.Text = "측정 완료: AP " + m.Readings.Count + "개 감지" + (m.ConnectedSsid.Length > 0 ? ", " + m.ConnectedSsid + " " + m.ConnectedRssi + " dBm" : "");
            Changed();
        }

        // ---- 파일 ----
        bool ConfirmDiscard()
        {
            if (!dirty || Quiet) return true;
            return MessageBox.Show(this, "저장하지 않은 변경 내용이 사라집니다. 계속할까요?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ConfirmDiscard()) e.Cancel = true;
            base.OnFormClosing(e);
        }

        public void NewProject(Project p, Bitmap plan)
        {
            if (Plan != null && !ReferenceEquals(Plan, plan)) Plan.Dispose();
            P = p; Plan = plan; Selected = null; undo.Clear(); dirty = false; hasFirst = false; filePath = null;
            status.Text = "";
            userMovedView = false; V.Fit(P, Canvas.Width, Canvas.Height);
            SyncViewControls(); Changed(false);
        }

        void OpenPlanDialog()
        {
            using (OpenFileDialog d = new OpenFileDialog { Title = "도면 이미지 선택", Filter = "이미지 파일 (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { LoadPlan(d.FileName); SetTool(Tool.Scale); status.Text = "도면을 불러왔습니다. 길이를 아는 두 점을 눌러 축척을 설정해 주세요."; }
                catch (Exception ex) { MessageBox.Show(this, "도면을 열 수 없습니다.\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        public void LoadPlan(string path)
        {
            Bitmap copy;
            using (Image src = Image.FromFile(path))
            {
                double down = Math.Min(1.0, 5000.0 / Math.Max(src.Width, src.Height));
                copy = new Bitmap(src, (int)(src.Width * down), (int)(src.Height * down));
            }
            Snap(); undo.Clear();
            if (Plan != null) Plan.Dispose();
            Plan = copy;
            P.PixelsPerMeter = 20; P.ScaleSet = false; P.HasArea = false;
            P.PlanW = copy.Width / P.PixelsPerMeter; P.PlanH = copy.Height / P.PixelsPerMeter;
            userMovedView = false; V.Fit(P, Canvas.Width, Canvas.Height);
            Changed();
        }

        void OpenProjectDialog()
        {
            if (!ConfirmDiscard()) return;
            using (OpenFileDialog d = new OpenFileDialog { Filter = "음영 분석 프로젝트 (*.wifiplan)|*.wifiplan" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { LoadProject(d.FileName); }
                catch (Exception ex) { MessageBox.Show(this, "프로젝트를 열 수 없습니다.\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        public void LoadProject(string path)
        {
            Project p = Json.Parse(File.ReadAllBytes(path));
            Bitmap plan = null;
            if (!string.IsNullOrEmpty(p.PlanImageBase64))
                using (MemoryStream m = new MemoryStream(Convert.FromBase64String(p.PlanImageBase64)))
                using (Image src = Image.FromStream(m)) plan = new Bitmap(src);
            p.PlanImageBase64 = null;
            NewProject(p, plan);
            filePath = path;
        }

        void SaveProjectDialog(bool saveAs)
        {
            string path = filePath;
            if (saveAs || path == null)
                using (SaveFileDialog d = new SaveFileDialog { Filter = "음영 분석 프로젝트 (*.wifiplan)|*.wifiplan", FileName = "음영분석.wifiplan" })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    path = d.FileName;
                }
            try { SaveProject(path); status.Text = "저장했습니다: " + path; }
            catch (Exception ex) { MessageBox.Show(this, "저장할 수 없습니다.\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        public void SaveProject(string path)
        {
            if (Plan != null)
                using (MemoryStream m = new MemoryStream())
                {
                    Plan.Save(m, ImageFormat.Png);
                    P.PlanImageBase64 = Convert.ToBase64String(m.ToArray());
                }
            try { File.WriteAllBytes(path, Json.Bytes(P)); }
            finally { P.PlanImageBase64 = null; }
            filePath = path; dirty = false;
        }

        void ExportPngDialog()
        {
            using (SaveFileDialog d = new SaveFileDialog { Filter = "PNG 이미지 (*.png)|*.png", FileName = "음영분석결과.png" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { ExportPng(d.FileName); status.Text = "내보냈습니다: " + d.FileName; }
                catch (Exception ex) { MessageBox.Show(this, "내보낼 수 없습니다.\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        public void ExportPng(string path)
        {
            ComputeNow();
            int w = 2000, planH = (int)(w * P.PlanH / P.PlanW), top = Ui.S(70);
            using (Bitmap bmp = new Bitmap(w, planH + top + Ui.S(16)))
            using (Graphics g = Graphics.FromImage(bmp))
            using (Font big = Ui.F(14f, FontStyle.Bold))
            using (Font small = Ui.F(10f))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                string mode = P.View == 1 ? "실측" : "예측";
                string title = "Wi-Fi 음영 분석 (" + mode + " · " + Rf.BandNames[P.Band] + ")";
                string line = Heat != null && Heat.Analyzed > 0
                    ? "커버리지 " + Heat.CoveragePct.ToString("0.0") + "%  ·  음영 " + Heat.DeadM2.ToString("0.0") + " m²  ·  기준 " + P.Threshold.ToString("0") + " dBm  ·  AP " + P.Aps.Count + "대  ·  실측 " + P.Points.Count + "곳"
                    : "분석 데이터 없음";
                g.DrawString(title, big, Brushes.Black, Ui.S(10), Ui.S(6));
                g.DrawString(line, small, Brushes.DimGray, Ui.S(10), Ui.S(38));
                View v = new View { Zoom = (w - 20.0) / P.PlanW, PanX = 10, PanY = top };
                Scene.Draw(g, P, Plan, Heat, v, null);
                Scene.Legend(g, P, w - Ui.S(312), Ui.S(6)); // 도면을 가리지 않도록 상단에 표시
                bmp.Save(path, ImageFormat.Png);
            }
        }

        void ExportCsvDialog()
        {
            using (SaveFileDialog d = new SaveFileDialog { Filter = "CSV 파일 (*.csv)|*.csv", FileName = "실측데이터.csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { ExportCsv(d.FileName); status.Text = "내보냈습니다: " + d.FileName; }
                catch (Exception ex) { MessageBox.Show(this, "내보낼 수 없습니다.\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        public void ExportCsv(string path)
        {
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine("지점,측정시각,X(m),Y(m),SSID,BSSID,대역,신호(dBm)");
                int n = 0;
                foreach (SurveyPoint s in P.Points)
                {
                    n++;
                    foreach (Reading r in s.Readings.OrderByDescending(a => a.Rssi))
                        w.WriteLine(string.Join(",", n, s.Time, s.X.ToString("0.00", CultureInfo.InvariantCulture), s.Y.ToString("0.00", CultureInfo.InvariantCulture), Csv(r.Ssid), r.Bssid, Rf.BandNames[r.Band], r.Rssi));
                }
            }
        }
        static string Csv(string v)
        {
            if (v.Length > 0 && "=+-@".IndexOf(v[0]) >= 0) v = "'" + v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }

        double AskNumber(string title, string prompt, double value)
        {
            using (Form d = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(Ui.S(320), Ui.S(130)), Font = Font, AutoScaleMode = AutoScaleMode.None })
            {
                Label l = new Label { Text = prompt, AutoSize = false }; l.SetBounds(Ui.S(14), Ui.S(12), Ui.S(290), Ui.S(26));
                NumericUpDown n = Nud(0.1m, 10000, 2, 0.5m); n.SetBounds(Ui.S(14), Ui.S(42), Ui.S(290), Ui.S(30));
                n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, (decimal)value));
                Button ok = new Button { Text = "확인", DialogResult = DialogResult.OK }; ok.SetBounds(Ui.S(116), Ui.S(86), Ui.S(90), Ui.S(32));
                Button no = new Button { Text = "취소", DialogResult = DialogResult.Cancel }; no.SetBounds(Ui.S(214), Ui.S(86), Ui.S(90), Ui.S(32));
                d.Controls.AddRange(new Control[] { l, n, ok, no });
                d.AcceptButton = ok; d.CancelButton = no;
                return d.ShowDialog(this) == DialogResult.OK ? (double)n.Value : -1;
            }
        }
    }

    // ---- 예제 도면 ----
    static class Demo
    {
        static void Box(Project p, double x, double y, double w, double h, int m)
        {
            p.Walls.Add(new WallItem { X1 = x, Y1 = y, X2 = x + w, Y2 = y, Material = m });
            p.Walls.Add(new WallItem { X1 = x + w, Y1 = y, X2 = x + w, Y2 = y + h, Material = m });
            p.Walls.Add(new WallItem { X1 = x + w, Y1 = y + h, X2 = x, Y2 = y + h, Material = m });
            p.Walls.Add(new WallItem { X1 = x, Y1 = y + h, X2 = x, Y2 = y, Material = m });
        }
        static ApItem Ap(string model, string name, double x, double y)
        {
            ApModel m = Catalog.All.First(a => a.Name == model);
            return new ApItem { Vendor = m.Vendor, Model = m.Name, Name = name, X = x, Y = y, Gain24 = m.G24, Gain5 = m.G5, Gain6 = m.Has6 ? m.G6 : 0, Has6 = m.Has6 };
        }
        public static Project Build()
        {
            Project p = new Project { PlanW = 48, PlanH = 30, HasArea = true, AreaX = 2, AreaY = 2, AreaW = 44, AreaH = 26 };
            Box(p, 2, 2, 44, 26, 4);       // 외벽
            Box(p, 20, 11, 8, 8, 4);       // 코어
            Box(p, 2, 2, 12, 9, 0);        // 회의실
            Box(p, 14, 2, 10, 9, 1);       // 유리 회의실
            Box(p, 34, 2, 12, 12, 3);      // 서버실
            Box(p, 2, 19, 14, 9, 0);       // 사무실
            p.Aps.Add(Ap("CW9166I", "1F-Cisco-01", 12, 15));
            p.Aps.Add(Ap("AP-635", "1F-Aruba-01", 31, 8));
            p.Aps.Add(Ap("AP45", "1F-Mist-01", 33, 23));
            return p;
        }
    }

    // ---- 자동 시험 ----
    static class SelfTest
    {
        static StringBuilder log = new StringBuilder();
        static int failed;
        static void Check(string name, bool ok, string detail)
        {
            log.AppendLine((ok ? "PASS  " : "FAIL  ") + name + " : " + detail);
            if (!ok) failed++;
        }
        static bool Near(double a, double b, double tol) { return Math.Abs(a - b) <= tol; }
        static void Pump(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(15); }
        }
        static void Click(MainForm f, double x, double y)
        {
            PointF s = f.V.P(x, y);
            f.CanvasDown(f.Canvas, new MouseEventArgs(MouseButtons.Left, 1, (int)Math.Round(s.X), (int)Math.Round(s.Y), 0));
        }

        // kind 0: 누름, 1: 이동, 2: 놓음
        static void Mouse(MainForm f, int kind, double x, double y)
        {
            PointF s = f.V.P(x, y);
            MouseEventArgs e = new MouseEventArgs(kind == 1 ? MouseButtons.None : MouseButtons.Left, 1, (int)Math.Round(s.X), (int)Math.Round(s.Y), 0);
            if (kind == 0) f.CanvasDown(f.Canvas, e); else if (kind == 1) f.CanvasMove(f.Canvas, e); else f.CanvasUp(f.Canvas, e);
        }

        public static int Run(string dir)
        {
            Directory.CreateDirectory(dir);
            // 전파 모델: 5.5 GHz, 10 m, n=2, Tx 18 + Gain 5 => 23 - (47.26 + 20) = -44.26
            Project p = new Project { Exponent = 2, PlanW = 40, PlanH = 20, Band = 1 };
            ApItem a = new ApItem { X = 5, Y = 10, Height = 1, Tx5 = 18, Gain5 = 5, Name = "A" };
            p.Aps.Add(a);
            double open = Rf.FromAp(p, a, 15, 10, 1);
            Check("경로 손실 계산", Near(open, -44.26, 0.05), open.ToString("0.00"));
            Check("근거리 1 m 하한", Near(Rf.FromAp(p, a, 5, 10, 1), 23 - 47.26, 0.05), Rf.FromAp(p, a, 5, 10, 1).ToString("0.00"));
            p.Walls.Add(new WallItem { X1 = 10, Y1 = 0, X2 = 10, Y2 = 20, Material = 4 });
            double blocked = Rf.FromAp(p, a, 15, 10, 1);
            Check("콘크리트 벽 손실", Near(open - blocked, 20, 0.001), (open - blocked).ToString("0.00") + " dB");
            Check("벽 앞쪽은 손실 없음", Near(Rf.FromAp(p, a, 8, 10, 1), Rf.FromAp(new Project { Exponent = 2 }, a, 8, 10, 1), 0.001), "동일");
            Check("벽 위의 지점은 통과 아님", !Rf.Crosses(5, 10, 10, 10, p.Walls[0]), "손실 없음");
            ApItem b = new ApItem { X = 20, Y = 10, Height = 1, Tx5 = 18, Gain5 = 5, Name = "B" };
            p.Aps.Add(b);
            ApItem server; Rf.Best(p, 15, 10, 1, out server);
            Check("가장 강한 AP 선택", server == b, server.Name);
            Check("6GHz 미지원 AP 제외", double.IsNegativeInfinity(Rf.FromAp(p, a, 15, 10, 2)), "제외됨");

            Project c = new Project { PlanW = 40, PlanH = 20, Band = 1, HasArea = true, AreaX = 0, AreaY = 0, AreaW = 20, AreaH = 20, Threshold = -55 };
            c.Aps.Add(new ApItem { X = 2, Y = 2, Tx5 = 18, Gain5 = 5 });
            HeatResult h = Rf.Compute(c, 0);
            Check("커버리지 + 음영 = 분석 면적", h.Covered + h.Dead == h.Analyzed && h.Dead > 0 && h.Covered > 0, h.CoveragePct.ToString("0.0") + "%, 음영 " + h.DeadM2.ToString("0.0") + " m²");
            Check("분석 범위 적용", Near(h.AnalyzedM2, 400, 12), h.AnalyzedM2.ToString("0.0") + " m²");

            Project s = new Project { PlanW = 40, PlanH = 20, Band = 1, SurveySsid = "X" };
            s.Points.Add(new SurveyPoint { X = 5, Y = 5, Readings = new List<Reading> { new Reading { Ssid = "X", Band = 1, Rssi = -50 }, new Reading { Ssid = "Y", Band = 1, Rssi = -30 } } });
            s.Points.Add(new SurveyPoint { X = 9, Y = 5, Readings = new List<Reading> { new Reading { Ssid = "Y", Band = 1, Rssi = -40 } } });
            Check("선택한 SSID만 사용", Rf.PointValue(s, s.Points[0]) == -50, Rf.PointValue(s, s.Points[0]).ToString());
            Check("SSID 미검출은 신호 없음", Rf.PointValue(s, s.Points[1]) == Rf.NoSignal, Rf.PointValue(s, s.Points[1]).ToString());
            double[][] pts = { new double[] { 5, 5, -50 }, new double[] { 9, 5, -90 } };
            Check("보간: 측정 지점 값 유지", Near(Rf.Idw(pts, 5, 5), -50, 0.01), Rf.Idw(pts, 5, 5).ToString("0.0"));
            Check("보간: 중간 지점 평균", Near(Rf.Idw(pts, 7, 5), -70, 0.01), Rf.Idw(pts, 7, 5).ToString("0.0"));
            Check("보간: 미측정 구역 제외", float.IsNaN(Rf.Idw(pts, 30, 15)), "제외됨");

            MainForm f = new MainForm { Quiet = true };
            f.Show(); Pump(300);
            f.NewProject(new Project { PlanW = 40, PlanH = 25 }, null); Pump(100);

            f.SetTool(Tool.PlaceAp); Click(f, 10, 10); Pump(50);
            Check("화면: AP 배치", f.P.Aps.Count == 1 && Near(f.P.Aps[0].X, 10, 0.2), f.P.Aps.Count + "대, " + f.P.Aps[0].Name);
            f.SetTool(Tool.Wall); Click(f, 20, 2); Click(f, 20, 22); Click(f, 30, 22); Pump(50);
            Check("화면: 벽 연속 그리기", f.P.Walls.Count == 2, f.P.Walls.Count + "개");
            f.Undo();
            Check("화면: 실행 취소", f.P.Walls.Count == 1, f.P.Walls.Count + "개");
            f.SetTool(Tool.Select); Click(f, 20, 12); Pump(50);
            Check("화면: 벽 선택", f.Selected is WallItem, "선택됨");
            f.DeleteSelected();
            Check("화면: 벽 삭제", f.P.Walls.Count == 0, f.P.Walls.Count + "개");
            f.ApplyScale(10, 20);
            Check("화면: 축척 적용", Near(f.P.PlanW, 80, 0.01) && Near(f.P.Aps[0].X, 20, 0.5), f.P.PlanW + " m");

            f.SetTool(Tool.Select);
            Mouse(f, 0, f.P.Aps[0].X, f.P.Aps[0].Y); Mouse(f, 1, 30, 22); Mouse(f, 2, 30, 22);
            Check("화면: AP 끌어서 이동", Near(f.P.Aps[0].X, 30, 0.5) && Near(f.P.Aps[0].Y, 22, 0.5), f.P.Aps[0].X.ToString("0.0") + ", " + f.P.Aps[0].Y.ToString("0.0"));
            f.SetTool(Tool.Area);
            Mouse(f, 0, 4, 4); Mouse(f, 1, 34, 24); Mouse(f, 2, 34, 24);
            Check("화면: 분석 범위 지정", f.P.HasArea && Near(f.P.AreaW, 30, 0.5) && Near(f.P.AreaH, 20, 0.5), f.P.AreaW.ToString("0.0") + " x " + f.P.AreaH.ToString("0.0") + " m");
            f.SetTool(Tool.Survey); Click(f, 12, 12); Pump(6500);
            Check("화면: 현장 실측 클릭", f.P.Points.Count == 1 && f.P.Points[0].Readings.Count > 0, f.P.Points.Count + "곳");

            string plan = Path.Combine(dir, "plan_input.png");
            using (Bitmap bmp = new Bitmap(800, 500))
            using (Graphics g = Graphics.FromImage(bmp)) { g.Clear(Color.White); g.DrawRectangle(Pens.Black, 40, 40, 700, 400); bmp.Save(plan, ImageFormat.Png); }
            f.NewProject(Demo.Build(), null);
            f.LoadPlan(plan);
            Check("도면 이미지 불러오기", f.Plan != null && f.Plan.Width == 800 && !f.P.ScaleSet, f.Plan.Width + "x" + f.Plan.Height);
            f.ApplyScale(40, 48);
            string proj = Path.Combine(dir, "roundtrip.wifiplan");
            int aps = f.P.Aps.Count, walls = f.P.Walls.Count;
            f.SaveProject(proj);
            f.NewProject(new Project(), null);
            f.LoadProject(proj);
            Check("프로젝트 저장/열기", f.P.Aps.Count == aps && f.P.Walls.Count == walls && f.Plan != null && f.Plan.Width == 800, aps + "대, 벽 " + walls + "개, 도면 포함");

            MeasureResult m = WifiReader.Measure(4000);
            Check("실제 Wi-Fi 측정", m.Error.Length == 0 && m.Readings.Count > 0 && m.ConnectedBand >= 0, "AP " + m.Readings.Count + "개, 연결 " + m.ConnectedRssi + " dBm, 오류 '" + m.Error + "'");
            f.NewProject(Demo.Build(), null);
            f.FinishMeasure(24, 24, m);
            Check("실측 지점 기록", f.P.Points.Count == 1 && f.P.View == 1 && f.P.SurveySsid == m.ConnectedSsid, f.P.Points[0].Readings.Count + "건");
            f.ComputeNow();
            Check("실측 히트맵 생성", f.Heat.Analyzed > 0, f.Heat.AnalyzedM2.ToString("0.0") + " m²");
            string csv = Path.Combine(dir, "survey.csv");
            f.ExportCsv(csv);
            Check("실측 CSV 내보내기", File.ReadAllLines(csv).Length == m.Readings.Count + 1, File.ReadAllLines(csv).Length + "행");

            f.NewProject(Demo.Build(), null); f.WindowState = FormWindowState.Normal; Pump(100);
            f.ComputeNow();
            Check("예제 예측 분석", f.Heat.Dead > 0 && f.Heat.Covered > 0, "커버리지 " + f.Heat.CoveragePct.ToString("0.0") + "%, 음영 " + f.Heat.DeadM2.ToString("0.0") + " m²");
            f.SetTool(Tool.Select); Click(f, f.P.Aps[1].X, f.P.Aps[1].Y); Mouse(f, 2, f.P.Aps[1].X, f.P.Aps[1].Y); Pump(300);
            f.ComputeNow(); Pump(200);
            using (Bitmap ui = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(ui, new Rectangle(0, 0, f.Width, f.Height)); ui.Save(Path.Combine(dir, "ui.png"), ImageFormat.Png); }
            f.ExportPng(Path.Combine(dir, "export.png"));
            Check("결과 이미지 내보내기", new FileInfo(Path.Combine(dir, "export.png")).Length > 10000, "저장됨");
            f.Close();

            log.AppendLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            File.WriteAllText(Path.Combine(dir, "selftest.txt"), log.ToString(), new UTF8Encoding(true));
            return failed == 0 ? 0 : 1;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Ui.K = g.DpiX / 96f;
            if (args.Length == 2 && args[0] == "--selftest") return SelfTest.Run(args[1]);
            MainForm f = new MainForm();
            f.Shown += delegate
            {
                f.NewProject(Demo.Build(), null);
                f.ShowStatus("예제 도면입니다. 실제 도면은 파일 > 도면 이미지 불러오기에서 열 수 있습니다.");
            };
            Application.Run(f);
            return 0;
        }
    }
}
