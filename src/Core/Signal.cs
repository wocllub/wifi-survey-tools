// 측정·분석 로직 (UI 없음) — 두 프로그램이 함께 쓰고, Windows 없이도 시험할 수 있다
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WifiSurvey.Core
{
    // ---- 신호 계산 ----
    static class SignalMath
    {
        // 이동평균: 창 크기 n으로 최근 값을 부드럽게 만든다
        public static double Smooth(IList<double> window, int n)
        {
            if (window == null || window.Count == 0) return double.NaN;
            int take = Math.Max(1, Math.Min(n, window.Count));
            double sum = 0; int count = 0;
            for (int i = window.Count - take; i < window.Count; i++)
            {
                double v = window[i];
                if (double.IsNaN(v)) continue;
                sum += v; count++;
            }
            return count == 0 ? double.NaN : sum / count;
        }

        // 신호 등급: 5 = 매우 좋음, 1 = 매우 약함
        public static int LevelOf(int rssi)
        {
            if (rssi >= -50) return 5;
            if (rssi >= -60) return 4;
            if (rssi >= -70) return 3;
            if (rssi >= -80) return 2;
            return 1;
        }
        public static string LevelName(int level)
        {
            switch (level)
            {
                case 5: return "매우 좋음";
                case 4: return "좋음";
                case 3: return "보통";
                case 2: return "약함";
                default: return "매우 약함";
            }
        }

        // 표준편차 (변동성)
        public static double StdDev(IList<double> values)
        {
            List<double> ok = values.Where(v => !double.IsNaN(v)).ToList();
            if (ok.Count < 2) return 0;
            double avg = ok.Average();
            return Math.Sqrt(ok.Sum(v => (v - avg) * (v - avg)) / (ok.Count - 1));
        }

        // 2.4 GHz 채널 점유 폭(±4 채널)을 반영한 인접 채널 판정
        public static bool Overlaps(int band, int a, int b)
        {
            if (a == b) return true;
            if (band != 0) return false;  // 5/6 GHz는 20MHz 채널이 겹치지 않는다고 본다
            return Math.Abs(a - b) <= 4;
        }

        public static double CenterMhz(int band, int channel)
        {
            if (band == 0) return 2407 + 5 * channel;
            if (band == 1) return 5000 + 5 * channel;
            return 5950 + 5 * channel;
        }
    }

    // ---- 관측된 AP 한 건 ----
    class Obs
    {
        public string Ssid = "", Bssid = "";
        public int Channel, Band, Rssi, Width = 20;
        public string Security = "";
        public string Vendor = "";
    }

    // ---- 채널 간섭 분석 ----
    class ChannelScore
    {
        public int Channel;
        public double Load;          // 0..1 정규화 점유
        public int ApCount;
        public double WorstRssi = -100;
        public bool Recommended;
    }

    class InterferenceReport
    {
        public int Band;
        public List<ChannelScore> Channels = new List<ChannelScore>();
        public int Recommended = -1;
        public double BandCongestion;      // 0..100
        public int CoChannel;              // 같은 채널 AP 수(내 AP 제외)
        public int Adjacent;               // 인접 채널 AP 수
        public string Summary = "";

        public ChannelScore Of(int ch)
        {
            foreach (ChannelScore c in Channels) if (c.Channel == ch) return c;
            return null;
        }
    }

    static class Interference
    {
        // 2.4GHz는 비중첩 채널(1/6/11), 5GHz는 36~64·100~144·149~165를 후보로 본다
        public static int[] Candidates(int band)
        {
            if (band == 0) return new int[] { 1, 6, 11, 2, 3, 4, 5, 7, 8, 9, 10, 12, 13 };
            if (band == 1) return new int[] { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165 };
            return new int[] { 1, 5, 9, 13, 17, 21, 25, 29, 33, 37, 41, 45, 49, 53, 57, 61, 65, 69, 73, 77, 81, 85, 89, 93, 97, 101, 105, 109, 113, 117, 121, 125, 129, 133, 137, 141, 145, 149, 153, 157, 161, 165, 169, 173, 177, 181, 185, 189, 193, 197, 201, 205, 209, 213, 217, 221, 225, 229, 233 };
        }

        static double Weight(int rssi)
        {
            // -40 dBm ≈ 1.2, -90 dBm ≈ 0.2 : 강한 AP가 점유에 더 크게 기여
            double w = (rssi + 100) / 50.0;
            return Math.Max(0.05, Math.Min(1.25, w));
        }

        // overlap: 두 채널이 같은 주파수 폭을 얼마나 나누는지 (0..1)
        public static double Overlap(int band, int aCh, int bCh, int bWidth)
        {
            double fa = SignalMath.CenterMhz(band, aCh), fb = SignalMath.CenterMhz(band, bCh);
            double half = Math.Max(10, bWidth / 2.0);
            double d = Math.Abs(fa - fb);
            if (band != 0) return d < 10 ? 1 : (d < half + 10 ? 1 - (d - 10) / half : 0);
            return Math.Max(0, 1 - d / 25.0);
        }

        public static InterferenceReport Analyze(IList<Obs> all, int band, int myChannel)
        {
            InterferenceReport r = new InterferenceReport { Band = band };
            List<Obs> list = all.Where(o => o.Band == band).ToList();
                int[] cand = Candidates(band);
            foreach (int ch in cand)
            {
                ChannelScore cs = new ChannelScore { Channel = ch };
                foreach (Obs o in list)
                {
                    double ov = Overlap(band, ch, o.Channel, o.Width);
                    if (ov <= 0) continue;
                    cs.Load += Weight(o.Rssi) * ov;
                    if (o.Channel == ch) cs.ApCount++;
                    if (o.Rssi > cs.WorstRssi) cs.WorstRssi = o.Rssi;
                }
                r.Channels.Add(cs);
            }
            double max = r.Channels.Count == 0 ? 1 : r.Channels.Max(c => c.Load);
            if (max <= 0) max = 1;
            foreach (ChannelScore cs in r.Channels)
            {
                cs.Load = Math.Min(1, cs.Load / max);
                // 같은 채널 수가 적고 점유가 낮을수록 좋다. 2.4GHz는 1/6/11에 가산점
                cs.Load = Math.Max(0, Math.Min(1, cs.Load * 0.85 + (band == 0 && (cs.Channel == 1 || cs.Channel == 6 || cs.Channel == 11) ? -0.12 : 0)));
            }
            // 추천 채널: 후보 중 점유가 가장 낮은 곳
            ChannelScore best = null;
            foreach (ChannelScore cs in r.Channels)
                if (best == null || cs.Load < best.Load) best = cs;
            if (best != null)
            {
                // 2.4GHz는 비중첩 3채널 안에서만 추천한다
                if (band == 0)
                {
                    ChannelScore b2 = null;
                    foreach (ChannelScore cs in r.Channels)
                        if ((cs.Channel == 1 || cs.Channel == 6 || cs.Channel == 11) && (b2 == null || cs.Load < b2.Load)) b2 = cs;
                    if (b2 != null) best = b2;
                }
                best.Recommended = true;
                r.Recommended = best.Channel;
            }
            if (myChannel > 0)
            {
                foreach (Obs o in list)
                {
                    if (o.Channel == myChannel) r.CoChannel++;
                    else if (SignalMath.Overlaps(band, o.Channel, myChannel)) r.Adjacent++;
                }
            }
            double congestion = list.Count == 0 ? 0 : Math.Min(100, 100.0 * list.Sum(o => Weight(o.Rssi)) / 12.0);
            r.BandCongestion = congestion;
            if (list.Count == 0) r.Summary = "이 대역에서 감지된 AP가 없습니다.";
            else if (myChannel > 0)
                r.Summary = "같은 채널 " + r.CoChannel + "대, 인접 채널 " + r.Adjacent + "대. "
                    + (r.Recommended > 0 ? r.Recommended + "번 채널이 가장 덜 붐빕니다." : "");
            else
                r.Summary = list.Count + "개 AP 감지. " + (r.Recommended > 0 ? r.Recommended + "번 채널 권장." : "");
            return r;
        }

        // 세션 요약: 최고/최저/평균, 등급별 체류 비율
        public static SessionSummary Summarize(IList<double> samples, double threshold)
        {
            SessionSummary s = new SessionSummary();
            List<double> ok = samples.Where(v => !double.IsNaN(v)).ToList();
            s.Samples = samples.Count;
            s.Valid = ok.Count;
            if (ok.Count == 0) return s;
            s.Min = ok.Min(); s.Max = ok.Max(); s.Avg = ok.Average();
            s.StdDev = SignalMath.StdDev(ok);
            int[] bins = new int[5];
            foreach (double v in ok) bins[5 - SignalMath.LevelOf((int)v)]++;
            for (int i = 0; i < 5; i++) s.Share[i] = 100.0 * bins[i] / ok.Count;   // 0=매우약함 ... 4=매우좋음
            double below = 0;
            foreach (double v in ok) if (v < threshold) below++;
            s.UnderPct = 100.0 * below / ok.Count;
            s.Flaky = s.UnderPct >= 10;
            return s;
        }
    }

    class SessionSummary
    {
        public int Samples, Valid;
        public double Min = double.NaN, Max = double.NaN, Avg = double.NaN, StdDev, UnderPct;
        public double[] Share = new double[5];
        public bool Flaky;

        public bool HasData { get { return Valid > 0; } }
        public string Verdict
        {
            get
            {
                if (!HasData) return "측정된 값이 없습니다.";
                if (Avg >= -60 && StdDev < 5) return "안정적입니다. 화상 회의·스트리밍에 충분합니다.";
                if (Avg >= -67) return "무난합니다. 지연에 민감한 작업은 AP 가까이에서 하세요.";
                if (Avg >= -75) return "불안정할 수 있습니다. AP 위치나 중계기를 검토하세요.";
                return "음영 구역입니다. AP 추가 배치가 필요합니다.";
            }
        }
    }

    // ---- 제조사(OUI) 조회: 내장 목록(일부) ----
    static class VendorDb
    {
        // 형식: "앞 6자리" = 제조사. 전체 OUI 목록이 아니라 자주 보이는 것만 담았다.
        static readonly string[] table = new string[]
        {
            "000C29=VMware", "005056=VMware", "000569=VMware",
            "001122=Cisco", "00000C=Cisco", "0026CB=Cisco", "F09E63=Cisco", "6C8BD3=Cisco", "CC7F75=Cisco",
            "001A1E=Aruba", "000B86=Aruba", "6CF37F=Aruba", "9C1C12=Aruba",
            "3C6105=Juniper", "5C5B35=Juniper", "84B59C=Juniper", "D4D7A9=Juniper",
            "24A43C=Ubiquiti", "002722=Ubiquiti", "788A20=Ubiquiti", "E063DA=Ubiquiti", "F09FC2=Ubiquiti",
            "18E829=Ruckus", "2C5D93=Ruckus", "C0C520=Ruckus",
            "00E04C=Realtek", "001CF0=Intel", "34E12D=Intel", "8086F2=Intel", "9C7BEF=Intel",
            "F0D1A9=Apple", "A4C361=Apple", "28F076=Apple", "3C2EFF=Apple", "7CD1C3=Apple",
            "001349=Meraki", "881544=Meraki", "E0C97A=Meraki",
            "50C7BF=TP-Link", "A42BB0=TP-Link", "C46E1F=TP-Link", "0027F8=TP-Link",
            "20E52A=Netgear", "A00460=Netgear", "C40415=Netgear", "44944A=Netgear",
            "0024B2=Netgear",
            "2C56DC=ASUS", "1C872C=ASUS", "F832E4=ASUS", "08606E=ASUS",
            "001632=Samsung", "5C0A5B=Samsung", "8C71F8=Samsung", "34AA8B=Samsung",
            "0018DD=Silicom", "647BD4=Huawei", "00259E=Huawei", "48DB50=Huawei", "D4B709=Huawei",
            "64CC2E=Xiaomi", "F8A45F=Xiaomi", "78117F=Xiaomi", "50EC50=Xiaomi",
            "C0A8E9=Grandstream", "000B82=Grandstream", "EC74D7=Grandstream",
            "70F96D=H3C", "0023AB=H3C", "3822E2=H3C",
            "00089F=EFM(ipTIME)", "00268E=iptime", "1C5F2B=EFM(ipTIME)", "E4F14C=EFM(ipTIME)",
            "0021CC=KT", "9C3AAF=KT", "D8FE8B=KT", "5CE28C=LG U+", "001E58=SK Broadband",
            "98FA9B=Sonos", "B8E937=Sonos", "34B1F7=TP-Link"
        };
        static Dictionary<string, string> map;

        static void Build()
        {
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string row in table)
            {
                int eq = row.IndexOf('=');
                if (eq <= 0) continue;
                string key = row.Substring(0, eq).Replace(":", "").Replace("-", "").ToUpperInvariant();
                if (key.Length != 6) continue;
                map[key] = row.Substring(eq + 1);
            }
        }

        public static string Of(string bssid)
        {
            if (string.IsNullOrEmpty(bssid)) return "";
            if (map == null) Build();
            string key = bssid.Replace(":", "").Replace("-", "").ToUpperInvariant();
            if (key.Length < 6) return "";
            string v;
            return map.TryGetValue(key.Substring(0, 6), out v) ? v : "";
        }
        public static int Count { get { if (map == null) Build(); return map.Count; } }
    }

    // ---- CSV ----
    static class Csv
    {
        public static string Field(string v)
        {
            if (v == null) v = "";
            // Excel 수식 주입 방지 (숫자는 건드리지 않는다)
            if (v.Length > 0 && "=+-@".IndexOf(v[0]) >= 0 && !LooksNumeric(v)) v = "'" + v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
        public static bool LooksNumeric(string v)
        {
            double d;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }
        public static string Line(params object[] cells)
        {
            StringBuilder s = new StringBuilder();
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) s.Append(',');
                object c = cells[i];
                if (c == null) { s.Append(""); continue; }
                if (c is double) { s.Append(((double)c).ToString("0.##", CultureInfo.InvariantCulture)); continue; }
                if (c is int) { s.Append(((int)c).ToString(CultureInfo.InvariantCulture)); continue; }
                string text = c.ToString();
                bool plain = text.Length > 0 && text.IndexOfAny(new char[] { ',', '"', '\n', '\r' }) < 0
                    && ("=+-@".IndexOf(text[0]) < 0 || LooksNumeric(text));
                s.Append(plain ? text : Field(text));
            }
            return s.ToString();
        }
        public static List<string> Split(string line)
        {
            List<string> outp = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (q)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                        else q = false;
                    }
                    else cur.Append(ch);
                }
                else if (ch == '"') q = true;
                else if (ch == ',') { outp.Add(cur.ToString()); cur.Length = 0; }
                else cur.Append(ch);
            }
            outp.Add(cur.ToString());
            return outp;
        }
    }

    // ---- 설정 저장: 사람이 읽을 수 있는 INI ----
    class Prefs
    {
        readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly string path;

        public Prefs(string path) { this.path = path; Load(); }

        public static string DefaultPath(string app)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WifiSurveyTools");
            return Path.Combine(dir, app + ".ini");
        }

        public void Load()
        {
            map.Clear();
            if (path == null || !File.Exists(path)) return;
            try
            {
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (IOException) { }
        }
        public void Save()
        {
            try
            {
                if (path == null) return;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                List<string> keys = map.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                StringBuilder s = new StringBuilder();
                s.AppendLine("# WifiSurveyTools 설정 (직접 고쳐도 됩니다)");
                foreach (string k in keys) s.AppendLine(k + "=" + map[k]);
                File.WriteAllText(path, s.ToString(), new UTF8Encoding(true));
            }
            catch (IOException) { }
        }
        public bool Has(string key) { return map.ContainsKey(key); }
        public string Get(string key, string fallback)
        {
            string v; return map.TryGetValue(key, out v) ? v : fallback;
        }
        public void Set(string key, string value) { map[key] = value == null ? "" : value; }
        public int GetInt(string key, int fallback)
        {
            int v; return int.TryParse(Get(key, ""), out v) ? v : fallback;
        }
        public void SetInt(string key, int value) { map[key] = value.ToString(CultureInfo.InvariantCulture); }
        public double GetDouble(string key, double fallback)
        {
            double v; return double.TryParse(Get(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }
        public void SetDouble(string key, double value) { map[key] = value.ToString("0.###", CultureInfo.InvariantCulture); }
        public bool GetBool(string key, bool fallback)
        {
            string v = Get(key, fallback ? "1" : "0");
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        public void SetBool(string key, bool value) { map[key] = value ? "1" : "0"; }
        public string FilePath { get { return path; } }
    }

    // ---- 세션 기록: 저장/불러오기 (CSV, 메타데이터는 # 줄) ----
    static class SessionFile
    {
        public const string Header = "elapsed,rssi";

        public static void Save(string path, string ssid, int interval, IList<double[]> rows)
        {
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine("# WifiSurveyTools 세션 v1");
                w.WriteLine("# ssid=" + ssid);
                w.WriteLine("# interval=" + interval);
                w.WriteLine("# started=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                w.WriteLine(Header);
                foreach (double[] r in rows)
                    w.WriteLine(r[0].ToString("0.###", CultureInfo.InvariantCulture) + "," +
                        (double.IsNaN(r[1]) ? "" : r[1].ToString("0", CultureInfo.InvariantCulture)));
            }
        }

        // 반환: {초, rssi} 목록. 끊긴 구간은 NaN
        public static List<double[]> Load(string path, out string ssid)
        {
            ssid = "";
            List<double[]> outp = new List<double[]>();
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#"))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && line.Substring(1, eq - 1).Trim().Equals("ssid", StringComparison.OrdinalIgnoreCase))
                        ssid = line.Substring(eq + 1).Trim();
                    continue;
                }
                if (line.StartsWith("elapsed")) continue;
                List<string> c = Csv.Split(line);
                if (c.Count < 2) continue;
                double t;
                if (!double.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out t)) continue;
                double v;
                if (!double.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) v = double.NaN;
                outp.Add(new double[] { t, v });
            }
            return outp;
        }
    }
}
