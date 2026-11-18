"""프로젝트 데이터, AP 제품 목록, 신호 계산.

프로젝트 파일(.wifiplan)은 Windows 버전과 같은 JSON 형식을 사용한다.
"""
from __future__ import annotations

import json
import math
from dataclasses import asdict, dataclass, field, fields
from typing import List, Optional, Tuple

import numpy as np

FREQ_MHZ = (2437.0, 5500.0, 6500.0)
BAND_NAMES = ("2.4 GHz", "5 GHz", "6 GHz")
CLIENT_HEIGHT = 1.0
IDW_RADIUS = 8.0
NO_SIGNAL = -100.0


@dataclass
class ApItem:
    Vendor: str = ""
    Model: str = ""
    Name: str = ""
    X: float = 0.0
    Y: float = 0.0
    Height: float = 3.0
    Tx24: float = 15.0
    Tx5: float = 18.0
    Tx6: float = 18.0
    Gain24: float = 4.0
    Gain5: float = 5.0
    Gain6: float = 5.0
    Has6: bool = False


@dataclass
class WallItem:
    X1: float = 0.0
    Y1: float = 0.0
    X2: float = 0.0
    Y2: float = 0.0
    Material: int = 0


@dataclass
class Reading:
    Ssid: str = ""
    Bssid: str = ""
    Band: int = 0
    Rssi: int = 0


@dataclass
class SurveyPoint:
    X: float = 0.0
    Y: float = 0.0
    Time: str = ""
    Readings: List[Reading] = field(default_factory=list)


@dataclass
class Project:
    PlanImageBase64: Optional[str] = None
    PixelsPerMeter: float = 20.0
    PlanW: float = 40.0
    PlanH: float = 25.0
    ScaleSet: bool = True
    HasArea: bool = False
    AreaX: float = 0.0
    AreaY: float = 0.0
    AreaW: float = 0.0
    AreaH: float = 0.0
    Threshold: float = -67.0
    Exponent: float = 2.6
    Band: int = 1
    View: int = 0
    SurveySsid: str = ""
    Aps: List[ApItem] = field(default_factory=list)
    Walls: List[WallItem] = field(default_factory=list)
    Points: List[SurveyPoint] = field(default_factory=list)

    def to_json(self) -> str:
        return json.dumps(asdict(self), ensure_ascii=False)

    @staticmethod
    def from_json(text: str) -> "Project":
        data = json.loads(text)
        p = _build(Project, data)
        p.Aps = [_build(ApItem, a) for a in data.get("Aps") or []]
        p.Walls = [_build(WallItem, w) for w in data.get("Walls") or []]
        p.Points = []
        for raw in data.get("Points") or []:
            pt = _build(SurveyPoint, raw)
            pt.Readings = [_build(Reading, r) for r in raw.get("Readings") or []]
            p.Points.append(pt)
        return p

    def clone(self) -> "Project":
        return Project.from_json(self.to_json())


def _build(cls, data: dict):
    names = {f.name for f in fields(cls)}
    return cls(**{k: v for k, v in data.items() if k in names})


# ---- 제품 목록 ----
@dataclass(frozen=True)
class ApModel:
    vendor: str
    name: str
    gen: str
    g24: float
    g5: float
    g6: Optional[float]
    verified: bool  # True: 제조사 자료로 안테나 이득을 확인함

    @property
    def has6(self) -> bool:
        return self.g6 is not None


CATALOG: Tuple[ApModel, ...] = (
    ApModel("Cisco", "CW9176I", "Wi-Fi 7", 4, 5, 5, False),
    ApModel("Cisco", "CW9166I", "Wi-Fi 6E", 3, 5, 4, False),
    ApModel("Cisco", "C9136I", "Wi-Fi 6E", 4, 6, 6, False),
    ApModel("Cisco", "C9130AXI", "Wi-Fi 6", 4, 5, None, False),
    ApModel("Cisco", "C9120AXI", "Wi-Fi 6", 4, 5, None, False),
    ApModel("HPE Aruba", "AP-735", "Wi-Fi 7", 4, 6, 6, False),
    ApModel("HPE Aruba", "AP-655", "Wi-Fi 6E", 4.8, 5.3, 5.4, False),
    ApModel("HPE Aruba", "AP-635", "Wi-Fi 6E", 4.6, 7.0, 6.3, True),
    ApModel("HPE Aruba", "AP-515", "Wi-Fi 6", 4.2, 7.5, None, True),
    ApModel("Juniper Mist", "AP47", "Wi-Fi 7", 4, 6, 6, True),
    ApModel("Juniper Mist", "AP45", "Wi-Fi 6E", 3, 5, 5, True),
    ApModel("Juniper Mist", "AP34", "Wi-Fi 6E", 4, 6, 6, True),
    ApModel("Juniper Mist", "AP43", "Wi-Fi 6", 4, 6, None, True),
    ApModel("Juniper Mist", "AP32", "Wi-Fi 6", 5, 6, None, True),
    ApModel("기타", "일반 AP (직접 입력)", "-", 4, 5, 5, False),
)
VENDORS = tuple(dict.fromkeys(m.vendor for m in CATALOG))
VENDOR_COLORS = {"Cisco": "#005caa", "HPE Aruba": "#d66010", "Juniper Mist": "#008078"}
VENDOR_MARKS = {"Cisco": "C", "HPE Aruba": "A", "Juniper Mist": "M"}


def new_ap(model: ApModel, name: str, x: float, y: float) -> ApItem:
    return ApItem(Vendor=model.vendor, Model=model.name, Name=name, X=x, Y=y,
                  Gain24=model.g24, Gain5=model.g5, Gain6=model.g6 or 0.0, Has6=model.has6)


# 이름, 대역별(2.4/5/6 GHz) 통과 손실 dB, 표시 색. 일반적인 설계 추정치.
MATERIALS = (
    ("석고보드", (3, 4, 5), "#78829a"),
    ("유리", (2, 4, 5), "#2896c8"),
    ("목재 문", (3, 4, 5), "#966432"),
    ("벽돌", (6, 10, 12), "#aa5032"),
    ("콘크리트", (12, 20, 25), "#323741"),
    ("금속 / 엘리베이터", (25, 30, 35), "#78288c"),
)


# ---- 전파 계산 ----
def _cross(ax, ay, bx, by, cx, cy):
    return (bx - ax) * (cy - ay) - (by - ay) * (cx - ax)


def _crosses(ax: float, ay: float, xs: np.ndarray, ys: np.ndarray, w: WallItem) -> np.ndarray:
    d1 = _cross(w.X1, w.Y1, w.X2, w.Y2, ax, ay)
    d2 = _cross(w.X1, w.Y1, w.X2, w.Y2, xs, ys)
    d3 = _cross(ax, ay, xs, ys, w.X1, w.Y1)
    d4 = _cross(ax, ay, xs, ys, w.X2, w.Y2)
    # 벽 위의 지점은 벽을 통과한 것으로 보지 않는다
    return ((d1 > 0) != (d2 > 0)) & ((d3 > 0) != (d4 > 0)) & (np.abs(d2) >= 1e-9)


def signal_from_ap(p: Project, ap: ApItem, xs, ys, band: int) -> np.ndarray:
    xs = np.asarray(xs, dtype=float)
    ys = np.asarray(ys, dtype=float)
    if band == 2 and not ap.Has6:
        return np.full(xs.shape, -np.inf)
    tx = (ap.Tx24, ap.Tx5, ap.Tx6)[band]
    gain = (ap.Gain24, ap.Gain5, ap.Gain6)[band]
    dz = ap.Height - CLIENT_HEIGHT
    dist = np.maximum(1.0, np.sqrt((ap.X - xs) ** 2 + (ap.Y - ys) ** 2 + dz * dz))
    loss = 20 * math.log10(FREQ_MHZ[band]) - 27.55 + 10 * p.Exponent * np.log10(dist)
    for w in p.Walls:
        loss = loss + _crosses(ap.X, ap.Y, xs, ys, w) * float(MATERIALS[w.Material][1][band])
    return tx + gain - loss


def best_at(p: Project, x: float, y: float, band: int) -> Tuple[float, Optional[ApItem]]:
    best, server = -math.inf, None
    for ap in p.Aps:
        v = float(signal_from_ap(p, ap, np.array([x]), np.array([y]), band)[0])
        if v > best:
            best, server = v, ap
    return best, server


def point_value(p: Project, pt: SurveyPoint) -> float:
    values = [r.Rssi for r in pt.Readings
              if r.Band == p.Band and (not p.SurveySsid or r.Ssid == p.SurveySsid)]
    return float(max(values)) if values else NO_SIGNAL


def idw(points: np.ndarray, xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
    """points: [[x, y, value], ...]. 측정 반경 밖은 NaN."""
    total = np.zeros(xs.shape)
    weight = np.zeros(xs.shape)
    exact = np.full(xs.shape, np.nan)
    for px, py, value in points:
        d2 = (px - xs) ** 2 + (py - ys) ** 2
        near = d2 <= IDW_RADIUS * IDW_RADIUS
        hit = d2 < 1e-4
        exact = np.where(hit & np.isnan(exact), value, exact)
        use = near & ~hit
        w = np.divide(1.0, d2, out=np.zeros(xs.shape), where=use)
        total += w * value
        weight += w
    result = np.divide(total, weight, out=np.full(xs.shape, np.nan), where=weight > 0)
    return np.where(np.isnan(exact), result, exact)


@dataclass
class HeatResult:
    cols: int
    rows: int
    cell: float
    values: np.ndarray
    threshold: float

    @property
    def analyzed(self) -> int:
        return int(np.count_nonzero(~np.isnan(self.values)))

    @property
    def dead(self) -> int:
        return int(np.count_nonzero(self.values < self.threshold))

    @property
    def covered(self) -> int:
        return self.analyzed - self.dead

    @property
    def coverage_pct(self) -> float:
        return 100.0 * self.covered / self.analyzed if self.analyzed else 0.0

    @property
    def dead_m2(self) -> float:
        return self.dead * self.cell * self.cell

    @property
    def analyzed_m2(self) -> float:
        return self.analyzed * self.cell * self.cell

    def rgba(self) -> np.ndarray:
        return colorize(self.values, self.threshold)

    def dead_runs(self) -> List[Tuple[float, float, float, float]]:
        """음영 셀을 행 단위 사각형(x, y, w, h, 미터)으로 묶는다."""
        runs = []
        dead = self.values < self.threshold
        for j in np.flatnonzero(dead.any(axis=1)):
            edges = np.diff(np.concatenate(([0], dead[j].astype(np.int8), [0])))
            for a, b in zip(np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)):
                runs.append((a * self.cell, j * self.cell, (b - a) * self.cell, self.cell))
        return runs


def compute(p: Project, mode: int) -> HeatResult:
    """mode 0: 예측, 1: 실측 보간"""
    cell = max(0.05, max(p.PlanW, p.PlanH) / 300.0)
    cols = max(1, math.ceil(p.PlanW / cell))
    rows = max(1, math.ceil(p.PlanH / cell))
    xs, ys = np.meshgrid((np.arange(cols) + 0.5) * cell, (np.arange(rows) + 0.5) * cell)
    values = np.full(xs.shape, np.nan)
    if mode == 0 and p.Aps:
        values = np.maximum.reduce([signal_from_ap(p, ap, xs, ys, p.Band) for ap in p.Aps])
        values = np.maximum(NO_SIGNAL, values)
    elif mode == 1 and p.Points:
        pts = np.array([[a.X, a.Y, point_value(p, a)] for a in p.Points], dtype=float)
        values = idw(pts, xs, ys)
    if p.HasArea:
        inside = (xs >= p.AreaX) & (xs <= p.AreaX + p.AreaW) & (ys >= p.AreaY) & (ys <= p.AreaY + p.AreaH)
        values = np.where(inside, values, np.nan)
    return HeatResult(cols, rows, cell, values, p.Threshold)


def colorize(values: np.ndarray, thr: float) -> np.ndarray:
    a = np.array([245, 196, 40], dtype=float)
    b = np.array([132, 192, 58], dtype=float)
    c = np.array([12, 128, 84], dtype=float)
    t = np.clip((np.nan_to_num(values, nan=thr) - thr) / max(1.0, -45 - thr), 0, 1)[..., None]
    low = a + (b - a) * (t * 2)
    high = b + (c - b) * ((t - 0.5) * 2)
    rgb = np.where(t < 0.5, low, high)
    alpha = np.full(values.shape, 145.0)
    dead = values < thr
    rgb[dead] = (190, 28, 48)
    alpha[dead] = 165
    alpha[np.isnan(values)] = 0
    return np.dstack([rgb, alpha]).astype(np.uint8)


def apply_scale(p: Project, measured: float, real: float) -> None:
    f = real / measured
    for a in p.Aps:
        a.X *= f
        a.Y *= f
    for w in p.Walls:
        w.X1 *= f
        w.Y1 *= f
        w.X2 *= f
        w.Y2 *= f
    for s in p.Points:
        s.X *= f
        s.Y *= f
    p.AreaX *= f
    p.AreaY *= f
    p.AreaW *= f
    p.AreaH *= f
    p.PlanW *= f
    p.PlanH *= f
    p.PixelsPerMeter /= f
    p.ScaleSet = True


def demo_project() -> Project:
    p = Project(PlanW=48, PlanH=30, HasArea=True, AreaX=2, AreaY=2, AreaW=44, AreaH=26)

    def box(x, y, w, h, m):
        p.Walls.extend([
            WallItem(x, y, x + w, y, m), WallItem(x + w, y, x + w, y + h, m),
            WallItem(x + w, y + h, x, y + h, m), WallItem(x, y + h, x, y, m),
        ])

    box(2, 2, 44, 26, 4)   # 외벽
    box(20, 11, 8, 8, 4)   # 코어
    box(2, 2, 12, 9, 0)    # 회의실
    box(14, 2, 10, 9, 1)   # 유리 회의실
    box(34, 2, 12, 12, 3)  # 서버실
    box(2, 19, 14, 9, 0)   # 사무실
    by_name = {m.name: m for m in CATALOG}
    p.Aps.append(new_ap(by_name["CW9166I"], "1F-Cisco-01", 12, 15))
    p.Aps.append(new_ap(by_name["AP-635"], "1F-Aruba-01", 31, 8))
    p.Aps.append(new_ap(by_name["AP45"], "1F-Mist-01", 33, 23))
    return p
