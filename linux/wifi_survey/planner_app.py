"""Wi-Fi 음영지역 분석기 (Linux)"""
from __future__ import annotations

import base64
import csv
import math
import sys
from datetime import datetime
from typing import Callable, List, Optional

from PySide6.QtCore import QBuffer, QIODevice, QObject, QPointF, QRectF, QRunnable, Qt, QThreadPool, QTimer, Signal
from PySide6.QtGui import QAction, QBrush, QColor, QImage, QKeySequence, QLinearGradient, QPainter, QPen
from PySide6.QtWidgets import (QButtonGroup, QComboBox, QDoubleSpinBox, QFileDialog, QFormLayout, QFrame, QHBoxLayout,
                               QInputDialog, QLabel, QLineEdit, QListWidget, QMainWindow, QMessageBox, QPushButton,
                               QScrollArea, QSizePolicy, QToolButton, QVBoxLayout, QWidget)

from . import rf
from .rf import ApItem, HeatResult, Project, SurveyPoint, WallItem
from .ui import ACCENT, INK, MUTED, WARN, app_font, create_app, csv_text
from .wifi import Backend, MeasureResult

SELECT, PLACE_AP, WALL, AREA, SCALE, SURVEY = range(6)
TOOL_LABELS = ("선택 / 이동", "AP 배치", "벽 그리기", "분석 범위 지정", "축척 설정", "현장 실측")
TOOL_HINTS = (
    "AP를 끌어서 옮기고, 벽이나 지점을 눌러 선택합니다. Delete 키로 삭제합니다.",
    "아래에서 제품을 고른 뒤 도면을 눌러 AP를 놓습니다.",
    "도면을 차례로 눌러 벽을 잇습니다. 오른쪽 클릭이나 Esc로 끝냅니다.",
    "건물 내부를 드래그해 지정합니다. 범위 안만 분석합니다.",
    "길이를 아는 두 점을 차례로 누른 뒤 실제 거리를 입력합니다.",
    "현재 서 있는 위치를 도면에서 누르면 실제 신호를 측정합니다.",
)
ENV_VALUES = (2.2, 2.6, 3.0, 3.4)
ENV_NAMES = ("개방형 공간", "일반 사무실", "밀집 공간", "창고 / 공장")


class View:
    def __init__(self):
        self.zoom, self.pan_x, self.pan_y = 20.0, 0.0, 0.0

    def p(self, x: float, y: float) -> QPointF:
        return QPointF(self.pan_x + x * self.zoom, self.pan_y + y * self.zoom)

    def world(self, pos: QPointF):
        return (pos.x() - self.pan_x) / self.zoom, (pos.y() - self.pan_y) / self.zoom

    def fit(self, project: Project, w: int, h: int):
        self.zoom = max(0.5, min((w - 40.0) / project.PlanW, (h - 40.0) / project.PlanH))
        self.pan_x = (w - project.PlanW * self.zoom) / 2
        self.pan_y = (h - project.PlanH * self.zoom) / 2


class HeatFrame:
    """계산 결과와 화면용 이미지."""

    def __init__(self, heat: HeatResult):
        self.heat = heat
        self.rgba = heat.rgba()
        self.image = QImage(self.rgba.data, heat.cols, heat.rows, heat.cols * 4, QImage.Format.Format_RGBA8888)
        self.runs = heat.dead_runs()


def halo_text(p: QPainter, text: str, color: QColor, cx: float, top: float):
    m = p.fontMetrics()
    w = m.horizontalAdvance(text) + 6
    rect = QRectF(cx - w / 2, top, w, m.height())
    p.fillRect(rect, QColor(255, 255, 255, 225))
    p.setPen(color)
    p.drawText(rect, Qt.AlignmentFlag.AlignCenter, text)


def draw_scene(p: QPainter, project: Project, plan: Optional[QImage], frame: Optional[HeatFrame], v: View, selected):
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
    o = v.p(0, 0)
    rect = QRectF(o.x(), o.y(), project.PlanW * v.zoom, project.PlanH * v.zoom)
    p.fillRect(rect, Qt.GlobalColor.white)
    if plan is not None:
        p.drawImage(rect, plan)
    else:
        step = 1.0 if v.zoom >= 12 else 5.0
        count_x, count_y = int(project.PlanW / step), int(project.PlanH / step)
        for i in range(count_x + 1):
            x = i * step
            p.setPen(QColor("#dadfe6") if int(x) % 5 == 0 else QColor("#eceff3"))
            p.drawLine(v.p(x, 0), v.p(x, project.PlanH))
        for j in range(count_y + 1):
            y = j * step
            p.setPen(QColor("#dadfe6") if int(y) % 5 == 0 else QColor("#eceff3"))
            p.drawLine(v.p(0, y), v.p(project.PlanW, y))

    p.save()
    p.setClipRect(rect)
    if frame is not None and project.View != 2:
        h = frame.heat
        p.drawImage(QRectF(o.x(), o.y(), h.cols * h.cell * v.zoom, h.rows * h.cell * v.zoom), frame.image)
        brush = QBrush(QColor(255, 255, 255, 120), Qt.BrushStyle.BDiagPattern)
        p.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        for x, y, w, rh in frame.runs:
            a = v.p(x, y)
            p.fillRect(QRectF(a.x(), a.y(), w * v.zoom + 0.5, rh * v.zoom + 0.5), brush)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.restore()

    p.setPen(QColor("#969eaa"))
    p.setBrush(Qt.BrushStyle.NoBrush)
    p.drawRect(rect)
    if project.HasArea:
        a = v.p(project.AreaX, project.AreaY)
        p.setPen(QPen(QColor(ACCENT), 2, Qt.PenStyle.DashLine))
        p.drawRect(QRectF(a.x(), a.y(), project.AreaW * v.zoom, project.AreaH * v.zoom))

    for w in project.Walls:
        sel = w is selected
        pen = QPen(QColor(ACCENT if sel else rf.MATERIALS[w.Material][2]), 5 if sel else 3)
        pen.setCapStyle(Qt.PenCapStyle.RoundCap)
        p.setPen(pen)
        p.drawLine(v.p(w.X1, w.Y1), v.p(w.X2, w.Y2))

    p.setFont(app_font(8))
    for s in project.Points:
        c = v.p(s.X, s.Y)
        value = rf.point_value(project, s)
        color = QColor(190, 28, 48) if value < project.Threshold else QColor(12, 118, 78)
        r = 7 if s is selected else 5
        p.setBrush(color)
        p.setPen(QPen(QColor(ACCENT) if s is selected else Qt.GlobalColor.white, 2))
        p.drawEllipse(c, r, r)
        halo_text(p, "신호 없음" if value <= rf.NO_SIGNAL else f"{value:.0f}", color, c.x(), c.y() + r + 1)

    for a in project.Aps:
        c = v.p(a.X, a.Y)
        r = 14
        if a is selected:
            p.setBrush(Qt.BrushStyle.NoBrush)
            p.setPen(QPen(QColor(ACCENT), 3))
            p.drawEllipse(c, r + 4, r + 4)
        p.setBrush(QColor(rf.VENDOR_COLORS.get(a.Vendor, "#5a606c")))
        p.setPen(QPen(Qt.GlobalColor.white, 2))
        p.drawEllipse(c, r, r)
        p.setFont(app_font(8.5, True))
        p.setPen(Qt.GlobalColor.white)
        p.drawText(QRectF(c.x() - r, c.y() - r, 2 * r, 2 * r), Qt.AlignmentFlag.AlignCenter, rf.VENDOR_MARKS.get(a.Vendor, "AP"))
        halo_text(p, a.Name, QColor(INK), c.x(), c.y() + r + 3)


def draw_legend(p: QPainter, project: Project, x: float, y: float):
    w, h = 300, 58
    p.setPen(QColor("#cdd2da"))
    p.setBrush(QColor(255, 255, 255, 238))
    p.drawRect(QRectF(x, y, w, h))
    bar = QRectF(x + 88, y + 10, w - 102, 14)
    grad = QLinearGradient(bar.topLeft(), bar.topRight())
    grad.setColorAt(0, QColor(245, 196, 40))
    grad.setColorAt(0.5, QColor(132, 192, 58))
    grad.setColorAt(1, QColor(12, 128, 84))
    p.fillRect(bar, grad)
    dead = QRectF(x + 12, y + 10, 64, 14)
    p.fillRect(dead, QColor(190, 28, 48))
    p.fillRect(dead, QBrush(QColor(255, 255, 255, 130), Qt.BrushStyle.BDiagPattern))
    p.setFont(app_font(8))
    p.setPen(QColor(INK))
    p.drawText(QRectF(x + 8, y + 28, 80, 22), Qt.AlignmentFlag.AlignVCenter, f"음영 {project.Threshold:.0f} 미만")
    p.drawText(QRectF(bar.x(), y + 28, 60, 22), Qt.AlignmentFlag.AlignVCenter, f"{project.Threshold:.0f}")
    p.drawText(QRectF(bar.right() - 100, y + 28, 100, 22), Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter, "-45 dBm 이상")


class Canvas(QWidget):
    def __init__(self, win: "PlannerWindow"):
        super().__init__()
        self.win = win
        self.setMouseTracking(True)
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        self.setMinimumSize(400, 300)

    def paintEvent(self, event):
        self.win.paint_canvas(self)

    def resizeEvent(self, event):
        self.win.canvas_resized()

    def mousePressEvent(self, e):
        self.win.mouse_down(e)

    def mouseMoveEvent(self, e):
        self.win.mouse_move(e)

    def mouseReleaseEvent(self, e):
        self.win.mouse_up(e)

    def wheelEvent(self, e):
        self.win.mouse_wheel(e)


class _Signals(QObject):
    done = Signal(object)


class Job(QRunnable):
    def __init__(self, work: Callable[[], object]):
        super().__init__()
        self.work = work
        self.signals = _Signals()

    def run(self):
        result = self.work()
        try:
            self.signals.done.emit(result)
        except RuntimeError:  # 창이 이미 닫힘
            pass


class PlannerWindow(QMainWindow):
    def __init__(self, backend: Optional[Backend] = None):
        super().__init__()
        self.setWindowTitle("Wi-Fi 음영지역 분석기")
        self.resize(1400, 900)
        self.setMinimumSize(1050, 680)
        self.backend = backend or Backend()
        self.project = Project()
        self.plan: Optional[QImage] = None
        self.frame: Optional[HeatFrame] = None
        self.selected = None
        self.tool = SELECT
        self.view = View()
        self.undo_stack: List[str] = []
        self.dirty = False
        self.quiet = False
        self.loading = False
        self.measuring = False
        self.dragging = False
        self.panning = False
        self.pan_moved = False
        self.area_drag = False
        self.has_first = False
        self.first = (0.0, 0.0)
        self.cur = (0.0, 0.0)
        self.last_mouse = QPointF()
        self.user_moved_view = False
        self.heat_version = 0
        self.file_path: Optional[str] = None
        self.ask_distance = self._ask_distance
        self.pool = QThreadPool(self)
        self._jobs = set()
        self.debounce = QTimer(self, singleShot=True, interval=90)
        self.debounce.timeout.connect(self.start_heat)

        self.canvas = Canvas(self)
        root = QWidget(objectName="root")
        row = QHBoxLayout(root)
        row.setContentsMargins(0, 0, 0, 0)
        row.setSpacing(0)
        row.addWidget(self._build_left())
        row.addWidget(self.canvas, 1)
        row.addWidget(self._build_right())
        self.setCentralWidget(root)
        self._build_menu()
        self.cursor_info = QLabel("")
        self.statusBar().addPermanentWidget(self.cursor_info)
        self.set_tool(SELECT)
        self.sync_controls()
        self.update_stats()

    # ---- 화면 구성 ----
    def _side(self, width: int):
        area = QScrollArea()
        area.setFixedWidth(width)
        area.setWidgetResizable(True)
        area.setFrameShape(QFrame.Shape.NoFrame)
        area.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        inner = QWidget(objectName="side")
        box = QVBoxLayout(inner)
        box.setContentsMargins(12, 8, 12, 8)
        box.setSpacing(5)
        area.setWidget(inner)
        return area, box

    @staticmethod
    def _head(text: str) -> QLabel:
        label = QLabel(text)
        label.setProperty("head", True)
        label.setFont(app_font(10, True))
        return label

    def _build_left(self):
        area, box = self._side(256)
        box.addWidget(self._head("작업 도구"))
        self.tool_group = QButtonGroup(self)
        self.tool_group.setExclusive(True)
        self.tool_buttons = []
        for i, text in enumerate(TOOL_LABELS):
            b = QToolButton(text=text, checkable=True)
            b.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Fixed)
            b.clicked.connect(lambda _=False, t=i: self.set_tool(t))
            self.tool_group.addButton(b, i)
            self.tool_buttons.append(b)
            box.addWidget(b)
        self.hint = QLabel(wordWrap=True)
        self.hint.setProperty("muted", True)
        self.hint.setMinimumHeight(62)
        self.hint.setAlignment(Qt.AlignmentFlag.AlignTop)
        box.addWidget(self.hint)
        box.addWidget(self._head("AP 제품"))
        self.vendor_box = QComboBox()
        self.vendor_box.addItems(rf.VENDORS)
        self.vendor_box.currentIndexChanged.connect(self.fill_models)
        box.addWidget(self.vendor_box)
        self.model_list = QListWidget()
        self.model_list.setFixedHeight(120)
        self.model_list.currentRowChanged.connect(self.show_model)
        self.model_list.itemClicked.connect(lambda _: self.set_tool(PLACE_AP))
        box.addWidget(self.model_list)
        self.model_info = QLabel(wordWrap=True)
        self.model_info.setMinimumHeight(84)
        self.model_info.setAlignment(Qt.AlignmentFlag.AlignTop)
        box.addWidget(self.model_info)
        box.addWidget(self._head("벽 재질"))
        self.wall_box = QComboBox()
        self.wall_box.addItems([f"{m[0]} (5GHz -{m[1][1]} dB)" for m in rf.MATERIALS])
        self.wall_box.currentIndexChanged.connect(self.wall_material_changed)
        box.addWidget(self.wall_box)
        box.addStretch(1)
        self.fill_models()
        return area

    def _build_right(self):
        area, box = self._side(290)
        box.addWidget(self._head("분석 결과"))
        self.stat_main = QLabel("-")
        self.stat_main.setFont(app_font(18, True))
        box.addWidget(self.stat_main)
        self.stat_sub = QLabel(wordWrap=True)
        self.stat_sub.setMinimumHeight(92)
        self.stat_sub.setAlignment(Qt.AlignmentFlag.AlignTop)
        box.addWidget(self.stat_sub)
        box.addWidget(self._head("표시 설정"))
        form = QFormLayout()
        self.view_box = QComboBox()
        self.view_box.addItems(["예측 신호", "실측 신호", "히트맵 끄기"])
        self.view_box.currentIndexChanged.connect(lambda i: self.view_setting("View", i))
        self.band_box = QComboBox()
        self.band_box.addItems(rf.BAND_NAMES)
        self.band_box.currentIndexChanged.connect(lambda i: self.view_setting("Band", i))
        self.thr_box = QDoubleSpinBox(minimum=-90, maximum=-50, decimals=0, singleStep=1)
        self.thr_box.valueChanged.connect(lambda v: self.view_setting("Threshold", float(v)))
        self.env_box = QComboBox()
        self.env_box.addItems(ENV_NAMES)
        self.env_box.currentIndexChanged.connect(lambda i: self.view_setting("Exponent", ENV_VALUES[i]))
        self.ssid_box = QComboBox()
        self.ssid_box.currentIndexChanged.connect(self.ssid_changed)
        form.addRow("보기", self.view_box)
        form.addRow("주파수 대역", self.band_box)
        form.addRow("음영 기준 (dBm)", self.thr_box)
        form.addRow("공간 유형", self.env_box)
        form.addRow("실측 SSID", self.ssid_box)
        box.addLayout(form)

        self.ap_panel = QWidget()
        panel = QVBoxLayout(self.ap_panel)
        panel.setContentsMargins(0, 6, 0, 0)
        panel.addWidget(self._head("선택한 AP"))
        self.ap_model = QLabel()
        self.ap_model.setProperty("muted", True)
        panel.addWidget(self.ap_model)
        ap_form = QFormLayout()
        self.ap_name = QLineEdit()
        self.ap_height = QDoubleSpinBox(minimum=1, maximum=15, decimals=1, singleStep=0.5)
        self.ap_tx24 = QDoubleSpinBox(minimum=0, maximum=30, decimals=0)
        self.ap_tx5 = QDoubleSpinBox(minimum=0, maximum=30, decimals=0)
        self.ap_tx6 = QDoubleSpinBox(minimum=0, maximum=30, decimals=0)
        ap_form.addRow("이름", self.ap_name)
        ap_form.addRow("설치 높이 (m)", self.ap_height)
        ap_form.addRow("2.4 GHz 출력 (dBm)", self.ap_tx24)
        ap_form.addRow("5 GHz 출력 (dBm)", self.ap_tx5)
        ap_form.addRow("6 GHz 출력 (dBm)", self.ap_tx6)
        panel.addLayout(ap_form)
        delete = QPushButton("AP 삭제")
        delete.clicked.connect(self.delete_selected)
        panel.addWidget(delete)
        self.ap_name.textEdited.connect(self.ap_edited)
        for w in (self.ap_height, self.ap_tx24, self.ap_tx5, self.ap_tx6):
            w.valueChanged.connect(self.ap_edited)
        box.addWidget(self.ap_panel)
        box.addStretch(1)
        return area

    def _build_menu(self):
        def add(menu, text, handler, shortcut=None):
            action = QAction(text, self)
            action.triggered.connect(lambda _=False: handler())
            if shortcut:
                action.setShortcut(QKeySequence(shortcut))
            menu.addAction(action)

        m = self.menuBar().addMenu("파일")
        add(m, "새 프로젝트", lambda: self.confirm_discard() and self.new_project(Project()), "Ctrl+N")
        add(m, "도면 이미지 불러오기...", self.open_plan_dialog, "Ctrl+I")
        add(m, "예제 도면 열기", lambda: self.confirm_discard() and self.new_project(rf.demo_project()))
        m.addSeparator()
        add(m, "프로젝트 열기...", self.open_project_dialog, "Ctrl+O")
        add(m, "프로젝트 저장", lambda: self.save_project_dialog(False), "Ctrl+S")
        add(m, "다른 이름으로 저장...", lambda: self.save_project_dialog(True), "Ctrl+Shift+S")
        m.addSeparator()
        add(m, "분석 결과 이미지로 내보내기...", self.export_png_dialog, "Ctrl+E")
        add(m, "실측 데이터 CSV로 내보내기...", self.export_csv_dialog)
        e = self.menuBar().addMenu("편집")
        add(e, "실행 취소", self.undo, "Ctrl+Z")
        add(e, "분석 범위 해제", self.clear_area)
        add(e, "실측 지점 모두 지우기", self.clear_points)
        v = self.menuBar().addMenu("보기")
        add(v, "도면 전체 보기", self.fit_view, "Ctrl+0")

    # ---- 제품 ----
    def fill_models(self):
        vendor = self.vendor_box.currentText()
        self.models = [m for m in rf.CATALOG if m.vendor == vendor]
        self.model_list.clear()
        self.model_list.addItems([m.name for m in self.models])
        self.model_list.setCurrentRow(0)

    def current_model(self):
        row = self.model_list.currentRow()
        return self.models[row] if 0 <= row < len(self.models) else None

    def show_model(self):
        m = self.current_model()
        if not m:
            return
        g6 = f"{m.g6:g} dBi" if m.has6 else "6GHz 없음"
        self.model_info.setText(f"{m.gen}\n안테나 이득 {m.g24:g} / {m.g5:g} / {g6}\n(2.4 / 5 / 6 GHz)\n"
                                + ("제조사 자료 기준" if m.verified else "참고값: 데이터시트 확인 필요"))
        self.model_info.setStyleSheet(f"color: {MUTED if m.verified else WARN};")

    def set_tool(self, tool: int):
        self.tool = tool
        self.has_first = self.area_drag = False
        self.tool_buttons[tool].setChecked(True)
        self.hint.setText(TOOL_HINTS[tool])
        self.canvas.setCursor(Qt.CursorShape.ArrowCursor if tool == SELECT else Qt.CursorShape.CrossCursor)
        self.canvas.update()

    # ---- 변경 관리 ----
    def snap(self):
        self.undo_stack.append(self.project.to_json())
        del self.undo_stack[:-40]

    def undo(self):
        if not self.undo_stack:
            return
        self.project = Project.from_json(self.undo_stack.pop())
        self.selected = None
        self.has_first = False
        self.sync_controls()
        self.changed()

    def changed(self, mark_dirty: bool = True):
        if mark_dirty:
            self.dirty = True
        self.debounce.start()
        self.canvas.update()

    def view_setting(self, name: str, value):
        if not self.loading:
            setattr(self.project, name, value)
            self.changed(False)

    def ssid_changed(self, index: int):
        if not self.loading:
            self.project.SurveySsid = "" if index <= 0 else self.ssid_box.currentText()
            self.changed(False)

    def start_heat(self):
        self.heat_version += 1
        version = self.heat_version
        copy = self.project.clone()
        if copy.View == 2:
            self.set_frame(None)
            return
        job = Job(lambda: HeatFrame(rf.compute(copy, copy.View)))
        job.setAutoDelete(False)
        self._jobs.add(job)

        def finished(frame):
            self._jobs.discard(job)
            if version == self.heat_version:
                self.set_frame(frame)

        job.signals.done.connect(finished, Qt.ConnectionType.QueuedConnection)
        self.pool.start(job)

    def compute_now(self):
        self.debounce.stop()
        self.heat_version += 1
        self.set_frame(None if self.project.View == 2 else HeatFrame(rf.compute(self.project, self.project.View)))

    def set_frame(self, frame: Optional[HeatFrame]):
        self.frame = frame
        self.update_stats()
        self.canvas.update()

    def prediction_error(self) -> Optional[str]:
        if not self.project.Aps:
            return None
        diff = []
        for pt in self.project.Points:
            measured = rf.point_value(self.project, pt)
            if measured <= rf.NO_SIGNAL:
                continue
            best, _ = rf.best_at(self.project, pt.X, pt.Y, self.project.Band)
            if not math.isinf(best):
                diff.append(abs(best - measured))
        return f"예측과 실측 차이  평균 {sum(diff) / len(diff):.1f} dB" if diff else None

    def update_stats(self):
        p = self.project
        lines = []
        heat = self.frame.heat if self.frame else None
        if heat is None or heat.analyzed == 0:
            self.stat_main.setText("-")
            self.stat_main.setStyleSheet(f"color: {INK};")
            lines.append({0: "AP를 도면에 배치해 주세요.", 1: "실측 지점이 아직 없습니다."}.get(p.View, "히트맵이 꺼져 있습니다."))
        else:
            pct = heat.coverage_pct
            color = "#0c764e" if pct >= 95 else "#a66400" if pct >= 80 else "#be1c30"
            self.stat_main.setText(f"커버리지 {pct:.1f}%")
            self.stat_main.setStyleSheet(f"color: {color};")
            lines.append(f"음영 면적  {heat.dead_m2:.1f} m²")
            lines.append(("측정된 면적  " if p.View == 1 else "분석 면적  ") + f"{heat.analyzed_m2:.1f} m²")
        lines.append(f"AP {len(p.Aps)}대 · 벽 {len(p.Walls)}개 · 실측 {len(p.Points)}곳")
        err = self.prediction_error()
        if err:
            lines.append(err)
        if not p.ScaleSet:
            lines.append("축척이 아직 설정되지 않았습니다.")
        self.stat_sub.setText("\n".join(lines))

    def sync_controls(self):
        p = self.project
        self.loading = True
        self.view_box.setCurrentIndex(p.View)
        self.band_box.setCurrentIndex(p.Band)
        self.thr_box.setValue(max(-90, min(-50, p.Threshold)))
        near = [i for i, e in enumerate(ENV_VALUES) if abs(e - p.Exponent) < 0.01]
        self.env_box.setCurrentIndex(near[0] if near else 1)
        self.loading = False
        self.refresh_ssids()
        self.show_selection()

    def refresh_ssids(self):
        self.loading = True
        self.ssid_box.clear()
        self.ssid_box.addItem("(모든 SSID)")
        self.ssid_box.addItems(sorted({r.Ssid for pt in self.project.Points for r in pt.Readings}))
        self.ssid_box.setCurrentIndex(max(0, self.ssid_box.findText(self.project.SurveySsid)))
        self.loading = False

    def show_selection(self):
        a = self.selected if isinstance(self.selected, ApItem) else None
        self.ap_panel.setVisible(a is not None)
        if isinstance(self.selected, WallItem):
            self.loading = True
            self.wall_box.setCurrentIndex(self.selected.Material)
            self.loading = False
        if a is None:
            return
        self.loading = True
        self.ap_model.setText(f"{a.Vendor} {a.Model}")
        self.ap_name.setText(a.Name)
        self.ap_height.setValue(a.Height)
        self.ap_tx24.setValue(a.Tx24)
        self.ap_tx5.setValue(a.Tx5)
        self.ap_tx6.setValue(a.Tx6)
        self.ap_tx6.setEnabled(a.Has6)
        self.loading = False

    def ap_edited(self, *_):
        a = self.selected
        if self.loading or not isinstance(a, ApItem):
            return
        self.snap()
        a.Name, a.Height = self.ap_name.text(), self.ap_height.value()
        a.Tx24, a.Tx5, a.Tx6 = self.ap_tx24.value(), self.ap_tx5.value(), self.ap_tx6.value()
        self.changed()

    def wall_material_changed(self, index: int):
        w = self.selected
        if not self.loading and isinstance(w, WallItem) and w.Material != index:
            self.snap()
            w.Material = index
            self.changed()

    def delete_selected(self):
        s = self.selected
        if s is None:
            return
        self.snap()
        for items in (self.project.Aps, self.project.Walls, self.project.Points):
            for i, item in enumerate(items):
                if item is s:
                    del items[i]
                    break
        self.selected = None
        self.show_selection()
        self.refresh_ssids()
        self.changed()

    def clear_area(self):
        self.snap()
        self.project.HasArea = False
        self.changed()

    def clear_points(self):
        self.snap()
        self.project.Points.clear()
        self.selected = None
        self.refresh_ssids()
        self.changed()

    def keyPressEvent(self, e):
        if e.key() == Qt.Key.Key_Escape:
            self.has_first = self.area_drag = False
            self.canvas.update()
        elif e.key() == Qt.Key.Key_Delete and self.canvas.hasFocus():
            self.delete_selected()
        else:
            super().keyPressEvent(e)

    # ---- 마우스 ----
    def clamp(self, pos: QPointF):
        x, y = self.view.world(pos)
        return max(0.0, min(self.project.PlanW, x)), max(0.0, min(self.project.PlanH, y))

    def hit_test(self, m: QPointF):
        for a in reversed(self.project.Aps):
            if self._dist(m, self.view.p(a.X, a.Y)) <= 18:
                return a
        for s in reversed(self.project.Points):
            if self._dist(m, self.view.p(s.X, s.Y)) <= 10:
                return s
        for w in reversed(self.project.Walls):
            if self._seg_dist(m, self.view.p(w.X1, w.Y1), self.view.p(w.X2, w.Y2)) <= 7:
                return w
        return None

    @staticmethod
    def _dist(a: QPointF, b: QPointF) -> float:
        return math.hypot(a.x() - b.x(), a.y() - b.y())

    def _seg_dist(self, m: QPointF, a: QPointF, b: QPointF) -> float:
        dx, dy = b.x() - a.x(), b.y() - a.y()
        length = dx * dx + dy * dy
        t = 0.0 if length == 0 else max(0.0, min(1.0, ((m.x() - a.x()) * dx + (m.y() - a.y()) * dy) / length))
        return self._dist(m, QPointF(a.x() + t * dx, a.y() + t * dy))

    def mouse_down(self, e):
        self.canvas.setFocus()
        pos = e.position()
        self.last_mouse = pos
        if e.button() in (Qt.MouseButton.RightButton, Qt.MouseButton.MiddleButton):
            self.panning, self.pan_moved = True, False
            return
        if e.button() != Qt.MouseButton.LeftButton:
            return
        x, y = self.clamp(pos)
        if self.tool == SELECT:
            self.selected = self.hit_test(pos)
            if isinstance(self.selected, ApItem):
                self.snap()
                self.dragging = True
            self.show_selection()
        elif self.tool == PLACE_AP:
            self.place_ap(x, y)
        elif self.tool == WALL:
            if self.has_first and math.hypot(self.first[0] - x, self.first[1] - y) > 0.05:
                self.snap()
                self.project.Walls.append(WallItem(self.first[0], self.first[1], x, y, self.wall_box.currentIndex()))
                self.changed()
            self.has_first, self.first = True, (x, y)
        elif self.tool == AREA:
            self.area_drag, self.first, self.cur = True, (x, y), (x, y)
        elif self.tool == SCALE:
            if not self.has_first:
                self.has_first, self.first = True, (x, y)
            else:
                self.has_first = False
                measured = math.hypot(self.first[0] - x, self.first[1] - y)
                if measured >= 0.01:
                    real = self.ask_distance(measured)
                    if real and real > 0:
                        self.snap()
                        self.apply_scale(measured, real)
        elif self.tool == SURVEY:
            self.start_measure(x, y)
        self.canvas.update()

    def place_ap(self, x: float, y: float):
        m = self.current_model()
        if not m:
            return
        self.snap()
        n = sum(1 for a in self.project.Aps if a.Model == m.name) + 1
        ap = rf.new_ap(m, f"{m.name}-{n}", x, y)
        self.project.Aps.append(ap)
        self.selected = ap
        self.show_selection()
        self.changed()

    def apply_scale(self, measured: float, real: float):
        rf.apply_scale(self.project, measured, real)
        self.fit_view()
        self.statusBar().showMessage(f"축척을 설정했습니다. 도면 크기 {self.project.PlanW:.1f} × {self.project.PlanH:.1f} m")
        self.changed()

    def mouse_move(self, e):
        pos = e.position()
        self.cur = self.clamp(pos)
        if self.panning:
            delta = pos - self.last_mouse
            self.view.pan_x += delta.x()
            self.view.pan_y += delta.y()
            if delta.manhattanLength() > 0:
                self.pan_moved = self.user_moved_view = True
            self.last_mouse = pos
            self.canvas.update()
        elif self.dragging and isinstance(self.selected, ApItem):
            self.selected.X, self.selected.Y = self.cur
            self.changed()
        elif self.has_first or self.area_drag:
            self.canvas.update()
        best, server = rf.best_at(self.project, self.cur[0], self.cur[1], self.project.Band)
        info = f"{self.cur[0]:.1f}, {self.cur[1]:.1f} m"
        if server:
            info += f"   예측 {best:.0f} dBm ({server.Name})"
        self.cursor_info.setText(info)

    def mouse_up(self, e):
        if self.panning:
            self.panning = False
            if e.button() == Qt.MouseButton.RightButton and not self.pan_moved:
                self.has_first = False
                self.canvas.update()
            return
        self.dragging = False
        if self.area_drag:
            self.area_drag = False
            x, y = self.clamp(e.position())
            w, h = abs(x - self.first[0]), abs(y - self.first[1])
            if w > 0.5 and h > 0.5:
                self.snap()
                p = self.project
                p.HasArea, p.AreaX, p.AreaY, p.AreaW, p.AreaH = True, min(x, self.first[0]), min(y, self.first[1]), w, h
                self.changed()
            self.canvas.update()

    def mouse_wheel(self, e):
        factor = 1.15 if e.angleDelta().y() > 0 else 1 / 1.15
        pos = e.position()
        wx, wy = self.view.world(pos)
        self.view.zoom = max(1.0, min(400.0, self.view.zoom * factor))
        self.view.pan_x, self.view.pan_y = pos.x() - wx * self.view.zoom, pos.y() - wy * self.view.zoom
        self.user_moved_view = True
        self.canvas.update()

    def canvas_resized(self):
        if not self.user_moved_view:
            self.view.fit(self.project, self.canvas.width(), self.canvas.height())

    def fit_view(self):
        self.user_moved_view = False
        self.view.fit(self.project, self.canvas.width(), self.canvas.height())
        self.canvas.update()

    def paint_canvas(self, widget: QWidget):
        p = QPainter(widget)
        p.fillRect(widget.rect(), QColor("#e2e6ec"))
        draw_scene(p, self.project, self.plan, self.frame, self.view, self.selected)
        pen = QPen(QColor(ACCENT), 2, Qt.PenStyle.DashLine)
        if self.has_first and self.tool in (WALL, SCALE):
            p.setPen(pen)
            a, b = self.view.p(*self.first), self.view.p(*self.cur)
            p.drawLine(a, b)
            p.setPen(QColor(INK))
            p.drawText(b + QPointF(10, 18), f"{math.hypot(self.first[0] - self.cur[0], self.first[1] - self.cur[1]):.1f} m")
        if self.area_drag:
            p.setPen(pen)
            p.setBrush(Qt.BrushStyle.NoBrush)
            a = self.view.p(min(self.first[0], self.cur[0]), min(self.first[1], self.cur[1]))
            p.drawRect(QRectF(a.x(), a.y(), abs(self.cur[0] - self.first[0]) * self.view.zoom,
                              abs(self.cur[1] - self.first[1]) * self.view.zoom))
        if self.project.View != 2:
            draw_legend(p, self.project, 12, widget.height() - 70)
        p.end()

    # ---- 현장 실측 ----
    def start_measure(self, x: float, y: float):
        if self.measuring:
            return
        self.measuring = True
        self.statusBar().showMessage("측정 중입니다. 그 자리에서 잠시만 기다려 주세요...")
        self.canvas.setCursor(Qt.CursorShape.WaitCursor)
        job = Job(self._measure_safe)
        job.setAutoDelete(False)
        self._jobs.add(job)

        def finished(result):
            self._jobs.discard(job)
            self.finish_measure(x, y, result)

        job.signals.done.connect(finished, Qt.ConnectionType.QueuedConnection)
        self.pool.start(job)

    def _measure_safe(self) -> MeasureResult:
        try:
            return self.backend.measure()
        except Exception as ex:  # 측정 실패가 프로그램 종료로 이어지지 않게 한다
            return MeasureResult(error=str(ex))

    def finish_measure(self, x: float, y: float, m: MeasureResult):
        self.measuring = False
        self.canvas.setCursor(Qt.CursorShape.ArrowCursor if self.tool == SELECT else Qt.CursorShape.CrossCursor)
        if m.error:
            self.statusBar().showMessage(f"측정 실패: {m.error}")
            return
        self.snap()
        p = self.project
        first = not p.Points
        p.Points.append(SurveyPoint(x, y, f"{datetime.now():%Y-%m-%d %H:%M:%S}", m.readings))
        if first:
            if m.connected_ssid:
                p.SurveySsid = m.connected_ssid
            if m.connected_band >= 0:
                p.Band = m.connected_band
            p.View = 1
            self.sync_controls()
        self.refresh_ssids()
        note = f", {m.connected_ssid} {m.connected_rssi} dBm" if m.connected_ssid else ""
        self.statusBar().showMessage(f"측정 완료: AP {len(m.readings)}개 감지{note}")
        self.changed()

    # ---- 파일 ----
    def confirm_discard(self) -> bool:
        if not self.dirty or self.quiet:
            return True
        answer = QMessageBox.warning(self, self.windowTitle(), "저장하지 않은 변경 내용이 사라집니다. 계속할까요?",
                                     QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No)
        return answer == QMessageBox.StandardButton.Yes

    def closeEvent(self, e):
        if self.confirm_discard():
            self.pool.waitForDone(6000)
            e.accept()
        else:
            e.ignore()

    def new_project(self, project: Project, plan: Optional[QImage] = None):
        self.project, self.plan = project, plan
        self.selected = None
        self.undo_stack.clear()
        self.dirty = self.has_first = False
        self.file_path = None
        self.statusBar().clearMessage()
        self.fit_view()
        self.sync_controls()
        self.changed(False)
        return True

    def _warn(self, text: str, ex: Exception):
        QMessageBox.warning(self, self.windowTitle(), f"{text}\n{ex}")

    def open_plan_dialog(self):
        path, _ = QFileDialog.getOpenFileName(self, "도면 이미지 선택", "", "이미지 파일 (*.png *.jpg *.jpeg *.bmp *.tif *.tiff)")
        if not path:
            return
        try:
            self.load_plan(path)
            self.set_tool(SCALE)
            self.statusBar().showMessage("도면을 불러왔습니다. 길이를 아는 두 점을 눌러 축척을 설정해 주세요.")
        except OSError as ex:
            self._warn("도면을 열 수 없습니다.", ex)

    def load_plan(self, path: str):
        image = QImage(path)
        if image.isNull():
            raise OSError("지원하지 않는 이미지 형식입니다.")
        if max(image.width(), image.height()) > 5000:
            image = image.scaled(5000, 5000, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation)
        self.undo_stack.clear()
        self.plan = image
        p = self.project
        p.PixelsPerMeter, p.ScaleSet, p.HasArea = 20.0, False, False
        p.PlanW, p.PlanH = image.width() / p.PixelsPerMeter, image.height() / p.PixelsPerMeter
        self.fit_view()
        self.changed()

    def open_project_dialog(self):
        if not self.confirm_discard():
            return
        path, _ = QFileDialog.getOpenFileName(self, "프로젝트 열기", "", "음영 분석 프로젝트 (*.wifiplan)")
        if path:
            try:
                self.load_project(path)
            except (OSError, ValueError, KeyError, TypeError) as ex:
                self._warn("프로젝트를 열 수 없습니다.", ex)

    def load_project(self, path: str):
        with open(path, "r", encoding="utf-8-sig") as f:
            project = Project.from_json(f.read())
        plan = None
        if project.PlanImageBase64:
            plan = QImage.fromData(base64.b64decode(project.PlanImageBase64))
            if plan.isNull():
                raise ValueError("프로젝트의 도면 이미지를 읽을 수 없습니다.")
        project.PlanImageBase64 = None
        self.new_project(project, plan)
        self.file_path = path

    def save_project_dialog(self, save_as: bool):
        path = self.file_path
        if save_as or not path:
            path, _ = QFileDialog.getSaveFileName(self, "프로젝트 저장", "음영분석.wifiplan", "음영 분석 프로젝트 (*.wifiplan)")
        if path:
            try:
                self.save_project(path)
                self.statusBar().showMessage(f"저장했습니다: {path}")
            except OSError as ex:
                self._warn("저장할 수 없습니다.", ex)

    def save_project(self, path: str):
        if self.plan is not None:
            buffer = QBuffer()
            buffer.open(QIODevice.OpenModeFlag.WriteOnly)
            self.plan.save(buffer, "PNG")
            self.project.PlanImageBase64 = base64.b64encode(bytes(buffer.data())).decode("ascii")
        try:
            with open(path, "w", encoding="utf-8") as f:
                f.write(self.project.to_json())
        finally:
            self.project.PlanImageBase64 = None
        self.file_path, self.dirty = path, False

    def export_png_dialog(self):
        path, _ = QFileDialog.getSaveFileName(self, "결과 이미지 저장", "음영분석결과.png", "PNG 이미지 (*.png)")
        if path:
            try:
                self.export_png(path)
                self.statusBar().showMessage(f"내보냈습니다: {path}")
            except OSError as ex:
                self._warn("내보낼 수 없습니다.", ex)

    def export_png(self, path: str):
        self.compute_now()
        p = self.project
        width, top = 2000, 90
        image = QImage(width, int(width * p.PlanH / p.PlanW) + top + 16, QImage.Format.Format_ARGB32_Premultiplied)
        image.fill(Qt.GlobalColor.white)
        painter = QPainter(image)
        heat = self.frame.heat if self.frame else None
        painter.setFont(app_font(16, True))
        painter.setPen(QColor(INK))
        painter.drawText(QRectF(14, 8, 1400, 36), Qt.AlignmentFlag.AlignVCenter,
                         f"Wi-Fi 음영 분석 ({'실측' if p.View == 1 else '예측'} · {rf.BAND_NAMES[p.Band]})")
        line = "분석 데이터 없음"
        if heat and heat.analyzed:
            line = (f"커버리지 {heat.coverage_pct:.1f}%  ·  음영 {heat.dead_m2:.1f} m²  ·  기준 {p.Threshold:.0f} dBm  ·  "
                    f"AP {len(p.Aps)}대  ·  실측 {len(p.Points)}곳")
        painter.setFont(app_font(11))
        painter.setPen(QColor(MUTED))
        painter.drawText(QRectF(14, 48, 1400, 30), Qt.AlignmentFlag.AlignVCenter, line)
        view = View()
        view.zoom, view.pan_x, view.pan_y = (width - 20.0) / p.PlanW, 10, top
        draw_scene(painter, p, self.plan, self.frame, view, None)
        draw_legend(painter, p, width - 312, 14)
        painter.end()
        if not image.save(path, "PNG"):
            raise OSError("이미지 파일을 쓸 수 없습니다.")

    def export_csv_dialog(self):
        path, _ = QFileDialog.getSaveFileName(self, "실측 데이터 저장", "실측데이터.csv", "CSV 파일 (*.csv)")
        if path:
            try:
                self.export_csv(path)
                self.statusBar().showMessage(f"내보냈습니다: {path}")
            except OSError as ex:
                self._warn("내보낼 수 없습니다.", ex)

    def export_csv(self, path: str):
        with open(path, "w", newline="", encoding="utf-8-sig") as f:
            w = csv.writer(f)
            w.writerow(["지점", "측정시각", "X(m)", "Y(m)", "SSID", "BSSID", "대역", "신호(dBm)"])
            for n, s in enumerate(self.project.Points, 1):
                for r in sorted(s.Readings, key=lambda a: a.Rssi, reverse=True):
                    w.writerow([n, s.Time, f"{s.X:.2f}", f"{s.Y:.2f}", csv_text(r.Ssid), r.Bssid, rf.BAND_NAMES[r.Band], r.Rssi])

    def _ask_distance(self, measured: float) -> Optional[float]:
        value, ok = QInputDialog.getDouble(self, "축척 설정", "두 점 사이의 실제 거리 (m)", measured, 0.1, 10000, 2)
        return value if ok else None


def main() -> int:
    app = create_app(sys.argv)
    window = PlannerWindow()
    window.show()
    QTimer.singleShot(0, lambda: (window.new_project(rf.demo_project()),
                                  window.statusBar().showMessage("예제 도면입니다. 실제 도면은 파일 > 도면 이미지 불러오기에서 열 수 있습니다.")))
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
