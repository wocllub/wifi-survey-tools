import math
import os
import unittest

import numpy as np

from wifi_survey import rf, wifi
from wifi_survey.rf import ApItem, Project, Reading, SurveyPoint, WallItem

WIFI_LIST = "\n".join([
    r"*:Office\:5G:80\:CA\:4B\:8A\:A9\:CB:44:5220 MHz:85:WPA2",
    r" :Office:80\:CA\:4B\:8A\:A9\:CA:8:2447 MHz:70:WPA2 WPA3",
    r" ::8A\:CA\:4B\:60\:C0\:77:40:5200 MHz:51:WPA2",
    r" :Lab6:02\:11\:22\:33\:44\:55:5:5975 MHz:40:",
])
IW_LINK = """Connected to 80:ca:4b:8a:a9:cb (on wlan0)
\tSSID: Office:5G
\tfreq: 5220.0
\tRX: 1 bytes 1 packets
\tsignal: -41 dBm
\trx bitrate: 1200.9 MBit/s 80MHz HE-MCS 11 HE-NSS 2
\ttx bitrate: 864.8 MBit/s 80MHz HE-MCS 8 HE-NSS 2
"""


def fake_runner(args, timeout):
    joined = " ".join(args)
    if joined.startswith("nmcli -t -f DEVICE"):
        return "eth0:ethernet:unavailable\nwlan0:wifi:connected\nlo:loopback:unmanaged\n"
    if joined.startswith("nmcli -t -f IN-USE"):
        return WIFI_LIST
    if joined == "iw dev wlan0 link":
        return IW_LINK
    if joined.startswith("ip -4 route"):
        return "default via 192.168.0.1 proto dhcp metric 600\n"
    if joined.startswith("ping"):
        return "64 bytes from 192.168.0.1: icmp_seq=1 ttl=64 time=2.41 ms\n"
    return None


class RadioModel(unittest.TestCase):
    def setUp(self):
        self.p = Project(Exponent=2, PlanW=40, PlanH=20, Band=1)
        self.a = ApItem(X=5, Y=10, Height=1, Tx5=18, Gain5=5, Name="A")
        self.p.Aps.append(self.a)

    def at(self, x, y, band=1):
        return float(rf.signal_from_ap(self.p, self.a, np.array([x]), np.array([y]), band)[0])

    def test_path_loss_matches_windows_version(self):
        self.assertAlmostEqual(self.at(15, 10), -44.26, delta=0.05)
        self.assertAlmostEqual(self.at(5, 10), 23 - 47.26, delta=0.05)

    def test_wall_loss_only_behind_wall(self):
        open_air, near = self.at(15, 10), self.at(8, 10)
        self.p.Walls.append(WallItem(10, 0, 10, 20, 4))
        self.assertAlmostEqual(open_air - self.at(15, 10), 20, places=6)
        self.assertAlmostEqual(near, self.at(8, 10), places=6)
        on_wall = rf.signal_from_ap(Project(Exponent=2), self.a, np.array([10.0]), np.array([10.0]), 1)[0]
        self.assertAlmostEqual(self.at(10, 10), float(on_wall), places=6)

    def test_six_ghz_requires_support(self):
        self.assertTrue(math.isinf(self.at(15, 10, band=2)))

    def test_best_server(self):
        b = ApItem(X=20, Y=10, Height=1, Tx5=18, Gain5=5, Name="B")
        self.p.Aps.append(b)
        self.assertIs(rf.best_at(self.p, 15, 10, 1)[1], b)

    def test_demo_matches_windows_result(self):
        heat = rf.compute(rf.demo_project(), 0)
        self.assertAlmostEqual(heat.coverage_pct, 96.0, delta=0.05)
        self.assertAlmostEqual(heat.dead_m2, 46.3, delta=0.15)
        self.assertEqual(heat.covered + heat.dead, heat.analyzed)
        self.assertAlmostEqual(sum(r[2] * r[3] for r in heat.dead_runs()), heat.dead_m2, places=6)

    def test_area_limits_analysis(self):
        p = Project(PlanW=40, PlanH=20, HasArea=True, AreaW=20, AreaH=20, Threshold=-55)
        p.Aps.append(ApItem(X=2, Y=2, Tx5=18, Gain5=5))
        heat = rf.compute(p, 0)
        self.assertAlmostEqual(heat.analyzed_m2, 400, delta=12)
        self.assertAlmostEqual(heat.coverage_pct, 60.7, delta=0.1)

    def test_survey_values(self):
        p = Project(Band=1, SurveySsid="X")
        hit = SurveyPoint(5, 5, "", [Reading("X", "", 1, -50), Reading("Y", "", 1, -30)])
        miss = SurveyPoint(9, 5, "", [Reading("Y", "", 1, -40)])
        self.assertEqual(rf.point_value(p, hit), -50)
        self.assertEqual(rf.point_value(p, miss), rf.NO_SIGNAL)

    def test_idw(self):
        pts = np.array([[5, 5, -50], [9, 5, -90]], dtype=float)
        got = rf.idw(pts, np.array([5.0, 7.0, 30.0]), np.array([5.0, 5.0, 15.0]))
        self.assertAlmostEqual(got[0], -50)
        self.assertAlmostEqual(got[1], -70)
        self.assertTrue(math.isnan(got[2]))

    def test_project_round_trip(self):
        p = rf.demo_project()
        p.Points.append(SurveyPoint(1, 2, "t", [Reading("한글", "AA", 1, -50)]))
        q = Project.from_json(p.to_json())
        self.assertEqual(p, q)

    @unittest.skipUnless(os.environ.get("WINDOWS_PROJECT"), "Windows 프로젝트 파일 없음")
    def test_loads_windows_project(self):
        with open(os.environ["WINDOWS_PROJECT"], encoding="utf-8-sig") as f:
            p = Project.from_json(f.read())
        self.assertEqual(len(p.Aps), 3)
        self.assertEqual(len(p.Walls), 24)
        self.assertTrue(p.PlanImageBase64)


class LinuxCommands(unittest.TestCase):
    def setUp(self):
        self.backend = wifi.Backend(fake_runner)

    def test_terse_escapes(self):
        self.assertEqual(wifi.split_terse(r"a\:b:c\\d:"), ["a:b", r"c\d", ""])

    def test_wifi_list(self):
        rows = {b.bssid: b for b in wifi.parse_wifi_list(WIFI_LIST)}
        mine = rows["80:CA:4B:8A:A9:CB"]
        self.assertEqual((mine.ssid, mine.channel, mine.band, mine.in_use), ("Office:5G", 44, 1, True))
        self.assertEqual(rows["80:CA:4B:8A:A9:CA"].band, 0)
        self.assertEqual(rows["8A:CA:4B:60:C0:77"].ssid, "(숨겨진 네트워크)")
        self.assertEqual((rows["02:11:22:33:44:55"].band, rows["02:11:22:33:44:55"].security), (2, "개방"))

    def test_link_uses_real_dbm(self):
        iface = self.backend.interface()
        self.assertEqual(iface, "wlan0")
        link = self.backend.link(iface, self.backend.nearby(iface))
        self.assertTrue(link.connected and link.rssi_exact)
        self.assertEqual((link.ssid, link.rssi, link.channel, link.band), ("Office:5G", -41, 44, 1))
        self.assertEqual((round(link.rx_mbps), round(link.tx_mbps), link.phy, link.security), (1201, 865, "Wi-Fi 6", "WPA2"))

    def test_gateway_and_ping(self):
        self.assertEqual(self.backend.gateway("wlan0"), "192.168.0.1")
        self.assertAlmostEqual(self.backend.ping("192.168.0.1"), 2.41)
        self.assertIsNone(wifi.parse_ping("100% packet loss"))

    def test_measure(self):
        m = self.backend.measure()
        self.assertEqual(m.error, "")
        self.assertEqual((len(m.readings), m.connected_ssid, m.connected_band, m.connected_rssi), (4, "Office:5G", 1, -41))
        self.assertIn(-41, [r.Rssi for r in m.readings])

    def test_missing_tools(self):
        backend = wifi.Backend(lambda args, timeout: None)
        self.assertEqual(backend.interface(), "")
        self.assertTrue(backend.link("").error)
        self.assertTrue(backend.measure().error)

    def test_disconnected(self):
        def runner(args, timeout):
            return "Not connected.\n" if args[:2] == ["iw", "dev"] and "link" in args else fake_runner(args, timeout)
        backend = wifi.Backend(runner)
        rows = [b for b in wifi.parse_wifi_list(WIFI_LIST)]
        for b in rows:
            b.in_use = False
        self.assertFalse(backend.link("wlan0", rows).connected)

    def test_channels(self):
        self.assertEqual((wifi.channel_of(2412), wifi.channel_of(2484), wifi.channel_of(5220), wifi.channel_of(5975)), (1, 14, 44, 5))


if __name__ == "__main__":
    unittest.main()
