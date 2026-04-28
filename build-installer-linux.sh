#!/usr/bin/env bash

set -euo pipefail

CONFIGURATION="${1:-Release}"
RUNTIME="${2:-win-x64}"

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT_DIR/ShareWorkin/ShareWorkin.csproj"
PUBLISH_DIR="$ROOT_DIR/dist/publish/ShareWorkin"
OUTPUT_DIR="$ROOT_DIR"
INNO_SCRIPT="$ROOT_DIR/ShareWorkin.iss"
README_TXT="$ROOT_DIR/ご利用にあたって.txt"
RUNTIME_INSTALLER="windowsdesktop-runtime-8.0.24-win-x64.exe"
RUNTIME_EXE="$ROOT_DIR/$RUNTIME_INSTALLER"
HASH_FILE="$ROOT_DIR/ShareWorkin_v1.04_SHA256.txt"
ZIP_FILE="$ROOT_DIR/ShareWorkin_v1.04_Setup.zip"
DOTNET_DIR="${DOTNET_DIR:-$HOME/.dotnet-msft}"
DOTNET_BIN="$DOTNET_DIR/dotnet"

if [[ ! -x "$DOTNET_BIN" ]]; then
  echo "Official .NET SDK was not found at: $DOTNET_BIN" >&2
  echo "Install it with:" >&2
  echo "  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh" >&2
  echo "  bash /tmp/dotnet-install.sh --channel 8.0 --install-dir \"$DOTNET_DIR\"" >&2
  exit 1
fi

WINDOWS_DESKTOP_TARGETS="$DOTNET_DIR/sdk/$(ls "$DOTNET_DIR/sdk" | sort -V | tail -n 1)/Sdks/Microsoft.NET.Sdk.WindowsDesktop/targets/Microsoft.NET.Sdk.WindowsDesktop.targets"
if [[ ! -f "$WINDOWS_DESKTOP_TARGETS" ]]; then
  echo "WindowsDesktop SDK was not found in: $DOTNET_DIR" >&2
  echo "This project is WPF (net8.0-windows), so Linux-hosted dotnet alone cannot compile it in the current environment." >&2
  echo "Build this app on Windows with .NET 8 SDK, or prepare a Windows .NET SDK/toolchain that includes Microsoft.NET.Sdk.WindowsDesktop." >&2
  exit 1
fi

ISCC_PATH="${ISCC_PATH:-}"
if [[ -z "$ISCC_PATH" ]]; then
  for candidate in \
    "$HOME/.wine/drive_c/Program Files/Inno Setup 6/ISCC.exe" \
    "$HOME/.wine/drive_c/Program Files (x86)/Inno Setup 6/ISCC.exe" \
    "$HOME/.wine/drive_c/Inno Setup 6/ISCC.exe" \
    "$HOME/.wine/drive_c/InnoSetup6/ISCC.exe"
  do
    if [[ -f "$candidate" ]]; then
      ISCC_PATH="$candidate"
      break
    fi
  done
fi

if [[ -z "$ISCC_PATH" ]]; then
  echo "Inno Setup 6 compiler was not found under ~/.wine." >&2
  echo "Set ISCC_PATH to the Wine path of ISCC.exe after installing Inno Setup 6." >&2
  exit 1
fi

if [[ ! -f "$RUNTIME_EXE" ]]; then
  echo "Runtime installer not found: $RUNTIME_EXE" >&2
  echo "Place '$RUNTIME_INSTALLER' in ShareWorkin_app before running." >&2
  exit 1
fi

rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"
rm -f "$OUTPUT_DIR"/ShareWorkin_v1.04_install*.exe
rm -f "$OUTPUT_DIR"/ShareWorkin_v1.04_SHA256*.txt
rm -f "$OUTPUT_DIR"/ShareWorkin_v1.03_install*.exe
rm -f "$OUTPUT_DIR"/ShareWorkin_v1.03_Setup.zip
rm -f "$OUTPUT_DIR"/ShareWorkin_v1.03_SHA256*.txt
rm -f "$OUTPUT_DIR"/ShareWorkin_v1.02_install*.exe
rm -f "$OUTPUT_DIR"/ShareWorkin_v1.02_Setup.zip
rm -f "$OUTPUT_DIR"/ShareWorkin_v1.02_SHA256*.txt
rm -f "$OUTPUT_DIR"/ShareWorkin_v1.02_package*.zip
rm -f "$OUTPUT_DIR"/ShareWorkin1.02_install.exe
rm -f "$OUTPUT_DIR"/ShareWorkin1.02_package.zip
rm -f "$OUTPUT_DIR"/ShareWorkin1.02_SHA256.txt
rm -f "$ZIP_FILE"

DOTNET_ROOT="$DOTNET_DIR" PATH="$DOTNET_DIR:$PATH" "$DOTNET_BIN" publish "$PROJECT" \
  --configuration "$CONFIGURATION" \
  --runtime "$RUNTIME" \
  --self-contained false \
  /p:EnableWindowsTargeting=true \
  /p:DebugType=None \
  /p:DebugSymbols=false \
  --output "$PUBLISH_DIR"

WINE_SCRIPT_PATH="$(winepath -w "$INNO_SCRIPT")"
WINE_CMD=(wine "$ISCC_PATH" "$WINE_SCRIPT_PATH")
if [[ -z "${DISPLAY:-}" ]] && command -v xvfb-run >/dev/null 2>&1; then
  WINE_CMD=(xvfb-run -a "${WINE_CMD[@]}")
fi

"${WINE_CMD[@]}"

INSTALLER_PATH="$OUTPUT_DIR/ShareWorkin_v1.04_install.exe"
if [[ ! -f "$INSTALLER_PATH" ]]; then
  echo "Installer was not created: $INSTALLER_PATH" >&2
  exit 1
fi

{
  echo "ShareWorkin 1.04 SHA-256"
  echo "Generated: $(date '+%Y-%m-%d %H:%M:%S %Z')"
  echo
} > "$HASH_FILE"

for file in "$INSTALLER_PATH" "$PUBLISH_DIR/ShareWorkin.exe" "$README_TXT" "$RUNTIME_EXE"; do
  if [[ -f "$file" ]]; then
    echo "[$(basename "$file")]" >> "$HASH_FILE"
    sha256sum "$file" | awk '{print $1}' >> "$HASH_FILE"
    echo >> "$HASH_FILE"
  fi
done

if command -v zip >/dev/null 2>&1; then
  (
    cd "$OUTPUT_DIR"
    zip -j -q "$(basename "$ZIP_FILE")" \
      "$INSTALLER_PATH" \
      "$RUNTIME_EXE" \
      "$README_TXT"
  )
elif command -v python3 >/dev/null 2>&1; then
  python3 - <<'PY' "$ZIP_FILE" "$INSTALLER_PATH" "$RUNTIME_EXE" "$README_TXT"
import sys
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

zip_path = Path(sys.argv[1])
items = [Path(p) for p in sys.argv[2:] if Path(p).is_file()]

with ZipFile(zip_path, "w", compression=ZIP_DEFLATED) as zf:
    for item in items:
        zf.write(item, arcname=item.name)
PY
else
  echo "ZIP package was not created because neither 'zip' nor 'python3' is available." >&2
  exit 1
fi

echo "Created installer: $INSTALLER_PATH"
echo "Created SHA-256 file outside package: $HASH_FILE"
echo "Created package zip: $ZIP_FILE"

rm -rf "$ROOT_DIR/build" "$ROOT_DIR/dist"
echo "Cleaned build intermediates: $ROOT_DIR/build, $ROOT_DIR/dist"
