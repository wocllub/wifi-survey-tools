# 빌드·검증 기록 (design/premium-ui)

작업 환경: Linux 샌드박스(Ubuntu 24.04, .NET SDK 8.0.428, C# 컴파일러 Roslyn).
이 저장소의 두 앱은 **Windows 전용 WinForms + WLAN API** 프로그램이므로 이 환경에서
실행할 수 없습니다. 그래서 검증을 세 층으로 나눴습니다.

## 1. 컴파일 검증 (실제 수행)

`.NET Framework 4.8 참조 어셈블리`에 대해 Roslyn `csc`로 원본과 동일한 조건
(`/target:winexe /platform:x64 /langversion:5`)으로 컴파일했습니다.
`langversion:5`는 Windows 기본 포함 csc.exe(.NET Framework 4.x)와 같은 문법 제약입니다.

```bash
# REFS = Microsoft.NETFramework.ReferenceAssemblies.net48 의 build/.NETFramework/v4.8
REFS=/path/to/v4.8 ./tools/compile-check.sh
```

결과:
```
WifiSignalChecker.exe     122,368 bytes   오류 0
WifiCoveragePlanner.exe   151,040 bytes   오류 0
```
- 두 프로그램 모두 **경고 0 / 오류 0** (일부 `CS0649` 미사용 필드 경고는
  공통 파일을 두 앱에 각각 컴파일하기 때문에 생기는 것으로 무해)
- 컴파일 대상: `src/Design/{Theme,Icons,Widgets,Charts}.cs`, `src/Core/Signal.cs`,
  `WifiSignalChecker.cs` / `WifiCoveragePlanner.cs`

이 검증이 잡아주는 것: 문법 오류, 존재하지 않는 API/형식, 이름 충돌,
`using` 누락, 시그니처 불일치, C# 6+ 문법 사용(예: 문자열 보간, `?.`, 식 본문 멤버) 여부.

잡아주지 못하는 것: 레이아웃이 실제로 예쁜지, 애니메이션이 60fps인지,
DPI 배율·폰트 폴백, GDI+ 렌더링 결과, WLAN API 동작.

### 실제로 컴파일 검증에서 잡은 문제 (기록)
1. `Icon`/`StatusBar`/`Timer` — `System.Drawing.Icon`, `System.Windows.Forms.StatusBar`,
   `System.Threading.Timer`와 이름 충돌 → `Glyph`, `FootBar`, 명시적 `System.Windows.Forms.Timer`로 변경
2. `Theme.Text`(색)와 `Theme.Text(...)`(메서드) 충돌 → 색 토큰을 `Ink`로 변경
3. `Stepper.Step`(필드)가 `Widget.Step(float)`을 가림 → `Increment`
4. `Card.Paint`(델리게이트)가 `Control.Paint`(이벤트)를 가림 → `Body`
5. `readonly` 필드를 생성자 밖에서 대입 → `menuStrip`에서 제거
6. `StreamGraph.Ghost`의 형 불일치(`Pt` vs `double[]`) → 세션 파일과 같은 `{초, rssi}`로 통일

## 2. 로직 시험 (실제 실행)

UI와 무관한 계산은 `src/Core/Signal.cs`로 분리해 .NET 8에서 **실제로 실행**했습니다.

```bash
dotnet run --project tests/logic     # net8.0
```

결과: **44 / 44 통과** (`ALL PASSED`)

검사 항목: 이동평균(창·NaN 처리), 등급 경계, 2.4/5GHz 채널 겹침, 채널 간섭·권장 채널,
80MHz 폭 인접 채널 계산, 세션 요약(최저/최고/평균/약전계 비율/등급 비율 합 100),
OUI 조회, CSV 이스케이프·수식 주입 방지·왕복, INI 설정 저장/불러오기, 세션 파일
저장/불러오기(끊김 구간 NaN 유지), 표준편차.

시험이 잡은 실제 버그 3건(수정 완료):
1. `double.TryParse` 실패 시 변수가 0으로 덮여 **끊긴 구간이 0 dBm으로 저장**되던 문제
2. `Csv.Field`가 음수(`-53.5`)를 수식으로 오인해 `'`를 붙이던 문제
3. 인접 채널 판정 테스트 기대값 오류(1↔3은 겹침, 1↔6은 비겹침) — 구현은 정상

## 3. 시각 검증 (목업)

`docs/mockup/design-review.html` — 실제 앱의 레이아웃·색·정보 구조를 옮긴 HTML 목업.
- 개선 전/후/나란히, 앱 2종, 모션 켜기/끄기, 주석 토글
- 같은 토큰(`#0B0E13`, `#4C8DFF`, 42pt 고정폭 숫자, 8pt 그리드)을 사용
- 히트맵은 실제 `Rf.Compute`와 같은 방식(작은 해상도 격자 → 확대, 로그거리 + 벽 감쇠)으로
  계산해 캔버스에 그립니다.
- 애니메이션은 실제 구현과 같은 규칙(스프링 보간, 스윕, 박동, 흐르는 윈도우)을 따릅니다.

목업은 **증거 자료이지 앱 화면 캡처가 아닙니다.** 실제 렌더링(폰트 힌팅, GDI+ 안티에일리어싱,
DPI)은 Windows에서만 확인할 수 있습니다.

## 4. 남은 미검증 항목
- Windows에서의 실제 실행·상호작용 (`docs/WINDOWS-CHECKLIST.md`)
- 60fps 달성 여부(특히 큰 도면에서 AP 박동 12fps 갱신)
- DPI 125/150/200% 레이아웃
- WLAN API 실측(주변 AP 스캔, 핑)
- `--selftest` 전체 통과
