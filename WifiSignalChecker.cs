// Wi-Fi 신호 체크 - Windows Native Wifi API 기반 실시간 신호 측정 도구
// 빌드: build.bat (Windows 기본 포함 .NET Framework 4.x의 csc.exe 사용)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WifiSignalChecker
{
    static class Native
    {
        [DllImport("wlanapi.dll")] public static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] public static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] public static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] public static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr opcodeType);
        [DllImport("wlanapi.dll")] public static extern uint WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, bool securityEnabled, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] public static extern uint WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
        [DllImport("wlanapi.dll")] public static extern void WlanFreeMemory(IntPtr memory);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        public const int OpInterfaceState = 6;
        public const int OpCurrentConnection = 7;
        public const int OpChannelNumber = 8;
        public const int OpRssi = 0x10000102;
        public const int BssAny = 3;
        public const int IfaceItemSize = 532;
        public const int BssEntrySize = 360;
    }

    class BssInfo
    {
        public string Ssid, Bssid, Band, Security;
        public int Rssi, Quality, Channel;
    }

    class Sample
    {
        public bool HasAdapter, Connected;
        public string AdapterName = "", Ssid = "", Bssid = "", Phy = "", Auth = "", Band = "", Error = "", NearbyError = "";
        public int Rssi, Quality, Channel;
        public double RxMbps, TxMbps;
        public List<BssInfo> Nearby = new List<BssInfo>();
        public int NearbyVersion;
        public DateTime LastScan;
        // 게이트웨이 핑
        public string Gateway = "";
        public int LastPing = -1, PingCount;
        public double AvgPing = -1, LossPct;
    }

    class Sampler
    {
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Queue<int> pings = new Queue<int>();
        volatile bool stop, scanRequested;
        volatile string ifaceId = "";
        volatile string gateway = "";
        Sample latest = new Sample();
        List<BssInfo> nearby = new List<BssInfo>();
        string nearbyError = "";
        int nearbyVersion;
        DateTime lastScan = DateTime.MinValue;

        public void Start()
        {
            new Thread(Loop) { IsBackground = true, Name = "wlan" }.Start();
            new Thread(PingLoop) { IsBackground = true, Name = "ping" }.Start();
        }
        public void Stop() { stop = true; wake.Set(); }
        public void RequestScan() { scanRequested = true; wake.Set(); }
        public Sample Latest { get { lock (gate) return latest; } }

        void Publish(Sample s) { lock (gate) latest = s; }

        void Loop()
        {
            uint negotiated; IntPtr h;
            uint rc = Native.WlanOpenHandle(2, IntPtr.Zero, out negotiated, out h);
            if (rc != 0)
            {
                Publish(new Sample { Error = "Wi-Fi 서비스(WLAN AutoConfig)에 연결할 수 없습니다. (코드 " + rc + ")" });
                return;
            }
            DateTime nextScan = DateTime.MinValue, nextList = DateTime.MinValue;
            while (!stop)
            {
                try
                {
                    Guid id; string name;
                    Sample s = new Sample();
                    if (FindInterface(h, out id, out name))
                    {
                        s.HasAdapter = true; s.AdapterName = name;
                        ifaceId = id.ToString("B");
                        DateTime now = DateTime.Now;
                        if (scanRequested || now >= nextScan)
                        {
                            bool manual = scanRequested;
                            scanRequested = false;
                            Native.WlanScan(h, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                            lastScan = now;
                            nextScan = now.AddSeconds(30);
                            // 스캔 결과가 드라이버에 반영될 시간을 준다
                            nextList = manual ? now.AddSeconds(4) : (nextList == DateTime.MinValue ? now : now.AddSeconds(4));
                        }
                        if (now >= nextList)
                        {
                            ReadBssList(h, id);
                            nextList = now.AddSeconds(5);
                        }
                        ReadConnection(h, id, s);
                    }
                    else
                    {
                        ifaceId = "";
                        s.Error = "Wi-Fi 어댑터를 찾을 수 없습니다.";
                    }
                    s.Nearby = nearby; s.NearbyVersion = nearbyVersion; s.NearbyError = nearbyError; s.LastScan = lastScan;
                    FillPing(s);
                    Publish(s);
                }
                catch (Exception ex)
                {
                    Publish(new Sample { Error = "측정 오류: " + ex.Message });
                }
                wake.WaitOne(1000);
            }
            Native.WlanCloseHandle(h, IntPtr.Zero);
        }

        static bool FindInterface(IntPtr h, out Guid id, out string name)
        {
            id = Guid.Empty; name = "";
            IntPtr list;
            if (Native.WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return false;
            try
            {
                int count = Marshal.ReadInt32(list, 0);
                bool found = false;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(list.ToInt64() + 8 + i * Native.IfaceItemSize);
                    byte[] g = new byte[16];
                    Marshal.Copy(item, g, 0, 16);
                    int state = Marshal.ReadInt32(item, 16 + 512);
                    // 연결된 어댑터를 우선 사용한다 (state 1 = connected)
                    if (!found || state == 1)
                    {
                        id = new Guid(g);
                        name = Marshal.PtrToStringUni(new IntPtr(item.ToInt64() + 16));
                        found = true;
                    }
                    if (state == 1) break;
                }
                return found;
            }
            finally { Native.WlanFreeMemory(list); }
        }

        void ReadConnection(IntPtr h, Guid id, Sample s)
        {
            int size; IntPtr data;
            if (Native.WlanQueryInterface(h, ref id, Native.OpCurrentConnection, IntPtr.Zero, out size, out data, IntPtr.Zero) != 0)
                return; // 연결되어 있지 않으면 실패한다
            try
            {
                if (Marshal.ReadInt32(data, 0) != 1) return;
                s.Connected = true;
                s.Ssid = ReadSsid(new IntPtr(data.ToInt64() + 520));
                s.Bssid = ReadMac(new IntPtr(data.ToInt64() + 560));
                s.Phy = PhyName(Marshal.ReadInt32(data, 568));
                s.Quality = Marshal.ReadInt32(data, 576);
                s.RxMbps = (uint)Marshal.ReadInt32(data, 580) / 1000.0;
                s.TxMbps = (uint)Marshal.ReadInt32(data, 584) / 1000.0;
                s.Auth = AuthName(Marshal.ReadInt32(data, 596));
            }
            finally { Native.WlanFreeMemory(data); }

            IntPtr r;
            if (Native.WlanQueryInterface(h, ref id, Native.OpRssi, IntPtr.Zero, out size, out r, IntPtr.Zero) == 0)
            {
                s.Rssi = Marshal.ReadInt32(r, 0);
                Native.WlanFreeMemory(r);
            }
            else s.Rssi = s.Quality / 2 - 100;

            BssInfo me = nearby.FirstOrDefault(b => b.Bssid == s.Bssid);
            if (me != null) { s.Channel = me.Channel; s.Band = me.Band; }
            else
            {
                IntPtr c;
                if (Native.WlanQueryInterface(h, ref id, Native.OpChannelNumber, IntPtr.Zero, out size, out c, IntPtr.Zero) == 0)
                {
                    s.Channel = Marshal.ReadInt32(c, 0);
                    s.Band = s.Channel <= 14 ? "2.4 GHz" : "5 GHz";
                    Native.WlanFreeMemory(c);
                }
            }
        }

        void ReadBssList(IntPtr h, Guid id)
        {
            IntPtr list;
            uint rc = Native.WlanGetNetworkBssList(h, ref id, IntPtr.Zero, Native.BssAny, false, IntPtr.Zero, out list);
            if (rc != 0)
            {
                nearbyError = rc == 5
                    ? "주변 네트워크를 보려면 Windows 설정 > 개인 정보 및 보안 > 위치에서 위치 서비스를 켜 주세요."
                    : "주변 네트워크를 읽지 못했습니다. (코드 " + rc + ")";
                return;
            }
            try
            {
                int count = Marshal.ReadInt32(list, 4);
                Dictionary<string, BssInfo> map = new Dictionary<string, BssInfo>();
                for (int i = 0; i < count; i++)
                {
                    IntPtr e = new IntPtr(list.ToInt64() + 8 + i * Native.BssEntrySize);
                    int mhz = Marshal.ReadInt32(e, 92) / 1000;
                    BssInfo b = new BssInfo
                    {
                        Ssid = ReadSsid(e),
                        Bssid = ReadMac(new IntPtr(e.ToInt64() + 40)),
                        Rssi = Marshal.ReadInt32(e, 56),
                        Quality = Marshal.ReadInt32(e, 60),
                        Channel = ChannelOf(mhz),
                        Band = BandOf(mhz),
                        Security = ParseSecurity(e)
                    };
                    map[b.Bssid] = b;
                }
                nearby = map.Values.OrderByDescending(b => b.Rssi).ToList();
                nearbyError = "";
                nearbyVersion++;
            }
            finally { Native.WlanFreeMemory(list); }
        }

        static string ReadSsid(IntPtr p)
        {
            int len = Math.Max(0, Math.Min(32, Marshal.ReadInt32(p, 0)));
            byte[] b = new byte[len];
            Marshal.Copy(new IntPtr(p.ToInt64() + 4), b, 0, len);
            string s = Encoding.UTF8.GetString(b).Trim('\0');
            return s.Length == 0 ? "(숨겨진 네트워크)" : s;
        }

        static string ReadMac(IntPtr p)
        {
            byte[] b = new byte[6];
            Marshal.Copy(p, b, 0, 6);
            return BitConverter.ToString(b).Replace('-', ':');
        }

        static int ChannelOf(int mhz)
        {
            if (mhz == 2484) return 14;
            if (mhz < 3000) return (mhz - 2407) / 5;
            if (mhz < 5925) return (mhz - 5000) / 5;
            return (mhz - 5950) / 5;
        }
        static string BandOf(int mhz) { return mhz < 3000 ? "2.4 GHz" : mhz < 5925 ? "5 GHz" : "6 GHz"; }

        static string PhyName(int t)
        {
            switch (t)
            {
                case 11: return "Wi-Fi 7|802.11be";
                case 10: return "Wi-Fi 6|802.11ax";
                case 8: return "Wi-Fi 5|802.11ac";
                case 7: return "Wi-Fi 4|802.11n";
                case 6: return "802.11g|";
                case 5: return "802.11b|";
                case 4: return "802.11a|";
                default: return "-|";
            }
        }

        static string AuthName(int a)
        {
            switch (a)
            {
                case 1: return "개방(암호 없음)";
                case 2: return "WEP";
                case 3: return "WPA-Enterprise";
                case 4: return "WPA-Personal";
                case 6: return "WPA2-Enterprise";
                case 7: return "WPA2-Personal";
                case 8: case 11: return "WPA3-Enterprise";
                case 9: return "WPA3-Personal";
                case 10: return "OWE";
                default: return "기타";
            }
        }

        static string ParseSecurity(IntPtr entry)
        {
            int capability = (ushort)Marshal.ReadInt16(entry, 88);
            int offset = Marshal.ReadInt32(entry, 352), size = Marshal.ReadInt32(entry, 356);
            bool rsn = false, wpa = false, sae = false, psk = false, owe = false;
            if (size > 0 && size < 8192)
            {
                byte[] ie = new byte[size];
                Marshal.Copy(new IntPtr(entry.ToInt64() + offset), ie, 0, size);
                int i = 0;
                while (i + 2 <= size)
                {
                    int id = ie[i], len = ie[i + 1], body = i + 2;
                    if (body + len > size) break;
                    if (id == 221 && len >= 4 && ie[body] == 0 && ie[body + 1] == 0x50 && ie[body + 2] == 0xF2 && ie[body + 3] == 1) wpa = true;
                    if (id == 48 && len >= 8)
                    {
                        rsn = true;
                        int p = body + 6; // version(2) + group cipher(4)
                        int end = body + len;
                        if (p + 2 <= end)
                        {
                            p += 2 + 4 * (ie[p] | ie[p + 1] << 8); // pairwise cipher 목록 건너뛰기
                            if (p + 2 <= end)
                            {
                                int akmCount = ie[p] | ie[p + 1] << 8;
                                p += 2;
                                for (int k = 0; k < akmCount && p + 4 <= end; k++, p += 4)
                                {
                                    int t = ie[p + 3];
                                    if (t == 2 || t == 4 || t == 6) psk = true;
                                    if (t == 8 || t == 9 || t == 24 || t == 25) sae = true;
                                    if (t == 18) owe = true;
                                }
                            }
                        }
                    }
                    i = body + len;
                }
            }
            if (sae && psk) return "WPA2/WPA3";
            if (sae) return "WPA3";
            if (owe) return "OWE";
            if (rsn) return psk ? "WPA2" : "WPA2-기업용";
            if (wpa) return "WPA";
            return (capability & 0x10) != 0 ? "WEP" : "개방";
        }

        // ---- 게이트웨이 핑 (지연/손실) ----
        void PingLoop()
        {
            int tick = 0;
            using (Ping ping = new Ping())
            {
                while (!stop)
                {
                    DateTime t0 = DateTime.Now;
                    try
                    {
                        if (tick++ % 10 == 0 || gateway.Length == 0) gateway = FindGateway(ifaceId);
                        string gw = gateway;
                        if (gw.Length > 0)
                        {
                            int result = -1;
                            try
                            {
                                PingReply r = ping.Send(gw, 900);
                                if (r.Status == IPStatus.Success) result = (int)r.RoundtripTime;
                            }
                            catch (PingException) { }
                            lock (pings)
                            {
                                pings.Enqueue(result);
                                while (pings.Count > 60) pings.Dequeue();
                            }
                        }
                        else lock (pings) pings.Clear();
                    }
                    catch (Exception) { }
                    int wait = 1000 - (int)(DateTime.Now - t0).TotalMilliseconds;
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
        }

        static string FindGateway(string id)
        {
            if (id.Length == 0) return "";
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (GatewayIPAddressInformation g in n.GetIPProperties().GatewayAddresses)
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                        return g.Address.ToString();
            }
            return "";
        }

        void FillPing(Sample s)
        {
            s.Gateway = gateway;
            lock (pings)
            {
                s.PingCount = pings.Count;
                if (pings.Count == 0) return;
                s.LastPing = pings.Last();
                int[] ok = pings.Where(p => p >= 0).ToArray();
                s.LossPct = 100.0 * (pings.Count - ok.Length) / pings.Count;
                s.AvgPing = ok.Length > 0 ? ok.Average() : -1;
            }
        }
    }

    // ---- 화면 공통 ----
    static class Ui
    {
        public static float K = 1f;
        public static int S(int v) { return (int)Math.Round(v * K); }
        public static readonly Color Bg = Color.FromArgb(243, 245, 248);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(222, 226, 232);
        public static readonly Color Ink = Color.FromArgb(28, 33, 41);
        public static readonly Color Muted = Color.FromArgb(98, 107, 120);
        public static readonly Color Accent = Color.FromArgb(0, 103, 192);
        public const string FontName = "Malgun Gothic";

        public static Font F(float pt, FontStyle st = FontStyle.Regular) { return new Font(FontName, pt, st); }

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags extra = TextFormatFlags.Left)
        {
            TextRenderer.DrawText(g, s, f, r, c, extra | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        }
    }

    class Level
    {
        public string Name; public Color Color; public int Bars;
        public static Level Of(int rssi)
        {
            if (rssi >= -50) return new Level { Name = "매우 좋음", Color = Color.FromArgb(14, 128, 88), Bars = 5 };
            if (rssi >= -60) return new Level { Name = "좋음", Color = Color.FromArgb(46, 125, 50), Bars = 4 };
            if (rssi >= -70) return new Level { Name = "보통", Color = Color.FromArgb(176, 112, 0), Bars = 3 };
            if (rssi >= -80) return new Level { Name = "약함", Color = Color.FromArgb(205, 84, 24), Bars = 2 };
            return new Level { Name = "매우 약함", Color = Color.FromArgb(196, 43, 48), Bars = 1 };
        }
    }

    abstract class CardControl : Control
    {
        protected CardControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Margin = new Padding(Ui.S(4));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int d = Ui.S(12);
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                using (SolidBrush b = new SolidBrush(Ui.Card)) g.FillPath(b, p);
                using (Pen pen = new Pen(Ui.Border)) g.DrawPath(pen, p);
            }
            PaintContent(g, r);
        }
        protected abstract void PaintContent(Graphics g, Rectangle r);
    }

    class HeaderCard : CardControl
    {
        readonly Font small = Ui.F(9.5f), big = Ui.F(21f, FontStyle.Bold), dbm = Ui.F(30f, FontStyle.Bold), level = Ui.F(10.5f, FontStyle.Bold);
        Sample s = new Sample();
        string advice = "측정을 시작하는 중입니다...";
        public void Set(Sample sample, string adviceText) { s = sample; advice = adviceText; Invalidate(); }

        protected override void PaintContent(Graphics g, Rectangle r)
        {
            int pad = Ui.S(20), rightZone = Ui.S(350);
            int textW = Math.Max(Ui.S(120), r.Width - pad * 2 - rightZone);
            string caption = s.Connected ? "연결된 네트워크" : (s.HasAdapter ? "연결 상태" : "Wi-Fi");
            string title = s.Connected ? s.Ssid : (s.Error.Length > 0 ? "확인 필요" : "연결 안 됨");
            Ui.Text(g, caption, small, Ui.Muted, new Rectangle(pad, Ui.S(12), textW, Ui.S(22)));
            Ui.Text(g, title, big, Ui.Ink, new Rectangle(pad, Ui.S(34), textW, Ui.S(46)));
            Ui.Text(g, s.Error.Length > 0 ? s.Error : advice, small, Ui.Muted, new Rectangle(pad, Ui.S(82), textW, Ui.S(24)));

            int barW = Ui.S(13), gap = Ui.S(6), barsW = barW * 5 + gap * 4;
            int barsRight = r.Right - pad, bottom = r.Bottom - Ui.S(24);
            Level lv = s.Connected ? Level.Of(s.Rssi) : null;
            for (int i = 0; i < 5; i++)
            {
                int h = Ui.S(18 + i * 13);
                Rectangle b = new Rectangle(barsRight - barsW + i * (barW + gap), bottom - h, barW, h);
                using (SolidBrush br = new SolidBrush(lv != null && i < lv.Bars ? lv.Color : Color.FromArgb(226, 230, 236))) g.FillRectangle(br, b);
            }
            int valueRight = barsRight - barsW - Ui.S(22);
            Rectangle valueRect = new Rectangle(valueRight - Ui.S(240), Ui.S(14), Ui.S(240), Ui.S(58));
            if (lv != null)
            {
                Ui.Text(g, s.Rssi + " dBm", dbm, lv.Color, valueRect, TextFormatFlags.Right);
                Ui.Text(g, lv.Name + "  ·  품질 " + s.Quality + "%", level, lv.Color, new Rectangle(valueRect.X, Ui.S(76), valueRect.Width, Ui.S(26)), TextFormatFlags.Right);
            }
            else Ui.Text(g, "-- dBm", dbm, Ui.Muted, valueRect, TextFormatFlags.Right);
        }
    }

    class Tile : CardControl
    {
        readonly Font titleFont = Ui.F(9f), valueFont = Ui.F(16f, FontStyle.Bold), subFont = Ui.F(8.5f);
        readonly string title;
        string value = "-", sub = "";
        Color color = Ui.Ink;
        public Tile(string title) { this.title = title; }
        public void Set(string v, string s, Color? c = null)
        {
            value = v; sub = s; color = c ?? Ui.Ink; Invalidate();
        }
        protected override void PaintContent(Graphics g, Rectangle r)
        {
            int pad = Ui.S(14), w = r.Width - pad * 2;
            Ui.Text(g, title, titleFont, Ui.Muted, new Rectangle(pad, Ui.S(9), w, Ui.S(20)));
            Ui.Text(g, value, valueFont, color, new Rectangle(pad, Ui.S(29), w, Ui.S(36)));
            Ui.Text(g, sub, subFont, Ui.Muted, new Rectangle(pad, Ui.S(66), w, Ui.S(20)));
        }
    }

    class GraphCard : CardControl
    {
        public const int Capacity = 180;
        readonly List<double> data = new List<double>();
        readonly Font titleFont = Ui.F(10.5f, FontStyle.Bold), axis = Ui.F(8.5f);
        const double Ceiling = -20, Floor = -100;

        public void Add(double v)
        {
            data.Add(v);
            if (data.Count > Capacity) data.RemoveAt(0);
            Invalidate();
        }
        public IEnumerable<double> Valid { get { return data.Where(d => !double.IsNaN(d)); } }

        protected override void PaintContent(Graphics g, Rectangle r)
        {
            Ui.Text(g, "신호 추이 · 최근 3분", titleFont, Ui.Ink, new Rectangle(Ui.S(16), Ui.S(9), Ui.S(260), Ui.S(24)));
            double[] v = Valid.ToArray();
            if (v.Length > 0)
                Ui.Text(g, string.Format("평균 {0:0} · 최저 {1:0} · 최고 {2:0} dBm", v.Average(), v.Min(), v.Max()), axis, Ui.Muted,
                    new Rectangle(r.Right - Ui.S(336), Ui.S(9), Ui.S(320), Ui.S(24)), TextFormatFlags.Right);

            Rectangle p = Rectangle.FromLTRB(r.Left + Ui.S(56), r.Top + Ui.S(40), r.Right - Ui.S(18), r.Bottom - Ui.S(28));
            if (p.Width < 20 || p.Height < 20) return;
            Func<double, float> y = d => (float)(p.Top + (Ceiling - Math.Max(Floor, Math.Min(Ceiling, d))) / (Ceiling - Floor) * p.Height);

            // 신호 구간 배경
            double[] edges = { -20, -50, -60, -70, -80, -100 };
            for (int i = 0; i < 5; i++)
            {
                Color c = Level.Of((int)edges[i + 1]).Color;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(20, c)))
                    g.FillRectangle(b, p.Left, y(edges[i]), p.Width, y(edges[i + 1]) - y(edges[i]));
            }
            using (Pen grid = new Pen(Color.FromArgb(226, 230, 236)))
                foreach (int d in new[] { -30, -50, -60, -70, -80, -90 })
                {
                    g.DrawLine(grid, p.Left, y(d), p.Right, y(d));
                    Ui.Text(g, d.ToString(), axis, Ui.Muted, new Rectangle(r.Left + Ui.S(8), (int)y(d) - Ui.S(10), Ui.S(40), Ui.S(20)), TextFormatFlags.Right);
                }
            Ui.Text(g, "3분 전", axis, Ui.Muted, new Rectangle(p.Left, p.Bottom + Ui.S(5), Ui.S(80), Ui.S(20)));
            Ui.Text(g, "지금", axis, Ui.Muted, new Rectangle(p.Right - Ui.S(80), p.Bottom + Ui.S(5), Ui.S(80), Ui.S(20)), TextFormatFlags.Right);

            // 측정값 선 (연결이 끊긴 구간은 비워 둔다)
            float step = p.Width / (float)(Capacity - 1);
            List<PointF> run = new List<PointF>();
            using (Pen line = new Pen(Ui.Accent, Ui.S(2)) { LineJoin = LineJoin.Round })
            {
                for (int i = 0; i <= data.Count; i++)
                {
                    bool gap = i == data.Count || double.IsNaN(data[i]);
                    if (gap)
                    {
                        if (run.Count > 1) g.DrawLines(line, run.ToArray());
                        run.Clear();
                    }
                    else run.Add(new PointF(p.Right - (data.Count - 1 - i) * step, y(data[i])));
                }
            }
            if (data.Count > 0 && !double.IsNaN(data[data.Count - 1]))
            {
                float dot = Ui.S(9), cy = y(data[data.Count - 1]);
                using (SolidBrush b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, p.Right - dot / 2, cy - dot / 2, dot, dot);
            }
        }
    }

    class SmoothListView : ListView
    {
        public SmoothListView() { DoubleBuffered = true; }
    }

    class MainForm : Form
    {
        readonly Sampler sampler = new Sampler();
        readonly HeaderCard header = new HeaderCard();
        readonly GraphCard graph = new GraphCard();
        readonly Tile tChannel = new Tile("채널"), tLink = new Tile("링크 속도"), tPing = new Tile("공유기 응답 시간"),
                      tLoss = new Tile("패킷 손실"), tPhy = new Tile("규격 / 보안"), tAdapter = new Tile("측정 횟수");
        readonly SmoothListView list = new SmoothListView();
        readonly Label nearbyTitle = new Label(), status = new Label();
        readonly Button scanBtn = new Button(), logBtn = new Button();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly string snapshotPath;
        readonly DateTime started = DateTime.Now;
        int shownVersion = -1, samples;
        string shownBssid = "";
        StreamWriter log;
        string logPath = "";
        int logRows;

        readonly string autoLogPath;
        DateTime statusHoldUntil = DateTime.MinValue;

        public MainForm(string snapshotPath, string autoLogPath)
        {
            this.snapshotPath = snapshotPath;
            this.autoLogPath = autoLogPath;
            Text = "Wi-Fi 신호 체크";
            Font = Ui.F(9.5f);
            BackColor = Ui.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size(Math.Min(Ui.S(940), wa.Width), Math.Min(Ui.S(700), wa.Height));
            ClientSize = new Size(Math.Min(Ui.S(1080), wa.Width - 20), Math.Min(Ui.S(860), wa.Height - 50));

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(Ui.S(10)), BackColor = Ui.Bg };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(128)));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(104)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(48)));

            header.Dock = DockStyle.Fill;
            root.Controls.Add(header, 0, 0);

            TableLayoutPanel tiles = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = Padding.Empty, BackColor = Ui.Bg };
            tiles.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Tile[] all = { tChannel, tLink, tPing, tLoss, tPhy, tAdapter };
            for (int i = 0; i < all.Length; i++)
            {
                tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / all.Length));
                all[i].Dock = DockStyle.Fill;
                tiles.Controls.Add(all[i], i, 0);
            }
            root.Controls.Add(tiles, 0, 1);

            graph.Dock = DockStyle.Fill;
            root.Controls.Add(graph, 0, 2);

            Panel listPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(Ui.S(4)), BackColor = Ui.Bg };
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.MultiSelect = false;
            list.Columns.Add("네트워크 이름", Ui.S(270));
            list.Columns.Add("신호", Ui.S(84), HorizontalAlignment.Right);
            list.Columns.Add("강도", Ui.S(92));
            list.Columns.Add("채널", Ui.S(58), HorizontalAlignment.Right);
            list.Columns.Add("대역", Ui.S(78));
            list.Columns.Add("보안", Ui.S(110));
            list.Columns.Add("BSSID", Ui.S(170));
            nearbyTitle.Dock = DockStyle.Top;
            nearbyTitle.Height = Ui.S(30);
            nearbyTitle.Font = Ui.F(10.5f, FontStyle.Bold);
            nearbyTitle.ForeColor = Ui.Ink;
            nearbyTitle.Text = "주변 Wi-Fi";
            listPanel.Controls.Add(list);
            listPanel.Controls.Add(nearbyTitle);
            root.Controls.Add(listPanel, 0, 3);

            Panel bottom = new Panel { Dock = DockStyle.Fill, Margin = new Padding(Ui.S(4), 0, Ui.S(4), 0), BackColor = Ui.Bg };
            Style(scanBtn, "지금 스캔", 0); scanBtn.Click += delegate { sampler.RequestScan(); Notify("주변 네트워크를 스캔하는 중입니다..."); };
            Style(logBtn, "CSV 기록 시작", Ui.S(118)); logBtn.Click += delegate { ToggleLog(); };
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleRight;
            status.ForeColor = Ui.Muted;
            status.AutoEllipsis = true;
            bottom.Controls.Add(status);
            bottom.Controls.Add(scanBtn);
            bottom.Controls.Add(logBtn);
            status.Padding = new Padding(Ui.S(250), 0, 0, 0);
            root.Controls.Add(bottom, 0, 4);

            Controls.Add(root);
            timer.Tick += delegate { Tick(); };
        }

        void Style(Button b, string text, int x)
        {
            b.Text = text;
            b.SetBounds(x, Ui.S(6), Ui.S(110), Ui.S(36));
            b.FlatStyle = FlatStyle.System;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (autoLogPath != null) StartLog(autoLogPath);
            sampler.Start();
            timer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            sampler.Stop();
            CloseLog();
            base.OnFormClosed(e);
        }

        void Tick()
        {
            Sample s = sampler.Latest;
            samples++;
            graph.Add(s.Connected ? s.Rssi : double.NaN);
            header.Set(s, Advice(s));
            UpdateTiles(s);
            UpdateList(s);
            WriteLog(s);
            if (DateTime.Now >= statusHoldUntil)
            {
                if (logRows > 0) status.Text = "기록 중: " + Path.GetFileName(logPath) + " (" + logRows + "행)";
                else if (s.LastScan != DateTime.MinValue) status.Text = "마지막 스캔 " + s.LastScan.ToString("HH:mm:ss") + " · 1초마다 측정";
            }

            if (snapshotPath != null && (DateTime.Now - started).TotalSeconds >= 12)
            {
                timer.Stop();
                using (Bitmap bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    bmp.Save(snapshotPath, ImageFormat.Png);
                }
                Close();
            }
        }

        void Notify(string message)
        {
            status.Text = message;
            statusHoldUntil = DateTime.Now.AddSeconds(6);
        }

        static string Advice(Sample s)
        {
            if (!s.Connected) return s.HasAdapter ? "Wi-Fi에 연결되어 있지 않습니다." : "측정을 시작하는 중입니다...";
            if (s.PingCount >= 10 && s.LossPct >= 5 && s.LossPct < 100) return "패킷 손실이 있습니다. 간섭이나 공유기 상태를 확인해 보세요.";
            if (s.Rssi >= -60) return "신호가 안정적입니다. 영상 통화와 스트리밍에 충분합니다.";
            if (s.Rssi >= -70) return "일반 사용에는 무난합니다. 화상 회의나 게임은 공유기 가까이에서 쓰세요.";
            if (s.Rssi >= -80) return "연결이 불안정할 수 있습니다. 공유기에 더 가까이 가 보세요.";
            return "연결이 끊길 수 있습니다. 공유기 위치를 옮기거나 중계기를 고려해 보세요.";
        }

        void UpdateTiles(Sample s)
        {
            if (s.Connected)
            {
                int same = s.Nearby.Count(b => b.Channel == s.Channel && b.Band == s.Band && b.Bssid != s.Bssid);
                tChannel.Set(s.Channel > 0 ? s.Channel.ToString() : "-", s.Band + " · 같은 채널 AP " + same + "개");
                tLink.Set(string.Format("{0:0} Mbps", s.RxMbps), string.Format("수신 {0:0} / 송신 {1:0}", s.RxMbps, s.TxMbps));
                string[] phy = s.Phy.Split('|');
                tPhy.Set(phy[0], (phy.Length > 1 && phy[1].Length > 0 ? phy[1] + " · " : "") + s.Auth);
            }
            else
            {
                tChannel.Set("-", ""); tLink.Set("-", ""); tPhy.Set("-", "");
            }

            if (!s.Connected || s.Gateway.Length == 0 || s.PingCount == 0)
            {
                tPing.Set("-", s.Connected ? "공유기 확인 중" : "");
                tLoss.Set("-", "");
            }
            else if (s.AvgPing < 0)
            {
                // 공유기가 핑에 응답하지 않도록 설정된 경우 손실로 단정하지 않는다
                tPing.Set("응답 없음", "공유기가 핑을 막았을 수 있음");
                tLoss.Set("-", "측정 불가");
            }
            else
            {
                tPing.Set(s.LastPing >= 0 ? s.LastPing + " ms" : "시간 초과", string.Format("평균 {0:0} ms", s.AvgPing),
                    s.LastPing < 0 ? Level.Of(-90).Color : (Color?)null);
                tLoss.Set(string.Format("{0:0.#}%", s.LossPct), "최근 " + s.PingCount + "회 기준",
                    s.LossPct >= 5 ? Level.Of(-90).Color : s.LossPct > 0 ? Level.Of(-65).Color : (Color?)null);
            }
            TimeSpan up = DateTime.Now - started;
            tAdapter.Set(samples + "회", string.Format("경과 {0:00}:{1:00}:{2:00}", (int)up.TotalHours, up.Minutes, up.Seconds));
        }

        void UpdateList(Sample s)
        {
            if (s.NearbyVersion == shownVersion && s.Bssid == shownBssid)
            {
                if (s.NearbyError.Length > 0) nearbyTitle.Text = s.NearbyError;
                return;
            }
            shownVersion = s.NearbyVersion; shownBssid = s.Bssid;
            nearbyTitle.Text = s.NearbyError.Length > 0 ? s.NearbyError : "주변 Wi-Fi · 액세스 포인트 " + s.Nearby.Count + "개";
            int top = list.TopItem != null ? list.TopItem.Index : 0;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (BssInfo b in s.Nearby)
            {
                Level lv = Level.Of(b.Rssi);
                bool mine = b.Bssid == s.Bssid;
                ListViewItem it = new ListViewItem(mine ? "● " + b.Ssid : b.Ssid) { UseItemStyleForSubItems = false };
                ListViewItem.ListViewSubItem sig = it.SubItems.Add(b.Rssi + " dBm");
                ListViewItem.ListViewSubItem bar = it.SubItems.Add(lv.Name);
                sig.ForeColor = lv.Color; bar.ForeColor = lv.Color;
                it.SubItems.Add(b.Channel.ToString());
                it.SubItems.Add(b.Band);
                it.SubItems.Add(b.Security);
                it.SubItems.Add(b.Bssid);
                if (mine)
                {
                    it.Font = Ui.F(9.5f, FontStyle.Bold);
                    foreach (ListViewItem.ListViewSubItem si in it.SubItems) si.BackColor = Color.FromArgb(232, 242, 252);
                }
                list.Items.Add(it);
            }
            list.EndUpdate();
            if (list.Items.Count > 0) list.TopItem = list.Items[Math.Min(top, list.Items.Count - 1)];
        }

        void ToggleLog()
        {
            if (log != null)
            {
                string saved = logPath;
                CloseLog();
                logBtn.Text = "CSV 기록 시작";
                Notify("저장됨: " + saved);
                return;
            }
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "신호 기록을 저장할 위치";
                d.Filter = "CSV 파일 (*.csv)|*.csv";
                d.FileName = "wifi_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
                if (d.ShowDialog(this) == DialogResult.OK) StartLog(d.FileName);
            }
        }

        void StartLog(string path)
        {
            try
            {
                // Excel에서 한글이 깨지지 않도록 BOM을 포함한다
                log = new StreamWriter(path, false, new UTF8Encoding(true)) { AutoFlush = true };
                log.WriteLine("시각,SSID,BSSID,신호(dBm),품질(%),채널,대역,수신(Mbps),송신(Mbps),응답시간(ms)");
                logPath = path; logRows = 0;
                logBtn.Text = "CSV 기록 중지";
            }
            catch (Exception ex)
            {
                log = null;
                MessageBox.Show(this, "파일을 만들 수 없습니다.\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void WriteLog(Sample s)
        {
            if (log == null) return;
            try
            {
                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                log.WriteLine(s.Connected
                    ? string.Join(",", time, Csv(s.Ssid), s.Bssid, s.Rssi, s.Quality, s.Channel, s.Band, s.RxMbps.ToString("0"), s.TxMbps.ToString("0"), s.LastPing >= 0 ? s.LastPing.ToString() : "")
                    : time + ",(연결 안 됨),,,,,,,,");
                logRows++;
            }
            catch (Exception ex)
            {
                CloseLog();
                logBtn.Text = "CSV 기록 시작";
                Notify("기록 중단: " + ex.Message);
            }
        }

        void CloseLog()
        {
            if (log != null) { try { log.Dispose(); } catch (IOException) { } }
            log = null; logRows = 0;
        }

        static string Csv(string v)
        {
            // SSID가 수식으로 해석되지 않도록 막는다
            if (v.Length > 0 && "=+-@".IndexOf(v[0]) >= 0) v = "'" + v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Ui.K = g.DpiX / 96f;
            // 점검용 옵션: --snapshot 화면.png [기록.csv] (12초 측정 후 화면을 저장하고 종료)
            string snapshot = args.Length >= 2 && args[0] == "--snapshot" ? args[1] : null;
            string autoLog = snapshot != null && args.Length >= 3 ? args[2] : null;
            Application.Run(new MainForm(snapshot, autoLog));
        }
    }
}
