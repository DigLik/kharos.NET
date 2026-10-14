#!/bin/zsh
# record.sh [ru|en|all] — снять клипы ролика в clips/ текущей рабочей папки. Сценки витрины общие для языков,
# планы с интерфейсом игры (разворот MCV, продажа, исследования) снимаются на каждом языке: deploy-ru, deploy-en…
# Сцены одни: кнопки они жмут по ключу словаря игры (c.t), текст берётся на языке съёмки.
# Нужны dev-сервер на 5199 и хуки съёмки (git apply tools/shorts/hooks.patch). Съёмки идут строго по одной.
S=${0:a:h}/../..; V=${0:a:h}; LANGS=(${1:-all}); [[ $1 == all || -z $1 ]] && LANGS=(ru en)
R() { bun $S/rec.ts "$1" clips/$2 $3 --css=$S/showcase.css --size=720x1280 --dpr=1.5 --warm=$4 2>&1 | tail -1 | sed "s/^/$2: /"; }
R "http://localhost:5199/?scene=air&zoom=56&hour=11&orbit=1.5" air 8 2500
R "http://localhost:5199/?scene=armor&zoom=56&hour=14&orbit=1.5" armor 12 3000
R "http://localhost:5199/?scene=construction&zoom=64&hour=10&orbit=1.5" construction 14 1500
R "http://localhost:5199/?scene=defense&zoom=46&hour=13&orbit=1.5" defense 18 2000
R "http://localhost:5199/?scene=mining&zoom=42&hour=16&orbit=1.5" mining 12 3000
R "http://localhost:5199/?scene=mining&zoom=42&hour=22.5&orbit=1.5" night 10 3000
for L in $LANGS; do
  lang=$([[ $L == en ]] && echo en-US || echo ru-RU); sc=$V/scenes
  bun $S/rec.ts "http://localhost:5199/" clips/deploy-$L 24 --size=720x1280 --dpr=1.5 --warm=300 --lang=$lang --setup=$sc/deploy_setup.ts --act=$sc/deploy_act.ts 2>&1 | tail -1
  bun $S/rec.ts "http://localhost:5199/?sandbox" clips/sale-$L 24 --size=720x1280 --dpr=1.5 --warm=300 --lang=$lang --setup=$sc/sale_setup.ts --act=$sc/sale_act.ts 2>&1 | tail -1
  bun $S/rec.ts "http://localhost:5199/?sandbox" clips/tech-$L 12 --size=720x1280 --dpr=1.5 --warm=2500 --lang=$lang --act=$sc/tech_act.ts 2>&1 | tail -1
done
