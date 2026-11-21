"""두 프로그램이 함께 쓰는 화면 요소."""
from __future__ import annotations

import os
import sys
from typing import Tuple

from PySide6.QtGui import QColor, QFont, QFontDatabase
from PySide6.QtWidgets import QApplication

BG = "#edf2f8"
INK = "#132238"
MUTED = "#626b78"
BORDER = "#dee2e8"
ACCENT = "#0757a8"
WARN = "#a65a00"

STYLE = f"""
QMainWindow, QWidget#root, QScrollArea, QWidget#side {{ background: {BG}; }}
QLabel {{ color: {INK}; background: transparent; }}
QLabel[muted="true"] {{ color: {MUTED}; }}
QLabel[head="true"] {{ font-weight: 700; padding-top: 8px; }}
QFrame#card {{ background: white; border: 1px solid {BORDER}; border-radius: 12px; }}
QToolButton {{ background: white; color: {INK}; border: 1px solid {BORDER}; border-radius: 8px; padding: 6px; }}
QToolButton:checked {{ background: #e3effb; border: 1px solid {ACCENT}; color: {ACCENT}; font-weight: 700; }}
QPushButton {{ background: white; color: {INK}; border: 1px solid {BORDER}; border-radius: 8px; padding: 7px 14px; }}
QPushButton:hover, QToolButton:hover {{ border-color: {ACCENT}; }}
QTableWidget {{ background: white; color: {INK}; border: 1px solid {BORDER}; gridline-color: transparent; }}
QHeaderView::section {{ background: white; color: {INK}; border: none; border-bottom: 1px solid {BORDER}; padding: 5px; }}
QListWidget, QComboBox, QLineEdit, QDoubleSpinBox {{ background: white; color: {INK}; }}
"""


def level_of(rssi: float) -> Tuple[str, QColor, int]:
    if rssi >= -50:
        return "매우 좋음", QColor(14, 128, 88), 5
    if rssi >= -60:
        return "좋음", QColor(46, 125, 50), 4
    if rssi >= -70:
        return "보통", QColor(176, 112, 0), 3
    if rssi >= -80:
        return "약함", QColor(205, 84, 24), 2
    return "매우 약함", QColor(196, 43, 48), 1


def csv_text(value: str) -> str:
    """SSID가 스프레드시트 수식으로 해석되지 않도록 막는다."""
    if value[:1] in ("=", "+", "-", "@"):
        value = "'" + value
    return value


def app_font(point: float = 10, bold: bool = False) -> QFont:
    font = QFont()
    font.setFamilies(["Noto Sans CJK KR", "Noto Sans KR", "NanumGothic", "Malgun Gothic", "DejaVu Sans", "Sans Serif"])
    font.setPointSizeF(point)
    font.setBold(bold)
    return font


def create_app(argv) -> QApplication:
    app = QApplication.instance() or QApplication(argv)
    if sys.platform == "win32":  # 개발용 시험 환경
        path = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", "malgun.ttf")
        if os.path.exists(path):
            QFontDatabase.addApplicationFont(path)
    app.setFont(app_font())
    app.setStyleSheet(STYLE)
    return app
