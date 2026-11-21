"""Wi-Fi 신호 체크 (Linux)"""
from __future__ import annotations

import csv
import math
import os
import sys
import threading
import time
from collections import deque
from dataclasses import dataclass, field
from datetime import datetime
from typing import List, Optional

from PySide6.QtCore import QPointF, QRectF, QSettings, Qt, QThread, QTimer, Signal
from PySide6.QtGui import QBrush, QColor, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import (QAbstractItemView, QFileDialog, QFrame, QGridLayout, QHBoxLayout, QHeaderView, QLabel,
                               QCheckBox, QComboBox, QLineEdit, QMainWindow, QMessageBox, QPushButton, QTableWidget, QTableWidgetItem, QVBoxLayout,
                               QWidget)

from .rf import BAND_NAMES
from .ui import ACCENT, BORDER, INK, MUTED, app_font, create_app, csv_text, level_of
from .wifi import Backend, Bss, Link

HISTORY = 180


@dataclass
class Sample:
    link: Link = field(default_factory=Link)
    nearby: List[Bss] = field(default_factory=list)
    nearby_version: int = 0
    nearby_error: str = ""
    last_scan: Optional[datetime] = None
    gateway: str = ""
    last_ping: Optional[float] = None
    avg_ping: Optional[float] = None
    loss_pct: float = 0.0
    ping_count: int = 0


class Sampler(QThread):
    sampled = Signal(object)

    def __init__(self, backend: Backend, parent=None):
        super().__init__(parent)
        self.backend = backend
        self._stop = threading.Event()
        self._wake = threading.Event()
        self._scan = False
        self._iface = ""
        self._gateway = ""
        self._pings = deque(maxlen=60)
        self._lock = threading.Lock()

    def request_scan(self):
        self._scan = True
        self._wake.set()

    def stop(self):
        self._stop.set()
        self._wake.set()
        self.wait(4000)

    def _ping_loop(self):
        tick = 0
        while not self._stop.is_set():
            started = time.monotonic()
            if tick % 10 == 0 or not self._gateway:
                self._gateway = self.backend.gateway(self._iface)
            tick += 1
            if self._gateway:
                result = self.backend.ping(self._gateway)
                with self._lock:
                    self._pings.append(result)
            self._stop.wait(max(0.05, 1.0 - (time.monotonic() - started)))

    def run(self):
        threading.Thread(target=self._ping_loop, daemon=True).start()
        nearby: List[Bss] = []
        version, error, last_scan = 0, "", None
        next_scan = next_list = 0.0
        while not self._stop.is_set():
            now = time.monotonic()
            self._iface = self.backend.interface()
            if self._iface and (self._scan or now >= next_list or now >= next_scan):
                rescan = self._scan or now >= next_scan
                self._scan = False
                found = self.backend.nearby(self._iface, rescan=rescan)
                if found is None:
                    error = "주변 네트워크를 읽지 못했습니다. NetworkManager가 실행 중인지 확인해 주세요."
                else:
                    nearby, error = found, ""
                    version += 1
                if rescan:
                    last_scan = datetime.now()
                    next_scan = time.monotonic() + 30
                next_list = time.monotonic() + 5
            s = Sample(link=self.backend.link(self._iface, nearby), nearby=nearby, nearby_version=version,
                       nearby_error=error, last_scan=last_scan, gateway=self._gateway)
            with self._lock:
                values = list(self._pings)
            if values:
                ok = [v for v in values if v is not None]
                s.ping_count = len(values)
                s.last_ping = values[-1]
                s.loss_pct = 100.0 * (len(values) - len(ok)) / len(values)
                s.avg_ping = sum(ok) / len(ok) if ok else None
            self.sampled.emit(s)
            self._wake.wait(1.0)
            self._wake.clear()


def advice(s: Sample) -> str:
    link = s.link
    if not link.connected:
        return link.error or "Wi-Fi에 연결되어 있지 않습니다."
    if s.ping_count >= 10 and 5 <= s.loss_pct < 100:
        return "패킷 손실이 있습니다. 간섭이나 공유기 상태를 확인해 보세요."
    if link.rssi >= -60:
        return "신호가 안정적입니다. 영상 통화와 스트리밍에 충분합니다."
    if link.rssi >= -70:
        return "일반 사용에는 무난합니다. 화상 회의나 게임은 공유기 가까이에서 쓰세요."
    if link.rssi >= -80:
        return "연결이 불안정할 수 있습니다. 공유기에 더 가까이 가 보세요."
    return "연결이 끊길 수 있습니다. 공유기 위치를 옮기거나 중계기를 고려해 보세요."


def card() -> QFrame:
    frame = QFrame()
    frame.setObjectName("card")
    return frame


class HeaderCard(QFrame):
    def __init__(self):
        super().__init__()
        self.setObjectName("card")
        self.setFixedHeight(126)
        self.sample = Sample()

    def set_sample(self, s: Sample):
        self.sample = s
        self.update()

    def paintEvent(self, event):
        super().paintEvent(event)
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        link = self.sample.link
        right_zone = 350
        text_w = max(120, self.width() - 40 - right_zone)
        p.setPen(QColor(MUTED))
        p.setFont(app_font(9.5))
        p.drawText(QRectF(20, 12, text_w, 22), Qt.AlignmentFlag.AlignVCenter, "연결된 네트워크" if link.connected else "연결 상태")
        p.setPen(QColor(INK))
        p.setFont(app_font(20, True))
        title = p.fontMetrics().elidedText(link.ssid if link.connected else "연결 안 됨", Qt.TextElideMode.ElideRight, text_w)
        p.drawText(QRectF(20, 34, text_w, 46), Qt.AlignmentFlag.AlignVCenter, title)
        p.setPen(QColor(MUTED))
        p.setFont(app_font(9.5))
        note = p.fontMetrics().elidedText(advice(self.sample), Qt.TextElideMode.ElideRight, text_w)
        p.drawText(QRectF(20, 84, text_w, 24), Qt.AlignmentFlag.AlignVCenter, note)

        level = level_of(link.rssi) if link.connected else None
        bar_w, gap = 13, 6
        bars_w = bar_w * 5 + gap * 4
        right = self.width() - 20
        bottom = self.height() - 24
        p.setPen(Qt.PenStyle.NoPen)
        for i in range(5):
            h = 18 + i * 13
            p.setBrush(level[1] if level and i < level[2] else QColor("#e2e6ec"))
            p.drawRect(QRectF(right - bars_w + i * (bar_w + gap), bottom - h, bar_w, h))
        value = QRectF(right - bars_w - 22 - 250, 12, 250, 60)
        p.setFont(app_font(28, True))
        p.setPen(level[1] if level else QColor(MUTED))
        prefix = "" if not link.connected or link.rssi_exact else "약 "
        p.drawText(value, Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter,
                   f"{prefix}{link.rssi} dBm" if link.connected else "-- dBm")
        if level:
            p.setFont(app_font(10.5, True))
            p.drawText(QRectF(value.x(), 76, value.width(), 26), Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter,
                       f"{level[0]}  ·  품질 {link.quality}%")


class Tile(QFrame):
    def __init__(self, title: str):
        super().__init__()
        self.setObjectName("card")
        self.setFixedHeight(96)
        box = QVBoxLayout(self)
        box.setContentsMargins(14, 8, 10, 8)
        box.setSpacing(0)
        head = QLabel(title)
        head.setProperty("muted", True)
        self.value = QLabel("-")
        self.value.setFont(app_font(15, True))
        self.sub = QLabel("")
        self.sub.setProperty("muted", True)
        self.sub.setFont(app_font(8.5))
        for w in (head, self.value, self.sub):
            box.addWidget(w)

    def set(self, value: str, sub: str = "", color: str = INK):
        self.value.setText(value)
        self.value.setStyleSheet(f"color: {color};")
        self.sub.setText(sub)


class Graph(QFrame):
    TOP, FLOOR = -20.0, -100.0

    def __init__(self):
        super().__init__()
        self.setObjectName("card")
        self.setMinimumHeight(190)
        self.data = deque(maxlen=HISTORY)
        self.previous = []
        self.smoothing = False

    def moving_average(self):
        window = deque(maxlen=5)
        for value in self.data:
            if not math.isfinite(value):
                window.clear()
                yield math.nan
                continue
            window.append(value)
            yield sum(window) / len(window)

    def add(self, value: float):
        self.data.append(value)
        self.update()

    def paintEvent(self, event):
        super().paintEvent(event)
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setFont(app_font(10.5, True))
        p.setPen(QColor(INK))
        p.drawText(QRectF(16, 8, 260, 24), Qt.AlignmentFlag.AlignVCenter, "신호 추이 · 최근 3분")
        valid = [v for v in self.data if not math.isnan(v)]
        p.setFont(app_font(8.5))
        p.setPen(QColor(MUTED))
        if valid:
            p.drawText(QRectF(self.width() - 340, 8, 324, 24), Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter,
                       f"평균 {sum(valid) / len(valid):.0f} · 최저 {min(valid):.0f} · 최고 {max(valid):.0f} dBm")
        area = QRectF(56, 40, self.width() - 74, self.height() - 68)
        if area.width() < 20 or area.height() < 20:
            return

        def y(v):
            return area.top() + (self.TOP - max(self.FLOOR, min(self.TOP, v))) / (self.TOP - self.FLOOR) * area.height()

        edges = [-20, -50, -60, -70, -80, -100]
        p.setPen(Qt.PenStyle.NoPen)
        for a, b in zip(edges, edges[1:]):
            c = QColor(level_of(b)[1])
            c.setAlpha(20)
            p.setBrush(c)
            p.drawRect(QRectF(area.left(), y(a), area.width(), y(b) - y(a)))
        for d in (-30, -50, -60, -70, -80, -90):
            p.setPen(QColor(BORDER))
            p.drawLine(QPointF(area.left(), y(d)), QPointF(area.right(), y(d)))
            p.setPen(QColor(MUTED))
            p.drawText(QRectF(8, y(d) - 10, 40, 20), Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter, str(d))
        p.drawText(QRectF(area.left(), area.bottom() + 4, 80, 20), Qt.AlignmentFlag.AlignVCenter, "3분 전")
        p.drawText(QRectF(area.right() - 80, area.bottom() + 4, 80, 20), Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter, "지금")

        step = area.width() / (HISTORY - 1)
        data = list(self.data)
        def draw_trace(values, pen):
            path = QPainterPath()
            pen_down = False
            for i, value in enumerate(values):
                if not math.isfinite(value):
                    pen_down = False
                    continue
                point = QPointF(area.right() - (len(values) - 1 - i) * step, y(value))
                if pen_down:
                    path.lineTo(point)
                else:
                    path.moveTo(point)
                    pen_down = True
            p.setPen(pen)
            p.drawPath(path)

        p.setBrush(Qt.BrushStyle.NoBrush)
        draw_trace(self.previous, QPen(QColor(MUTED), 2, Qt.PenStyle.DashLine))
        data = list(self.moving_average()) if self.smoothing else data
        draw_trace(data, QPen(QColor(ACCENT), 2))
        if data and not math.isnan(data[-1]):
            p.setBrush(QBrush(QColor(ACCENT)))
            p.setPen(Qt.PenStyle.NoPen)
            p.drawEllipse(QPointF(area.right(), y(data[-1])), 4.5, 4.5)


class NumericItem(QTableWidgetItem):
    def __lt__(self, other):
        return self.data(Qt.ItemDataRole.UserRole) < other.data(Qt.ItemDataRole.UserRole)


class SignalWindow(QMainWindow):
    def __init__(self, backend: Optional[Backend] = None, autostart: bool = True):
        super().__init__()
        self.setWindowTitle("Wi-Fi 신호 체크")
        self.resize(1080, 840)
        self.setMinimumSize(900, 660)
        self.backend = backend or Backend()
        self.sampler = Sampler(self.backend, self)
        self.sampler.sampled.connect(self.on_sample)
        self.latest = Sample()
        self.samples = 0
        self.started = time.monotonic()
        self.shown_version = -1
        self.shown_bssid = ""
        self.log_file = None
        self.log_writer = None
        self.log_rows = 0
        self.log_path = ""

        root = QWidget(objectName="root")
        self.setCentralWidget(root)
        box = QVBoxLayout(root)
        box.setContentsMargins(12, 12, 12, 12)
        box.setSpacing(8)
        self.header = HeaderCard()
        box.addWidget(self.header)
        grid = QGridLayout()
        grid.setSpacing(8)
        self.t_channel, self.t_link, self.t_ping = Tile("채널"), Tile("링크 속도"), Tile("공유기 응답 시간")
        self.t_loss, self.t_phy, self.t_count = Tile("패킷 손실"), Tile("규격 / 보안"), Tile("측정 횟수")
        for i, t in enumerate((self.t_channel, self.t_link, self.t_ping, self.t_loss, self.t_phy, self.t_count)):
            grid.addWidget(t, 0, i)
            grid.setColumnStretch(i, 1)
        box.addLayout(grid)
        self.graph = Graph()
        box.addWidget(self.graph, 45)
        graph_controls = QHBoxLayout()
        self.settings = QSettings("WifiSurveyTools", "SignalChecker")
        smoothing = QCheckBox("5초 이동평균")
        self.graph.smoothing = self.settings.value("smoothing", False, type=bool)
        smoothing.setChecked(self.graph.smoothing)
        smoothing.toggled.connect(self.set_smoothing)
        save = QPushButton("최근 3분 저장")
        save.clicked.connect(self.save_session)
        compare = QPushButton("이전 기록 비교")
        compare.clicked.connect(self.load_session)
        clear = QPushButton("비교 지우기")
        clear.clicked.connect(self.clear_comparison)
        for control in (smoothing, save, compare, clear):
            graph_controls.addWidget(control)
        graph_controls.addStretch()
        box.addLayout(graph_controls)
        self.nearby_title = QLabel("주변 Wi-Fi")
        self.nearby_title.setFont(app_font(10.5, True))
        box.addWidget(self.nearby_title)
        filters = QHBoxLayout()
        self.search = QLineEdit()
        self.search.setPlaceholderText("SSID 또는 BSSID 검색")
        self.search.textChanged.connect(self.filter_table)
        self.band_filter = QComboBox()
        self.band_filter.addItems(["전체 대역", *BAND_NAMES])
        self.band_filter.currentIndexChanged.connect(self.filter_table)
        self.channel_hint = QLabel()
        filters.addWidget(self.search, 1)
        filters.addWidget(self.band_filter)
        filters.addWidget(self.channel_hint)
        box.addLayout(filters)
        self.table = QTableWidget(0, 7)
        self.table.setSortingEnabled(True)
        self.table.setHorizontalHeaderLabels(["네트워크 이름", "신호", "강도", "채널", "대역", "보안", "BSSID"])
        self.table.verticalHeader().hide()
        self.table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.table.setShowGrid(False)
        self.table.setFocusPolicy(Qt.FocusPolicy.NoFocus)
        self.table.horizontalHeader().setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        for col, width in ((1, 96), (2, 92), (3, 58), (4, 78), (5, 130), (6, 170)):
            self.table.setColumnWidth(col, width)
        box.addWidget(self.table, 55)
        row = QHBoxLayout()
        self.scan_btn = QPushButton("지금 스캔")
        self.scan_btn.clicked.connect(self.scan_now)
        self.log_btn = QPushButton("CSV 기록 시작")
        self.log_btn.clicked.connect(self.toggle_log)
        self.status = QLabel("")
        self.status.setProperty("muted", True)
        row.addWidget(self.scan_btn)
        row.addWidget(self.log_btn)
        row.addStretch(1)
        row.addWidget(self.status)
        box.addLayout(row)
        self.hold_until = 0.0
        if autostart:
            QTimer.singleShot(0, self.sampler.start)

    def set_smoothing(self, enabled):
        self.graph.smoothing = enabled
        self.settings.setValue("smoothing", enabled)
        self.graph.update()

    def save_session(self):
        path, _ = QFileDialog.getSaveFileName(self, "최근 신호 기록 저장", "session.csv", "CSV (*.csv)")
        if not path:
            return
        try:
            with open(path, "w", newline="", encoding="utf-8") as stream:
                writer = csv.writer(stream)
                writer.writerow(["rssi_dbm"])
                writer.writerows([value if math.isfinite(value) else ""] for value in self.graph.data)
        except OSError as error:
            QMessageBox.warning(self, "기록 저장 오류", str(error))

    def load_session(self):
        path, _ = QFileDialog.getOpenFileName(self, "이전 기록 비교", "", "CSV (*.csv)")
        if not path:
            return
        try:
            with open(path, newline="", encoding="utf-8-sig") as stream:
                reader = csv.DictReader(stream)
                if reader.fieldnames != ["rssi_dbm"]:
                    raise ValueError("최근 3분 저장으로 만든 CSV를 선택해 주세요.")
                previous = deque(maxlen=HISTORY)
                for row in reader:
                    raw = row["rssi_dbm"]
                    value = float(raw) if raw else math.nan
                    if raw and (not math.isfinite(value) or not -127 <= value <= 0):
                        raise ValueError("신호 값 범위가 올바르지 않습니다.")
                    previous.append(value)
            self.graph.previous = list(previous)
            self.graph.update()
        except (OSError, ValueError, csv.Error) as error:
            QMessageBox.warning(self, "기록 불러오기 오류", str(error))

    def clear_comparison(self):
        self.graph.previous = []
        self.graph.update()

    def filter_table(self, *_):
        self.shown_version = -1
        self.update_table(self.latest)

    def scan_now(self):
        self.sampler.request_scan()
        self.notify("주변 네트워크를 스캔하는 중입니다...")

    def notify(self, text: str):
        self.status.setText(text)
        self.hold_until = time.monotonic() + 6

    def on_sample(self, s: Sample):
        self.latest = s
        self.samples += 1
        self.graph.add(float(s.link.rssi) if s.link.connected else math.nan)
        self.header.set_sample(s)
        self.update_tiles(s)
        self.update_table(s)
        self.write_log(s)
        if time.monotonic() >= self.hold_until:
            if self.log_writer:
                self.status.setText(f"기록 중: {os.path.basename(self.log_path)} ({self.log_rows}행)")
            elif s.last_scan:
                self.status.setText(f"마지막 스캔 {s.last_scan:%H:%M:%S} · 1초마다 측정")

    def update_tiles(self, s: Sample):
        link = s.link
        if link.connected:
            band = BAND_NAMES[link.band] if link.band >= 0 else "-"
            same = sum(1 for b in s.nearby if b.channel == link.channel and b.band == link.band and b.bssid != link.bssid)
            self.t_channel.set(str(link.channel or "-"), f"{band} · 같은 채널 AP {same}개")
            if link.rx_mbps:
                self.t_link.set(f"{link.rx_mbps:.0f} Mbps", f"수신 {link.rx_mbps:.0f} / 송신 {link.tx_mbps:.0f}")
            else:
                self.t_link.set("-", "iw 설치 시 표시")
            self.t_phy.set(link.phy or "-", link.security or "-")
        else:
            self.t_channel.set("-")
            self.t_link.set("-")
            self.t_phy.set("-")
        red = level_of(-90)[1].name()
        if not link.connected or not s.gateway or not s.ping_count:
            self.t_ping.set("-", "공유기 확인 중" if link.connected else "")
            self.t_loss.set("-")
        elif s.avg_ping is None:
            # 공유기가 핑에 응답하지 않도록 설정된 경우 손실로 단정하지 않는다
            self.t_ping.set("응답 없음", "공유기가 핑을 막았을 수 있음")
            self.t_loss.set("-", "측정 불가")
        else:
            last = f"{s.last_ping:.0f} ms" if s.last_ping is not None else "시간 초과"
            self.t_ping.set(last, f"평균 {s.avg_ping:.0f} ms", red if s.last_ping is None else INK)
            color = red if s.loss_pct >= 5 else level_of(-65)[1].name() if s.loss_pct > 0 else INK
            self.t_loss.set(f"{s.loss_pct:.1f}%".replace(".0%", "%"), f"최근 {s.ping_count}회 기준", color)
        up = int(time.monotonic() - self.started)
        self.t_count.set(f"{self.samples}회", f"경과 {up // 3600:02d}:{up // 60 % 60:02d}:{up % 60:02d}")

    def update_table(self, s: Sample):
        if s.nearby_error:
            self.nearby_title.setText(s.nearby_error)
        if s.nearby_version == self.shown_version and s.link.bssid == self.shown_bssid:
            return
        self.shown_version, self.shown_bssid = s.nearby_version, s.link.bssid
        if not s.nearby_error:
            self.nearby_title.setText(f"주변 Wi-Fi · 액세스 포인트 {len(s.nearby)}개 (주변 AP 신호는 추정값)")
        scroll = self.table.verticalScrollBar().value()
        query = self.search.text().strip().casefold()
        band = self.band_filter.currentIndex() - 1
        rows = [b for b in s.nearby if (band < 0 or b.band == band)
                and (query in b.ssid.casefold() or query in b.bssid.casefold())]
        observed = [b for b in s.nearby if b.band == 0 and b.channel > 0 and b.bssid != s.link.bssid]
        if observed and not s.nearby_error:
            # In 2.4 GHz, account for overlapping 20 MHz channels as well as co-channel APs.
            def congestion(channel):
                return sum(max(0, 5 - abs(channel - b.channel)) * 10 ** (b.rssi / 10) for b in observed)

            recommended = min((1, 6, 11), key=congestion)
            self.channel_hint.setText(f"2.4 GHz 권장 채널 {recommended}")
        else:
            self.channel_hint.clear()
        self.table.setSortingEnabled(False)
        self.table.setRowCount(len(rows))
        for r, b in enumerate(rows):
            mine = b.bssid == s.link.bssid
            rssi = s.link.rssi if mine and s.link.rssi_exact else b.rssi
            name, color, _ = level_of(rssi)
            cells = [("● " if mine else "") + b.ssid, f"{rssi} dBm", name, str(b.channel), BAND_NAMES[b.band], b.security, b.bssid]
            for c, text in enumerate(cells):
                if c in (1, 3):
                    item = NumericItem(text)
                    item.setData(Qt.ItemDataRole.UserRole, rssi if c == 1 else b.channel)
                else:
                    item = QTableWidgetItem(text)
                if c in (1, 2):
                    item.setForeground(color)
                if c in (1, 3):
                    item.setTextAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
                if mine:
                    item.setBackground(QColor("#e8f2fc"))
                    item.setFont(app_font(10, True))
                self.table.setItem(r, c, item)
            self.table.setRowHeight(r, 26)
        self.table.setSortingEnabled(True)
        self.table.verticalScrollBar().setValue(scroll)

    def toggle_log(self):
        if self.log_writer:
            saved = self.log_path
            self.close_log()
            self.notify(f"저장됨: {saved}")
            return
        name = f"wifi_log_{datetime.now():%Y%m%d_%H%M%S}.csv"
        path, _ = QFileDialog.getSaveFileName(self, "신호 기록을 저장할 위치", name, "CSV 파일 (*.csv)")
        if path:
            self.start_log(path)

    def start_log(self, path: str):
        try:
            self.log_file = open(path, "w", newline="", encoding="utf-8-sig")
        except OSError as ex:
            QMessageBox.warning(self, self.windowTitle(), f"파일을 만들 수 없습니다.\n{ex}")
            return
        self.log_writer = csv.writer(self.log_file)
        self.log_writer.writerow(["시각", "SSID", "BSSID", "신호(dBm)", "품질(%)", "채널", "대역", "수신(Mbps)", "송신(Mbps)", "응답시간(ms)"])
        self.log_path, self.log_rows = path, 0
        self.log_btn.setText("CSV 기록 중지")

    def write_log(self, s: Sample):
        if not self.log_writer:
            return
        now = f"{datetime.now():%Y-%m-%d %H:%M:%S}"
        link = s.link
        try:
            if link.connected:
                band = BAND_NAMES[link.band] if link.band >= 0 else ""
                ping = f"{s.last_ping:.0f}" if s.last_ping is not None else ""
                self.log_writer.writerow([now, csv_text(link.ssid), link.bssid, link.rssi, link.quality, link.channel, band,
                                          f"{link.rx_mbps:.0f}", f"{link.tx_mbps:.0f}", ping])
            else:
                self.log_writer.writerow([now, "(연결 안 됨)"])
            self.log_file.flush()
            self.log_rows += 1
        except OSError as ex:
            self.close_log()
            self.notify(f"기록 중단: {ex}")

    def close_log(self):
        if self.log_file:
            self.log_file.close()
        self.log_file = self.log_writer = None
        self.log_rows = 0
        self.log_btn.setText("CSV 기록 시작")

    def closeEvent(self, event):
        self.sampler.stop()
        self.close_log()
        super().closeEvent(event)


def main() -> int:
    app = create_app(sys.argv)
    window = SignalWindow()
    window.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
