#!/bin/sh
# Installs Wi-Fi Survey Tools for the current user. Root is not required.
set -eu

SRC_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
DATA_HOME=${XDG_DATA_HOME:-"$HOME/.local/share"}
APP_DIR="$DATA_HOME/wifi-survey-tools"
BIN_DIR="$HOME/.local/bin"
DESKTOP_DIR="$DATA_HOME/applications"

if ! command -v python3 >/dev/null 2>&1; then
    echo "python3 is required." >&2
    exit 1
fi
if ! python3 -c "import venv, ensurepip" >/dev/null 2>&1; then
    echo "The Python venv module is required." >&2
    echo "Debian/Ubuntu: sudo apt install python3-venv" >&2
    exit 1
fi

mkdir -p "$APP_DIR" "$BIN_DIR" "$DESKTOP_DIR"
rm -rf "$APP_DIR/wifi_survey"
cp -R "$SRC_DIR/wifi_survey" "$APP_DIR/wifi_survey"
python3 -m venv "$APP_DIR/venv"
"$APP_DIR/venv/bin/python" -m pip install --quiet --upgrade pip
"$APP_DIR/venv/bin/python" -m pip install --quiet -r "$SRC_DIR/requirements.txt"

make_launcher() {
    cat > "$BIN_DIR/$1" <<EOF
#!/bin/sh
cd "$APP_DIR" && exec "$APP_DIR/venv/bin/python" -m wifi_survey.$2 "\$@"
EOF
    chmod +x "$BIN_DIR/$1"
    cat > "$DESKTOP_DIR/$1.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=$3
Exec=$BIN_DIR/$1
Icon=network-wireless
Categories=Network;Utility;
Terminal=false
EOF
}

make_launcher wifi-signal-checker signal_app "Wi-Fi 신호 체크"
make_launcher wifi-coverage-planner planner_app "Wi-Fi 음영지역 분석기"

echo "Installed. Start from the application menu or run:"
echo "  $BIN_DIR/wifi-signal-checker"
echo "  $BIN_DIR/wifi-coverage-planner"
for tool in nmcli iw ping ip; do
    command -v "$tool" >/dev/null 2>&1 || echo "Warning: '$tool' was not found. Some measurements will be unavailable."
done
