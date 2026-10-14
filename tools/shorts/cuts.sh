#!/bin/zsh
# cuts.sh <клип> — моменты (с от начала записи), где кадр резко меняется по яркости: смена слайда витрины.
# Бой кончается примерно за 1,2 с до такого момента (затемнение).
ffmpeg -f concat -safe 0 -i <(python3 -c "
import json,os
f=json.load(open('clips/$1/frames.json'))
for a,b in zip(f,f[1:]): print(f\"file '{os.path.abspath(a['file'])}'\nduration {b['t']-a['t']:.4f}\")") \
  -vf "fps=10,scale=180:-1,signalstats,metadata=print:key=lavfi.signalstats.YAVG" -f null - 2>&1 \
  | grep -E "pts_time|YAVG" | paste - - \
  | awk '{split($0,a,"pts_time:"); split(a[2],b," "); split($0,c,"YAVG="); printf "%.1f %.0f\n", b[1], c[2]}' \
  | awk 'NR>1 && (($2-p)>6 || (p-$2)>6){print "cut", $1, p, "->", $2} {p=$2}'
