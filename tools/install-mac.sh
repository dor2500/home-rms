#!/usr/bin/env bash
set -euo pipefail

ARCH="$(uname -m)"
if [[ "$ARCH" == "arm64" ]]; then
  ASSET="HomeRmsController-macOS-osx-arm64.zip"
else
  ASSET="HomeRmsController-macOS-osx-x64.zip"
fi

echo "Fetching latest Home RMS Controller ($ASSET) from GitHub..."
URL="$(curl -fsSL https://api.github.com/repos/dor2500/home-rms/releases/latest | grep -o "\"browser_download_url\": *\"[^\"]*${ASSET}\"" | head -n1 | cut -d'"' -f4)"

if [[ -z "$URL" ]]; then
  echo "Error: Could not find $ASSET in latest GitHub release." >&2
  exit 1
fi

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

echo "Downloading $ASSET..."
curl -fL "$URL" -o "$TMP_DIR/controller.zip"
unzip -q "$TMP_DIR/controller.zip" -d "$TMP_DIR/out"

echo "Installing HomeRmsController.app into /Applications..."
rm -rf /Applications/HomeRmsController.app
cp -R "$TMP_DIR/out/HomeRmsController.app" /Applications/HomeRmsController.app
chmod +x /Applications/HomeRmsController.app/Contents/MacOS/HomeRmsController
xattr -cr /Applications/HomeRmsController.app 2>/dev/null || true

echo "Installed! Launching Home RMS Controller..."
open /Applications/HomeRmsController.app
