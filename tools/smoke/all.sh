#!/bin/zsh
# Всё рабочее разом: тесты, сборки как в CI (клиент, сервер бандлом и бинарником, оболочка), игра в Chrome с Vite и из
# dist (песочница и меню), сервер вживую с репликой. Печатает OK или FAIL по каждой проверке; кадры — в .cache/smoke.
# Запуск из корня: tools/smoke/all.sh
cd "${0:A:h}/../.."
OUT=.cache/smoke
mkdir -p $OUT
ok() { echo "OK   $1" }
bad() { echo "FAIL $1"; echo "$2" | tail -30 }

out=$(bun test 2>&1)
echo "$out" | grep -E "^ *[0-9]+ (pass|fail)$" | tr '\n' ' '; echo
echo "$out" | grep -q "^ 0 fail" && ok test || bad test "$out"
out=$(bun run build 2>&1) && ok build || bad build "$out"
rm -rf $OUT/out
out=$(bun build server/main.ts --target=bun --minify --outdir $OUT/out --entry-naming kharos-server-bun.js --asset-naming '[name].[ext]' 2>&1) && [ -f $OUT/out/luau.wasm ] && ok server-bundle || bad server-bundle "$out"
out=$(bun build server/main.ts --compile --minify --outfile $OUT/out/kharos-server 2>&1) && ok server-binary || bad server-binary "$out"
out=$(bun run bundle:native 2>&1) && ok native-bundle || bad native-bundle "$out"
out=$(cd native && cargo check 2>&1) && ok cargo-check || bad cargo-check "$out"

pkill -f "vite --port 5199"; pkill -f "vite preview --port 5200"; sleep 0.5
(npx vite --port 5199 --strictPort > $OUT/vite.log 2>&1 &)
(npx vite preview --port 5200 --strictPort > $OUT/preview.log 2>&1 &)
for i in {1..20}; do curl -s -o /dev/null http://localhost:5199/ && curl -s -o /dev/null http://localhost:5200/ && break; sleep 0.5; done
bun tools/smoke/browser.ts "http://localhost:5199/?sandbox" $OUT/dev.png 8 4 > $OUT/web.log && ok web-dev || bad web-dev "$(cat $OUT/web.log)"
bun tools/smoke/browser.ts "http://localhost:5200/" $OUT/menu.png 6 4 > $OUT/web.log && ok web-menu || bad web-menu "$(cat $OUT/web.log)"
bun tools/smoke/browser.ts "http://localhost:5200/?sandbox" $OUT/dist.png 6 4 > $OUT/web.log && ok web-dist || bad web-dist "$(cat $OUT/web.log)"
# Без воркеров, как в нативной оболочке: хост локальной игры — в том же потоке.
SMOKE_NO_WORKERS=1 bun tools/smoke/browser.ts "http://localhost:5200/?sandbox" $OUT/inprocess.png 8 4 > $OUT/web.log && ok web-inprocess || bad web-inprocess "$(cat $OUT/web.log)"
pkill -f "vite --port 5199"; pkill -f "vite preview --port 5200"

# С модом mods/hello: машина Luau (luau.wasm) должна найтись и рядом с бандлом, и внутри бинарника.
out=$(SMOKE_MOD=mods/hello bun tools/smoke/server.ts bun $OUT/out/kharos-server-bun.js 2>&1) && ok server-live-bundle || bad server-live-bundle "$out"
out=$(SMOKE_MOD=mods/hello bun tools/smoke/server.ts $OUT/out/kharos-server 2>&1) && ok server-live-binary || bad server-live-binary "$out"
