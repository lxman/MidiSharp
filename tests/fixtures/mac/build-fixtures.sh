#!/usr/bin/env bash
# Builds the macOS clean-room native plugin fixtures into ./out (git-ignored). arm64, ad-hoc signed.
set -euo pipefail
root="$(cd "$(dirname "$0")" && pwd)"
out="$root/out"
mkdir -p "$out"
clang="${CLANG:-/usr/bin/clang}"

# ── VST2: flat dylib named .so (Vst2Format globs *.so on non-Windows) ──
"$clang" -x objective-c -shared -O2 -fno-objc-arc -framework Cocoa \
  -o "$out/midisharp_gui_vst2.so" "$root/vst2_gui_fixture.m"
codesign -s - "$out/midisharp_gui_vst2.so"
echo "Built: $out/midisharp_gui_vst2.so"
