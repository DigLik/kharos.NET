#!/bin/zsh
# Сверка переноса графики: кадры чертежей из TS (tools/art/dump.ts) и из модов (apps/client/examples/looks.rs) должны
# совпасть до пикселя. Запуск из корня: tools/art/check.sh. Код выхода — 0, только если совпали все.
cd "${0:A:h}/../.."
OUT=.cache/art
mkdir -p $OUT
bun tools/art/dump.ts $OUT/ts.bin || exit 1
cargo run -q -p kharos-client --release --example looks $OUT/mods.bin units/buggy turrets/gunner buildings/factory || exit 1
python3 - $OUT/ts.bin $OUT/mods.bin <<'PY'
import sys
a, b = open(sys.argv[1], 'rb').read(), open(sys.argv[2], 'rb').read()
parts = [('units/buggy', 256, 32 * 32 * 4), ('turrets/gunner', 64, 32 * 32 * 4), ('buildings/factory', 16, 80 * 80 * 4)]
off, failed = 0, 0
for name, frames, size in parts:
    bad = sum(1 for i in range(frames) if a[off + i * size:off + (i + 1) * size] != b[off + i * size:off + (i + 1) * size])
    off += frames * size
    print(('OK  ' if not bad else 'FAIL') + f' {name}: {frames - bad} из {frames} кадров совпали')
    failed += bad > 0
sys.exit(failed)
PY
