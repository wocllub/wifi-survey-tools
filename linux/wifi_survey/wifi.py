"""Linux Wi-Fi 측정. NetworkManager(nmcli)와 iw 명령의 출력을 읽는다.

연결된 AP의 신호는 iw가 알려 주는 실제 dBm 값을 쓴다. 주변 AP의 신호는 관리자 권한 없이
읽을 수 있는 nmcli의 백분율을 dBm으로 환산한 추정값이다.
"""
from __future__ import annotations

import os
import re
import shutil
import subprocess
from dataclasses import dataclass, field
from typing import Callable, List, Optional

from .rf import Reading

Runner = Callable[[List[str], float], Optional[str]]


def default_runner(args: List[str], timeout: float) -> Optional[str]:
    """명령을 실행해 표준 출력을 돌려준다. 실패하면 None."""
    if shutil.which(args[0]) is None:
        return None
    env = dict(os.environ, LC_ALL="C", LANG="C")
    try:
        done = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                              timeout=timeout, env=env, check=False)
    except (OSError, subprocess.TimeoutExpired):
        return None
    if done.returncode != 0:
        return None
    return done.stdout.decode("utf-8", "replace")


@dataclass
class Bss:
    ssid: str
    bssid: str
    channel: int
    band: int
    quality: int
    rssi: int
    security: str
    in_use: bool = False


@dataclass
class Link:
    has_adapter: bool = False
    connected: bool = False
    iface: str = ""
    ssid: str = ""
    bssid: str = ""
    rssi: int = 0
    rssi_exact: bool = False
    quality: int = 0
    channel: int = 0
    band: int = -1
    rx_mbps: float = 0.0
    tx_mbps: float = 0.0
    phy: str = ""
    security: str = ""
    error: str = ""


@dataclass
class MeasureResult:
    readings: List[Reading] = field(default_factory=list)
    connected_ssid: str = ""
    connected_band: int = -1
    connected_rssi: int = 0
    error: str = ""


def band_of(mhz: int) -> int:
    return 0 if mhz < 3000 else 1 if mhz < 5925 else 2


def channel_of(mhz: int) -> int:
    if mhz == 2484:
        return 14
    if mhz < 3000:
        return (mhz - 2407) // 5
    if mhz < 5925:
        return (mhz - 5000) // 5
    return (mhz - 5950) // 5


def percent_to_dbm(pct: int) -> int:
    """NetworkManager의 백분율 신호를 dBm으로 환산한 추정값."""
    return max(-100, min(-20, pct - 120))


def dbm_to_quality(dbm: int) -> int:
    return max(0, min(100, 2 * (dbm + 100)))


def split_terse(line: str) -> List[str]:
    """nmcli -t 출력을 나눈다. ':'와 '\\'는 역슬래시로 이스케이프되어 있다."""
    parts, cur, i = [], [], 0
    while i < len(line):
        ch = line[i]
        if ch == "\\" and i + 1 < len(line):
            cur.append(line[i + 1])
            i += 2
            continue
        if ch == ":":
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
        i += 1
    parts.append("".join(cur))
    return parts


def parse_wifi_list(text: str) -> List[Bss]:
    found = {}
    for line in text.splitlines():
        parts = split_terse(line)
        if len(parts) < 7:
            continue
        in_use, ssid, bssid, chan, freq, signal, security = parts[:7]
        m = re.match(r"\d+", freq)
        if not m or not signal.isdigit():
            continue
        mhz = int(m.group())
        quality = int(signal)
        bssid = bssid.upper()
        found[bssid] = Bss(
            ssid=ssid or "(숨겨진 네트워크)", bssid=bssid,
            channel=int(chan) if chan.isdigit() else channel_of(mhz), band=band_of(mhz),
            quality=quality, rssi=percent_to_dbm(quality),
            security=security.strip() or "개방", in_use=in_use.strip() == "*")
    return sorted(found.values(), key=lambda b: b.rssi, reverse=True)


def phy_name(bitrate_line: str) -> str:
    for token, name in (("EHT-MCS", "Wi-Fi 7"), ("HE-MCS", "Wi-Fi 6"), ("VHT-MCS", "Wi-Fi 5"), ("MCS", "Wi-Fi 4")):
        if token in bitrate_line:
            return name
    return ""


def parse_iw_link(text: str, link: Link) -> bool:
    m = re.search(r"Connected to ([0-9a-fA-F:]{17})", text)
    if not m:
        return False
    link.bssid = m.group(1).upper()
    ssid = re.search(r"^\s*SSID: (.*)$", text, re.M)
    if ssid:
        link.ssid = ssid.group(1).strip()
    freq = re.search(r"^\s*freq: (\d+)", text, re.M)
    if freq:
        mhz = int(freq.group(1))
        link.band, link.channel = band_of(mhz), channel_of(mhz)
    signal = re.search(r"^\s*signal: (-?\d+) dBm", text, re.M)
    if signal:
        link.rssi, link.rssi_exact = int(signal.group(1)), True
        link.quality = dbm_to_quality(link.rssi)
    rx = re.search(r"^\s*rx bitrate: ([\d.]+) MBit/s(.*)$", text, re.M)
    tx = re.search(r"^\s*tx bitrate: ([\d.]+) MBit/s(.*)$", text, re.M)
    if rx:
        link.rx_mbps = float(rx.group(1))
        link.phy = phy_name(rx.group(2))
    if tx:
        link.tx_mbps = float(tx.group(1))
        link.phy = link.phy or phy_name(tx.group(2))
    return True


def parse_ping(text: str) -> Optional[float]:
    m = re.search(r"time[=<]([\d.]+) ?ms", text)
    return float(m.group(1)) if m else None


class Backend:
    def __init__(self, runner: Runner = default_runner):
        self.run = runner

    def interface(self) -> str:
        out = self.run(["nmcli", "-t", "-f", "DEVICE,TYPE,STATE", "device"], 5)
        if out:
            rows = [split_terse(x) for x in out.splitlines()]
            wifi = [r for r in rows if len(r) >= 3 and r[1] == "wifi"]
            wifi.sort(key=lambda r: not r[2].startswith("connected"))
            if wifi:
                return wifi[0][0]
        out = self.run(["iw", "dev"], 5)
        m = re.search(r"Interface (\S+)", out or "")
        return m.group(1) if m else ""

    def nearby(self, iface: str, rescan: bool = False) -> Optional[List[Bss]]:
        base = ["nmcli", "-t", "-f", "IN-USE,SSID,BSSID,CHAN,FREQ,SIGNAL,SECURITY", "device", "wifi", "list"]
        if iface:
            base += ["ifname", iface]
        out = self.run(base + ["--rescan", "yes" if rescan else "no"], 25 if rescan else 8)
        if out is None and rescan:  # 스캔 요청이 너무 잦으면 거절되므로 최근 결과를 쓴다
            out = self.run(base + ["--rescan", "no"], 8)
        return None if out is None else parse_wifi_list(out)

    def link(self, iface: str, nearby: Optional[List[Bss]] = None) -> Link:
        link = Link(iface=iface, has_adapter=bool(iface))
        if not iface:
            link.error = "Wi-Fi 어댑터를 찾을 수 없습니다. nmcli 또는 iw가 설치되어 있는지 확인해 주세요."
            return link
        out = self.run(["iw", "dev", iface, "link"], 5)
        link.connected = bool(out) and parse_iw_link(out, link)
        mine = next((b for b in nearby or [] if (link.connected and b.bssid == link.bssid) or
                     (not link.connected and b.in_use)), None)
        if mine:
            link.connected = True
            link.ssid = link.ssid or mine.ssid
            link.bssid = link.bssid or mine.bssid
            link.security = mine.security
            if link.band < 0:
                link.band, link.channel = mine.band, mine.channel
            if not link.rssi_exact:
                link.rssi, link.quality = mine.rssi, mine.quality
        return link

    def gateway(self, iface: str) -> str:
        out = self.run(["ip", "-4", "route", "show", "default", "dev", iface], 3) if iface else None
        m = re.search(r"default via (\S+)", out or "")
        return m.group(1) if m else ""

    def ping(self, host: str) -> Optional[float]:
        out = self.run(["ping", "-n", "-c", "1", "-W", "1", host], 3)
        return parse_ping(out) if out else None

    def measure(self) -> MeasureResult:
        result = MeasureResult()
        iface = self.interface()
        if not iface:
            result.error = "Wi-Fi 어댑터를 찾을 수 없습니다."
            return result
        nearby = self.nearby(iface, rescan=True)
        if nearby is None:
            result.error = "주변 네트워크를 읽지 못했습니다. NetworkManager가 실행 중인지 확인해 주세요."
            return result
        link = self.link(iface, nearby)
        for b in nearby:
            exact = link.connected and b.bssid == link.bssid and link.rssi_exact
            result.readings.append(Reading(Ssid=b.ssid, Bssid=b.bssid, Band=b.band, Rssi=link.rssi if exact else b.rssi))
        if link.connected:
            result.connected_ssid, result.connected_band, result.connected_rssi = link.ssid, link.band, link.rssi
        return result
