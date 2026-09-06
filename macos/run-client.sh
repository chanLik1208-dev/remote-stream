#!/usr/bin/env bash
# 建置並啟動 macOS 客戶端。用法: ./run-client.sh <主機IP> [額外參數…]
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v ffplay >/dev/null 2>&1 && [ ! -x /opt/homebrew/bin/ffplay ] && [ ! -x /usr/local/bin/ffplay ]; then
  echo "找不到 ffplay，請先： brew install ffmpeg"
fi

HOST="${1:-}"
if [ -z "$HOST" ]; then echo "用法: ./run-client.sh <主機IP> [--fullscreen] [--mouse abs] …"; exit 1; fi
shift || true

swift build -c release
exec ./.build/release/remoteplay client --host "$HOST" "$@"
