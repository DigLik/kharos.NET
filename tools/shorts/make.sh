#!/bin/zsh
# make.sh <videos/NN-имя> <ru|en> — из уже снятых клипов (clips/ в текущей рабочей папке) собирает ролик языка:
# озвучка → подписи → музыка → монтаж → обложка. Результат — <язык>/<out>.mp4 и <out>-cover.jpg в рабочей папке.
# Нужен dev-сервер на 5199 (шрифт подписей берётся из игры).
set -e
S=${0:a:h}; V=${1:a}; L=$2; D=$V/$L
[[ -f $D/plan.json ]] || { echo "нет $D/plan.json"; exit 1 }
voice=$(python3 -c "import json;print(json.load(open('$D/plan.json'))['voice'])")
rate=$(python3 -c "import json;print(json.load(open('$D/plan.json')).get('rate','+8%'))")
mkdir -p $L
if [[ ! -f music.wav ]]; then
  bun $S/music.ts
  ffmpeg -v error -y -i music_raw.wav -af "aecho=0.8:0.7:120|260|430:0.35|0.25|0.18,lowpass=f=5000,loudnorm=I=-24" music.wav
fi
cd $L
VOICE=$voice RATE=$rate zsh $S/tts.sh $D/lines.txt
zsh $S/open.sh 1080 1920 1 "http://localhost:5199/?scene=night" >/dev/null
bun $S/caps.ts $D/caps.json caps
pkill -f kharos-chrome-ui || true
python3 $S/build.py $D/plan.json
