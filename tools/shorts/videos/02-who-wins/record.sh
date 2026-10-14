#!/bin/zsh
# record.sh — снять четыре дуэли в clips/ текущей рабочей папки. Интерфейса в кадре нет — клипы общие для языков.
# Нужны dev-сервер на 5199 и хуки съёмки (git apply tools/shorts/hooks.patch). Съёмки идут строго по одной.
# Витрина после конца боя затемняется и начинает новый бой на другой карте: границы боёв ищет cuts.sh,
# и под них подогнаны начала планов в plan.json — после пересъёмки сверить.
S=${0:a:h}/../..
U="http://localhost:5199/?scene=battle&orbit=1.5"
R() { bun $S/rec.ts "$1" clips/$2 $3 --size=720x1280 --dpr=1.5 --css=$S/showcase.css 2>&1 | tail -1 | sed "s/^/$2: /"; }
R "$U&zoom=40&hour=13&a=infantry:40&b=tank:4" d1 32
R "$U&zoom=44&hour=10&a=tank:4&b=drone:10" d2 32
R "$U&zoom=44&hour=16&a=drone:10&b=rocketeer:20" d3 24
R "$U&zoom=40&hour=21&a=rocketeer:20&b=infantry:40" d4 26
