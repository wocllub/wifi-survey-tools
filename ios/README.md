# Wi-Fi 음영 분석 for iOS

iPhone과 iPad에서 2D 도면에 AP를 배치하고 신호와 음영지역을 분석하는 앱입니다. 프로젝트(`.wifiplan`) 파일은 Windows, Linux 버전과 서로 열 수 있습니다.

## 기능

- 사진 또는 파일에서 도면 불러오기, 축척 설정
- Cisco, HPE Aruba, Juniper Mist, Grandstream, H3C AP 배치와 출력 설정
- 벽 재질별 감쇠를 반영한 2.4 / 5 / 6 GHz 히트맵
- 음영지역 표시, 커버리지 비율, 음영 면적 계산
- 측정값 직접 입력을 통한 실측 히트맵
- 프로젝트, 결과 이미지, 실측 CSV 내보내기

## 다른 버전과 다른 점

**자동 실측과 Wi-Fi 신호 체크 기능은 없습니다.** iOS는 일반 앱이 Wi-Fi 신호 세기와 주변 AP 목록을 읽는 것을 허용하지 않습니다. 실측은 측정기나 Windows, Linux 버전에서 확인한 값을 위치별로 입력하는 방식입니다.

## 빌드

Mac과 Xcode 16 이상이 필요합니다. 앱은 iOS 17 이상에서 실행됩니다.

1. Xcode에서 `ios/WifiPlanner.xcodeproj`를 엽니다.
2. WifiPlanner 타깃의 **Signing & Capabilities**에서 본인의 Team을 선택합니다.
3. 실행할 기기나 시뮬레이터를 고른 뒤 실행합니다.

실제 기기 설치와 App Store 배포에는 Apple 계정의 서명 설정이 필요합니다.

## 시험

```sh
cd ios
xcodebuild test -project WifiPlanner.xcodeproj -scheme WifiPlanner \
  -destination 'platform=iOS Simulator,name=iPhone 16' CODE_SIGNING_ALLOWED=NO
```

예측 신호는 실제 건물에서 보정하지 않은 추정치입니다.
