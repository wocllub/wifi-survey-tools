# Wi-Fi Survey Tools for Linux

Linux용 Wi-Fi 신호 체크와 음영지역 분석기입니다. 음영지역 분석 프로젝트(`.wifiplan`) 파일은 Windows 버전과 서로 열 수 있습니다.

## 1.3 업데이트

- 신호 그래프에 5초 이동평균과 최근 3분 기록 저장·비교를 추가했습니다.
- 주변 AP 표를 SSID·BSSID로 검색하거나 대역별로 필터링할 수 있습니다. 헤더를 눌러 정렬할 수 있고, 주변 2.4 GHz AP를 바탕으로 채널 1·6·11 중 하나를 추천합니다.
- 음영 분석기의 **파일 → 분석 리포트 내보내기**에서 HTML, PNG, AP 목록 CSV를 한 폴더에 저장합니다.

## 설치

Debian / Ubuntu 기준으로 필요한 패키지를 먼저 설치합니다.

```sh
sudo apt install python3-venv network-manager iw iputils-ping iproute2 fonts-noto-cjk libxcb-cursor0
```

저장소를 내려받은 뒤 설치합니다. 관리자 권한은 필요 없습니다.

```sh
cd linux
sh install.sh
```

앱 메뉴에서 실행하거나 터미널에서 실행합니다.

```sh
~/.local/bin/wifi-signal-checker
~/.local/bin/wifi-coverage-planner
```

## Windows 버전과 다른 점

- Wi-Fi 연결을 NetworkManager가 관리해야 주변 AP를 검색할 수 있습니다.
- 연결된 AP의 신호는 `iw`가 알려 주는 실제 dBm 값입니다.
- 주변 AP의 신호는 `nmcli`의 백분율을 dBm으로 환산한 **추정값**입니다.
- 현장 실측 시 연결된 AP 외의 값도 추정값이므로, 정확한 측정은 측정할 SSID에 연결한 상태에서 진행하세요.

## 시험

```sh
pip install -r requirements.txt
QT_QPA_PLATFORM=offscreen PYTHONPATH=. python3 -m unittest discover -s tests
```
