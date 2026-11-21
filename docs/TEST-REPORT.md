# 실행 테스트 보고서 — Wi-Fi 측정 도구 2종 (Wine / Xvfb)

- 대상: `design/premium-ui` 브랜치, 커밋 `6a240fa` (UI 고도화 직후)
- 검증일: 2026-11-21
- 검증자: Spark (자동 에이전트)
- 목적: 목업이 아니라 **실제 exe를 실행·조작**해 동작을 확인한다.

## 1. 한 줄 결론

**부분 동작** — 두 프로그램 모두 실제로 실행되고 화면·조작·저장·내보내기가 동작하며, 그 과정에서 **실행을 막던 치명적 결함 1건을 포함해 9건의 실제 버그를 찾아 수정**했다. 다만 Wi-Fi 측정 자체는 Wine에 WLAN API가 없어 이 환경에서 검증할 수 없었다(오류 메시지로 안내하고 죽지 않는 것만 확인).

## 2. 환경

| 항목 | 값 |
| --- | --- |
| OS | Ubuntu 24.04.5 LTS (x86_64, 4 vCPU, 7 GB) |
| 디스플레이 | Xvfb 1600x1000x24 (`:77`), 창 관리자 없음 |
| 런타임 | Wine 9.0 (`wine64`, 64비트 전용) + wine-mono 9.0.0 |
| 빌드 | .NET SDK 8.0.428의 Roslyn csc + .NET Framework 4.8 참조 어셈블리 (`tools/compile-check.sh`) |
| 입력 자동화 | xdotool (클릭·키 입력) |
| 캡처 | ImageMagick `import` |
| 폰트 | `Segoe UI`→`NanumGothic` 대체, `fonts-nanum` 설치 |

### 준비 절차 (재현용)

```bash
sudo apt-get install -y wine64 winetricks x11-utils xdotool imagemagick fonts-nanum
export WINEPREFIX=/tmp/winep WINEARCH=win64 WINEDEBUG=-all DISPLAY=:77
wineboot --init
wine msiexec /i wine-mono-9.0.0-x86.msi /qn      # https://dl.winehq.org/wine/wine-mono/9.0.0/
cp /usr/share/fonts/truetype/nanum/NanumGothic*.ttf $WINEPREFIX/drive_c/windows/Fonts/
wine regedit fontsub.reg                          # HKCU\Software\Wine\Fonts\Replacements
Xvfb :77 -screen 0 1600x1000x24 & DISPLAY=:77
export PATH=$HOME/.dotnet:$PATH
REFS=/tmp/refs/build/.NETFramework/v4.8 bash tools/compile-check.sh   # 두 exe 빌드
wine WifiSignalChecker.exe &  wine WifiCoveragePlanner.exe &
```

> 폰트 주의: Windows는 Segoe UI + Malgun Gothic을 쓰지만 이 환경에는 두 폰트가 없다. 대체 폰트(Nanum Gothic)의 자간·행 높이가 달라 글자 폭·줄 간격이 조금 다르게 보인다. 아래 "폰트 영향 가능 항목"에 따로 표시했다.

## 3. 시나리오별 결과

| # | 항목 | 결과 | 근거 |
| --- | --- | --- | --- |
| 1 | 두 exe 실행·첫 화면 | **통과** | 신호 체크: 대시보드 정상 표시. 음영 분석기: **수정 전 실행 즉시 크래시**(BUG-1), 수정 후 정상 |
| 2 | 창 크기 최소/기본/최대 | **통과** | 1100x720(최소)·1280x900·1516x930 모두 카드 겹침·잘림 없이 재배치, 캔버스 자동 맞춤. 좌우 패널은 필요 시 스크롤 |
| 3 | 신호 체크: 스캔·dBm·그래프·세션 | **부분 통과** | 스캔 → 오류 안내 정상(크래시 없음), dBm/그래프는 어댑터 없어 빈 상태 유지. 세션 저장(`session1.csv`, 120행)·비교 행 추가·CSV 로그(`wifi_log.csv`) 실제 생성 확인 |
| 4 | AP 표 정렬·검색·필터·OUI | **부분 통과** | 검색창·대역 필터·빈 상태 문구 정상. Wine에 감지되는 AP가 없어 정렬·OUI 표시는 실데이터로 확인 불가(Windows 항목) |
| 5 | 채널 간섭·권장 채널 | **통과** | 권장 채널 카드, 대역 혼잡도 링, 채널별 점유 스펙트럼, 채널 순위 빈 상태 모두 표시. 2.4/5/6 GHz 탭 전환 시 재계산 |
| 6 | 음영 분석기: 도면 로드·AP 배치·드래그·히트맵·리포트 | **통과** | 도면 PNG(800x500) 로드→축척 미설정 안내→축척 도구 자동 선택. AP 드래그로 위치 변경(18.6, 16.7) 후 커버리지 96.0%→98.4% 재계산. **리포트 버튼으로 HTML·PNG·CSV 3개 파일 실제 생성**(내용 확인, 표 열은 AP 목록으로 수정 BUG-6) |
| 7 | 설정 저장/복원·모션 끄기 | **통과** | `checker.ini`에 `motion=0`, `tab=3` 등 기록 → 재실행 시 설정 탭으로 열리고 상태칩 "모션 꺼짐" 유지 |
| 8 | `--selftest` | **통과(32항목 중 29 통과)** | 실패 3건은 모두 Wi-Fi 어댑터 의존 항목. 수정 전에는 어댑터 없을 때 **셀프테스트 자체가 크래시**(BUG-10) |
| 9 | WLAN API 없는 환경에서 스캔 | **통과(의도대로)** | 크래시 없음. 어댑터 카드가 "확인 필요", 상태칩 "어댑터 없음", 주황색 안내 "Wi-Fi 어댑터를 찾을 수 없습니다." 표시 후 대기 |

### 스크린샷 (모두 `shots/` 폴더, 1280 폭)

| 파일 | 내용 |
| --- | --- |
| `00_수정전_카드뒤에가린컨트롤.png` | 수정 전: 제조사/모델/표시 설정 컨트롤이 카드에 가려 안 보임 |
| `01_신호체크_실시간_어댑터없음.png` | 실시간 탭 + 스캔 후 "Wi-Fi 어댑터를 찾을 수 없습니다." |
| `02_신호체크_세션.png` | 세션 탭, 빈 상태와 요약 카드 |
| `03_신호체크_채널분석.png` | 채널 탭, 권장 채널·혼잡도·스펙트럼 |
| `04_신호체크_설정.png` | 설정 탭(모션 토글 포함) |
| `05_음영분석기_기본화면.png` | 수정 후 기본 화면(헤더·도면·히트맵·양쪽 패널) |
| `06_음영분석기_AP선택_패널.png` | AP 선택 시 "선택한 AP" 편집 카드 |
| `07_음영분석기_최소크기.png` | 최소 크기 1100x720 |
| `08_음영분석기_리포트PNG.png` | 리포트 버튼으로 생성된 `coverage_*.png` |
| `09_음영분석기_결과이미지.png` | 셀프테스트의 `export.png` |

## 4. 발견한 버그와 수정 내역

모두 **플랫폼 비의존** 코드 결함이다(WinForms/WPF 공통 규칙 위반). 커밋 하나로 묶었다.

### BUG-1 (치명) 음영 분석기가 실행 즉시 죽는다 — `KeyNotFoundException: 'PlaceAp'`

- 재현: `WifiCoveragePlanner.exe` 실행 → 즉시 종료(창 안 뜸). `--selftest`도 같은 지점에서 죽음.
- 원인: `AddTool()`이 호출될 때마다 `LayoutRail()`을 부르는데, `LayoutRail()`은 6개 도구 버튼을 **전부** `toolButtons[t]`로 읽는다. 첫 호출 시점에는 사전에 1개만 들어 있어 예외.
- 수정: `LayoutRail()`이 아직 없는 버튼은 건너뛰도록 `TryGetValue` 사용.
- 비고: Windows에서도 100% 동일하게 발생한다. 지난 검증이 "UI 없는 계산 44개 시험"만 돌려서 놓친 부분.

### BUG-2 음영 분석기 헤더(제목·KPI·도면/저장/리포트)가 아예 안 보인다

- 원인: `HeadBar`를 `Dock = DockStyle.Top`으로 붙였지만 높이를 지정하지 않아 높이 0. (신호 체크의 `TopBar`는 `Height = Theme.S(64)`를 지정한다.)
- 수정: 생성자에서 `Height = Theme.S(64)` 지정. → KPI 4종과 상단 버튼이 정상 표시.

### BUG-3 (치명) 카드 위의 입력 컨트롤이 카드에 가려 보이지 않고 클릭도 안 된다

- 재현: 음영 분석기 실행 → 왼쪽 "AP 제품"의 제조사·모델 목록·벽 재질이 보이지 않고, 오른쪽 "표시 설정"은 라벨만 있고 콤보/스테퍼가 없음. 그 자리를 눌러도 반응 없음.
- 원인: WinForms는 먼저 추가한 자식이 z-order 앞에 온다. 카드를 먼저 `Controls.Add`하고 그 위에 놓일 콤보·스테퍼를 나중에 추가해 카드가 앞에서 덮었다(히트테스트도 카드가 가져감).
- 수정: 카드에 `SendToBack()`을 명시 호출(좌측 `libCard`, 우측 `infoCard`/`displayCard`/`inspCard`).
- 결과: 제조사 선택, 모델 목록, 벽 재질, 보기/대역/기준/공간/SSID 설정이 모두 보이고 조작된다.

### BUG-4 "표시 설정" 라벨과 컨트롤이 한 줄씩 어긋나고, 카드 높이가 모자라 라벨·각주가 겹친다

- 원인: 라벨은 카드 본문 좌표(`r.Y + 42 + i*48`)에, 컨트롤은 카드 상단 기준(`card.Top + 42 + i*48`)에 배치 → 44 px 어긋남. 또 5행(42+4*48+14=248)이 카드 본문 높이(242)를 넘어 각주와 겹쳤다.
- 수정: 컨트롤을 라벨 바로 아래로 정렬(`contentTop + label + 15`), 카드 높이 300→372로 확장하고 아래 카드 위치 조정.

### BUG-5 "선택한 AP" 카드가 숨겨져도 그 입력 컨트롤은 화면에 떠 있다

- 재현: 실행 직후 AP를 선택하지 않은 상태에서 오른쪽 아래에 제목·라벨 없는 스테퍼 4개(3 m, 15, 18, 18)가 떠 있음.
- 원인: `inspCard.Visible`만 끄고, 카드가 아닌 `rightPanel`의 자식인 `apName`/스테퍼/삭제 버튼은 그대로 둠.
- 수정: `ShowSelection()`에서 선택이 없을 때 해당 컨트롤도 함께 숨김.

### BUG-6 리포트의 `access-points_*.csv`가 AP 목록이 아니라 실측 데이터(빈 헤더)다

- 재현: 리포트 내보내기 → CSV를 열면 `지점,측정시각,X,Y,...` 헤더 한 줄뿐. HTML 표에는 AP 3대가 나온다.
- 원인: `ExportReport()`가 실측 CSV용 `ExportCsv()`를 그대로 호출.
- 수정: `ExportApCsv()` 추가 — HTML 표와 같은 열(이름/제조사/모델/X/Y/높이/2.4·5·6 GHz 출력)로 기록. 실측 CSV 메뉴는 그대로 둠.

### BUG-7 버튼 라벨이 잘린다 ("세션 저장" → "세…", "비우기"는 글자 없음)

- 재현: 세션 탭, 상단 PNG/스캔 버튼.
- 원인: `Widgets.UiButton.OnPaint`에서 라벨 사각형 폭을 `아이콘 + 11px`로 잡아 텍스트 폭(`TextRenderer.MeasureText`)을 더하지 않았다. 아이콘이 있는 모든 버튼에서 라벨이 잘린다.
- 수정: 총 폭에 텍스트 폭 포함, 라벨 사각형 폭을 실제 텍스트 폭으로. → "세션 저장", "요약 CSV", "리포트", "도면", "저장" 모두 정상.

### BUG-8 위험(빨강) 버튼이 불투명 빨강으로 칠해져 라벨이 사라진다

- 재현: 세션 탭의 "비우기" 버튼이 글자 없는 빨간 알약.
- 원인: `Theme.Mix()`가 `Color.FromArgb(r,g,b)` 3-인자 오버로드를 써서 **알파를 항상 255로** 만든다. `Alpha(Lv1, 26)`(10%)이 불투명 빨강이 되고, 전경색도 같은 빨강이라 글자가 묻힘.
- 수정: `Mix()`가 알파까지 보간하도록 변경. → 빨간 테두리+빨간 글자의 위험 버튼으로 정상 표시.

### BUG-9 세션 표에 `NaN dBm`이 표시된다

- 재현: Wi-Fi가 연결되지 않은 상태에서 세션 탭 → "지금 측정  NaN dBm  NaN dBm".
- 원인: 표본은 있으나 유효 표본(`Valid`)이 0일 때 평균/최저가 NaN. 행을 추가하는 조건은 `session.Count > 0`이라 표시된다.
- 수정: `Valid == 0`이면 `—`로 표시(현재 세션·비교 세션 모두).

### BUG-10 어댑터가 없으면 `--selftest`가 FAIL 대신 크래시

- 원인: 검사 상세 문자열에서 `f.P.Points[0]`을 조건과 무관하게 먼저 평가.
- 수정: 지점이 없으면 안내 문자열로 대체. **추가로** 리포트 내보내기(HTML+PNG+CSV 3종 생성, CSV가 AP 목록인지) 검사를 셀프테스트에 넣었다(자가 회귀 방지).

## 5. 이 환경에서 확인할 수 없었던 것 (Windows 실기 확인 필요)

1. **실제 WLAN 스캔·dBm·링크 속도·패킷 손실** — Wine에는 WlanEnumInterfaces가 스텁이라 감지 0. 앱은 "Wi-Fi 어댑터를 찾을 수 없습니다."로 안내하고 계속 동작한다(크래시 없음). 실측값·평활·약전계 판정은 Windows에서 확인해야 한다.
2. **AP 표의 정렬·검색·제조사 OUI 표시** — 감지된 AP가 없어 실데이터로 확인 불가. 검색·필터·빈 상태 UI는 동작 확인.
3. **세션 비교 점선 오버레이** — 저장은 됐지만 유효 표본이 없어 그래프에 그릴 값이 없었다.
4. **파일 선택 대화상자 자동 입력** — Wine의 공용 대화상자는 열리고 저장은 됐으나(로그/세션/리포트 실제 생성 확인), 경로 입력 자동화가 불안정해 모든 대화상자 경로를 클릭으로 재현하지는 못했다. 도면 로드/프로젝트 저장·열기는 셀프테스트로 왕복 검증(이미지 800x500 + AP 3대 + 벽 24개 보존).
5. **고DPI(125%/150%) 렌더링** — `Theme.K` 배율 로직은 96 DPI에서만 확인.
6. **폰트 영향 가능 항목** — 대체 폰트 때문에 (a) 작은 캡션에서 한글 받침이 살짝 잘려 보임, (b) 분석 결과 카드의 라벨/값 간격이 매우 좁아 보임. Windows의 Malgun Gothic에서도 같은지 확인 필요. 폭 계산 문제(BUG-7)는 별개로 확인·수정했다.

## 6. 재현 절차 요약

```bash
# 1) 빌드
REFS=/tmp/refs/build/.NETFramework/v4.8 bash tools/compile-check.sh
# 2) 실행
WINEPREFIX=/tmp/winep DISPLAY=:77 wine WifiCoveragePlanner.exe
# 3) 자동 시험
WINEPREFIX=/tmp/winep DISPLAY=:77 wine WifiCoveragePlanner.exe --selftest Z:\\tmp\\selftest
# 4) 신호 체크 실행 후 상단 [스캔] → 어댑터 없음 안내 확인
WINEPREFIX=/tmp/winep DISPLAY=:77 wine WifiSignalChecker.exe
# 5) 리포트: 음영 분석기 상단 [리포트] → 폴더 선택 → HTML/PNG/CSV 생성 확인
```

## 7. 남은 일

- Windows 실기에서 위 1~6 항목 확인(특히 실제 스캔·AP 표·고DPI).
- 확인 후 `main` 병합 및 릴리스 노트 갱신.
