#!/bin/sh
# Упаковка linux-релиза без PowerShell: то же, что package.ps1 (docs/dotnet-n7.md, Н7-6, Н7-7).
# Использование: sh dotnet/tools/package.sh <rid> <desktop|server> <версия> <папка-вывода>
# rid: linux-x64 | linux-arm64. Моды (host.wasm, client.wasm) собираются заранее: tools/wasi-sdk.sh.
set -eu
RID=${1:?rid}; KIND=${2:?desktop|server}; VERSION=${3:?версия}; OUT=${4:?папка}
case "$RID" in linux-x64|linux-arm64) ;; *) echo "package.sh: только linux-x64 и linux-arm64" >&2; exit 2;; esac
ROOT=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
if [ "$KIND" = desktop ]; then
  PROJECT=apps/Kharos.Desktop/Kharos.Desktop.csproj; EXE=kharos; TOP=kharos-$VERSION-$RID; ARCHIVE=kharos-$RID.tar.gz
  REQ="kharos libwgpu_native.so libopenal.so libglfw.so.3 libwasmtime.so kharos.desktop kharos.png"
  SECT=DESKTOP; DROP=SERVER
else
  PROJECT=apps/Kharos.Server/Kharos.Server.csproj; EXE=kharos-server; TOP=kharos-server-$VERSION-$RID; ARCHIVE=kharos-server-$RID.tar.gz
  REQ="kharos-server libwasmtime.so"
  SECT=SERVER; DROP=DESKTOP
fi
WORK=$OUT/work-$KIND-$RID
rm -rf "$WORK"; mkdir -p "$WORK"
echo "== publish $KIND $RID $VERSION"
dotnet publish "$ROOT/$PROJECT" -c Release -r "$RID" -o "$WORK/$TOP" "-p:KharosVersion=$VERSION" -p:KharosShipMods=base -nologo
D=$WORK/$TOP
# Символы отладки проектных сборок NativeAOT кладёт в публикацию сама - в архив их не берём.
find "$D" -type f \( -name '*.pdb' -o -name '*.dbg' -o -name 'Kharos.*.xml' \) -delete

# --- состав ---
BAD=""
for f in $REQ mods/base/mod.json mods/base/host.wasm mods/base/client.wasm; do [ -f "$D/$f" ] || BAD="$BAD\n  нет $f"; done
for m in data schema i18n; do ls "$D"/mods/base/$m/*.json >/dev/null 2>&1 || BAD="$BAD\n  нет mods/base/$m/*.json"; done
EXTRA=$(cd "$D" && find . -type f \( -name '*.pdb' -o -name '*.dbg' -o -path '*/hello/*' \) ; ls mods 2>/dev/null | grep -v '^base$' | sed 's|^|mods/|' || true)
[ -z "$EXTRA" ] || BAD="$BAD\n  лишнее: $EXTRA"
if [ -n "$BAD" ]; then printf "Состав сборки не годится:$BAD\n" >&2; exit 1; fi
echo "== верхний уровень: $(cd "$D" && ls -p | grep -v / | tr '\n' ' ')"

# --- лицензии и README ---
mkdir -p "$D/LICENSES"; cp -r "$ROOT"/licenses/* "$D/LICENSES/"
DROPOS=WIN
sed -e "/^\[\[$DROP\]\]\$/,/^\[\[\/$DROP\]\]\$/d" -e "/^\[\[$DROPOS\]\]\$/,/^\[\[\/$DROPOS\]\]\$/d" \
    -e '/^\[\[\/\?[A-Z]*\]\]$/d' \
    -e "s|{VERSION}|$VERSION|g; s|{RID}|$RID|g; s|{EXE}|$EXE|g; s|{ARCHIVE}|$ARCHIVE|g" \
    "$ROOT/dist/README.txt" > "$D/README.txt"

# --- архив ---
chmod +x "$D/$EXE"
rm -f "$OUT/$ARCHIVE"
tar -czf "$OUT/$ARCHIVE" -C "$WORK" "$TOP"
(cd "$OUT" && sha256sum "$ARCHIVE" > "$ARCHIVE.sha256")
echo "== $ARCHIVE: $(du -h "$OUT/$ARCHIVE" | cut -f1), $(cut -d' ' -f1 "$OUT/$ARCHIVE.sha256")"
rm -rf "$WORK"
