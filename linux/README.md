# Wi-Fi Survey Tools for Linux

Windows 버전과 같은 기능을 제공하는 Linux 버전입니다. 음영지역 분석 프로젝트(`.wifiplan`) 파일은 Windows 버전과 서로 열 수 있습니다.

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
