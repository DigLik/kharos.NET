#!/usr/bin/env sh
# Ставит WASI SDK для сборки модов NativeAOT-LLVM (docs/dotnet.md, Р5) в dotnet/.cache/wasi-sdk и печатает путь.
# Двойник tools/wasi-sdk.ps1 для Linux и macOS (CI). Использование: sh dotnet/tools/wasi-sdk.sh [версия]
set -eu

version="${1:-29}"
cache="$(cd "$(dirname "$0")/.." && pwd)/.cache/wasi-sdk"
case "$(uname -m)" in
  x86_64 | amd64) arch=x86_64 ;;
  aarch64 | arm64) arch=arm64 ;;
  *) echo "WASI SDK: архитектура $(uname -m) не поддерживается" >&2; exit 1 ;;
esac
case "$(uname -s)" in
  Darwin) os=macos ;;
  *) os=linux ;;
esac
name="wasi-sdk-$version.0-$arch-$os"
target="$cache/$name"

if [ ! -f "$target/VERSION" ]; then
  mkdir -p "$cache"
  url="https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-$version/$name.tar.gz"
  echo "WASI SDK: качаю $url" >&2
  curl -fsSL "$url" | tar -xz -C "$cache"
fi

echo "$target"
