@echo off
rem 두 프로그램을 Windows 기본 포함 .NET Framework 4.x 컴파일러로 빌드한다.
rem 소스는 프로그램 파일 + 공통 디자인/로직 파일로 나뉜다.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe를 찾지 못했습니다. Windows 10/11에서 실행해 주세요.
  exit /b 1
)
set COMMON=src\Design\Theme.cs src\Design\Icons.cs src\Design\Widgets.cs src\Design\Charts.cs src\Core\Signal.cs
set FLAGS=/nologo /codepage:65001 /optimize+ /target:winexe /platform:x64 /langversion:5

"%CSC%" %FLAGS% /out:WifiSignalChecker.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll %COMMON% WifiSignalChecker.cs
if errorlevel 1 goto failed

"%CSC%" %FLAGS% /out:WifiCoveragePlanner.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll /r:System.Xml.dll %COMMON% WifiCoveragePlanner.cs
if errorlevel 1 goto failed

echo Build complete: WifiSignalChecker.exe, WifiCoveragePlanner.exe
exit /b 0
:failed
echo Build failed.
exit /b 1
