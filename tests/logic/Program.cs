// 신호 분석 로직 시험 — Windows 없이 실행: dotnet run --project tests/logic
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WifiSurvey.Core;

namespace WifiSurvey.Tests
{
    static class Program
    {
        static int failed, passed;

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { passed++; Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  (" + detail + ")" : "")); }
            else { failed++; Console.WriteLine("FAIL  " + name + "  → " + detail); }
        }
        static bool Near(double a, double b, double tol) { return Math.Abs(a - b) <= tol; }

        static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // --- 이동평균 ---
            List<double> w = new List<double> { -50, -60, -70, -80, -90 };
            Check("이동평균 3", Near(SignalMath.Smooth(w, 3), -80, 0.001), SignalMath.Smooth(w, 3).ToString("0.0"));
            Check("이동평균 1 = 마지막 값", Near(SignalMath.Smooth(w, 1), -90, 0.001), "ok");
            Check("이동평균 창이 길면 전체", Near(SignalMath.Smooth(w, 99), -70, 0.001), "ok");
            Check("이동평균: 끊긴 값 제외", Near(SignalMath.Smooth(new List<double> { -50, double.NaN, -70 }, 3), -60, 0.001), "NaN 무시");
            Check("이동평균: 값 없음", double.IsNaN(SignalMath.Smooth(new List<double>(), 3)), "NaN");

            // --- 등급 경계 ---
            Check("등급 -50 = 5", SignalMath.LevelOf(-50) == 5, "");
            Check("등급 -51 = 4", SignalMath.LevelOf(-51) == 4, "");
            Check("등급 -70 = 3", SignalMath.LevelOf(-70) == 3, "");
            Check("등급 -95 = 1", SignalMath.LevelOf(-95) == 1, "");

            // --- 채널 겹침 ---
            Check("2.4G 1↔1 겹침", SignalMath.Overlaps(0, 1, 1), "");
            Check("2.4G 1↔4 겹침", SignalMath.Overlaps(0, 1, 4), "");
            Check("2.4G 1↔6 비겹침", !SignalMath.Overlaps(0, 1, 6), "");
            Check("5G 36↔40 비겹침", !SignalMath.Overlaps(1, 36, 40), "");

            // --- 채널 간섭 분석 ---
            List<Obs> aps = new List<Obs>();
            aps.Add(new Obs { Ssid = "A", Bssid = "AA:AA:AA:00:00:01", Band = 0, Channel = 1, Rssi = -45, Width = 20 });
            aps.Add(new Obs { Ssid = "B", Bssid = "AA:AA:AA:00:00:02", Band = 0, Channel = 1, Rssi = -60, Width = 20 });
            aps.Add(new Obs { Ssid = "C", Bssid = "AA:AA:AA:00:00:03", Band = 0, Channel = 6, Rssi = -70, Width = 20 });
            aps.Add(new Obs { Ssid = "E", Bssid = "AA:AA:AA:00:00:05", Band = 0, Channel = 3, Rssi = -68, Width = 20 });
            aps.Add(new Obs { Ssid = "D", Bssid = "AA:AA:AA:00:00:04", Band = 1, Channel = 36, Rssi = -55, Width = 80 });
            InterferenceReport r = Interference.Analyze(aps, 0, 1);
            Check("혼잡한 1번 채널은 추천하지 않는다", r.Recommended != 1, "추천 " + r.Recommended);
            Check("2.4G 추천은 비중첩 채널", r.Recommended == 6 || r.Recommended == 11, "추천 " + r.Recommended);
            Check("같은 채널 AP 수", r.CoChannel == 2, r.CoChannel + "대");
            Check("인접 채널 AP 수(1↔3)", r.Adjacent == 1, r.Adjacent + "대");
            ChannelScore c1 = r.Of(1), c11 = r.Of(11);
            Check("1번 채널이 11번보다 혼잡", c1.Load > c11.Load, c1.Load.ToString("0.00") + " vs " + c11.Load.ToString("0.00"));
            Check("혼잡도 0~100", r.BandCongestion >= 0 && r.BandCongestion <= 100, r.BandCongestion.ToString("0"));

            // 5GHz 80MHz 폭이면 인접 20MHz 채널도 겹친다
            List<Obs> wide = new List<Obs> { new Obs { Band = 1, Channel = 36, Rssi = -50, Width = 80 } };
            Check("80MHz AP는 44번 채널과 겹침", Interference.Overlap(1, 44, 36, 80) > 0, Math.Round(Interference.Overlap(1, 44, 36, 80), 2).ToString());
            Check("80MHz AP는 64번 채널과 안 겹침", Interference.Overlap(1, 64, 36, 80) == 0, "0");
            InterferenceReport r5 = Interference.Analyze(wide, 1, 36);
            Check("5GHz 추천은 붐비는 36번이 아님", r5.Recommended != 36, "추천 " + r5.Recommended);

            // AP가 없을 때
            InterferenceReport empty = Interference.Analyze(new List<Obs>(), 0, 0);
            Check("AP 없음: 문장 유지", empty.Channels.Count > 0 && empty.Summary.Length > 0, empty.Summary);

            // --- 세션 요약 ---
            List<double> sig = new List<double> { -55, -57, -53, -60, -75, -58, -56 };
            SessionSummary s = Interference.Summarize(sig, -70);
            Check("세션 최저", Near(s.Min, -75, 0.001), s.Min.ToString());
            Check("세션 최고", Near(s.Max, -53, 0.001), s.Max.ToString());
            Check("세션 평균", Near(s.Avg, -59.14, 0.05), s.Avg.ToString("0.00"));
            Check("세션 약전계 비율", Near(s.UnderPct, 100.0 / 7, 0.05), s.UnderPct.ToString("0.0") + "%");
            Check("등급 비율 합 100", Near(s.Share.Sum(), 100, 0.01), s.Share.Sum().ToString("0.0"));
            Check("빈 세션은 데이터 없음", !Interference.Summarize(new List<double>(), -70).HasData, "");

            // --- 제조사 ---
            Check("OUI Apple", VendorDb.Of("F0:D1:A9:11:22:33") == "Apple", VendorDb.Of("F0:D1:A9:11:22:33"));
            Check("OUI 소문자/하이픈", VendorDb.Of("f0-d1-a9-11-22-33") == "Apple", "");
            Check("OUI 모르는 제조사", VendorDb.Of("02:00:00:00:00:01") == "", "빈 문자열");
            Check("OUI 목록 크기", VendorDb.Count >= 40, VendorDb.Count + "개");

            // --- CSV ---
            Check("CSV 쉼표 이스케이프", Csv.Field("a,b") == "\"a,b\"", Csv.Field("a,b"));
            Check("CSV 따옴표 이스케이프", Csv.Field("a\"b") == "\"a\"\"b\"", Csv.Field("a\"b"));
            Check("CSV 수식 주입 방지", Csv.Field("=cmd").StartsWith("\"'"), Csv.Field("=cmd"));
            string line = Csv.Line(-53.5, "내 SSID", 6);
            List<string> back = Csv.Split(line);
            Check("CSV 왕복", back.Count == 3 && back[0] == "-53.5" && back[1] == "내 SSID", line);

            // --- 설정 ---
            string dir = Path.Combine(Path.GetTempPath(), "wstest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string ini = Path.Combine(dir, "t.ini");
            Prefs p = new Prefs(ini);
            Check("설정 기본값", p.GetInt("interval", 1) == 1, "1");
            p.SetInt("interval", 3); p.SetDouble("threshold", -70.5); p.SetBool("motion", false); p.Set("lastSsid", "내 와이파이");
            p.Save();
            Prefs q = new Prefs(ini);
            Check("설정 저장/불러오기", q.GetInt("interval", 1) == 3 && Near(q.GetDouble("threshold", 0), -70.5, 0.001)
                && !q.GetBool("motion", true) && q.Get("lastSsid", "") == "내 와이파이", q.FilePath);

            // --- 세션 파일 ---
            string sess = Path.Combine(dir, "s.csv");
            SessionFile.Save(sess, "테스트SSID", 1, new List<double[]> {
                new double[] { 0, -50 }, new double[] { 1, -52 }, new double[] { 2, double.NaN }, new double[] { 3, -58 } });
            string ssid;
            List<double[]> rows = SessionFile.Load(sess, out ssid);
            Check("세션 파일 행 수", rows.Count == 4, rows.Count + "행");
            Check("세션 파일 끊김 구간 유지", double.IsNaN(rows[2][1]) && Near(rows[3][1], -58, 0.001), "NaN 유지");
            Check("세션 파일 SSID", ssid == "테스트SSID", ssid);

            // --- 표준편차 ---
            Check("표준편차 0", Near(SignalMath.StdDev(new List<double> { -50, -50, -50 }), 0, 0.001), "0");
            Check("표준편차 양수", SignalMath.StdDev(new List<double> { -50, -70 }) > 10, "");

            Directory.Delete(dir, true);
            Console.WriteLine();
            Console.WriteLine(failed == 0 ? "ALL PASSED (" + passed + ")" : failed + " FAILED / " + (passed + failed));
            return failed == 0 ? 0 : 1;
        }
    }
}
