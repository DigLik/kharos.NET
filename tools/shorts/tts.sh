#!/bin/zsh
# tts.sh <lines.txt> — озвучка реплик «NN|текст» голосом ru-RU-DmitryNeural (или VOICE=…, RATE=…) в vo/NN.aiff (тишина по краям срезается).
# Нужен edge-tts: python3 -m venv ~/.kharos-tts && ~/.kharos-tts/bin/pip install edge-tts
mkdir -p vo
while IFS='|' read n t; do
  ~/.kharos-tts/bin/edge-tts --voice ${VOICE:-ru-RU-DmitryNeural} --rate=${RATE:-+8%} --text "$t" --write-media vo/$n.mp3 2>/dev/null
  ffmpeg -v error -y -i vo/$n.mp3 -af "silenceremove=start_periods=1:start_threshold=-50dB,areverse,silenceremove=start_periods=1:start_threshold=-50dB,areverse" vo/$n.aiff
  printf "%s %.2f\n" $n $(ffprobe -v error -show_entries format=duration -of csv=p=0 vo/$n.aiff)
done < ${1:-lines.txt}
