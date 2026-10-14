#!/bin/zsh
# open.sh <w> <h> <dpr> [url] — headless Chrome на 9334 с окном нужного размера, в новой игре
S=${0:a:h}; W=$1; H=$2; D=$3; URL=${4:-http://localhost:5199/}
pkill -f kharos-chrome-ui; sleep 0.5; rm -rf /tmp/kharos-chrome-ui
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless=new --remote-debugging-port=9334 --user-data-dir=/tmp/kharos-chrome-ui --window-size=$W,$((H+87)) --force-device-scale-factor=$D --hide-scrollbars --mute-audio $URL >/dev/null 2>&1 &
sleep 3
cd $S
b() { bun cdp.ts eval "(()=>{const b=[...document.querySelectorAll('button')].find(b=>b.textContent.trim()==='$1'); b&&b.click(); return !!b})()" >/dev/null; }
for i in {1..20}; do r=$(bun cdp.ts eval "!![...document.querySelectorAll('button')].find(b=>b.textContent.trim()==='Продолжить')"); [[ $r == true ]] && break; sleep 0.5; done
b Продолжить; sleep 1
if [[ -z $4 ]]; then b 'Новая игра'; sleep 0.8
  if [[ -n $SEED ]]; then bun cdp.ts eval "(()=>{const i=document.querySelector('input[type=number]'); const set=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set; set.call(i,'$SEED'); i.dispatchEvent(new Event('input',{bubbles:true})); return i.value})()" >/dev/null; sleep 0.3; fi
  b Начать; sleep 5; fi
bun cdp.ts eval "[innerWidth, innerHeight]" | tr -d '\n '; echo
