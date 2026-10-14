#!/bin/zsh
# record.sh [ru|en|all] — снять клипы ролика в clips/ текущей рабочей папки. Всё снимается в ?sandbox с интерфейсом игры,
# поэтому на каждом языке: base-ru, strike-en… Постановку (чужая крепость, своя шахта с боеголовкой) делает
# scenes/stage.js прямо в воркере хоста — через хук __host; ?fog=0 убирает туман. Оба хука — в hooks.patch.
# Нужен dev-сервер на 5199 и git apply tools/shorts/hooks.patch. Съёмки идут строго по одной; подготовка ждёт ~30 с.
S=${0:a:h}/../..; sc=${0:a:h}/scenes; LANGS=(${1:-all}); [[ $1 == all || -z $1 ]] && LANGS=(ru en)
U="http://localhost:5199/?sandbox&fog=0"
for L in $LANGS; do
  lang=$([[ $L == en ]] && echo en-US || echo ru-RU)
  R() { bun $S/rec.ts "$U" clips/$1-$L $2 --size=720x1280 --dpr=1.5 --warm=300 --lang=$lang --css=$sc/nuke.css --setup=$sc/$3 --act=$sc/$1_act.ts 2>&1 | tail -1 | sed "s/^/$1-$L: /"; }
  R base 9 setup.ts
  R strike 30 setup.ts
  R icp 12 setup_icp.ts
done
