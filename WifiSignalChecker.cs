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
using WifiSurvey.Design;
using WifiSurvey.Core;

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
    // ---- 화면: 다크 프리미엄 재설계 ----
    static class Fmt
    {
        public static string Ago(DateTime t)
        {
            if (t == DateTime.MinValue) return "스캔 전";
            double s = (DateTime.Now - t).TotalSeconds;
            if (s < 60) return (int)s + "초 전 스캔";
            if (s < 3600) return (int)(s / 60) + "분 전 스캔";
            return t.ToString("HH:mm") + " 스캔";
        }
        public static int BandIndex(string band)
        {
            if (band == null) return 0;
            if (band.StartsWith("5")) return 1;
            if (band.StartsWith("6")) return 2;
            return 0;
        }
    }

    class TopBar : Widget
    {
        public Segmented Tabs = new Segmented();
        public UiButton Scan = new UiButton(), Log = new UiButton(), Shot = new UiButton();
        public bool Live;
        readonly Spring pulse = new Spring(0);

        public TopBar()
        {
            Height = Theme.S(64);
            Tabs.Items.Add("실시간"); Tabs.Items.Add("세션"); Tabs.Items.Add("채널"); Tabs.Items.Add("설정");
            Scan.Label = "스캔"; Scan.Glyph = Glyph.Scan; Scan.Kind = Btn.Subtle;
            Log.Label = "기록"; Log.Glyph = Glyph.Record; Log.Kind = Btn.Subtle;
            Shot.Label = "PNG"; Shot.Glyph = Glyph.Export; Shot.Kind = Btn.Subtle;
            Controls.Add(Tabs); Controls.Add(Scan); Controls.Add(Log); Controls.Add(Shot);
            foreach (Control c in Controls) c.BackColor = Theme.Bg;
            Live = true;
            Poke();
        }
        public void SetPulse(bool on) { Live = on; Poke(); }
        protected override bool Step(float dt)
        {
            if (!Live) return false;
            double t = (DateTime.Now.TimeOfDay.TotalMilliseconds % 2000) / 2000.0;
            pulse.Set(0.45 + 0.55 * (0.5 + 0.5 * Math.Sin(t * Math.PI * 2)));
            return true;
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int h = Theme.S(32);
            int right = Width - Theme.S(16);
            Shot.SetBounds(right - Theme.S(70), Theme.S(16), Theme.S(70), h);
            Log.SetBounds(Shot.Left - Theme.S(8) - Theme.S(66), Theme.S(16), Theme.S(66), h);
            Scan.SetBounds(Log.Left - Theme.S(8) - Theme.S(70), Theme.S(16), Theme.S(70), h);
            int tabW = Theme.S(296);
            Tabs.SetBounds(Math.Max(Theme.S(320), (Width - tabW) / 2), Theme.S(12), tabW, Theme.S(32));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            int x = Theme.S(20);
            Theme.Fill(g, new Rectangle(x, Theme.S(16), Theme.S(32), Theme.S(32)), Theme.S(9), Theme.AccentSoft);
            Icons.Draw(g, Glyph.Wifi, new RectangleF(x + Theme.S(6), Theme.S(22), Theme.S(20), Theme.S(20)), Theme.Accent, 2f);
            x += Theme.S(44);
            Theme.Text(g, "무선 시그널 분석", Theme.F(13f, FontStyle.Bold), Theme.Ink, new Rectangle(x, Theme.S(12), Theme.S(230), Theme.S(22)));
            Theme.Text(g, "Wi-Fi 신호 체크 · 실시간 계측", Theme.F(9f), Theme.TextMute, new Rectangle(x, Theme.S(33), Theme.S(230), Theme.S(18)));
            if (Live && Scan.Left > Tabs.Right + Theme.S(60))
            {
                Rectangle pill = new Rectangle(Scan.Left - Theme.S(8) - Theme.S(62), Theme.S(20), Theme.S(62), Theme.S(24));
                Theme.Fill(g, pill, Theme.S(12), Theme.Alpha(Theme.Lv5, 26));
                Theme.Stroke(g, pill, Theme.S(12), Theme.Alpha(Theme.Lv5, 70));
                Theme.Glow(g, new PointF(pill.X + Theme.S(13), pill.Y + pill.Height / 2f), Theme.S(7) * (float)pulse.Value + Theme.S(3), Theme.Lv5, 3);
                using (SolidBrush b = new SolidBrush(Theme.Lv5))
                    g.FillEllipse(b, pill.X + Theme.S(9), pill.Y + pill.Height / 2f - Theme.S(3), Theme.S(6), Theme.S(6));
                Theme.Text(g, "LIVE", Theme.F(8.5f, FontStyle.Bold), Theme.Lv5, new Rectangle(pill.X + Theme.S(19), pill.Y, pill.Width - Theme.S(21), pill.Height));
            }
        }
    }

    // 히어로 카드: SSID + 큰 dBm + 등급 + 조언
    class HeroCard : Card
    {
        Sample s = new Sample();
        string advice = "측정을 시작하는 중입니다";
        readonly Spring dbm = new Spring(-100);
        readonly LevelBars bars = new LevelBars();

        public HeroCard()
        {
            Pad = 20;
            bars.BackColor = Theme.Surface;
            Controls.Add(bars);
        }
        public void Set(Sample sample, string text, bool measuring)
        {
            s = sample; advice = text;
            bool ok = s.Connected && s.Rssi < 0;
            dbm.To(ok ? s.Rssi : -100);
            bars.Set(s.Rssi, !ok);
            bars.Pulsing = measuring;
            Poke();
        }
        protected override bool Step(float dt) { dbm.Step(dt); return !dbm.Settled; }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            bars.SetBounds(Width - Theme.S(96), Theme.S(28), Theme.S(46), Theme.S(56));
        }
        protected override void PaintBody(Graphics g)
        {
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int p = Theme.S(Pad);
            Theme.CaptionText(g, s.Connected ? "연결된 네트워크" : s.HasAdapter ? "연결 상태" : "어댑터", Theme.TextMute, new Rectangle(p, p, Width / 2, Theme.S(14)));
            Theme.Text(g, s.Connected ? s.Ssid : (s.Error.Length > 0 ? "확인 필요" : "연결 안 됨"),
                Theme.F(15f, FontStyle.Bold), s.Connected ? Theme.Ink : Theme.TextDim, new Rectangle(p, p + Theme.S(18), Width - p * 2 - Theme.S(40), Theme.S(26)));
            bool ok = s.Connected && s.Rssi < 0;
            Color tone = ok ? Theme.Tone(s.Rssi) : Theme.None;
            string val = ok ? Math.Round(dbm.Value).ToString("0") : "—";
            Theme.Mono(g, val, Theme.F(42f, FontStyle.Bold), tone, new Rectangle(p, p + Theme.S(46), Theme.S(170), Theme.S(54)), Theme.Align.Left);
            int vw = TextRenderer.MeasureText(g, val, Theme.F(42f, FontStyle.Bold), Size.Empty, TextFormatFlags.NoPadding).Width;
            Theme.Text(g, "dBm", Theme.F(10.5f, FontStyle.Bold), Theme.Alpha(tone, 200), new Rectangle(p + Math.Min(vw, Theme.S(160)) + Theme.S(5), p + Theme.S(74), Theme.S(40), Theme.S(18)));
            if (ok)
            {
                string name = SignalMath.LevelName(SignalMath.LevelOf(s.Rssi));
                int cw = TextRenderer.MeasureText(g, name, Theme.F(9f, FontStyle.Bold), Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(20);
                Deco.Chip(g, new Rectangle(p, p + Theme.S(108), cw, Theme.S(20)), name, tone, true);
                Theme.Text(g, "품질 " + s.Quality + "%", Theme.F(9f), Theme.TextDim, new Rectangle(p + cw + Theme.S(10), p + Theme.S(108), Theme.S(90), Theme.S(20)));
            }
            else
                Deco.Chip(g, new Rectangle(p, p + Theme.S(108), Theme.S(78), Theme.S(20)), s.HasAdapter ? "대기" : "어댑터 없음", Theme.None, true);
            Theme.Text(g, s.Error.Length > 0 ? s.Error : advice, Theme.F(9f), s.Error.Length > 0 ? Theme.Lv2 : Theme.TextDim,
                new Rectangle(p, r.Bottom - p - Theme.S(18), Width - p * 2, Theme.S(18)));
        }
    }

    class QualityCard : Card
    {
        public readonly Ring Dial = new Ring();
        public QualityCard()
        {
            Pad = 12;
            Dial.BackColor = Theme.Surface;
            Dial.Caption = "링크 품질";
            Controls.Add(Dial);
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Dial.SetBounds(0, Theme.S(4), Width, Height - Theme.S(6)); }
        public void Set(Sample s, bool measuring)
        {
            bool ok = s.Connected && s.Rssi < 0;
            Dial.Center = ok ? s.Quality + "%" : "—";
            Dial.Sub = ok ? SignalMath.LevelName(SignalMath.LevelOf(s.Rssi)) : "신호 없음";
            Dial.Tone = ok ? Theme.Tone(s.Rssi) : Theme.None;
            Dial.Pulsing = measuring && ok;
            Dial.Set(ok ? s.Quality : 0);
        }
    }

    class StreamCard : Card
    {
        public readonly StreamGraph Graph = new StreamGraph();
        public readonly Segmented Range = new Segmented();
        public string Avg = "", Note = "";

        public StreamCard()
        {
            Pad = 16;
            Range.Items.Add("1분"); Range.Items.Add("3분"); Range.Items.Add("5분"); Range.Items.Add("10분");
            Range.Selected = 1;
            int[] wins = { 60, 180, 300, 600 };
            Range.Changed += delegate { Graph.Window = wins[Math.Max(0, Math.Min(3, Range.Selected))]; Graph.Invalidate(); };
            Range.BackColor = Theme.Surface;
            Graph.BackColor = Theme.Surface;
            Graph.Window = 180;
            Controls.Add(Graph); Controls.Add(Range);
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Range.SetBounds(Width - Theme.S(16 + 196), Theme.S(8), Theme.S(196), Theme.S(26));
            Graph.SetBounds(Theme.S(8), Theme.S(40), Width - Theme.S(16), Math.Max(Theme.S(40), Height - Theme.S(90)));
        }
        protected override void PaintBody(Graphics g)
        {
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int p = Theme.S(Pad);
            Theme.CaptionText(g, "신호 추이", Theme.TextMute, new Rectangle(p, p - Theme.S(4), Theme.S(120), Theme.S(14)));
            Theme.Text(g, Avg, Theme.F(9.5f), Theme.TextDim, new Rectangle(p, p + Theme.S(10), Width - Theme.S(240), Theme.S(18)));
            Theme.Text(g, Note, Theme.F(8.5f), Theme.TextMute, new Rectangle(p, r.Bottom - Theme.S(26), Width - p * 2, Theme.S(18)));
        }
    }

    class ChartCard : Card
    {
        public readonly Spectrum Bars = new Spectrum();
        public readonly Segmented Band = new Segmented();
        public string Head = "", Foot = "";

        public ChartCard()
        {
            Pad = 16;
            Band.Items.Add("2.4G"); Band.Items.Add("5G"); Band.Items.Add("6G");
            Band.BackColor = Theme.Surface; Bars.BackColor = Theme.Surface;
            Controls.Add(Bars); Controls.Add(Band);
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Band.SetBounds(Width - Theme.S(16 + 148), Theme.S(8), Theme.S(148), Theme.S(26));
            Bars.SetBounds(Theme.S(6), Theme.S(40), Width - Theme.S(12), Math.Max(Theme.S(40), Height - Theme.S(86)));
        }
        protected override void PaintBody(Graphics g)
        {
            int p = Theme.S(Pad);
            Theme.CaptionText(g, "채널 점유", Theme.TextMute, new Rectangle(p, p - Theme.S(4), Theme.S(120), Theme.S(14)));
            Theme.Text(g, Head, Theme.F(9.5f), Theme.TextDim, new Rectangle(p, p + Theme.S(10), Width - Theme.S(180), Theme.S(18)));
            Theme.Text(g, Foot, Theme.F(8.5f), Theme.TextMute, new Rectangle(p, Height - Theme.S(28), Width - p * 2, Theme.S(18)));
        }
    }

    class ApCard : Card
    {
        public readonly DataTable Table = new DataTable();
        public readonly Segmented Filter = new Segmented();
        public readonly SearchBox Search = new SearchBox();
        public string Count = "";
        public event EventHandler QueryChanged;

        public ApCard()
        {
            Pad = 16;
            Filter.Items.Add("전체"); Filter.Items.Add("2.4G"); Filter.Items.Add("5G"); Filter.Items.Add("6G");
            Filter.BackColor = Theme.Surface; Table.BackColor = Theme.Surface;
            Filter.Changed += delegate { if (QueryChanged != null) QueryChanged(this, EventArgs.Empty); };
            Search.Placeholder = "SSID · BSSID · 제조사 검색";
            Search.TextChanged += delegate { if (QueryChanged != null) QueryChanged(this, EventArgs.Empty); };
            Table.Columns.Add(new Column { Title = "네트워크", Width = 210 });
            Table.Columns.Add(new Column { Title = "신호", Width = 66, Align = Theme.Align.Right });
            Table.Columns.Add(new Column { Title = "세기", Width = 86 });
            Table.Columns.Add(new Column { Title = "채널", Width = 52, Align = Theme.Align.Right });
            Table.Columns.Add(new Column { Title = "대역", Width = 72 });
            Table.Columns.Add(new Column { Title = "보안", Width = 100 });
            Table.Columns.Add(new Column { Title = "제조사", Width = 104 });
            Table.Columns.Add(new Column { Title = "BSSID", Width = 140 });
            Table.SortColumn = 1; Table.SortAscending = false;
            Table.EmptyTitle = "감지된 네트워크가 없습니다";
            Table.EmptyHint = "‘스캔’을 누르거나 Windows 위치 서비스가 켜져 있는지 확인하세요.";
            Controls.Add(Table); Controls.Add(Filter); Controls.Add(Search);
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Filter.SetBounds(Width - Theme.S(16 + 200), Theme.S(24), Theme.S(200), Theme.S(26));
            Search.SetBounds(Width - Theme.S(16 + 200 + 8 + 172), Theme.S(26), Theme.S(162), Theme.S(22));
            Table.SetBounds(Theme.S(12), Theme.S(58), Width - Theme.S(24), Math.Max(Theme.S(40), Height - Theme.S(70)));
        }
        protected override void PaintBody(Graphics g)
        {
            int p = Theme.S(Pad);
            Theme.CaptionText(g, "주변 네트워크", Theme.TextMute, new Rectangle(p, p - Theme.S(4), Theme.S(150), Theme.S(14)));
            Theme.Text(g, Count, Theme.F(9.5f), Theme.TextDim, new Rectangle(p, p + Theme.S(10), Theme.S(240), Theme.S(18)));
            Rectangle box = new Rectangle(Search.Left - Theme.S(26), Search.Top - Theme.S(5), Search.Width + Theme.S(34), Search.Height + Theme.S(10));
            Theme.Fill(g, box, Theme.S(6), Theme.SurfaceHi);
            Theme.Stroke(g, box, Theme.S(6), Theme.Border);
            Icons.Draw(g, Glyph.Search, new RectangleF(box.X + Theme.S(7), box.Y + (box.Height - Theme.S(12)) / 2f, Theme.S(12), Theme.S(12)), Theme.TextMute, 1.7f);
        }
    }

    // ---- 메인 창 ----
    class MainForm : Form
    {
        readonly Sampler sampler = new Sampler();
        readonly TopBar top = new TopBar();
        readonly FootBar status = new FootBar();
        readonly Panel host = new Panel();
        readonly Prefs prefs;

        // 실시간
        readonly HeroCard hero = new HeroCard();
        readonly QualityCard quality = new QualityCard();
        readonly Kpi kCh = new Kpi(), kLink = new Kpi(), kPing = new Kpi(), kLoss = new Kpi();
        readonly StreamCard streamCard = new StreamCard();
        readonly ChartCard spectrumCard = new ChartCard();
        readonly ApCard apCard = new ApCard();

        // 세션
        readonly Kpi sMax = new Kpi(), sMin = new Kpi(), sAvg = new Kpi(), sJit = new Kpi();
        readonly Card sDist = new Card(), sVerdict = new Card(), sFiles = new Card();
        readonly DataTable sTable = new DataTable();
        readonly UiButton sSave = new UiButton(), sOpen = new UiButton(), sCsv = new UiButton(), sClear = new UiButton();

        // 채널
        readonly Card congCard = new Card(), chanRec = new Card(), chanListCard = new Card();
        readonly Ring congestion = new Ring();
        readonly ChartCard chanChartCard = new ChartCard();
        readonly DataTable chanTable = new DataTable();
        readonly Segmented chanBand = new Segmented();
        readonly UiButton chanScan = new UiButton();

        // 설정
        readonly Card setCard = new Card();
        readonly Stepper setInterval = new Stepper(), setSmooth = new Stepper(), setThresh = new Stepper(), setWindow = new Stepper();
        readonly Switch setMotion = new Switch(), setAutoLog = new Switch();
        readonly UiButton setSaveBtn = new UiButton(), setReset = new UiButton();

        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer { Interval = 15 };
        readonly string snapshotPath;
        readonly DateTime started = DateTime.Now;

        readonly List<double> raw = new List<double>();
        readonly List<double[]> session = new List<double[]>();
        List<double[]> ghost;
        string ghostSsid = "";
        int samples, shownVersion = -1, page;
        float pageProgress = 1;
        string shownBssid = "";
        StreamWriter log;
        string logPath = "";
        int logRows;
        InterferenceReport interference;
        readonly List<BssInfo> filtered = new List<BssInfo>();

        public MainForm(string snapshotPath)
        {
            this.snapshotPath = snapshotPath;
            prefs = new Prefs(Prefs.DefaultPath("checker"));
            Text = "무선 시그널 분석 · Wi-Fi 신호 체크";
            BackColor = Theme.Bg;
            ForeColor = Theme.Ink;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            KeyPreview = true;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size(Math.Min(Theme.S(1024), wa.Width), Math.Min(Theme.S(720), wa.Height));
            ClientSize = new Size(Math.Min(Theme.S(1240), wa.Width - 40), Math.Min(Theme.S(900), wa.Height - 60));

            top.Dock = DockStyle.Top;
            status.Dock = DockStyle.Bottom;
            status.Height = Theme.S(32);
            host.Dock = DockStyle.Fill;
            host.BackColor = Theme.Bg;
            Controls.Add(host);
            Controls.Add(top);
            Controls.Add(status);

            BuildLive();
            BuildSession();
            BuildChannel();
            BuildSettings();

            top.Tabs.Changed += delegate { SetPage(top.Tabs.Selected); };
            top.Scan.Click += delegate { sampler.RequestScan(); status.Notice("주변 네트워크를 스캔하는 중입니다…", Theme.AccentHi, 4000); };
            top.Log.Click += delegate { ToggleLog(); };
            top.Shot.Click += delegate { SaveSnapshot(); };
            tick.Tick += delegate { Tick(); };
            fade.Tick += delegate
            {
                pageProgress += 0.07f;
                if (pageProgress >= 1) { pageProgress = 1; fade.Stop(); }
                LayoutPage();
            };
            Resize += delegate { LayoutPage(); };

            ApplySettings();
            SetPage(prefs.GetInt("tab", 0), true);
            LayoutPage();
        }

        // ---------- 페이지 구성 ----------
        void AddCard(Control c, params Control[] children)
        {
            host.Controls.Add(c);
            foreach (Control child in children) { child.BackColor = Theme.Surface; c.Controls.Add(child); }
        }

        void BuildLive()
        {
            kCh.Caption = "채널"; kCh.ShowSpark = false;
            kLink.Caption = "링크 속도"; kLink.Unit = "Mbps"; kLink.ShowSpark = true;
            kPing.Caption = "공유기 응답"; kPing.Unit = "ms"; kPing.ShowSpark = true;
            kLoss.Caption = "패킷 손실"; kLoss.Unit = "%"; kLoss.Decimals = 1; kLoss.ShowSpark = true;
            spectrumCard.Head = "주변 AP 밀도";
            spectrumCard.Foot = "막대 높이는 AP 수입니다. 액센트는 권장 채널입니다.";
            apCard.QueryChanged += delegate { RebuildTable(true); };
            apCard.Table.SortChanged += delegate { RebuildTable(false); };
            host.Controls.Add(hero); host.Controls.Add(quality);
            host.Controls.Add(kCh); host.Controls.Add(kLink); host.Controls.Add(kPing); host.Controls.Add(kLoss);
            host.Controls.Add(streamCard); host.Controls.Add(spectrumCard); host.Controls.Add(apCard);
        }

        void BuildSession()
        {
            sMax.Caption = "최고 신호"; sMax.Unit = "dBm"; sMax.Tone = Theme.Lv5; sMax.ShowSpark = false;
            sMin.Caption = "최저 신호"; sMin.Unit = "dBm"; sMin.Tone = Theme.Lv1; sMin.ShowSpark = false;
            sAvg.Caption = "평균 신호"; sAvg.Unit = "dBm"; sAvg.Tone = Theme.Accent; sAvg.ShowSpark = true;
            sJit.Caption = "변동성"; sJit.Unit = "dB"; sJit.Decimals = 1; sJit.ShowSpark = false;
            sSave.Label = "세션 저장"; sSave.Glyph = Glyph.Folder; sSave.Kind = Btn.Primary;
            sOpen.Label = "세션 열기"; sOpen.Glyph = Glyph.Layers; sOpen.Kind = Btn.Subtle;
            sCsv.Label = "요약 CSV"; sCsv.Glyph = Glyph.Export; sCsv.Kind = Btn.Subtle;
            sClear.Label = "비우기"; sClear.Glyph = Glyph.Trash; sClear.Kind = Btn.Danger;
            sSave.Click += delegate { SaveSession(); };
            sOpen.Click += delegate { OpenSession(); };
            sCsv.Click += delegate { SaveSummaryCsv(); };
            sClear.Click += delegate { ClearSession(); };
            sTable.Columns.Add(new Column { Title = "세션", Width = 250 });
            sTable.Columns.Add(new Column { Title = "평균", Width = 84, Align = Theme.Align.Right, Sortable = false });
            sTable.Columns.Add(new Column { Title = "최저", Width = 84, Align = Theme.Align.Right, Sortable = false });
            sTable.Columns.Add(new Column { Title = "표본", Width = 90, Align = Theme.Align.Right, Sortable = false });
            sTable.EmptyTitle = "저장된 세션이 없습니다";
            sTable.EmptyHint = "‘세션 저장’을 누르면 지금 측정이 남고, 다음 세션과 점선으로 겹쳐 비교됩니다.";
            sDist.Body = PaintDist;
            sVerdict.Body = PaintVerdict;
            sFiles.Body = PaintFiles;
            sFiles.Resize += delegate
            {
                int w = sFiles.Width, h = sFiles.Height;
                sSave.SetBounds(Theme.S(16), Theme.S(44), Theme.S(96), Theme.S(30));
                sOpen.SetBounds(Theme.S(120), Theme.S(44), Theme.S(96), Theme.S(30));
                sCsv.SetBounds(Theme.S(224), Theme.S(44), Theme.S(96), Theme.S(30));
                sClear.SetBounds(w - Theme.S(16 + 86), Theme.S(44), Theme.S(86), Theme.S(30));
                sTable.SetBounds(Theme.S(12), Theme.S(86), w - Theme.S(24), Math.Max(Theme.S(40), h - Theme.S(98)));
            };
            AddCard(sDist); AddCard(sVerdict); AddCard(sFiles, sSave, sOpen, sCsv, sClear, sTable);
            host.Controls.Add(sMax); host.Controls.Add(sMin); host.Controls.Add(sAvg); host.Controls.Add(sJit);
        }

        void BuildChannel()
        {
            congestion.Caption = "대역 혼잡도";
            congestion.Tone = Theme.Lv3;
            congCard.BackColor = Theme.Bg;
            congCard.Controls.Add(congestion);
            congCard.Resize += delegate { congestion.SetBounds(0, Theme.S(6), congCard.Width, congCard.Height - Theme.S(10)); };
            chanBand.Items.Add("2.4 GHz"); chanBand.Items.Add("5 GHz"); chanBand.Items.Add("6 GHz");
            chanBand.Selected = 0;
            chanBand.Changed += delegate { SyncBand(chanBand.Selected); };
            spectrumCard.Band.Changed += delegate { SyncBand(spectrumCard.Band.Selected); };
            chanChartCard.Band.Changed += delegate { SyncBand(chanChartCard.Band.Selected); };
            chanRec.Body = PaintRecommendation;
            chanChartCard.Head = "채널별 점유";
            chanChartCard.Foot = "권장 채널을 액센트로 표시합니다. 2.4 GHz는 1·6·11만 권장합니다.";
            chanListCard.Title = "채널 순위";
            chanTable.Columns.Add(new Column { Title = "채널", Width = 64, Align = Theme.Align.Right, Sortable = false });
            chanTable.Columns.Add(new Column { Title = "점유", Width = 96, Sortable = false });
            chanTable.Columns.Add(new Column { Title = "AP", Width = 48, Align = Theme.Align.Right, Sortable = false });
            chanTable.Columns.Add(new Column { Title = "최강 신호", Width = 90, Align = Theme.Align.Right, Sortable = false });
            chanTable.Columns.Add(new Column { Title = "판정", Width = 80, Sortable = false });
            chanTable.EmptyTitle = "채널 데이터가 없습니다";
            chanTable.EmptyHint = "주변 네트워크를 스캔하면 채널별 점유가 계산됩니다.";
            chanScan.Label = "다시 스캔"; chanScan.Glyph = Glyph.Scan; chanScan.Kind = Btn.Subtle;
            chanScan.Click += delegate { sampler.RequestScan(); status.Notice("채널 분석을 위해 다시 스캔합니다…", Theme.AccentHi, 3000); };
            chanListCard.Resize += delegate
            {
                int w = chanListCard.Width, h = chanListCard.Height;
                chanBand.SetBounds(Theme.S(16), Theme.S(46), Theme.S(232), Theme.S(26));
                chanScan.SetBounds(w - Theme.S(16 + 96), Theme.S(44), Theme.S(96), Theme.S(28));
                chanTable.SetBounds(Theme.S(12), Theme.S(84), w - Theme.S(24), Math.Max(Theme.S(40), h - Theme.S(96)));
            };
            AddCard(congCard); AddCard(chanRec); AddCard(chanChartCard);
            AddCard(chanListCard, chanBand, chanScan, chanTable);
        }

        void BuildSettings()
        {
            setInterval.Min = 1; setInterval.Max = 10; setInterval.Increment = 1; setInterval.Suffix = "초";
            setSmooth.Min = 1; setSmooth.Max = 9; setSmooth.Increment = 2; setSmooth.Suffix = "회";
            setThresh.Min = -90; setThresh.Max = -50; setThresh.Increment = 1; setThresh.Suffix = "dBm";
            setWindow.Min = 60; setWindow.Max = 600; setWindow.Increment = 60; setWindow.Suffix = "초";
            setSaveBtn.Label = "설정 저장"; setSaveBtn.Glyph = Glyph.Check; setSaveBtn.Kind = Btn.Primary;
            setReset.Label = "기본값"; setReset.Glyph = Glyph.Undo; setReset.Kind = Btn.Ghost;
            setSaveBtn.Click += delegate { SaveSettings(); };
            setReset.Click += delegate
            {
                setInterval.Value = 1; setSmooth.Value = 3; setThresh.Value = -70; setWindow.Value = 180;
                setMotion.Set(true); setAutoLog.Set(false);
                status.Notice("기본값으로 되돌렸습니다. ‘설정 저장’을 누르면 적용됩니다.", Theme.TextDim, 4000);
            };
            setCard.Title = "측정 설정";
            setCard.Sub = "여기서 바꾼 값은 ‘설정 저장’을 눌러야 다음 실행에도 유지됩니다.";
            setCard.Body = PaintSettingRows;
            setCard.Resize += delegate
            {
                int x = Theme.S(24), y = Theme.S(78);
                int wx = Theme.S(340);
                setInterval.SetBounds(x + wx, y, Theme.S(110), Theme.S(30));
                setSmooth.SetBounds(x + wx, y + Theme.S(44), Theme.S(110), Theme.S(30));
                setThresh.SetBounds(x + wx, y + Theme.S(88), Theme.S(110), Theme.S(30));
                setWindow.SetBounds(x + wx, y + Theme.S(132), Theme.S(110), Theme.S(30));
                setMotion.SetBounds(x + wx, y + Theme.S(176), Theme.S(44), Theme.S(30));
                setAutoLog.SetBounds(x + wx, y + Theme.S(220), Theme.S(44), Theme.S(30));
                setSaveBtn.SetBounds(x, y + Theme.S(268), Theme.S(118), Theme.S(34));
                setReset.SetBounds(x + Theme.S(130), y + Theme.S(268), Theme.S(96), Theme.S(34));
            };
            AddCard(setCard, setInterval, setSmooth, setThresh, setWindow, setMotion, setAutoLog, setSaveBtn, setReset);
        }

        void SetPage(int i, bool instant = false)
        {
            page = Math.Max(0, Math.Min(3, i));
            top.Tabs.Selected = page;
            pageProgress = instant ? 1 : 0;
            fade.Stop();
            if (!instant) fade.Start();
            LayoutPage();
        }

        // ---------- 레이아웃 ----------
        void LayoutPage()
        {
            int w = host.ClientSize.Width, h = host.ClientSize.Height;
            if (w < 60 || h < 60) return;
            int pad = Theme.S(16), gap = Theme.S(12);
            int inner = w - pad * 2;
            bool live = page == 0, sess = page == 1, chan = page == 2, set = page == 3;

            hero.Visible = quality.Visible = kCh.Visible = kLink.Visible = kPing.Visible = kLoss.Visible = live;
            streamCard.Visible = spectrumCard.Visible = apCard.Visible = live;
            sMax.Visible = sMin.Visible = sAvg.Visible = sJit.Visible = sDist.Visible = sVerdict.Visible = sFiles.Visible = sess;
            congCard.Visible = chanRec.Visible = chanChartCard.Visible = chanListCard.Visible = chan;
            setCard.Visible = set;

            if (live)
            {
                int row1 = Theme.S(184);
                int left = (int)(inner * 0.46);
                int ring = (int)(inner * 0.2);
                Set(hero, pad, pad, left, row1);
                Set(quality, pad + left + gap, pad, ring, row1);
                int kx = pad + left + gap + ring + gap;
                int kw = w - pad - kx, kh = (row1 - gap) / 2, kw2 = (kw - gap) / 2;
                Set(kCh, kx, pad, kw2, kh);
                Set(kLink, kx + kw2 + gap, pad, kw - kw2 - gap, kh);
                Set(kPing, kx, pad + kh + gap, kw2, kh);
                Set(kLoss, kx + kw2 + gap, pad + kh + gap, kw - kw2 - gap, kh);

                int y2 = pad + row1 + gap;
                int avail = h - pad - y2 - gap;
                int row2 = Math.Max(Theme.S(170), (int)(avail * 0.52));
                int row3 = Math.Max(Theme.S(120), avail - row2 - gap);
                int sw = (int)(inner * 0.62);
                Set(streamCard, pad, y2, sw, row2);
                Set(spectrumCard, pad + sw + gap, y2, inner - sw - gap, row2);
                Set(apCard, pad, y2 + row2 + gap, inner, row3);
            }
            else if (sess)
            {
                int row1 = Theme.S(104);
                int kw = (inner - gap * 3) / 4;
                Set(sMax, pad, pad, kw, row1);
                Set(sMin, pad + kw + gap, pad, kw, row1);
                Set(sAvg, pad + (kw + gap) * 2, pad, kw, row1);
                Set(sJit, pad + (kw + gap) * 3, pad, inner - (kw + gap) * 3, row1);
                int y2 = pad + row1 + gap;
                int row2 = Theme.S(156);
                int bw = (inner - gap) / 2;
                Set(sDist, pad, y2, bw, row2);
                Set(sVerdict, pad + bw + gap, y2, inner - bw - gap, row2);
                int y3 = y2 + row2 + gap;
                Set(sFiles, pad, y3, inner, Math.Max(Theme.S(150), h - pad - y3));
            }
            else if (chan)
            {
                int row1 = Theme.S(140);
                int bw = (int)(inner * 0.28);
                Set(congCard, pad, pad, bw, row1);
                Set(chanRec, pad + bw + gap, pad, inner - bw - gap, row1);
                int y2 = pad + row1 + gap;
                int row2 = Math.Max(Theme.S(140), (int)((h - pad - y2 - gap) * 0.44));
                Set(chanChartCard, pad, y2, inner, row2);
                Set(chanListCard, pad, y2 + row2 + gap, inner, Math.Max(Theme.S(140), h - pad - y2 - row2 - gap));
            }
            else if (set)
            {
                int cw = Math.Min(inner, Theme.S(760));
                Set(setCard, pad, pad, cw, Math.Min(h - pad * 2, Theme.S(470)));
            }
        }

        void Set(Control c, int x, int y, int w, int h)
        {
            if (pageProgress < 1)
                y += (int)((1 - Ease.OutCubic(pageProgress)) * Theme.S(10));
            c.SetBounds(x, y, Math.Max(1, w), Math.Max(1, h));
        }

        // ---------- 갱신 ----------
        void Tick()
        {
            Sample s = sampler.Latest;
            samples++;
            double rawValue = s.Connected && s.Rssi < 0 ? s.Rssi : double.NaN;
            raw.Add(rawValue);
            if (raw.Count > 64) raw.RemoveAt(0);
            int n = Math.Max(1, (int)setSmooth.Value);
            double smoothValue = double.IsNaN(rawValue) ? double.NaN : SignalMath.Smooth(raw, n);
            double t = (DateTime.Now - started).TotalSeconds;
            session.Add(new double[] { t, rawValue });
            if (session.Count > 7200) session.RemoveAt(0);
            streamCard.Graph.AddSample(t, smoothValue);
            streamCard.Avg = BuildAverageText();
            streamCard.Graph.Threshold = (double)setThresh.Value;

            hero.Set(s, Advice(s), true);
            quality.Set(s, true);
            UpdateKpis(s);
            UpdateStatus(s);
            if (s.NearbyVersion != shownVersion || s.Bssid != shownBssid)
            {
                shownVersion = s.NearbyVersion; shownBssid = s.Bssid;
                RebuildTable(true);
                UpdateChannel();
            }
            WriteLog(s);
            if (samples % 3 == 0) UpdateSession();
            if (snapshotPath != null && (DateTime.Now - started).TotalSeconds >= 14)
            {
                tick.Stop();
                try
                {
                    using (Bitmap bmp = new Bitmap(Width, Height))
                    {
                        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                        bmp.Save(snapshotPath, ImageFormat.Png);
                    }
                }
                catch (Exception) { }
                Close();
            }
        }

        string BuildAverageText()
        {
            List<double> vals = new List<double>();
            foreach (double[] row in session)
                if (!double.IsNaN(row[1]) && row[0] > (DateTime.Now - started).TotalSeconds - streamCard.Graph.Window) vals.Add(row[1]);
            if (vals.Count == 0) return "아직 표본이 없습니다";
            double avg = 0, lo = double.MaxValue, hi = double.MinValue;
            foreach (double v in vals) { avg += v; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            avg /= vals.Count;
            return "평균 " + avg.ToString("0.0") + " · 최저 " + lo.ToString("0") + " · 최고 " + hi.ToString("0") + " dBm · " + vals.Count + "개 표본";
        }

        void UpdateKpis(Sample s)
        {
            bool ok = s.Connected;
            kCh.Set(ok ? s.Channel : double.NaN);
            kCh.Sub = ok ? s.Band + " · 같은 채널 " + SameChannel(s) + "대" : "연결 안 됨";
            kCh.Tone = Theme.Ink;
            kLink.Set(ok ? s.RxMbps : double.NaN);
            kLink.Sub = ok ? "수신 " + s.RxMbps.ToString("0") + " / 송신 " + s.TxMbps.ToString("0") : "—";
            kLink.Push(s.RxMbps);
            kLink.Tone = Theme.Ink;
            if (!ok || s.PingCount == 0 || s.AvgPing < 0)
            {
                kPing.Set(double.NaN); kPing.Sub = ok ? "측정 중" : "—"; kPing.Tone = Theme.TextMute;
                kLoss.Set(double.NaN); kLoss.Sub = "—"; kLoss.Tone = Theme.TextMute;
                return;
            }
            kPing.Set(s.LastPing >= 0 ? s.LastPing : s.AvgPing);
            kPing.Sub = "평균 " + s.AvgPing.ToString("0") + " ms · " + s.PingCount + "회";
            kPing.Tone = s.LastPing < 0 || s.AvgPing > 80 ? Theme.Lv2 : Theme.Ink;
            kPing.Push(s.LastPing >= 0 ? s.LastPing : double.NaN);
            kLoss.Set(s.LossPct);
            kLoss.Sub = "최근 " + s.PingCount + "회";
            kLoss.Tone = s.LossPct >= 5 ? Theme.Lv1 : s.LossPct > 0 ? Theme.Lv3 : Theme.Lv5;
            kLoss.Push(s.LossPct);
        }

        static int SameChannel(Sample s)
        {
            int n = 0;
            foreach (BssInfo b in s.Nearby) if (b.Channel == s.Channel && b.Band == s.Band && b.Bssid != s.Bssid) n++;
            return n;
        }

        static string Advice(Sample s)
        {
            if (!s.Connected) return s.HasAdapter ? "Wi-Fi에 연결되어 있지 않습니다." : "측정을 시작하는 중입니다…";
            if (s.PingCount >= 10 && s.LossPct >= 5 && s.LossPct < 100) return "패킷 손실이 있습니다. 간섭이나 공유기 상태를 확인하세요.";
            if (s.Rssi >= -60) return "신호가 안정적입니다. 화상 회의·스트리밍에 충분합니다.";
            if (s.Rssi >= -70) return "일반 사용에는 무난합니다. 지연에 민감한 작업은 AP 가까이에서 하세요.";
            if (s.Rssi >= -80) return "연결이 불안정할 수 있습니다. AP에 더 가까이 이동해 보세요.";
            return "연결이 끊길 수 있습니다. AP 위치를 옮기거나 중계기를 검토하세요.";
        }

        void UpdateStatus(Sample s)
        {
            status.Live = s.Connected;
            status.LiveText = "측정 중";
            status.Message = s.LastScan == DateTime.MinValue ? "첫 스캔을 기다리는 중입니다"
                : Fmt.Ago(s.LastScan) + " · " + samples + "회 측정" + (s.NearbyError.Length > 0 ? " · " + s.NearbyError : "");
            status.Chips.Clear();
            if (logRows > 0) status.Chips.Add("기록 " + logRows + "행");
            status.Chips.Add("간격 " + (int)setInterval.Value + "초");
            status.Chips.Add("스무딩 " + (int)setSmooth.Value + "회");
            status.Chips.Add("모션 " + (setMotion.On ? "켜짐" : "꺼짐"));
            status.Invalidate();
        }

        // ---------- 주변 네트워크 표 ----------
        void RebuildTable(bool resetSelection)
        {
            Sample s = sampler.Latest;
            host.SuspendLayout();
            string q = apCard.Search.Text.Trim().ToLowerInvariant();
            int bandFilter = apCard.Filter.Selected - 1;
            filtered.Clear();
            foreach (BssInfo b in s.Nearby)
            {
                if (bandFilter >= 0 && Fmt.BandIndex(b.Band) != bandFilter) continue;
                if (q.Length > 0)
                {
                    string hay = (b.Ssid + " " + b.Bssid + " " + VendorDb.Of(b.Bssid) + " " + b.Channel + " " + b.Security).ToLowerInvariant();
                    if (hay.IndexOf(q, StringComparison.Ordinal) < 0) continue;
                }
                filtered.Add(b);
            }
            Comparison<BssInfo> cmp;
            switch (apCard.Table.SortColumn)
            {
                case 0: cmp = delegate (BssInfo a, BssInfo b) { return string.Compare(a.Ssid, b.Ssid, StringComparison.CurrentCulture); }; break;
                case 3: cmp = delegate (BssInfo a, BssInfo b) { return a.Channel.CompareTo(b.Channel); }; break;
                case 4: cmp = delegate (BssInfo a, BssInfo b) { return string.Compare(a.Band, b.Band, StringComparison.Ordinal); }; break;
                case 5: cmp = delegate (BssInfo a, BssInfo b) { return string.Compare(a.Security, b.Security, StringComparison.Ordinal); }; break;
                case 6: cmp = delegate (BssInfo a, BssInfo b) { return string.Compare(VendorDb.Of(a.Bssid), VendorDb.Of(b.Bssid), StringComparison.Ordinal); }; break;
                case 7: cmp = delegate (BssInfo a, BssInfo b) { return string.Compare(a.Bssid, b.Bssid, StringComparison.Ordinal); }; break;
                default: cmp = delegate (BssInfo a, BssInfo b) { return a.Rssi.CompareTo(b.Rssi); }; break;
            }
            filtered.Sort(cmp);
            if (!apCard.Table.SortAscending) filtered.Reverse();

            DataTable t = apCard.Table;
            object keep = t.SelectedRow != null ? t.SelectedRow.Tag : null;
            t.Rows.Clear();
            foreach (BssInfo b in filtered)
            {
                bool mine = b.Bssid == s.Bssid;
                t.Rows.Add(new Row
                {
                    Cells = new string[] { b.Ssid, b.Rssi + " dBm", "", b.Channel.ToString(), b.Band, b.Security, VendorDb.Of(b.Bssid), b.Bssid },
                    Tones = new Color[] { mine ? Theme.Ink : Theme.TextDim, Theme.Tone(b.Rssi), Theme.Empty, Theme.TextDim, Theme.TextMute, Theme.TextMute, Theme.TextMute, Theme.TextMute },
                    Emphasis = mine,
                    Tag = b,
                    BarColumn = 2,
                    Bar = (100 + b.Rssi) / 50.0,
                    BarTone = Theme.Tone(b.Rssi)
                });
                if (mine && keep == null) keep = b;
            }
            apCard.Count = filtered.Count + "개" + (filtered.Count != s.Nearby.Count ? " · 전체 " + s.Nearby.Count + "개" : "") + " · 검색/필터 적용";
            if (resetSelection && keep != null)
                for (int i = 0; i < t.Rows.Count; i++) if (ReferenceEquals(t.Rows[i].Tag, keep)) { t.SelectedIndex = i; break; }
            apCard.Invalidate();
            host.ResumeLayout();
        }

        void UpdateChannel()
        {
            Sample s = sampler.Latest;
            List<Obs> obs = new List<Obs>();
            foreach (BssInfo b in s.Nearby)
                obs.Add(new Obs { Ssid = b.Ssid, Bssid = b.Bssid, Band = Fmt.BandIndex(b.Band), Channel = b.Channel, Rssi = b.Rssi, Security = b.Security, Vendor = VendorDb.Of(b.Bssid) });
            int band = chanBand.Selected;
            spectrumCard.Band.Selected = band;
            chanChartCard.Band.Selected = band;
            interference = Interference.Analyze(obs, band, Fmt.BandIndex(s.Band) == band ? s.Channel : 0);

            BuildSpectrum(spectrumCard.Bars, obs, band, interference, false);
            BuildSpectrum(chanChartCard.Bars, obs, band, interference, true);

            chanTable.Rows.Clear();
            List<ChannelScore> sorted = new List<ChannelScore>(interference.Channels);
            sorted.Sort(delegate (ChannelScore a, ChannelScore b) { return a.Load.CompareTo(b.Load); });
            foreach (ChannelScore cs in sorted)
            {
                if (cs.ApCount == 0 && cs.Load <= 0.001) continue;
                chanTable.Rows.Add(new Row
                {
                    Cells = new string[] { cs.Channel.ToString(), "", cs.ApCount.ToString(), cs.WorstRssi > -100 ? cs.WorstRssi + " dBm" : "—",
                        cs.Recommended ? "추천" : cs.Load > 0.6 ? "혼잡" : "여유" },
                    Tones = new Color[] { Theme.Ink, Theme.Empty, Theme.TextDim, Theme.TextDim, cs.Recommended ? Theme.Lv5 : cs.Load > 0.6 ? Theme.Lv1 : Theme.TextMute },
                    Bar = cs.Load, BarColumn = 1,
                    BarTone = cs.Load > 0.6 ? Theme.Lv1 : cs.Load > 0.3 ? Theme.Lv3 : Theme.Lv5,
                    Emphasis = cs.Recommended
                });
            }
            congestion.Tone = interference.BandCongestion > 55 ? Theme.Lv1 : interference.BandCongestion > 30 ? Theme.Lv3 : Theme.Lv5;
            congestion.Center = interference.BandCongestion.ToString("0") + "%";
            congestion.Sub = interference.BandCongestion > 55 ? "매우 붐빔" : interference.BandCongestion > 30 ? "보통" : "여유";
            congestion.Set(interference.BandCongestion);
            spectrumCard.Head = interference.Summary;
            chanRec.Invalidate();
            apCard.Invalidate();
        }

        // 세 곳의 대역 선택을 하나로 맞춘다
        void SyncBand(int band)
        {
            band = Math.Max(0, Math.Min(2, band));
            chanBand.Selected = band;
            spectrumCard.Band.Selected = band;
            chanChartCard.Band.Selected = band;
            UpdateChannel();
        }

        void BuildSpectrum(Spectrum target, List<Obs> obs, int band, InterferenceReport rep, bool highlight)
        {
            int[] cand = Interference.Candidates(band);
            List<string> labels = new List<string>();
            List<double> vals = new List<double>();
            List<Color> tones = new List<Color>();
            int hi = -1;
            foreach (int ch in cand)
            {
                ChannelScore cs = rep.Of(ch);
                if (cs == null) continue;
                int count = 0;
                foreach (Obs o in obs) if (o.Band == band && o.Channel == ch) count++;
                labels.Add(ch.ToString());
                vals.Add(count);
                tones.Add(Theme.Mix(Theme.Accent, Theme.Lv1, cs.Load * 0.85));
                if (highlight && cs.Recommended && hi < 0) hi = labels.Count - 1;
            }
            target.Set(labels.ToArray(), vals.ToArray(), tones.ToArray(), hi);
            target.HighlightText = hi >= 0 && rep.Recommended > 0 ? "추천 " + rep.Recommended : "";
        }

        void PaintRecommendation(Graphics g, Rectangle r)
        {
            if (interference == null) return;
            Theme.CaptionText(g, "권장 채널", Theme.TextMute, new Rectangle(r.X, r.Y, r.Width, Theme.S(14)));
            Color tone = Theme.Lv5;
            if (interference.Recommended > 0)
            {
                string big = interference.Recommended.ToString();
                Theme.Mono(g, big, Theme.F(30f, FontStyle.Bold), tone, new Rectangle(r.X, r.Y + Theme.S(14), Theme.S(90), Theme.S(38)), Theme.Align.Left);
                int bw = TextRenderer.MeasureText(g, big, Theme.F(30f, FontStyle.Bold), Size.Empty, TextFormatFlags.NoPadding).Width;
                Theme.Text(g, "번 채널", Theme.F(10.5f, FontStyle.Bold), Theme.Alpha(tone, 200), new Rectangle(r.X + Math.Min(bw, Theme.S(80)) + Theme.S(4), r.Y + Theme.S(28), Theme.S(70), Theme.S(20)));
            }
            else
                Theme.Text(g, "—", Theme.F(26f, FontStyle.Bold), Theme.None, new Rectangle(r.X, r.Y + Theme.S(14), Theme.S(60), Theme.S(34)));
            Theme.Text(g, interference.Summary, Theme.F(9f), Theme.TextDim, new Rectangle(r.X, r.Y + Theme.S(52), r.Width, Theme.S(18)));
            Theme.Text(g, "같은 채널 " + interference.CoChannel + " · 인접 채널 " + interference.Adjacent + " · 스캔 시점 기준",
                Theme.F(8.5f), Theme.TextMute, new Rectangle(r.X, r.Y + Theme.S(72), r.Width, Theme.S(16)));
            Rectangle bar = new Rectangle(r.X, r.Bottom - Theme.S(12), r.Width, Theme.S(4));
            Theme.Fill(g, bar, Theme.S(2), Theme.Alpha(Theme.TextMute, 30));
            ChannelScore cs = interference.Of(interference.Recommended);
            if (cs != null && cs.Load < 1)
                Theme.Fill(g, new Rectangle(bar.X, bar.Y, Math.Max(Theme.S(6), (int)(bar.Width * (1 - cs.Load))), bar.Height), Theme.S(2), Theme.Lv5);
        }

        // ---------- 세션 ----------
        void ClearSession()
        {
            session.Clear(); raw.Clear();
            ghost = null; ghostSsid = "";
            streamCard.Graph.Clear();
            streamCard.Graph.Ghost = null;
            UpdateSession();
            status.Notice("세션 기록을 비웠습니다.", Theme.TextDim, 3000);
        }

        void UpdateSession()
        {
            SessionSummary sum = Interference.Summarize(Values(session), (double)setThresh.Value);
            if (sum.HasData)
            {
                sMax.Set(sum.Max); sMin.Set(sum.Min); sAvg.Set(sum.Avg); sJit.Set(sum.StdDev);
                sMax.Sub = sum.Valid + "개 표본"; sMin.Sub = "약전계 " + sum.UnderPct.ToString("0") + "%";
                sAvg.Sub = "지금 세션"; sJit.Sub = sum.Flaky ? "흔들림 있음" : "안정적";
                sJit.Tone = sum.StdDev > 6 ? Theme.Lv2 : Theme.Ink;
                sAvg.Push(sum.Avg);
            }
            else
            {
                sMax.Set(double.NaN); sMin.Set(double.NaN); sAvg.Set(double.NaN); sJit.Set(double.NaN);
                sMax.Sub = sMin.Sub = sAvg.Sub = sJit.Sub = "표본 없음";
            }
            sDist.Invalidate(); sVerdict.Invalidate(); sFiles.Invalidate();
            sTable.Rows.Clear();
            if (ghost != null)
            {
                SessionSummary gs = Interference.Summarize(Values(ghost), (double)setThresh.Value);
                sTable.Rows.Add(new Row
                {
                    Cells = new string[] { ("비교: " + (ghostSsid.Length > 0 ? ghostSsid : "불러온 세션")), gs.Valid > 0 ? gs.Avg.ToString("0.0") + " dBm" : "—", gs.Valid > 0 ? gs.Min.ToString("0") + " dBm" : "—", gs.Valid + "개" },
                    Tones = new Color[] { Theme.Accent, Theme.TextDim, Theme.TextDim, Theme.TextMute },
                    Emphasis = true
                });
            }
            if (session.Count > 0)
                sTable.Rows.Add(new Row
                {
                    Cells = new string[] { "지금 측정", sum.Valid > 0 ? sum.Avg.ToString("0.0") + " dBm" : "—", sum.Valid > 0 ? sum.Min.ToString("0") + " dBm" : "—", sum.Valid + "개" },
                    Tones = new Color[] { Theme.Ink, Theme.TextDim, Theme.TextDim, Theme.TextMute }
                });
            streamCard.Graph.Ghost = ghost;
            streamCard.Graph.Invalidate();
        }

        static List<double> Values(List<double[]> rows)
        {
            List<double> v = new List<double>();
            foreach (double[] r in rows) v.Add(r[1]);
            return v;
        }

        void PaintDist(Graphics g, Rectangle r)
        {
            Theme.CaptionText(g, "등급별 체류 비율", Theme.TextMute, new Rectangle(r.X, r.Y, r.Width, Theme.S(14)));
            SessionSummary sum = Interference.Summarize(Values(session), (double)setThresh.Value);
            string[] names = { "매우 약함", "약함", "보통", "좋음", "매우 좋음" };
            Color[] tones = { Theme.Lv1, Theme.Lv2, Theme.Lv3, Theme.Lv4, Theme.Lv5 };
            int barH = Theme.S(12), gap = Theme.S(12), labelW = Theme.S(68), pctW = Theme.S(52);
            int y = r.Y + Theme.S(26);
            for (int i = 0; i < 5; i++)
            {
                double pct = sum.Share[i];
                Theme.Text(g, names[i], Theme.F(9f), pct > 0.5 ? Theme.TextDim : Theme.TextMute, new Rectangle(r.X, y, labelW, barH + Theme.S(4)));
                Rectangle track = new Rectangle(r.X + labelW + Theme.S(6), y + Theme.S(4), r.Width - labelW - pctW - Theme.S(12), barH - Theme.S(4));
                Theme.Fill(g, track, track.Height / 2, Theme.Alpha(Theme.TextMute, 30));
                int fw = (int)Math.Round(track.Width * pct / 100.0);
                if (fw > 2) Theme.Fill(g, new Rectangle(track.X, track.Y, fw, track.Height), track.Height / 2, tones[i]);
                Theme.Text(g, pct.ToString("0.0") + "%", Theme.F(9f), pct > 0.5 ? Theme.Ink : Theme.TextMute, new Rectangle(r.Right - pctW, y, pctW, barH + Theme.S(4)), Theme.Align.Right);
                y += barH + gap;
            }
        }

        void PaintVerdict(Graphics g, Rectangle r)
        {
            Theme.CaptionText(g, "해석", Theme.TextMute, new Rectangle(r.X, r.Y, r.Width, Theme.S(14)));
            SessionSummary sum = Interference.Summarize(Values(session), (double)setThresh.Value);
            Theme.Wrap(g, sum.HasData ? sum.Verdict : "측정이 쌓이면 이 자리에 판정이 나옵니다.", Theme.F(11f), Theme.Ink, new Rectangle(r.X, r.Y + Theme.S(22), r.Width, Theme.S(46)));
            Theme.Text(g, "약전계 기준 " + (int)setThresh.Value + " dBm · 표본 " + sum.Valid + "개",
                Theme.F(8.5f), Theme.TextMute, new Rectangle(r.X, r.Y + Theme.S(74), r.Width, Theme.S(18)));
            Theme.Text(g, "세션을 저장하면 다음 측정과 점선으로 겹쳐 비교합니다.",
                Theme.F(8.5f), Theme.TextMute, new Rectangle(r.X, r.Bottom - Theme.S(18), r.Width, Theme.S(18)));
        }

        void PaintFiles(Graphics g, Rectangle r)
        {
            Theme.CaptionText(g, "세션 기록", Theme.TextMute, new Rectangle(r.X, r.Y, Theme.S(160), Theme.S(14)));
            Theme.Text(g, ghost != null ? "'" + (ghostSsid.Length > 0 ? ghostSsid : "이전 세션") + "'을 겹쳐 보는 중" : "저장하거나 불러온 세션은 신호 추이에 점선으로 겹칩니다",
                Theme.F(8.5f), Theme.TextMute, new Rectangle(r.X + Theme.S(150), r.Y, r.Width - Theme.S(160), Theme.S(14)));
        }

        void SaveSession()
        {
            if (session.Count == 0) { status.Notice("저장할 측정값이 없습니다.", Theme.Lv2, 3000); return; }
            using (SaveFileDialog d = new SaveFileDialog { Title = "세션 저장", Filter = "세션 기록 (*.csv)|*.csv", FileName = "session_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    SessionFile.Save(d.FileName, sampler.Latest.Ssid, (int)setInterval.Value, session);
                    SessionSummary sum = Interference.Summarize(Values(session), (double)setThresh.Value);
                    ghostSsid = sampler.Latest.Ssid;
                    ghost = new List<double[]>();
                    foreach (double[] row in session) ghost.Add(new double[] { row[0], row[1] });
                    UpdateSession();
                    status.Notice("세션을 저장했습니다 · 평균 " + sum.Avg.ToString("0.0") + " dBm · " + Path.GetFileName(d.FileName), Theme.Lv5, 6000);
                }
                catch (Exception ex) { ShowError("세션을 저장할 수 없습니다.", ex); }
            }
        }

        void OpenSession()
        {
            using (OpenFileDialog d = new OpenFileDialog { Title = "세션 열기", Filter = "세션 기록 (*.csv)|*.csv|모든 파일 (*.*)|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string ssid;
                    List<double[]> rows = SessionFile.Load(d.FileName, out ssid);
                    if (rows.Count == 0) { status.Notice("이 파일에서 측정값을 찾지 못했습니다.", Theme.Lv2, 4000); return; }
                    ghost = rows; ghostSsid = ssid;
                    UpdateSession();
                    status.Notice("불러왔습니다 · " + rows.Count + "개 표본을 점선으로 겹쳐 표시합니다.", Theme.AccentHi, 6000);
                }
                catch (Exception ex) { ShowError("세션을 열 수 없습니다.", ex); }
            }
        }

        void SaveSummaryCsv()
        {
            using (SaveFileDialog d = new SaveFileDialog { Title = "요약 CSV 저장", Filter = "CSV (*.csv)|*.csv", FileName = "session_summary_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    SessionSummary sum = Interference.Summarize(Values(session), (double)setThresh.Value);
                    SessionSummary prev = ghost != null ? Interference.Summarize(Values(ghost), (double)setThresh.Value) : null;
                    using (StreamWriter w = new StreamWriter(d.FileName, false, new UTF8Encoding(true)))
                    {
                        w.WriteLine("지표,값,단위");
                        w.WriteLine(Csv.Line("SSID", sampler.Latest.Ssid, ""));
                        w.WriteLine(Csv.Line("측정 표본", sum.Valid, "개"));
                        w.WriteLine(Csv.Line("평균", sum.Avg.ToString("0.0"), "dBm"));
                        w.WriteLine(Csv.Line("최저", sum.Min.ToString("0"), "dBm"));
                        w.WriteLine(Csv.Line("최고", sum.Max.ToString("0"), "dBm"));
                        w.WriteLine(Csv.Line("표준편차", sum.StdDev.ToString("0.0"), "dB"));
                        w.WriteLine(Csv.Line("약전계 비율", sum.UnderPct.ToString("0.0"), "%"));
                        if (prev != null && prev.HasData)
                        {
                            w.WriteLine(Csv.Line("비교 세션 평균", prev.Avg.ToString("0.0"), "dBm"));
                            w.WriteLine(Csv.Line("평균 차이", (sum.Avg - prev.Avg).ToString("+0.0;-0.0;0"), "dB"));
                        }
                        w.WriteLine(Csv.Line("약전계 기준", (int)setThresh.Value, "dBm"));
                    }
                    status.Notice("요약 CSV를 저장했습니다: " + Path.GetFileName(d.FileName), Theme.Lv5, 5000);
                }
                catch (Exception ex) { ShowError("CSV를 저장할 수 없습니다.", ex); }
            }
        }

        // ---------- 설정 ----------
        static readonly string[] setLabels = { "측정 주기", "신호 스무딩", "약전계 기준", "그래프 창 길이", "애니메이션 (모션)", "시작할 때 자동 기록" };
        static readonly string[] setHints = { "자주 읽을수록 촘촘하지만 CPU를 더 씁니다", "최근 N회 평균으로 흔들림을 줄입니다", "이 값보다 약하면 약전계로 표시합니다", "그래프에 표시할 최근 구간", "끄면 값이 즉시 바뀝니다 (원격·저사양에 유리)", "실행하자마자 CSV 기록을 시작합니다" };

        void PaintSettingRows(Graphics g, Rectangle content)
        {
            int x = Theme.S(24), y = Theme.S(78), w = Theme.S(320);
            for (int i = 0; i < setLabels.Length; i++)
            {
                Theme.Text(g, setLabels[i], Theme.LabelBold, Theme.Ink, new Rectangle(x, y + Theme.S(2), w, Theme.S(18)));
                Theme.Text(g, setHints[i], Theme.F(8.5f), Theme.TextMute, new Rectangle(x, y + Theme.S(19), w, Theme.S(16)));
                if (i < setLabels.Length - 1) Deco.Divider(g, x, y + Theme.S(38), Theme.S(680));
                y += Theme.S(44);
            }
        }

        void ApplySettings()
        {
            tick.Interval = Math.Max(500, prefs.GetInt("interval", 1) * 1000);
            setInterval.Value = prefs.GetInt("interval", 1);
            setSmooth.Value = prefs.GetInt("smooth", 3);
            setThresh.Value = (decimal)prefs.GetDouble("threshold", -70);
            setWindow.Value = prefs.GetInt("window", 180);
            setMotion.Set(prefs.GetBool("motion", true));
            setAutoLog.Set(prefs.GetBool("autolog", false));
            Motion.Enabled = setMotion.On;
            streamCard.Graph.Threshold = (double)setThresh.Value;
            streamCard.Graph.Window = (int)setWindow.Value;
            apCard.Filter.Selected = Math.Max(0, Math.Min(3, prefs.GetInt("band", 0)));
            chanBand.Selected = Math.Max(0, Math.Min(2, prefs.GetInt("chanband", 0)));
        }

        void SaveSettings()
        {
            prefs.SetInt("interval", (int)setInterval.Value);
            prefs.SetInt("smooth", (int)setSmooth.Value);
            prefs.SetDouble("threshold", (double)setThresh.Value);
            prefs.SetInt("window", (int)setWindow.Value);
            prefs.SetBool("motion", setMotion.On);
            prefs.SetBool("autolog", setAutoLog.On);
            prefs.Set("lastSsid", sampler.Latest.Ssid);
            prefs.SetInt("tab", page);
            prefs.SetInt("band", apCard.Filter.Selected);
            prefs.SetInt("chanband", chanBand.Selected);
            prefs.Save();
            tick.Interval = Math.Max(500, (int)setInterval.Value * 1000);
            Motion.Enabled = setMotion.On;
            Motion.InvalidateAll();
            streamCard.Graph.Threshold = (double)setThresh.Value;
            streamCard.Graph.Window = (int)setWindow.Value;
            UpdateSession();
            UpdateChannel();
            status.Notice("설정을 저장했습니다: " + prefs.FilePath, Theme.Lv5, 4000);
        }

        // ---------- 기록 ----------
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            sampler.Start();
            tick.Start();
            if (setAutoLog.On && snapshotPath == null)
            {
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WifiSurveyTools");
                    Directory.CreateDirectory(dir);
                    StartLog(Path.Combine(dir, "wifi_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"));
                }
                catch (Exception) { }
            }
            status.Message = "1초마다 측정합니다. ‘스캔’으로 주변 AP를 새로 읽습니다.";
            UpdateSession();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop();
            sampler.Stop();
            CloseLog();
            base.OnFormClosed(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.F5) sampler.RequestScan();
            else if (e.KeyCode == Keys.D1) { SetPage(0); }
            else if (e.KeyCode == Keys.D2) { SetPage(1); }
            else if (e.KeyCode == Keys.D3) { SetPage(2); }
            else if (e.KeyCode == Keys.D4) { SetPage(3); }
            else if (e.Control && e.KeyCode == Keys.L) ToggleLog();
            else if (e.Control && e.KeyCode == Keys.S) SaveSession();
            else if (e.Control && e.KeyCode == Keys.E) SaveSnapshot();
        }

        void SaveSnapshot()
        {
            using (SaveFileDialog d = new SaveFileDialog { Title = "화면 PNG 저장", Filter = "PNG (*.png)|*.png", FileName = "wifi_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (Bitmap bmp = new Bitmap(Width, Height))
                    {
                        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                        bmp.Save(d.FileName, ImageFormat.Png);
                    }
                    status.Notice("화면을 저장했습니다: " + Path.GetFileName(d.FileName), Theme.Lv5, 4000);
                }
                catch (Exception ex) { ShowError("화면을 저장할 수 없습니다.", ex); }
            }
        }

        void ToggleLog()
        {
            if (log != null)
            {
                string saved = logPath;
                int rows = logRows;
                CloseLog();
                status.Notice("기록을 마쳤습니다: " + Path.GetFileName(saved) + " · " + rows + "행", Theme.Lv5, 6000);
                return;
            }
            using (SaveFileDialog d = new SaveFileDialog { Title = "신호 기록을 저장할 위치", Filter = "CSV 파일 (*.csv)|*.csv", FileName = "wifi_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv" })
            {
                if (d.ShowDialog(this) == DialogResult.OK) StartLog(d.FileName);
            }
        }

        void StartLog(string path)
        {
            try
            {
                log = new StreamWriter(path, false, new UTF8Encoding(true)) { AutoFlush = true };
                log.WriteLine("시각,SSID,BSSID,신호(dBm),품질(%),채널,대역,수신(Mbps),송신(Mbps),응답시간(ms)");
                logPath = path; logRows = 0;
                top.Log.Label = "중지"; top.Log.Glyph = Glyph.Stop; top.Log.Active = true;
                top.Log.Invalidate();
                status.Notice("기록을 시작했습니다: " + Path.GetFileName(path), Theme.Lv5, 5000);
            }
            catch (Exception ex)
            {
                log = null;
                ShowError("파일을 만들 수 없습니다.", ex);
            }
        }

        void WriteLog(Sample s)
        {
            if (log == null) return;
            try
            {
                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                log.WriteLine(s.Connected
                    ? Csv.Line(time, s.Ssid, s.Bssid, s.Rssi, s.Quality, s.Channel, s.Band, s.RxMbps.ToString("0"), s.TxMbps.ToString("0"), s.LastPing >= 0 ? s.LastPing.ToString() : "")
                    : Csv.Line(time, "(연결 안 됨)"));
                logRows++;
            }
            catch (Exception ex)
            {
                CloseLog();
                ShowError("기록이 중단되었습니다.", ex);
            }
        }

        void CloseLog()
        {
            if (log != null) { try { log.Dispose(); } catch (IOException) { } }
            log = null; logRows = 0;
            top.Log.Label = "기록"; top.Log.Glyph = Glyph.Record; top.Log.Active = false;
            top.Log.Invalidate();
        }

        void ShowError(string message, Exception ex)
        {
            status.Notice(message + " " + ex.Message, Theme.Lv1, 8000);
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
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Theme.K = g.DpiX / 96f;
            string snapshot = args.Length >= 2 && args[0] == "--snapshot" ? args[1] : null;
            Application.Run(new MainForm(snapshot));
        }
    }
}
