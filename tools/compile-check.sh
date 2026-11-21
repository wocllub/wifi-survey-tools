#!/usr/bin/env bash
# Windows 없이 두 프로그램의 C# 컴파일만 검사한다 (실행은 불가).
# 필요: .NET SDK 8+ , .NET Framework 4.8 참조 어셈블리
#   참조: https://www.nuget.org/packages/Microsoft.NETFramework.ReferenceAssemblies.net48
# 사용: REFS=/path/to/build/.NETFramework/v4.8 ./tools/compile-check.sh
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SDK_DIR="$(dotnet --list-sdks | tail -1 | sed -E 's/^[^ ]+ \[(.*)\]$/\1/')"
SDK_VER="$(dotnet --list-sdks | tail -1 | cut -d' ' -f1)"
CSC="$SDK_DIR/$SDK_VER/Roslyn/bincore/csc.dll"
if [ -z "$REFS" ]; then echo "REFS 환경변수에 .NET Framework 4.8 참조 어셈블리 경로를 지정하세요."; exit 2; fi
COMMON="$ROOT/src/Design/Theme.cs $ROOT/src/Design/Icons.cs $ROOT/src/Design/Widgets.cs $ROOT/src/Design/Charts.cs $ROOT/src/Core/Signal.cs"
R="-r:$REFS/mscorlib.dll -r:$REFS/System.dll -r:$REFS/System.Core.dll -r:$REFS/System.Drawing.dll -r:$REFS/System.Windows.Forms.dll -r:$REFS/System.Xml.dll"
OUT="$(mktemp -d)"
set -x
dotnet "$CSC" -nologo -langversion:5 -nostdlib+ -target:winexe -platform:x64 -nowarn:169,649 \
  -out:"$OUT/WifiSignalChecker.exe" $R $COMMON "$ROOT/WifiSignalChecker.cs"
dotnet "$CSC" -nologo -langversion:5 -nostdlib+ -target:winexe -platform:x64 -nowarn:169,649 \
  -out:"$OUT/WifiCoveragePlanner.exe" $R -r:$REFS/System.Runtime.Serialization.dll $COMMON "$ROOT/WifiCoveragePlanner.cs"
echo "OK: $OUT"
