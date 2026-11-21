// Wi-Fi 음영지역 분석기 - 2D 도면 AP 배치, 신호 예측, 현장 실측
// 빌드: build.bat (Windows 기본 포함 .NET Framework 4.x의 csc.exe 사용)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
using WifiSurvey.Design;
using Csv = WifiSurvey.Core.Csv;

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

        // 신호 5단계(체커와 동일) 색으로 히트맵을 만든다: 약전계 빨강 → 청록
        public static Color ColorOf(double v, double thr)
        {
            if (v < thr) return Color.FromArgb(178, 255, 93, 108);
            double span = Math.Max(1, -45 - thr);
            double t = Math.Max(0, Math.Min(1, (v - thr) / span));
            Color[] stops = {
                Color.FromArgb(255, 93, 108), Color.FromArgb(255, 154, 61), Color.FromArgb(242, 198, 61),
                Color.FromArgb(95, 209, 106), Color.FromArgb(17, 201, 160)
            };
            double pos = t * (stops.Length - 1);
            int i = Math.Min(stops.Length - 2, (int)pos);
            double k = pos - i;
            Color a = stops[i], b = stops[i + 1];
            return Color.FromArgb(178,
                (int)Math.Round(a.R + (b.R - a.R) * k),
                (int)Math.Round(a.G + (b.G - a.G) * k),
                (int)Math.Round(a.B + (b.B - a.B) * k));
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

    // ---- 화면 그리기 (다크 프리미엄) ----
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
        static readonly Font Small = Theme.F(8f), Tiny = Theme.F(7.5f), Mark = Theme.F(8.5f, FontStyle.Bold), Label = Theme.F(8.5f, FontStyle.Bold);

        public static void Draw(Graphics g, Project p, Bitmap plan, HeatResult heat, View v, object selected)
        {
            Draw(g, p, plan, heat, v, selected, 1f, 0f);
        }

        // reveal: 히트맵이 좌→우로 채워지는 진행도(0..1), pulse: AP 링 박동 위상(0..1)
        public static void Draw(Graphics g, Project p, Bitmap plan, HeatResult heat, View v, object selected, float reveal, float pulse)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PointF o = v.P(0, 0);
            RectangleF pr = new RectangleF(o.X, o.Y, (float)(p.PlanW * v.Zoom), (float)(p.PlanH * v.Zoom));

            if (plan != null)
            {
                g.FillRectangle(Brushes.White, pr);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(plan, pr);
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(16, 20, 27))) g.FillRectangle(b, pr);
                DrawGrid(g, p, v, pr);
            }

            GraphicsState st = g.Save();
            g.SetClip(pr);
            if (heat != null && heat.Image != null && p.View != 2)
            {
                RectangleF hr = new RectangleF(o.X, o.Y, (float)(heat.Cols * heat.Cell * v.Zoom), (float)(heat.Rows * heat.Cell * v.Zoom));
                if (reveal >= 0.999f)
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(heat.Image, hr);
                    g.InterpolationMode = InterpolationMode.Default;
                    DrawDead(g, p, heat, v);
                }
                else
                {
                    RectangleF part = new RectangleF(hr.X, hr.Y, hr.Width * reveal, hr.Height);
                    GraphicsState s2 = g.Save();
                    g.SetClip(part);
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(heat.Image, hr);
                    g.InterpolationMode = InterpolationMode.Default;
                    g.Restore(s2);
                    float edge = hr.X + hr.Width * reveal;
                    using (Pen glow = new Pen(Theme.Alpha(Theme.AccentHi, 90), Theme.SF(6f))) g.DrawLine(glow, edge, hr.Top, edge, hr.Bottom);
                    using (Pen line = new Pen(Theme.AccentHi, Theme.SF(1.6f))) g.DrawLine(line, edge, hr.Top, edge, hr.Bottom);
                }
            }
            g.Restore(st);

            using (Pen b = new Pen(plan != null ? Color.FromArgb(90, 255, 255, 255) : Theme.BorderStrong, Theme.SF(1f))) g.DrawRectangle(b, pr.X, pr.Y, pr.Width, pr.Height);

            if (p.HasArea)
                using (Pen dash = new Pen(Theme.Alpha(Theme.AccentHi, 180), Theme.SF(1.6f)) { DashStyle = DashStyle.Dash, DashPattern = new float[] { 5f, 4f } })
                {
                    PointF a = v.P(p.AreaX, p.AreaY);
                    g.DrawRectangle(dash, a.X, a.Y, (float)(p.AreaW * v.Zoom), (float)(p.AreaH * v.Zoom));
                }

            foreach (WallItem w in p.Walls)
            {
                bool sel = ReferenceEquals(w, selected);
                PointF a = v.P(w.X1, w.Y1), b2 = v.P(w.X2, w.Y2);
                using (Pen pen = new Pen(sel ? Theme.AccentHi : Material.All[w.Material].Color, Theme.SF(sel ? 5f : 3.4f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, a, b2);
                }
            }
            foreach (SurveyPoint s in p.Points) DrawPoint(g, p, s, v, ReferenceEquals(s, selected));
            foreach (ApItem a in p.Aps) DrawAp(g, a, v, ReferenceEquals(a, selected), pulse);

            DrawScaleBar(g, p, v, pr);
        }

        static void DrawDead(Graphics g, Project p, HeatResult heat, View v)
        {
            foreach (RectangleF r in heat.DeadRuns)
            {
                PointF a = v.P(r.X, r.Y);
                RectangleF rr = new RectangleF(a.X, a.Y, (float)(r.Width * v.Zoom) + 0.5f, (float)(r.Height * v.Zoom) + 0.5f);
                using (HatchBrush hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(110, 255, 93, 108), Color.Transparent))
                    g.FillRectangle(hb, rr);
            }
        }

        static void DrawGrid(Graphics g, Project p, View v, RectangleF pr)
        {
            double step = v.Zoom >= 12 ? 1 : 5;
            using (Pen thin = new Pen(Color.FromArgb(28, 255, 255, 255)))
            using (Pen thick = new Pen(Color.FromArgb(46, 255, 255, 255)))
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
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(215, 8, 10, 14)))
                g.FillRectangle(bg, x - sz.Width / 2 - 3, y - 1, sz.Width + 6, sz.Height + 1);
            using (SolidBrush b = new SolidBrush(c))
                g.DrawString(s, f, b, x - sz.Width / 2, y);
        }

        static void DrawAp(Graphics g, ApItem a, View v, bool selected, float pulse)
        {
            PointF c = v.P(a.X, a.Y);
            float r = Theme.SF(11f);
            Color vc = Catalog.VendorColor(a.Vendor);
            // 박동 링
            float ring = r + Theme.SF(6f) + Theme.SF(12f) * pulse;
            using (Pen pr = new Pen(Theme.Alpha(vc, (int)(150 * (1 - pulse))), Theme.SF(1.6f)))
                g.DrawEllipse(pr, c.X - ring, c.Y - ring, ring * 2, ring * 2);
            Theme.Glow(g, c, r + Theme.SF(8f), vc, 3);
            using (SolidBrush b = new SolidBrush(Theme.Mix(vc, Colors_black, 0.25))) g.FillEllipse(b, c.X - r, c.Y - r, r * 2, r * 2);
            using (Pen ringPen = new Pen(Theme.Mix(vc, Color.White, 0.45), Theme.SF(1.6f))) g.DrawEllipse(ringPen, c.X - r, c.Y - r, r * 2, r * 2);
            Icons.Draw(g, Glyph.Ap, new RectangleF(c.X - r * 0.62f, c.Y - r * 0.62f, r * 1.24f, r * 1.24f), Color.White, 2.2f);
            if (selected)
                using (Pen sel = new Pen(Theme.Accent, Theme.SF(2f)))
                {
                    float sr = r + Theme.SF(5f);
                    g.DrawEllipse(sel, c.X - sr, c.Y - sr, sr * 2, sr * 2);
                }
            Halo(g, a.Name, Label, Theme.Ink, c.X, c.Y + r + Theme.SF(4f));
        }
        static readonly Color Colors_black = Color.Black;

        static void DrawPoint(Graphics g, Project p, SurveyPoint s, View v, bool selected)
        {
            PointF c = v.P(s.X, s.Y);
            double value = Rf.PointValue(p, s);
            float r = Theme.SF(selected ? 6.5f : 4.5f);
            Color col = value < p.Threshold ? Theme.Lv1 : Theme.Lv5;
            Theme.Glow(g, c, r + Theme.SF(6f), col, 3);
            using (SolidBrush b = new SolidBrush(col)) g.FillEllipse(b, c.X - r, c.Y - r, r * 2, r * 2);
            using (Pen w = new Pen(selected ? Theme.AccentHi : Color.FromArgb(220, 255, 255, 255), Theme.SF(1.4f))) g.DrawEllipse(w, c.X - r, c.Y - r, r * 2, r * 2);
            Halo(g, value <= Rf.NoSignal ? "신호 없음" : value.ToString("0"), Small, col, c.X, c.Y + r + Theme.SF(2f));
        }

        static void DrawScaleBar(Graphics g, Project p, View v, RectangleF pr)
        {
            double meters = 5;
            if (v.Zoom * 5 > pr.Width * 0.5) meters = 1;
            if (v.Zoom < 6) meters = 10;
            float len = (float)(meters * v.Zoom);
            if (len < Theme.SF(20f)) return;
            float x = pr.Right - len - Theme.SF(16f), y = pr.Bottom - Theme.SF(18f);
            using (Pen pen = new Pen(Theme.Alpha(Theme.Ink, 190), Theme.SF(1.4f)))
            {
                g.DrawLine(pen, x, y, x + len, y);
                g.DrawLine(pen, x, y - Theme.SF(4f), x, y + Theme.SF(2f));
                g.DrawLine(pen, x + len, y - Theme.SF(4f), x + len, y + Theme.SF(2f));
            }
            Halo(g, meters.ToString("0") + " m", Tiny, Theme.Ink, x + len / 2, y - Theme.SF(17f));
        }

        // 부유 범례 카드
        public static void Legend(Graphics g, Project p, int x, int y)
        {
            int w = Theme.S(252), h = Theme.S(96);
            Rectangle r = new Rectangle(x, y, w, h);
            Theme.Fill(g, r, Theme.S(10), Color.FromArgb(236, 16, 20, 27));
            Theme.Stroke(g, r, Theme.S(10), Theme.Alpha(Theme.BorderStrong, 220));
            int pad = Theme.S(12);
            Theme.CaptionText(g, "신호 세기", Theme.TextMute, new Rectangle(x + pad, y + Theme.S(9), w - pad * 2, Theme.S(14)));
            Theme.Text(g, "dBm", Theme.F(8f), Theme.TextMute, new Rectangle(x + pad, y + Theme.S(9), w - pad * 2, Theme.S(14)), Theme.Align.Right);

            Rectangle bar = new Rectangle(x + pad, y + Theme.S(28), w - pad * 2, Theme.S(10));
            for (int i = 0; i < bar.Width; i++)
            {
                double t = (double)i / Math.Max(1, bar.Width - 1);
                double dbm = p.Threshold + (-45 - p.Threshold) * t;
                using (SolidBrush b = new SolidBrush(Rf.ColorOf(dbm, p.Threshold)))
                    g.FillRectangle(b, bar.X + i, bar.Y, 1, bar.Height);
            }
            using (Pen edge = new Pen(Theme.Alpha(Theme.BorderStrong, 160))) g.DrawRectangle(edge, bar);

            string[] ticks = { p.Threshold.ToString("0"), "-60", "-50", "-45" };
            for (int i = 0; i < ticks.Length; i++)
                Theme.Text(g, ticks[i], Tiny, Theme.TextMute,
                    new Rectangle(bar.X + (bar.Width - Theme.S(26)) * i / (ticks.Length - 1) - (i == 0 ? 0 : 0), bar.Bottom + Theme.S(1), Theme.S(30), Theme.S(14)),
                    i == 0 ? Theme.Align.Left : i == ticks.Length - 1 ? Theme.Align.Right : Theme.Align.Center);

            Rectangle dead = new Rectangle(x + pad, y + Theme.S(56), Theme.S(14), Theme.S(14));
            Theme.Fill(g, dead, Theme.S(3), Theme.Lv1);
            using (HatchBrush hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Theme.Alpha(Color.White, 150), Color.Transparent))
                g.FillRectangle(hb, dead);
            Theme.Text(g, "약전계 " + p.Threshold.ToString("0") + " 미만", Theme.F(8.5f), Theme.TextDim, new Rectangle(dead.Right + Theme.S(7), dead.Y - Theme.S(1), Theme.S(110), Theme.S(16)));
            Theme.Text(g, "AP " + p.Aps.Count + "대", Theme.F(8.5f), Theme.TextMute, new Rectangle(x + w - pad - Theme.S(60), dead.Y - Theme.S(1), Theme.S(60), Theme.S(16)), Theme.Align.Right);
            Theme.Text(g, p.View == 1 ? "실측 보간 히트맵" : "예측 히트맵 · 로그거리 모델", Theme.F(8f), Theme.TextMute,
                new Rectangle(x + pad, y + h - Theme.S(18), w - pad * 2, Theme.S(14)));
        }
    }

    // 캔버스: 히트맵 스윕 등장 + AP 박동
    class PlanCanvas : Widget
    {
        public Project Model;
        public float Reveal = 1f;
        public bool Pulse = true;
        readonly Spring reveal = new Spring(1);
        readonly System.Windows.Forms.Timer beat = new System.Windows.Forms.Timer { Interval = 90 };

        public PlanCanvas()
        {
            BackColor = Color.FromArgb(14, 17, 22);
            TabStop = true;
            beat.Tick += delegate { if (Pulse && Motion.Enabled && Model != null && Model.Aps.Count > 0) Invalidate(); };
            beat.Start();
        }
        public void StartReveal()
        {
            if (!Motion.Enabled) { reveal.Set(1); Reveal = 1f; Invalidate(); return; }
            reveal.Set(0);
            Reveal = 0;
            Poke();
        }
        protected override bool Step(float dt)
        {
            Reveal = (float)reveal.Step(dt);
            if (reveal.Settled) Reveal = 1f;
            return !reveal.Settled;
        }
        public float PulsePhase { get { return (float)((DateTime.Now.TimeOfDay.TotalMilliseconds % 2400) / 2400.0); } }
    }

    enum Tool { Select, PlaceAp, Wall, Area, Scale, Survey }

    class HeadBar : Widget
    {
        public string Title = "Wi-Fi 음영지역 분석기", Sub = "2D 도면 AP 배치 · 신호 예측 · 현장 실측";
        public readonly UiButton OpenPlan = new UiButton(), Save = new UiButton(), Report = new UiButton();
        public double Coverage { get; set; }
        public double Dead { get; set; }
        public double Analyzed { get; set; }
        public int Aps { get; set; }
        public int Points { get; set; }
        public string Mode { get; set; }

        public HeadBar()
        {
            Height = Theme.S(64);   // Dock=Top인데 높이를 지정하지 않아 헤더가 보이지 않던 문제
            Coverage = -1; Mode = "";
            OpenPlan.Label = "도면"; OpenPlan.Glyph = Glyph.Folder; OpenPlan.Kind = Btn.Subtle;
            Save.Label = "저장"; Save.Glyph = Glyph.Check; Save.Kind = Btn.Subtle;
            Report.Label = "리포트"; Report.Glyph = Glyph.Export; Report.Kind = Btn.Primary;
            Controls.Add(OpenPlan); Controls.Add(Save); Controls.Add(Report);
            foreach (Control c in Controls) c.BackColor = Theme.Bg;
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int y = Theme.S(19), h = Theme.S(32), right = Width - Theme.S(16);
            Report.SetBounds(right - Theme.S(92), y, Theme.S(92), h);
            Save.SetBounds(Report.Left - Theme.S(8) - Theme.S(74), y, Theme.S(74), h);
            OpenPlan.SetBounds(Save.Left - Theme.S(8) - Theme.S(74), y, Theme.S(74), h);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            int x = Theme.S(20);
            Theme.Fill(g, new Rectangle(x, Theme.S(18), Theme.S(30), Theme.S(30)), Theme.S(8), Theme.AccentSoft);
            Icons.Draw(g, Glyph.Ap, new RectangleF(x + Theme.S(6), Theme.S(24), Theme.S(18), Theme.S(18)), Theme.Accent, 2f);
            x += Theme.S(40);
            Theme.Text(g, Title, Theme.F(13f, FontStyle.Bold), Theme.Ink, new Rectangle(x, Theme.S(13), Theme.S(300), Theme.S(22)));
            Theme.Text(g, Sub, Theme.F(9f), Theme.TextMute, new Rectangle(x, Theme.S(33), Theme.S(320), Theme.S(18)));

            // KPI 블록
            int bx = Math.Max(x + Theme.S(340), (Width - Theme.S(560)) / 2);
            int bw = Theme.S(122);
            DrawStat(g, bx, "커버리지", Coverage < 0 ? "—" : Coverage.ToString("0.0") + "%", Coverage < 0 ? Theme.None : Coverage >= 95 ? Theme.Lv5 : Coverage >= 80 ? Theme.Lv3 : Theme.Lv1, bw);
            DrawStat(g, bx + bw, "음영 면적", Analyzed > 0 ? Dead.ToString("0.0") + " m²" : "—", Theme.Ink, bw);
            DrawStat(g, bx + bw * 2, "분석 면적", Analyzed > 0 ? Analyzed.ToString("0") + " m²" : "—", Theme.Ink, bw);
            DrawStat(g, bx + bw * 3, "AP / 실측", Aps + " / " + Points, Theme.Ink, bw);
            if (Mode.Length > 0)
                Theme.Text(g, Mode, Theme.F(8f), Theme.TextMute, new Rectangle(bx + bw * 4, Theme.S(30), Theme.S(120), Theme.S(14)));
        }
        void DrawStat(Graphics g, int x, string caption, string value, Color tone, int w)
        {
            Theme.CaptionText(g, caption, Theme.TextMute, new Rectangle(x, Theme.S(16), w - Theme.S(10), Theme.S(13)));
            Theme.Mono(g, value, Theme.F(17f, FontStyle.Bold), tone, new Rectangle(x, Theme.S(30), w - Theme.S(10), Theme.S(24)), Theme.Align.Left);
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, x - Theme.S(10), Theme.S(20), x - Theme.S(10), Theme.S(54));
        }
    }

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
        readonly HeadBar head = new HeadBar();
        readonly FootBar foot = new FootBar();
        readonly Panel rail = new Panel(), leftPanel = new Panel(), rightPanel = new Panel();
        readonly Dictionary<Tool, RailButton> toolButtons = new Dictionary<Tool, RailButton>();

        readonly Select vendorBox = new Select(), wallBox = new Select(), viewBox = new Select(), bandBox = new Select(), envBox = new Select(), ssidBox = new Select();
        readonly DataTable modelList = new DataTable();
        readonly Card infoCard = new Card(), displayCard = new Card(), apCard = new Card(), libCard = new Card(), inspCard = new Card();
        readonly Stepper thrBox = new Stepper(), apHeight = new Stepper(), apTx24 = new Stepper(), apTx5 = new Stepper(), apTx6 = new Stepper();
        readonly TextBox apName = new TextBox();
        readonly UiButton delAp = new UiButton();
        readonly UiButton zoomIn = new UiButton(), zoomOut = new UiButton(), zoomFit = new UiButton();
        MenuStrip menuStrip;

        static readonly double[] EnvValues = { 2.2, 2.6, 3.0, 3.4 };
        static readonly string[] EnvNames = { "개방형 공간", "일반 사무실", "밀집 공간", "창고 / 공장" };
        bool loading, dirty, measuring, dragging, panning, panMoved, areaDrag, hasFirst;
        double firstX, firstY, curX, curY;
        Point lastMouse;
        int heatVersion, coverageShown = -1;
        string filePath;
        public bool Quiet;
        readonly Spring coverAnim = new Spring(0);
        readonly System.Windows.Forms.Timer headTick = new System.Windows.Forms.Timer { Interval = 60 };

        public MainForm()
        {
            Text = "Wi-Fi 음영지역 분석기";
            BackColor = Theme.Bg;
            ForeColor = Theme.Ink;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size(Math.Min(Theme.S(1100), wa.Width), Math.Min(Theme.S(720), wa.Height));
            Size = new Size(Math.Min(Theme.S(1440), wa.Width), Math.Min(Theme.S(920), wa.Height));

            Canvas.Dock = DockStyle.Fill;
            BuildRail();
            BuildLeft();
            BuildRight();
            BuildMenu();
            head.Dock = DockStyle.Top;
            foot.Dock = DockStyle.Bottom;
            leftPanel.Dock = DockStyle.Left;
            leftPanel.Width = Theme.S(236);
            rightPanel.Dock = DockStyle.Right;
            rightPanel.Width = Theme.S(276);
            rail.Dock = DockStyle.Left;
            rail.Width = Theme.S(64);

            Controls.Add(Canvas);
            Controls.Add(rightPanel);
            Controls.Add(leftPanel);
            Controls.Add(rail);
            Controls.Add(foot);
            Controls.Add(head);
            if (menuStrip != null) Controls.Add(menuStrip);

            Canvas.Paint += CanvasPaint;
            Canvas.MouseDown += CanvasDown;
            Canvas.MouseMove += CanvasMove;
            Canvas.MouseUp += CanvasUp;
            Canvas.MouseWheel += CanvasWheel;
            Canvas.Resize += delegate { if (!userMovedView) { V.Fit(P, Canvas.Width, Canvas.Height); Canvas.Invalidate(); } };
            debounce.Tick += delegate { debounce.Stop(); StartHeat(); };
            head.OpenPlan.Click += delegate { OpenPlanDialog(); };
            head.Save.Click += delegate { SaveProjectDialog(false); };
            head.Report.Click += delegate { ExportReportDialog(); };
            headTick.Tick += delegate
            {
                coverAnim.Step(1.0 / 16.0);
                infoCard.Invalidate();
                if (coverAnim.Settled) headTick.Stop();
            };
            SetTool(Tool.Select);
            SyncViewControls();
            UpdateStats();
        }

        bool moved;   // 사용자가 확대/이동한 뒤에는 창 크기 변경에 자동 맞춤하지 않는다
        bool userMovedView { get { return moved; } set { moved = value; } }

        // ---- 메뉴 (모든 명령 유지) ----
        void BuildMenu()
        {
            MenuStrip m = new MenuStrip { BackColor = Theme.Surface, ForeColor = Theme.TextDim, Renderer = new DarkMenu(), Padding = new Padding(Theme.S(8), 0, 0, 0) };
            menuStrip = m;
            ToolStripMenuItem file = new ToolStripMenuItem("파일");
            Item(file, "새 프로젝트", Keys.Control | Keys.N, delegate { if (ConfirmDiscard()) NewProject(new Project(), null); });
            Item(file, "도면 이미지 불러오기…", Keys.Control | Keys.I, delegate { OpenPlanDialog(); });
            Item(file, "예제 도면 열기", Keys.None, delegate { if (ConfirmDiscard()) NewProject(Demo.Build(), null); });
            file.DropDownItems.Add(new ToolStripSeparator());
            Item(file, "프로젝트 열기…", Keys.Control | Keys.O, delegate { OpenProjectDialog(); });
            Item(file, "프로젝트 저장", Keys.Control | Keys.S, delegate { SaveProjectDialog(false); });
            Item(file, "다른 이름으로 저장…", Keys.Control | Keys.Shift | Keys.S, delegate { SaveProjectDialog(true); });
            file.DropDownItems.Add(new ToolStripSeparator());
            Item(file, "결과 이미지(PNG) 내보내기…", Keys.Control | Keys.E, delegate { ExportPngDialog(); });
            Item(file, "리포트(HTML + PNG + CSV) 내보내기…", Keys.Control | Keys.Shift | Keys.E, delegate { ExportReportDialog(); });
            Item(file, "실측 데이터 CSV 내보내기…", Keys.None, delegate { ExportCsvDialog(); });
            ToolStripMenuItem edit = new ToolStripMenuItem("보기");
            Item(edit, "실행 취소", Keys.Control | Keys.Z, delegate { Undo(); });
            Item(edit, "도면 전체 보기", Keys.Control | Keys.D0, delegate { userMovedView = false; V.Fit(P, Canvas.Width, Canvas.Height); Canvas.Invalidate(); });
            Item(edit, "애니메이션 켜기/끄기", Keys.None, delegate { Motion.Enabled = !Motion.Enabled; Motion.InvalidateAll(); Canvas.Invalidate(); head.Invalidate(); ShowStatus(Motion.Enabled ? "애니메이션을 켰습니다." : "애니메이션을 껐습니다. (원격 접속·저사양)"); });
            m.Items.Add(file); m.Items.Add(edit);
            MainMenuStrip = m;
        }
        static void Item(ToolStripMenuItem parent, string text, Keys keys, EventHandler h)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text, null, h);
            if (keys != Keys.None) i.ShortcutKeys = keys;
            parent.DropDownItems.Add(i);
        }

        class DarkMenu : ToolStripProfessionalRenderer
        {
            public DarkMenu() : base(new DarkColors()) { }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Selected ? Theme.Ink : Theme.TextDim;
                base.OnRenderItemText(e);
            }
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(e.ToolStrip is MenuStrip ? Theme.Surface : Theme.SurfaceTop)) e.Graphics.FillRectangle(b, e.AffectedBounds);
            }
            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(Theme.SurfaceTop)) e.Graphics.FillRectangle(b, e.AffectedBounds);
            }
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected) return;
                Theme.Fill(e.Graphics, new Rectangle(1, 1, (int)e.Item.Size.Width - 2, (int)e.Item.Size.Height - 2), Theme.S(6), Theme.AccentSoft);
            }
        }
        class DarkColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Theme.AccentSoft; } }
            public override Color MenuItemBorder { get { return Theme.AccentSoft; } }
            public override Color ToolStripDropDownBackground { get { return Theme.SurfaceTop; } }
            public override Color ImageMarginGradientBegin { get { return Theme.SurfaceTop; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.SurfaceTop; } }
            public override Color ImageMarginGradientEnd { get { return Theme.SurfaceTop; } }
            public override Color MenuBorder { get { return Theme.BorderStrong; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Border; } }
        }

        // ---- 좌측 아이콘 레일 ----
        void BuildRail()
        {
            rail.BackColor = Theme.Surface;
            rail.Paint += delegate (object o, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, rail.Width - 1, 0, rail.Width - 1, rail.Height);
            };
            AddTool(Tool.Select, Glyph.Target, "선택", 8);
            AddTool(Tool.PlaceAp, Glyph.Ap, "AP", 8);
            AddTool(Tool.Wall, Glyph.Wall, "벽", 104);
            AddTool(Tool.Area, Glyph.Area, "범위", 66);
            AddTool(Tool.Scale, Glyph.Ruler, "축척", 66);
            AddTool(Tool.Survey, Glyph.Wifi, "실측", 66);
            rail.Resize += delegate { LayoutRail(); };
        }
        void LayoutRail()
        {
            int y = Theme.S(8);
            Tool[] order = { Tool.Select, Tool.PlaceAp, Tool.Wall, Tool.Area, Tool.Scale, Tool.Survey };
            foreach (Tool t in order)
            {
                // AddTool이 하나씩 추가하며 매번 부르므로 아직 없는 버튼은 건너뛴다.
                RailButton b;
                if (!toolButtons.TryGetValue(t, out b)) continue;
                b.SetBounds(0, y, rail.Width - Theme.S(1), Theme.S(58));
                y += Theme.S(62);
                if (t == Tool.PlaceAp) y += Theme.S(8);
            }
        }
        void AddTool(Tool t, Glyph glyph, string label, int top)
        {
            RailButton b = new RailButton { Glyph = glyph, Label = label, Tip = ToolTipFor(t), BackColor = Theme.Surface };
            b.Click += delegate { SetTool(t); };
            toolButtons[t] = b;
            rail.Controls.Add(b);
            LayoutRail();
        }
        static string ToolTipFor(Tool t)
        {
            switch (t)
            {
                case Tool.Select: return "선택 / 이동 — AP를 끌어 옮기고 벽·지점을 고릅니다 (Delete: 삭제)";
                case Tool.PlaceAp: return "AP 배치 — 제품을 고른 뒤 도면을 누릅니다";
                case Tool.Wall: return "벽 그리기 — 두 점을 차례로 누릅니다 (Esc: 끝)";
                case Tool.Area: return "분석 범위 — 건물 내부를 드래그합니다";
                case Tool.Scale: return "축척 설정 — 길이를 아는 두 점을 누릅니다";
                default: return "현장 실측 — 설 자리를 누르면 4초간 실제 신호를 잽니다";
            }
        }

        // ---- 좌측 패널 ----
        void BuildLeft()
        {
            leftPanel.BackColor = Theme.Bg;
            leftPanel.AutoScroll = true;
            libCard.Title = "AP 제품";
            libCard.Pad = 12;
            vendorBox.BackColor = Theme.Surface;
            foreach (string v in Catalog.Vendors) vendorBox.Items.Add(v);
            vendorBox.SelectedIndex = 0;
            vendorBox.Changed += delegate { if (!loading) FillModels(); };
            modelList.BackColor = Theme.Surface;
            modelList.RowHeight = 26;
            modelList.Columns.Add(new Column { Title = "모델", Width = 186 });
            modelList.SelectionChanged += delegate { if (!loading) { ShowModel(); SetTool(Tool.PlaceAp); } };
            wallBox.BackColor = Theme.Surface;
            foreach (Material mt in Material.All) wallBox.Items.Add(mt.Name + " · 5GHz -" + mt.Loss[1] + "dB");
            wallBox.SelectedIndex = 0;
            wallBox.Changed += delegate
            {
                WallItem w = Selected as WallItem;
                if (w != null && w.Material != wallBox.SelectedIndex) { Snap(); w.Material = wallBox.SelectedIndex; Changed(); }
            };
            libCard.Body = PaintLib;
            FillModels();
            leftPanel.Controls.Add(libCard);
            leftPanel.Controls.Add(vendorBox); leftPanel.Controls.Add(modelList); leftPanel.Controls.Add(wallBox);
            libCard.SendToBack();
            leftPanel.Resize += delegate { LayoutLeft(); };
        }
        void LayoutLeft()
        {
            int w = leftPanel.ClientSize.Width - Theme.S(16) * 2;
            int scroll = leftPanel.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
            w -= scroll;
            int x = Theme.S(16), y = Theme.S(8);
            libCard.SetBounds(x, y, w, leftPanel.Height - Theme.S(16));
            vendorBox.SetBounds(x + Theme.S(12), y + Theme.S(44), w - Theme.S(24), Theme.S(28));
            modelList.SetBounds(x + Theme.S(12), y + Theme.S(80), w - Theme.S(24), Theme.S(172));
            wallBox.SetBounds(x + Theme.S(12), y + Theme.S(80 + 172 + 104), w - Theme.S(24), Theme.S(28));
        }
        ModelInfo modelInfo2;
        void PaintLib(Graphics g, Rectangle content)
        {
            int w = libCard.Width;
            Theme.Text(g, "제조사", Theme.F(9f), Theme.TextMute, new Rectangle(Theme.S(12), Theme.S(30), w - Theme.S(24), Theme.S(14)));
            Theme.Text(g, "모델", Theme.F(9f), Theme.TextMute, new Rectangle(Theme.S(12), Theme.S(66), w - Theme.S(24), Theme.S(14)));
            if (modelInfo2 != null)
            {
                int y = Theme.S(80 + 172 + 8);
                Theme.Wrap(g, modelInfo2.Text, Theme.F(8.5f), modelInfo2.Warn ? Theme.Lv2 : Theme.TextDim,
                    new Rectangle(Theme.S(12), y, w - Theme.S(24), Theme.S(64)));
            }
            Theme.Text(g, "벽 재질", Theme.F(9f), Theme.TextMute, new Rectangle(Theme.S(12), Theme.S(80 + 172 + 86), w - Theme.S(24), Theme.S(14)));
            Theme.Wrap(g, HintFor(ActiveTool), Theme.F(8.5f), Theme.TextMute,
                new Rectangle(Theme.S(12), libCard.Height - Theme.S(78), w - Theme.S(24), Theme.S(62)));
            Deco.Divider(g, Theme.S(12), libCard.Height - Theme.S(84), w - Theme.S(24));
        }
        class ModelInfo { public string Text = ""; public bool Warn; }

        static string HintFor(Tool t)
        {
            switch (t)
            {
                case Tool.Select: return "AP를 끌어서 옮기고, 벽이나 지점을 눌러 고릅니다. Delete로 삭제.";
                case Tool.PlaceAp: return "아래 목록에서 제품을 고른 뒤 도면을 누르면 AP가 놓입니다.";
                case Tool.Wall: return "도면을 차례로 눌러 벽을 잇습니다. 오른쪽 클릭이나 Esc로 끝냅니다.";
                case Tool.Area: return "건물 내부를 드래그해 지정합니다. 지정한 범위만 분석합니다.";
                case Tool.Scale: return "길이를 아는 두 점을 누른 뒤 실제 거리를 입력합니다.";
                default: return "현재 위치를 도면에서 누르면 약 4초간 실제 신호를 측정합니다.";
            }
        }

        // ---- 우측 패널 ----
        void BuildRight()
        {
            rightPanel.BackColor = Theme.Bg;
            rightPanel.AutoScroll = true;
            infoCard.Title = "분석 결과";
            infoCard.Pad = 14;
            infoCard.Body = PaintInfo;
            displayCard.Title = "표시 설정";
            displayCard.Pad = 14;
            displayCard.Body = PaintDisplay;
            inspCard.Title = "선택한 AP";
            inspCard.Pad = 14;
            inspCard.Body = PaintInspector;

            viewBox.BackColor = Theme.Surface;
            viewBox.Items.Add("예측 신호"); viewBox.Items.Add("실측 신호"); viewBox.Items.Add("히트맵 끄기");
            viewBox.SelectedIndex = 0;
            viewBox.Changed += delegate { if (loading) return; P.View = viewBox.SelectedIndex; Changed(false); };
            bandBox.BackColor = Theme.Surface;
            foreach (string b in Rf.BandNames) bandBox.Items.Add(b);
            bandBox.SelectedIndex = 1;
            bandBox.Changed += delegate { if (loading) return; P.Band = bandBox.SelectedIndex; Changed(false); };
            thrBox.Min = -90; thrBox.Max = -50; thrBox.Increment = 1; thrBox.Suffix = " dBm";
            thrBox.Value = -67;
            thrBox.ValueChanged += delegate { if (loading) return; P.Threshold = (double)thrBox.Value; Changed(false); };
            envBox.BackColor = Theme.Surface;
            foreach (string e in EnvNames) envBox.Items.Add(e);
            envBox.SelectedIndex = 1;
            envBox.Changed += delegate { if (loading) return; P.Exponent = EnvValues[envBox.SelectedIndex]; Changed(false); };
            ssidBox.BackColor = Theme.Surface;
            ssidBox.Changed += delegate { if (loading) return; P.SurveySsid = ssidBox.SelectedIndex <= 0 ? "" : ssidBox.SelectedItem; Changed(false); };

            apName.BorderStyle = BorderStyle.None;
            apName.BackColor = Theme.SurfaceHi;
            apName.ForeColor = Theme.Ink;
            apName.Font = Theme.Body;
            apName.TextChanged += delegate { ApEdited(); };
            apHeight.Min = 1; apHeight.Max = 15; apHeight.Increment = 0.5m; apHeight.Value = 3; apHeight.Suffix = " m";
            apTx24.Min = 0; apTx24.Max = 30; apTx24.Increment = 1; apTx24.Value = 15;
            apTx5.Min = 0; apTx5.Max = 30; apTx5.Increment = 1; apTx5.Value = 18;
            apTx6.Min = 0; apTx6.Max = 30; apTx6.Increment = 1; apTx6.Value = 18;
            foreach (Stepper n in new Stepper[] { apHeight, apTx24, apTx5, apTx6 }) n.ValueChanged += delegate { ApEdited(); };
            delAp.Label = "AP 삭제"; delAp.Glyph = Glyph.Trash; delAp.Kind = Btn.Danger;
            delAp.Click += delegate { DeleteSelected(); };

            zoomIn.Label = ""; zoomIn.Glyph = Glyph.Plus; zoomIn.Kind = Btn.Subtle; zoomIn.Width = Theme.S(32);
            zoomOut.Label = ""; zoomOut.Glyph = Glyph.Minus; zoomOut.Kind = Btn.Subtle;
            zoomFit.Label = ""; zoomFit.Glyph = Glyph.Grid; zoomFit.Kind = Btn.Subtle;
            zoomIn.Click += delegate { ZoomBy(1.25, Canvas.Width / 2, Canvas.Height / 2); };
            zoomOut.Click += delegate { ZoomBy(1 / 1.25, Canvas.Width / 2, Canvas.Height / 2); };
            zoomFit.Click += delegate { userMovedView = false; V.Fit(P, Canvas.Width, Canvas.Height); Canvas.Invalidate(); };

            rightPanel.Controls.Add(infoCard);
            rightPanel.Controls.Add(displayCard);
            rightPanel.Controls.Add(inspCard);
            rightPanel.Controls.Add(viewBox); rightPanel.Controls.Add(bandBox); rightPanel.Controls.Add(thrBox);
            rightPanel.Controls.Add(envBox); rightPanel.Controls.Add(ssidBox);
            rightPanel.Controls.Add(apName); rightPanel.Controls.Add(apHeight); rightPanel.Controls.Add(apTx24);
            rightPanel.Controls.Add(apTx5); rightPanel.Controls.Add(apTx6); rightPanel.Controls.Add(delAp);
            zoomIn.BackColor = Color.FromArgb(20, 24, 32); zoomOut.BackColor = Color.FromArgb(20, 24, 32); zoomFit.BackColor = Color.FromArgb(20, 24, 32);
            Canvas.Controls.Add(zoomIn); Canvas.Controls.Add(zoomOut); Canvas.Controls.Add(zoomFit);
            // 카드는 배경이고 그 위 컨트롤이 앞에 와야 한다. WinForms는 먼저 추가한 자식이 앞에 그려지므로
            // 카드를 명시적으로 뒤로 보낸다(플랫폼 기본 순서에 기대지 않는다).
            infoCard.SendToBack(); displayCard.SendToBack(); inspCard.SendToBack();
            rightPanel.Resize += delegate { LayoutRight(); };
            Canvas.Resize += delegate { LayoutCanvasOverlay(); };
        }
        void LayoutRight()
        {
            int w = rightPanel.ClientSize.Width - Theme.S(16) * 2;
            int scroll = rightPanel.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
            w -= scroll;
            int x = Theme.S(16), y = Theme.S(8);
            infoCard.SetBounds(x, y, w, Theme.S(196));
            displayCard.SetBounds(x, y + Theme.S(204), w, Theme.S(372));
            inspCard.SetBounds(x, y + Theme.S(584), w, inspCard.Visible ? Theme.S(380) : 0);
            // 표시 설정 라벨은 카드 본문 좌표(r.Y + 42 + i*48)에 그려지므로 컨트롤도 같은 기준에 맞춘다.
            int dy = y + Theme.S(204) + Theme.S(14 + 30) + Theme.S(42) + Theme.S(15);
            viewBox.SetBounds(x + Theme.S(12), dy, w - Theme.S(24), Theme.S(28));
            bandBox.SetBounds(x + Theme.S(12), dy + Theme.S(48), w - Theme.S(24), Theme.S(28));
            thrBox.SetBounds(x + Theme.S(12), dy + Theme.S(96), w - Theme.S(24), Theme.S(28));
            envBox.SetBounds(x + Theme.S(12), dy + Theme.S(144), w - Theme.S(24), Theme.S(28));
            ssidBox.SetBounds(x + Theme.S(12), dy + Theme.S(192), w - Theme.S(24), Theme.S(28));
            int iy = y + Theme.S(584);
            int ic = iy + Theme.S(14 + 30);
            apName.SetBounds(x + Theme.S(12), ic + Theme.S(50), w - Theme.S(24), Theme.S(24));
            apHeight.SetBounds(x + Theme.S(12), ic + Theme.S(96), w - Theme.S(24), Theme.S(28));
            apTx24.SetBounds(x + Theme.S(12), ic + Theme.S(146), w - Theme.S(24), Theme.S(28));
            apTx5.SetBounds(x + Theme.S(12), ic + Theme.S(196), w - Theme.S(24), Theme.S(28));
            apTx6.SetBounds(x + Theme.S(12), ic + Theme.S(246), w - Theme.S(24), Theme.S(28));
            delAp.SetBounds(x + Theme.S(12), ic + Theme.S(290), w - Theme.S(24), Theme.S(32));
        }
        void LayoutCanvasOverlay()
        {
            int x = Canvas.ClientSize.Width - Theme.S(48), y = Canvas.ClientSize.Height - Theme.S(104);
            zoomIn.SetBounds(x, y, Theme.S(30), Theme.S(30));
            zoomOut.SetBounds(x, y + Theme.S(34), Theme.S(30), Theme.S(30));
            zoomFit.SetBounds(x, y + Theme.S(68), Theme.S(30), Theme.S(30));
        }

        void PaintInfo(Graphics g, Rectangle r)
        {
            // 커버리지 링
            int size = Theme.S(84);
            RectangleF box = new RectangleF(r.X, r.Y + Theme.S(4), size, size);
            using (Pen track = new Pen(Theme.Alpha(Theme.TextMute, 44), Theme.SF(6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(track, box, 135, 270);
            double shown = coverAnim.Value * 100;
            Color tone = head.Coverage < 0 ? Theme.None : shown >= 95 ? Theme.Lv5 : shown >= 80 ? Theme.Lv3 : Theme.Lv1;
            using (Pen arc = new Pen(tone, Theme.SF(6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                if (shown > 0.4) g.DrawArc(arc, box, 135, (float)(270 * Math.Min(1, shown / 100.0)));
            Theme.Mono(g, head.Coverage < 0 ? "—" : shown.ToString("0.0"), Theme.F(16f, FontStyle.Bold), Theme.Ink,
                new Rectangle((int)box.X, (int)box.Y + Theme.S(26), size, Theme.S(22)), Theme.Align.Center);
            Theme.Text(g, "커버리지 %", Theme.F(8f), Theme.TextMute, new Rectangle((int)box.X, (int)box.Y + Theme.S(50), size, Theme.S(14)), Theme.Align.Center);

            int tx = r.X + size + Theme.S(14);
            int tw = r.Right - tx;
            int ty = r.Y + Theme.S(4);
            Line(g, tx, ty, tw, "음영 면적", head.Analyzed > 0 ? head.Dead.ToString("0.0") + " m²" : "—", Theme.Lv1);
            Line(g, tx, ty + Theme.S(30), tw, "분석 면적", head.Analyzed > 0 ? head.Analyzed.ToString("0") + " m²" : "—", Theme.Ink);
            Line(g, tx, ty + Theme.S(60), tw, "실측 지점", head.Points + "곳", Theme.Ink);
            string err = PredictionError();
            Theme.Text(g, err ?? (P.ScaleSet ? "축척 설정됨" : "축척 미설정 — ‘축척’ 도구로 두 점 거리를 입력하세요"),
                Theme.F(8f), err != null ? Theme.Lv2 : Theme.TextMute, new Rectangle(r.X, r.Y + size + Theme.S(10), r.Width, Theme.S(30)));
        }
        static void Line(Graphics g, int x, int y, int w, string caption, string value, Color tone)
        {
            Theme.Text(g, caption, Theme.F(8.5f), Theme.TextMute, new Rectangle(x, y, w, Theme.S(14)));
            Theme.Mono(g, value, Theme.F(11f, FontStyle.Bold), tone, new Rectangle(x, y + Theme.S(12), w, Theme.S(18)), Theme.Align.Left);
        }

        void PaintDisplay(Graphics g, Rectangle r)
        {
            int x = r.X + Theme.S(12);
            string[] labels = { "보기", "주파수 대역", "음영 기준", "공간 유형", "실측 SSID" };
            for (int i = 0; i < labels.Length; i++)
                Theme.Text(g, labels[i], Theme.F(8.5f), Theme.TextMute, new Rectangle(x, r.Y + Theme.S(42 + i * 48), Theme.S(200), Theme.S(14)));
            Theme.Text(g, "히트맵은 벽 재질 감쇠를 반영한 로그거리 모델 추정치입니다.",
                Theme.F(8f), Theme.TextMute, new Rectangle(x, r.Bottom - Theme.S(26), r.Width - Theme.S(20), Theme.S(28)));
        }

        void PaintInspector(Graphics g, Rectangle r)
        {
            int x = r.X + Theme.S(12);
            Theme.Text(g, apModelText, Theme.F(9f), Theme.TextDim, new Rectangle(x, r.Y + Theme.S(4), r.Width - Theme.S(24), Theme.S(18)));
            string[] labels = { "이름", "설치 높이", "2.4 GHz 출력", "5 GHz 출력", "6 GHz 출력" };
            int[] ys = { 34, 80, 130, 180, 230 };
            for (int i = 0; i < labels.Length; i++)
                Theme.Text(g, labels[i], Theme.F(8.5f), Theme.TextMute, new Rectangle(x, r.Y + Theme.S(ys[i]), Theme.S(200), Theme.S(14)));
            Rectangle nameBox = new Rectangle(x - Theme.S(4), r.Y + Theme.S(50), r.Width - Theme.S(20), Theme.S(24));
            Theme.Fill(g, nameBox, Theme.S(6), Theme.SurfaceHi);
            Theme.Stroke(g, nameBox, Theme.S(6), Theme.Border);
        }
        string apModelText = "";

        // ---- 상태 ----
        public void ShowStatus(string text)
        {
            foot.Message = text;
            foot.Invalidate();
        }
        public void SetTool(Tool t)
        {
            ActiveTool = t; hasFirst = false; areaDrag = false;
            foreach (KeyValuePair<Tool, RailButton> kv in toolButtons)
            {
                kv.Value.Active = kv.Key == t;
                kv.Value.Invalidate();
            }
            Canvas.Cursor = t == Tool.Select ? Cursors.Default : Cursors.Cross;
            Canvas.Invalidate();
            if (libCard != null) libCard.Invalidate();
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
            foot.Notice("실행 취소", Theme.AccentHi, 2000);
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
            Heat = h;
            Canvas.Model = P;
            Canvas.StartReveal();
            UpdateStats(); Canvas.Invalidate();
        }

        void UpdateStats()
        {
            head.Aps = P.Aps.Count; head.Points = P.Points.Count;
            head.Mode = P.View == 1 ? "실측 모드" : P.View == 0 ? "예측 모드" : "히트맵 꺼짐";
            if (Heat == null || Heat.Analyzed == 0)
            {
                head.Coverage = -1; head.Dead = 0; head.Analyzed = 0;
                coverAnim.Set(0); coverageShown = -1;
                foot.Message = P.View == 1 ? "실측 지점이 아직 없습니다." : P.View == 0 ? "AP를 도면에 배치해 주세요." : "히트맵이 꺼져 있습니다.";
            }
            else
            {
                head.Coverage = Heat.CoveragePct; head.Dead = Heat.DeadM2; head.Analyzed = Heat.AnalyzedM2;
                coverageShown = (int)Heat.CoveragePct;
                if (Motion.Enabled) { coverAnim.To(Heat.CoveragePct / 100.0); headTick.Start(); }
                else coverAnim.Set(Heat.CoveragePct / 100.0);
                foot.Message = "음영 " + Heat.DeadM2.ToString("0.0") + " m² · 분석 " + Heat.AnalyzedM2.ToString("0") + " m² · 기준 " + P.Threshold.ToString("0") + " dBm";
            }
            foot.Chips.Clear();
            foot.Chips.Add("AP " + P.Aps.Count);
            foot.Chips.Add("벽 " + P.Walls.Count);
            foot.Chips.Add("실측 " + P.Points.Count);
            if (!P.ScaleSet) foot.Chips.Add("축척 미설정");
            foot.Invalidate(); head.Invalidate(); infoCard.Invalidate();
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
            return diff.Count == 0 ? null : "예측–실측 차이 평균 " + diff.Average().ToString("0.0") + " dB";
        }

        void SyncViewControls()
        {
            loading = true;
            viewBox.SelectedIndex = Math.Max(0, Math.Min(2, P.View));
            bandBox.SelectedIndex = Math.Max(0, Math.Min(2, P.Band));
            thrBox.Value = (decimal)Math.Max(-90, Math.Min(-50, P.Threshold));
            int env = Array.FindIndex(EnvValues, e => Math.Abs(e - P.Exponent) < 0.01);
            envBox.SelectedIndex = env < 0 ? 1 : env;
            RefreshSsids();
            ShowSelection();
            loading = false;
        }
        void RefreshSsids()
        {
            bool was = loading;
            loading = true;
            ssidBox.Items.Clear();
            ssidBox.Items.Add("(모든 SSID)");
            foreach (string s in P.Points.SelectMany(a => a.Readings).Select(r => r.Ssid).Distinct().OrderBy(a => a)) ssidBox.Items.Add(s);
            int i = ssidBox.Items.IndexOf(P.SurveySsid);
            ssidBox.SelectedIndex = i < 0 ? 0 : i;
            loading = was;
        }

        void ShowSelection()
        {
            ApItem a = Selected as ApItem;
            inspCard.Visible = a != null;
            apName.Visible = a != null; apHeight.Visible = a != null; apTx24.Visible = a != null;
            apTx5.Visible = a != null; apTx6.Visible = a != null; delAp.Visible = a != null;
            WallItem w = Selected as WallItem;
            if (w != null) wallBox.SelectedIndex = w.Material;
            if (a == null) { LayoutRight(); return; }
            loading = true;
            apModelText = a.Vendor + " " + a.Model;
            apName.Text = a.Name;
            apHeight.Value = Clamp(apHeight, a.Height);
            apTx24.Value = Clamp(apTx24, a.Tx24);
            apTx5.Value = Clamp(apTx5, a.Tx5);
            apTx6.Value = Clamp(apTx6, a.Tx6);
            apTx6.Enabled = a.Has6;
            loading = false;
            inspCard.Invalidate();
            LayoutRight();
        }
        static decimal Clamp(Stepper n, double v) { return Math.Max(n.Min, Math.Min(n.Max, (decimal)v)); }
        void ApEdited()
        {
            ApItem a = Selected as ApItem;
            if (loading || a == null) return;
            Snap();
            a.Name = apName.Text; a.Height = (double)apHeight.Value;
            a.Tx24 = (double)apTx24.Value; a.Tx5 = (double)apTx5.Value; a.Tx6 = (double)apTx6.Value;
            inspCard.Invalidate();
            Changed();
        }

        public void DeleteSelected()
        {
            if (Selected == null) return;
            Snap();
            P.Aps.Remove(Selected as ApItem); P.Walls.Remove(Selected as WallItem); P.Points.Remove(Selected as SurveyPoint);
            Selected = null; ShowSelection(); RefreshSsids(); Changed(); Canvas.Invalidate();
            foot.Notice("선택 항목을 삭제했습니다.", Theme.TextDim, 2500);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { hasFirst = false; areaDrag = false; Canvas.Invalidate(); }
            if (e.KeyCode == Keys.Delete && ActiveControl == Canvas) { DeleteSelected(); e.Handled = true; }
            if (e.KeyCode == Keys.F5) ComputeNow();
        }

        // ---- 마우스 ----
        double ClampX(double x) { return Math.Max(0, Math.Min(P.PlanW, x)); }
        double ClampY(double y) { return Math.Max(0, Math.Min(P.PlanH, y)); }

        object HitTest(Point m)
        {
            foreach (ApItem a in Enumerable.Reverse(P.Aps))
            {
                PointF c = V.P(a.X, a.Y);
                if (Dist(m.X, m.Y, c.X, c.Y) <= Theme.S(18)) return a;
            }
            foreach (SurveyPoint s in Enumerable.Reverse(P.Points))
            {
                PointF c = V.P(s.X, s.Y);
                if (Dist(m.X, m.Y, c.X, c.Y) <= Theme.S(10)) return s;
            }
            foreach (WallItem w in Enumerable.Reverse(P.Walls))
            {
                PointF a = V.P(w.X1, w.Y1), b = V.P(w.X2, w.Y2);
                if (SegDist(m.X, m.Y, a.X, a.Y, b.X, b.Y) <= Theme.S(7)) return w;
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
                        P.Walls.Add(new WallItem { X1 = firstX, Y1 = firstY, X2 = x, Y2 = y, Material = wallBox.SelectedIndex < 0 ? 0 : wallBox.SelectedIndex });
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
            P.Aps.Add(new ApItem
            {
                Vendor = m.Vendor, Model = m.Name, Name = m.Name + "-" + n, X = x, Y = y,
                Gain24 = m.G24, Gain5 = m.G5, Gain6 = m.Has6 ? m.G6 : 0, Has6 = m.Has6
            });
            Selected = P.Aps[P.Aps.Count - 1];
            ShowSelection(); Changed();
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
            ShowStatus("축척을 설정했습니다. 도면 크기 " + P.PlanW.ToString("0.0") + " × " + P.PlanH.ToString("0.0") + " m");
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
            foot.Chips.Clear();
            foot.Chips.Add(curX.ToString("0.0") + ", " + curY.ToString("0.0") + " m");
            if (server != null) foot.Chips.Add("예측 " + b.ToString("0") + " dBm · " + server.Name);
            foot.Invalidate();
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
            ZoomBy(e.Delta > 0 ? 1.15 : 1 / 1.15, e.X, e.Y);
        }
        void ZoomBy(double f, int sx, int sy)
        {
            double wx = V.WX(sx), wy = V.WY(sy);
            V.Zoom = Math.Max(1, Math.Min(400, V.Zoom * f));
            V.PanX = sx - wx * V.Zoom; V.PanY = sy - wy * V.Zoom;
            userMovedView = true;
            Canvas.Invalidate();
        }

        void CanvasPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.FromArgb(14, 17, 22));
            Scene.Draw(g, P, Plan, Heat, V, Selected, Canvas.Reveal, Canvas.PulsePhase);
            if (P.Aps.Count == 0 && Plan == null && !hasFirst)
            {
                Rectangle box = new Rectangle(Canvas.Width / 2 - Theme.S(190), Canvas.Height / 2 - Theme.S(60), Theme.S(380), Theme.S(140));
                Theme.Fill(g, box, Theme.S(10), Color.FromArgb(200, 16, 20, 27));
                Theme.Stroke(g, box, Theme.S(10), Theme.Border);
                Deco.EmptyState(g, new Rectangle(box.X, box.Y + Theme.S(6), box.Width, box.Height - Theme.S(10)), Glyph.Folder,
                    "도면을 불러와 시작하세요", "파일 > 도면 이미지 불러오기 (Ctrl+I) · 예제는 파일 > 예제 도면 열기");
            }
            if (hasFirst && (ActiveTool == Tool.Wall || ActiveTool == Tool.Scale))
                using (Pen pen = new Pen(Theme.AccentHi, Theme.SF(1.6f)) { DashStyle = DashStyle.Dash })
                {
                    g.DrawLine(pen, V.P(firstX, firstY), V.P(curX, curY));
                    PointF m = V.P(curX, curY);
                    g.DrawString(Dist(firstX, firstY, curX, curY).ToString("0.0") + " m", Theme.F(9f, FontStyle.Bold), Brushes.White, m.X + Theme.S(10), m.Y + Theme.S(8));
                }
            if (areaDrag)
                using (Pen pen = new Pen(Theme.AccentHi, Theme.SF(1.6f)) { DashStyle = DashStyle.Dash })
                {
                    PointF a = V.P(Math.Min(firstX, curX), Math.Min(firstY, curY));
                    g.DrawRectangle(pen, a.X, a.Y, (float)(Math.Abs(curX - firstX) * V.Zoom), (float)(Math.Abs(curY - firstY) * V.Zoom));
                }
            if (P.View != 2) Scene.Legend(g, P, Theme.S(12), Canvas.Height - Theme.S(108));
        }

        // ---- 현장 실측 ----
        void StartMeasure(double x, double y)
        {
            if (measuring) return;
            measuring = true;
            ShowStatus("측정 중입니다. 그 자리에서 약 4초만 기다려 주세요…");
            foot.Chips.Clear(); foot.Chips.Add("측정 중"); foot.Invalidate();
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
            if (m.Error.Length > 0) { foot.Notice("측정 실패: " + m.Error, Theme.Lv1, 6000); return; }
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
            foot.Notice("측정 완료: AP " + m.Readings.Count + "개 감지" + (m.ConnectedSsid.Length > 0 ? " · " + m.ConnectedSsid + " " + m.ConnectedRssi + " dBm" : ""), Theme.Lv5, 6000);
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
            userMovedView = false; V.Fit(P, Canvas.Width, Canvas.Height);
            Canvas.Model = P;
            SyncViewControls(); Changed(false);
            foot.Message = "";
        }

        void OpenPlanDialog()
        {
            using (OpenFileDialog d = new OpenFileDialog { Title = "도면 이미지 선택", Filter = "이미지 파일 (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { LoadPlan(d.FileName); SetTool(Tool.Scale); foot.Notice("도면을 불러왔습니다. 길이를 아는 두 점을 눌러 축척을 설정하세요.", Theme.AccentHi, 6000); }
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
            try { SaveProject(path); foot.Notice("저장했습니다: " + Path.GetFileName(path), Theme.Lv5, 4000); }
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
                try { ExportPng(d.FileName); foot.Notice("내보냈습니다: " + Path.GetFileName(d.FileName), Theme.Lv5, 4000); }
                catch (Exception ex) { MessageBox.Show(this, "내보낼 수 없습니다.\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        public void ExportPng(string path)
        {
            ComputeNow();
            int w = 2000, planH = (int)(w * P.PlanH / Math.Max(1, P.PlanW)), top = Theme.S(150);
            using (Bitmap bmp = new Bitmap(w, planH + top + Theme.S(30)))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(11, 14, 19));
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                string mode = P.View == 1 ? "실측" : "예측";
                Theme.Text(g, "Wi-Fi 음영 분석 리포트", Theme.F(22f, FontStyle.Bold), Theme.Ink, new Rectangle(Theme.S(24), Theme.S(20), w - Theme.S(48), Theme.S(34)));
                string line = "모드 " + mode + " · " + Rf.BandNames[P.Band] + "  |  커버리지 " +
                    (Heat != null && Heat.Analyzed > 0 ? Heat.CoveragePct.ToString("0.0") + "%" : "—") +
                    "  ·  음영 " + (Heat != null ? Heat.DeadM2.ToString("0.0") : "0") + " m²  ·  기준 " + P.Threshold.ToString("0") + " dBm  ·  AP " + P.Aps.Count + "대  ·  실측 " + P.Points.Count + "곳";
                Theme.Text(g, line, Theme.F(11f), Theme.TextDim, new Rectangle(Theme.S(24), Theme.S(58), w - Theme.S(48), Theme.S(22)));
                View v = new View();
                v.Zoom = Math.Min((w - Theme.S(48)) / Math.Max(1, P.PlanW), (planH - Theme.S(20)) / Math.Max(1, P.PlanH));
                v.PanX = (w - P.PlanW * v.Zoom) / 2; v.PanY = top + (planH - P.PlanH * v.Zoom) / 2;
                Scene.Draw(g, P, Plan, Heat, v, null, 1f, 0f);
                Scene.Legend(g, P, Theme.S(24), top + planH - Theme.S(112));
                Theme.Text(g, "예측값은 로그거리 경로손실 모델 추정치입니다. 벽 감쇠·AP 출력은 입력값 기준이며 실제 환경과 다를 수 있습니다.",
                    Theme.F(9f), Theme.TextMute, new Rectangle(Theme.S(24), bmp.Height - Theme.S(26), w - Theme.S(48), Theme.S(18)));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        // 리포트: PNG + HTML + CSV를 한 번에
        void ExportReportDialog()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog { Description = "리포트를 저장할 폴더" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { ExportReport(d.SelectedPath); }
                catch (Exception ex) { MessageBox.Show(this, "리포트를 저장할 수 없습니다.\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        public void ExportReport(string dir)
        {
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
            string png = Path.Combine(dir, "coverage_" + stamp + ".png");
            string csv = Path.Combine(dir, "access-points_" + stamp + ".csv");
            string html = Path.Combine(dir, "report_" + stamp + ".html");
            ExportPng(png);
            ExportApCsv(csv);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>Wi-Fi 음영 분석 리포트</title><style>");
            sb.AppendLine(":root{color-scheme:dark}body{margin:0;background:#0b0e13;color:#e9eef6;font:14px/1.6 'Segoe UI',Malgun Gothic,sans-serif}");
            sb.AppendLine("main{max-width:1040px;margin:0 auto;padding:32px}h1{font-size:22px;margin:0 0 4px}p.sub{color:#98a4b5;margin:0 0 24px}");
            sb.AppendLine(".kpi{display:grid;grid-template-columns:repeat(4,1fr);gap:12px;margin:0 0 24px}.k{background:#12161d;border:1px solid #232a35;border-radius:12px;padding:14px}");
            sb.AppendLine(".k span{display:block;color:#66717f;font-size:11px;letter-spacing:.08em;text-transform:uppercase}.k b{font-size:22px;color:#e9eef6}");
            sb.AppendLine("img{width:100%;border:1px solid #232a35;border-radius:12px}table{width:100%;border-collapse:collapse;margin-top:20px;font-size:13px}");
            sb.AppendLine("th,td{text-align:left;padding:8px 10px;border-bottom:1px solid #232a35}th{color:#66717f;font-weight:600;font-size:11px;text-transform:uppercase;letter-spacing:.06em}");
            sb.AppendLine("footer{color:#66717f;font-size:12px;margin-top:24px}@media print{body{background:#0b0e13;color:#e9eef6;-webkit-print-color-adjust:exact;print-color-adjust:exact}.k,img,table{page-break-inside:avoid}}");
            sb.AppendLine("</style></head><body><main>");
            sb.AppendLine("<h1>Wi-Fi 음영 분석 리포트</h1>");
            sb.AppendLine("<p class=\"sub\">" + (P.View == 1 ? "실측" : "예측") + " · " + Rf.BandNames[P.Band] + " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "</p>");
            sb.AppendLine("<div class=\"kpi\">");
            sb.AppendLine("<div class=\"k\"><span>커버리지</span><b>" + (Heat != null && Heat.Analyzed > 0 ? Heat.CoveragePct.ToString("0.0") + "%" : "—") + "</b></div>");
            sb.AppendLine("<div class=\"k\"><span>음영 면적</span><b>" + (Heat != null ? Heat.DeadM2.ToString("0.0") + " m²" : "—") + "</b></div>");
            sb.AppendLine("<div class=\"k\"><span>분석 면적</span><b>" + (Heat != null ? Heat.AnalyzedM2.ToString("0") + " m²" : "—") + "</b></div>");
            sb.AppendLine("<div class=\"k\"><span>AP / 실측</span><b>" + P.Aps.Count + " / " + P.Points.Count + "</b></div>");
            sb.AppendLine("</div>");
            sb.AppendLine("<img src=\"" + Path.GetFileName(png) + "\" alt=\"커버리지 히트맵\">");
            sb.AppendLine("<table><tr><th>이름</th><th>제조사</th><th>모델</th><th>위치 (m)</th><th>높이</th><th>2.4G</th><th>5G</th><th>6G</th></tr>");
            foreach (ApItem a in P.Aps)
                sb.AppendLine("<tr><td>" + Esc(a.Name) + "</td><td>" + Esc(a.Vendor) + "</td><td>" + Esc(a.Model) + "</td><td>" + a.X.ToString("0.0") + ", " + a.Y.ToString("0.0") +
                    "</td><td>" + a.Height.ToString("0.0") + " m</td><td>" + a.Tx24.ToString("0") + " dBm</td><td>" + a.Tx5.ToString("0") + " dBm</td><td>" + (a.Has6 ? a.Tx6.ToString("0") + " dBm" : "—") + "</td></tr>");
            sb.AppendLine("</table>");
            sb.AppendLine("<footer>음영 기준 " + P.Threshold.ToString("0") + " dBm · 공간 지수 n=" + P.Exponent.ToString("0.0") +
                " · 예측값은 로그거리 경로손실 모델 추정치이며 실제 측정과 다를 수 있습니다. 브라우저 인쇄로 PDF 저장이 가능합니다.</footer>");
            sb.AppendLine("</main></body></html>");
            File.WriteAllText(html, sb.ToString(), new UTF8Encoding(true));
            foot.Notice("리포트를 저장했습니다: " + Path.GetFileName(html) + " (PNG·CSV 포함)", Theme.Lv5, 8000);
        }
        static string Esc(string s) { return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;"); }

        void ExportCsvDialog()
        {
            using (SaveFileDialog d = new SaveFileDialog { Filter = "CSV 파일 (*.csv)|*.csv", FileName = "실측데이터.csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { ExportCsv(d.FileName); foot.Notice("내보냈습니다: " + Path.GetFileName(d.FileName), Theme.Lv5, 4000); }
                catch (Exception ex) { MessageBox.Show(this, "내보낼 수 없습니다.\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        // 리포트의 access-points CSV는 HTML 표와 같은 AP 목록이어야 한다(실측 데이터는 별도 CSV).
        public void ExportApCsv(string path)
        {
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine("이름,제조사,모델,X(m),Y(m),높이(m),2.4GHz(dBm),5GHz(dBm),6GHz(dBm)");
                foreach (ApItem a in P.Aps)
                    w.WriteLine(string.Join(",", Csv.Field(a.Name), Csv.Field(a.Vendor), Csv.Field(a.Model),
                        a.X.ToString("0.0", CultureInfo.InvariantCulture), a.Y.ToString("0.0", CultureInfo.InvariantCulture),
                        a.Height.ToString("0.0", CultureInfo.InvariantCulture),
                        a.Tx24.ToString("0", CultureInfo.InvariantCulture), a.Tx5.ToString("0", CultureInfo.InvariantCulture),
                        a.Has6 ? a.Tx6.ToString("0", CultureInfo.InvariantCulture) : ""));
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
                        w.WriteLine(string.Join(",", n, s.Time, s.X.ToString("0.00", CultureInfo.InvariantCulture), s.Y.ToString("0.00", CultureInfo.InvariantCulture),
                            Csv.Field(r.Ssid), r.Bssid, Rf.BandNames[r.Band], r.Rssi));
                }
            }
        }
        double AskNumber(string title, string prompt, double value)
        {
            using (Form d = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(Theme.S(330), Theme.S(140)), Font = Theme.Body, BackColor = Theme.Bg, ForeColor = Theme.Ink, AutoScaleMode = AutoScaleMode.None })
            {
                Label l = new Label { Text = prompt, AutoSize = false, ForeColor = Theme.TextDim }; l.SetBounds(Theme.S(16), Theme.S(14), Theme.S(298), Theme.S(24));
                Stepper n = new Stepper { Min = 0.1m, Max = 10000, Increment = 0.5m, Decimals = 2, BackColor = Theme.Bg };
                n.SetBounds(Theme.S(16), Theme.S(44), Theme.S(298), Theme.S(30));
                n.Value = Math.Max(n.Min, Math.Min(n.Max, (decimal)value));
                UiButton ok = new UiButton { Label = "확인", Kind = Btn.Primary, ShowGlyph = false, BackColor = Theme.Bg };
                ok.SetBounds(Theme.S(118), Theme.S(92), Theme.S(92), Theme.S(32));
                UiButton no = new UiButton { Label = "취소", Kind = Btn.Ghost, ShowGlyph = false, BackColor = Theme.Bg };
                no.SetBounds(Theme.S(218), Theme.S(92), Theme.S(92), Theme.S(32));
                ok.Click += delegate { d.DialogResult = DialogResult.OK; d.Close(); };
                no.Click += delegate { d.DialogResult = DialogResult.Cancel; d.Close(); };
                d.Controls.AddRange(new Control[] { l, n, ok, no });
                return d.ShowDialog(this) == DialogResult.OK ? (double)n.Value : -1;
            }
        }

        void FillModels()
        {
            loading = true;
            modelList.Rows.Clear();
            foreach (ApModel m in Catalog.All.Where(a => a.Vendor == (string)vendorBox.SelectedItem))
                modelList.Rows.Add(new Row { Cells = new string[] { m.Name }, Tones = new Color[] { Theme.TextDim } });
            modelList.SelectedIndex = modelList.Rows.Count > 0 ? 0 : -1;
            loading = false;
            ShowModel();
        }
        ApModel CurrentModel()
        {
            string name = modelList.SelectedRow != null ? modelList.SelectedRow.Cells[0] : null;
            return name == null ? null : Catalog.All.FirstOrDefault(a => a.Vendor == (string)vendorBox.SelectedItem && a.Name == name);
        }
        void ShowModel()
        {
            ApModel m = CurrentModel();
            if (m == null) return;
            modelInfo2 = new ModelInfo();
            modelInfo2.Warn = !m.Verified;
            modelInfo2.Text = m.Gen + "\n안테나 이득 " + m.G24 + " / " + m.G5 + " / (6GHz " + (m.Has6 ? m.G6 + " dBi)" : "없음)") + "\n" + (m.Verified ? "제조사 자료 기준" : "참고값 — 데이터시트 확인 필요");
            libCard.Invalidate();
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
            // Wi-Fi 어댑터가 없으면 FinishMeasure가 지점을 만들지 않으므로 상세 문자열에서 먼저 확인한다.
            Check("실측 지점 기록", f.P.Points.Count == 1 && f.P.View == 1 && f.P.SurveySsid == m.ConnectedSsid,
                f.P.Points.Count == 0 ? "지점 없음 (측정 오류 '" + m.Error + "')" : f.P.Points[0].Readings.Count + "건");
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

            string rdir = Path.Combine(dir, "report");
            f.ExportReport(rdir);
            string[] htmls = Directory.GetFiles(rdir, "*.html"), pngs = Directory.GetFiles(rdir, "*.png"), csvs = Directory.GetFiles(rdir, "*.csv");
            Check("리포트 내보내기(HTML+PNG+CSV)", htmls.Length == 1 && pngs.Length == 1 && csvs.Length == 1
                && File.ReadAllText(htmls[0]).Contains("<!doctype html>") && new FileInfo(pngs[0]).Length > 10000,
                htmls.Length + " HTML / " + pngs.Length + " PNG / " + csvs.Length + " CSV");
            Check("리포트 CSV는 AP 목록", csvs.Length == 1 && File.ReadAllLines(csvs[0]).Length == f.P.Aps.Count + 1,
                csvs.Length == 1 ? File.ReadAllLines(csvs[0]).Length + "행 (AP " + f.P.Aps.Count + "대)" : "CSV 없음");
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
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Theme.K = g.DpiX / 96f;
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
