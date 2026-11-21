# Wi-Fi Survey Tools

Windows에서 설치 없이 실행하는 Wi-Fi 측정 도구 2종입니다.

## 다운로드

[최신 릴리스](https://github.com/wocllub/wifi-survey-tools/releases/latest)에서 EXE 파일을 내려받아 더블클릭하면 바로 실행됩니다.

- `WifiSignalChecker.exe`: Wi-Fi 신호 체크
- `WifiCoveragePlanner.exe`: Wi-Fi 음영지역 분석기

실행 파일에 코드 서명이 없어 처음 실행할 때 Windows SmartScreen 경고가 나올 수 있습니다. 릴리스에 적힌 SHA-256 값으로 파일을 확인한 뒤 **추가 정보 > 실행**을 선택하세요.

Linux 버전은 [linux 폴더의 안내](linux/README.md)를, iOS 버전은 [ios 폴더의 안내](ios/README.md)를 참고하세요.

## 프로그램

### Wi-Fi 신호 체크 (`WifiSignalChecker`)

현재 연결된 Wi-Fi의 상태를 1초마다 측정합니다. 다크 테마 실시간 대시보드입니다.

- 큰 신호 값(고정폭 숫자), 등급, 품질 링, 조언 문장
- KPI 4종: 채널, 링크 속도, 공유기 응답, 패킷 손실(추세선 포함)
- 신호 추이 실시간 그래프(최근 1/3/5/10분, 곡선·그라데이션, 마우스 값 표시)
- 주변 AP 표: 정렬·검색·대역 필터·제조사(OUI)·신호 막대, CSV 내보내기
- 채널 간섭 분석: 같은/인접 채널, 대역 혼잡도, 권장 채널
- 세션 요약(최고·최저·평균·표준편차·등급별 비율), 세션 저장/불러오기, 이전 세션 점선 비교
- 이동평균 스무딩, 약전계 기준선, 설정 저장, 측정 중 애니메이션(끄기 가능)

### Wi-Fi 음영지역 분석기 (`WifiCoveragePlanner`)

2D 도면에 AP를 배치해 신호를 예측하고, 현장 실측으로 음영지역을 확인합니다.

- 도면 이미지(PNG, JPG, BMP, TIFF) 불러오기와 축척 설정
- Cisco, HPE Aruba, Juniper Mist, Grandstream, H3C AP 제품 선택과 배치
- 벽 재질별 감쇠를 반영한 2.4 / 5 / 6 GHz 히트맵(스윕 등장, 부유 범례)
- 음영지역 표시, 커버리지 비율, 음영 면적 계산 (상단 KPI)
- 현장 실측 히트맵과 예측 대비 오차 표시
- 프로젝트 저장, 결과 PNG, 실측 CSV, **리포트(HTML + PNG + CSV)** 내보내기

## 빌드

Windows 10 이상에 기본 포함된 .NET Framework 4.x 컴파일러를 사용하므로 별도 설치가 필요 없습니다.

```powershell
.\build.bat
```

빌드하면 `WifiSignalChecker.exe`와 `WifiCoveragePlanner.exe`가 만들어집니다.
소스는 프로그램 파일과 공통 파일로 나뉩니다.

| 경로 | 내용 |
| --- | --- |
| `WifiSignalChecker.cs` | 신호 체크 본체(측정 + 화면) |
| `WifiCoveragePlanner.cs` | 음영 분석기 본체(전파 모델 + 화면) |
| `src/Design/Theme.cs` | 색·여백·타이포 토큰, 모션(스프링·60fps 러너), 드로잉 헬퍼 |
| `src/Design/Icons.cs` | 선 아이콘 세트 |
| `src/Design/Widgets.cs` | 카드·버튼·선택기·탭·상태바 |
| `src/Design/Charts.cs` | KPI·링·스트림 그래프·스펙트럼·데이터 표 |
| `src/Core/Signal.cs` | UI 없는 계산(스무딩·간섭·세션 요약·CSV·설정·OUI) |

테마를 바꾸려면 `src/Design/Theme.cs`의 색/여백 값만 고치면 두 프로그램에 함께 적용됩니다.

### 로직 시험 (Windows 불필요)

```bash
dotnet run --project tests/logic     # net8.0, 44개 시험
```

### Windows 없이 컴파일 검사

`tools/compile-check.sh` (Roslyn + .NET Framework 4.8 참조 어셈블리)

## 자동 시험

```powershell
.\WifiCoveragePlanner.exe --selftest .\test-output
```

결과는 `test-output\selftest.txt`에 저장됩니다. Wi-Fi 어댑터가 연결된 상태에서 실행해야 합니다.

## 예측값 안내

- 예측 신호는 로그 거리 경로 손실 모델로 계산한 추정치이며, 실제 건물에서 보정한 값이 아닙니다.
- AP 송신 출력 기본값(2.4 GHz 15 dBm, 5 / 6 GHz 18 dBm)은 설계용 가정값입니다. 실제 운용 출력으로 바꿔 입력하세요.
- 벽 재질별 감쇠량은 일반적인 추정치입니다.
- 프로그램에서 주황색으로 표시되는 AP의 안테나 이득은 참고값입니다. 제조사 데이터시트와 비교해 사용하세요.
- 제품명과 회사명은 각 소유자의 상표이며, 이 프로젝트는 해당 제조사와 관련이 없습니다.

## 시스템 요구 사항

- Windows 10 / 11 (x64)
- Wi-Fi 어댑터
- 주변 네트워크 검색을 위한 Windows 위치 서비스

## 디자인 문서

- [`docs/DESIGN-AUDIT.md`](docs/DESIGN-AUDIT.md) — 기존 UI 진단(무엇이 왜 저렴해 보이는지)
- [`docs/mockup/design-review.html`](docs/mockup/design-review.html) — 개선 전/후 목업(애니메이션 포함)
- [`docs/BUILD-VERIFICATION.md`](docs/BUILD-VERIFICATION.md) — 빌드·검증 기록과 한계
- [`docs/WINDOWS-CHECKLIST.md`](docs/WINDOWS-CHECKLIST.md) — Windows에서 확인할 체크리스트
- [`CHANGELOG.md`](CHANGELOG.md) — 변경 요약

## 라이선스

[MIT License](LICENSE)로 배포합니다. 저작권 및 라이선스 고지를 유지하면 누구나 자유롭게 사용, 수정, 재배포할 수 있습니다.
