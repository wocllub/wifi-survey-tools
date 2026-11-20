import csv
import os
import tempfile
import time
import unittest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QPoint, Qt  # noqa: E402
from PySide6.QtGui import QColor, QImage, QWheelEvent  # noqa: E402
from PySide6.QtTest import QTest  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

from test_core import fake_runner  # noqa: E402
from wifi_survey import planner_app as pa  # noqa: E402
from wifi_survey import rf, signal_app, ui, wifi  # noqa: E402

app = ui.create_app([])
OUT = os.environ.get("GUI_OUTPUT") or tempfile.mkdtemp()
os.makedirs(OUT, exist_ok=True)


def pump(ms=120):
    end = time.monotonic() + ms / 1000
    while time.monotonic() < end:
        QApplication.processEvents()
        time.sleep(0.01)


class Planner(unittest.TestCase):
    def setUp(self):
        self.w = pa.PlannerWindow(wifi.Backend(fake_runner))
        self.w.quiet = True
        self.w.show()
        pump()
        self.w.new_project(rf.Project(PlanW=40, PlanH=25))
        pump()

    def tearDown(self):
        self.w.close()
        pump(30)

    def pt(self, x, y) -> QPoint:
        return self.w.view.p(x, y).toPoint()

    def click(self, x, y, button=Qt.MouseButton.LeftButton):
        QTest.mouseClick(self.w.canvas, button, Qt.KeyboardModifier.NoModifier, self.pt(x, y))

    def drag(self, a, b):
        c = self.w.canvas
        QTest.mousePress(c, Qt.MouseButton.LeftButton, Qt.KeyboardModifier.NoModifier, self.pt(*a))
        QTest.mouseMove(c, self.pt(*b))
        pump(30)
        QTest.mouseRelease(c, Qt.MouseButton.LeftButton, Qt.KeyboardModifier.NoModifier, self.pt(*b))

    def test_every_vendor_can_be_placed(self):
        self.w.set_tool(pa.PLACE_AP)
        vendors = ("Cisco", "HPE Aruba", "Juniper Mist", "Grandstream", "H3C")
        for i, vendor in enumerate(vendors):
            self.w.vendor_box.setCurrentText(vendor)
            self.click(4 + i * 7, 10)
        self.assertEqual(tuple(a.Vendor for a in self.w.project.Aps), vendors)
        self.assertEqual([a.Name for a in self.w.project.Aps[3:]], ["GWN7664-1", "WA7638-1"])
        self.assertTrue(self.w.project.Aps[4].Has6)
        self.assertAlmostEqual(self.w.project.Aps[0].X, 4, delta=0.2)
        self.assertTrue(self.w.ap_panel.isVisible())

    def test_walls_undo_select_delete(self):
        self.w.set_tool(pa.WALL)
        self.w.wall_box.setCurrentIndex(4)
        self.click(20, 2)
        self.click(20, 22)
        self.click(30, 22)
        self.assertEqual(len(self.w.project.Walls), 2)
        self.assertEqual(self.w.project.Walls[0].Material, 4)
        self.w.undo()
        self.assertEqual(len(self.w.project.Walls), 1)
        self.w.set_tool(pa.SELECT)
        self.click(20, 12)
        self.assertIsInstance(self.w.selected, rf.WallItem)
        self.w.canvas.setFocus()
        QTest.keyClick(self.w, Qt.Key.Key_Delete)
        self.assertEqual(len(self.w.project.Walls), 0)

    def test_right_click_ends_wall_chain(self):
        self.w.set_tool(pa.WALL)
        self.click(5, 5)
        self.click(10, 5, Qt.MouseButton.RightButton)
        self.click(15, 5)
        self.assertEqual(len(self.w.project.Walls), 0)

    def test_drag_ap_and_edit(self):
        self.w.set_tool(pa.PLACE_AP)
        self.click(10, 10)
        self.w.set_tool(pa.SELECT)
        self.drag((10, 10), (25, 15))
        ap = self.w.project.Aps[0]
        self.assertAlmostEqual(ap.X, 25, delta=0.2)
        self.assertAlmostEqual(ap.Y, 15, delta=0.2)
        self.w.ap_tx5.setValue(12)
        self.assertEqual(ap.Tx5, 12)
        self.w.undo()
        self.assertEqual(self.w.project.Aps[0].Tx5, 18)

    def test_area_and_scale(self):
        self.w.set_tool(pa.AREA)
        self.drag((4, 4), (34, 24))
        p = self.w.project
        self.assertTrue(p.HasArea)
        self.assertAlmostEqual(p.AreaW, 30, delta=0.2)
        self.w.ask_distance = lambda measured: 20.0
        self.w.set_tool(pa.SCALE)
        self.click(0, 0)
        self.click(10, 0)
        self.assertAlmostEqual(self.w.project.PlanW, 80, delta=0.5)
        self.assertAlmostEqual(self.w.project.AreaW, 60, delta=1)

    def test_zoom_keeps_cursor_position(self):
        pos = self.pt(10, 10).toPointF()
        before = self.w.view.world(pos)
        zoom = self.w.view.zoom
        event = QWheelEvent(pos, pos, QPoint(0, 0), QPoint(0, 120), Qt.MouseButton.NoButton,
                            Qt.KeyboardModifier.NoModifier, Qt.ScrollPhase.NoScrollPhase, False)
        QApplication.sendEvent(self.w.canvas, event)
        self.assertAlmostEqual(self.w.view.zoom, zoom * 1.15, places=6)
        after = self.w.view.world(pos)
        self.assertAlmostEqual(before[0], after[0], places=6)
        self.assertAlmostEqual(before[1], after[1], places=6)

    def test_survey_click(self):
        self.w.set_tool(pa.SURVEY)
        self.click(12, 12)
        for _ in range(100):
            pump(20)
            if self.w.project.Points:
                break
        p = self.w.project
        self.assertEqual(len(p.Points), 1)
        self.assertEqual((p.View, p.SurveySsid, p.Band), (1, "Office:5G", 1))
        self.assertEqual(rf.point_value(p, p.Points[0]), -41)
        self.w.compute_now()
        self.assertGreater(self.w.frame.heat.analyzed, 0)
        path = os.path.join(OUT, "survey.csv")
        self.w.export_csv(path)
        with open(path, encoding="utf-8-sig", newline="") as f:
            self.assertEqual(len(list(csv.reader(f))), 5)

    def test_measure_failure_is_reported(self):
        self.w.backend = wifi.Backend(lambda a, t: None)
        self.w.set_tool(pa.SURVEY)
        self.click(12, 12)
        for _ in range(100):
            pump(20)
            if not self.w.measuring:
                break
        self.assertEqual(len(self.w.project.Points), 0)
        self.assertIn("측정 실패", self.w.statusBar().currentMessage())

    def test_plan_save_and_open(self):
        image = QImage(800, 500, QImage.Format.Format_RGB32)
        image.fill(QColor("white"))
        plan = os.path.join(OUT, "plan.png")
        image.save(plan)
        self.w.new_project(rf.demo_project())
        self.w.load_plan(plan)
        self.assertFalse(self.w.project.ScaleSet)
        self.assertAlmostEqual(self.w.project.PlanW, 40)
        path = os.path.join(OUT, "roundtrip.wifiplan")
        self.w.save_project(path)
        self.assertIsNone(self.w.project.PlanImageBase64)
        self.w.new_project(rf.Project())
        self.w.load_project(path)
        self.assertEqual((len(self.w.project.Aps), len(self.w.project.Walls), self.w.plan.width()), (3, 24, 800))

    @unittest.skipUnless(os.environ.get("WINDOWS_PROJECT"), "Windows 프로젝트 파일 없음")
    def test_open_windows_project(self):
        self.w.load_project(os.environ["WINDOWS_PROJECT"])
        self.assertEqual((len(self.w.project.Aps), self.w.plan.width()), (3, 800))

    def test_background_heat_and_screens(self):
        self.w.new_project(rf.demo_project())
        for _ in range(150):
            pump(20)
            if self.w.frame is not None and self.w.frame.heat.analyzed:
                break
        self.assertGreater(self.w.frame.heat.analyzed, 0)
        self.assertIn("96.0", self.w.stat_main.text())
        self.w.set_tool(pa.SELECT)
        self.click(31, 8)
        pump()
        self.assertTrue(self.w.grab().save(os.path.join(OUT, "planner.png")))
        path = os.path.join(OUT, "export.png")
        self.w.export_png(path)
        self.assertGreater(os.path.getsize(path), 10000)


class SignalWindow(unittest.TestCase):
    def sample(self):
        backend = wifi.Backend(fake_runner)
        nearby = backend.nearby("wlan0")
        return signal_app.Sample(link=backend.link("wlan0", nearby), nearby=nearby, nearby_version=1, gateway="192.168.0.1",
                                 last_ping=2.4, avg_ping=3.1, ping_count=12)

    def test_screen_and_log(self):
        w = signal_app.SignalWindow(wifi.Backend(fake_runner), autostart=False)
        w.show()
        path = os.path.join(OUT, "signal.csv")
        w.start_log(path)
        for rssi in range(-41, -53, -1):
            s = self.sample()
            s.link.rssi = rssi
            w.on_sample(s)
        pump()
        self.assertEqual(w.table.rowCount(), 4)
        self.assertEqual(w.t_channel.value.text(), "44")
        self.assertEqual(w.t_phy.value.text(), "Wi-Fi 6")
        self.assertEqual(w.t_link.value.text(), "1201 Mbps")
        self.assertTrue(w.grab().save(os.path.join(OUT, "signal.png")))
        w.close_log()
        with open(path, encoding="utf-8-sig", newline="") as f:
            self.assertEqual(len(list(csv.reader(f))), 13)
        w.close()

    def test_disconnected_and_blocked_ping(self):
        w = signal_app.SignalWindow(wifi.Backend(fake_runner), autostart=False)
        w.on_sample(signal_app.Sample(link=wifi.Link(has_adapter=True, iface="wlan0")))
        self.assertEqual(w.t_channel.value.text(), "-")
        s = self.sample()
        s.avg_ping, s.last_ping, s.loss_pct = None, None, 100.0
        w.on_sample(s)
        self.assertEqual(w.t_ping.value.text(), "응답 없음")
        w.close()

    def test_live_sampler(self):
        w = signal_app.SignalWindow(wifi.Backend(fake_runner), autostart=True)
        for _ in range(250):
            pump(20)
            if w.samples:
                break
        self.assertGreater(w.samples, 0)
        w.close()
        self.assertTrue(w.sampler.isFinished())


if __name__ == "__main__":
    unittest.main()
