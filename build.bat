@echo off
rem Builds both programs with the C# compiler included in Windows.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /codepage:65001 /optimize+ /target:winexe /platform:x64 /out:WifiSignalChecker.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll WifiSignalChecker.cs
if errorlevel 1 goto failed
"%CSC%" /nologo /codepage:65001 /optimize+ /target:winexe /platform:x64 /out:WifiCoveragePlanner.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll /r:System.Xml.dll WifiCoveragePlanner.cs
if errorlevel 1 goto failed
echo Build complete: WifiSignalChecker.exe, WifiCoveragePlanner.exe
exit /b 0
:failed
echo Build failed.
exit /b 1
