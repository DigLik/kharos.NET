#!/bin/zsh
# sheet.sh <clip> — 5 кадров клипа в ряд, 1500 px шириной
d=clips/$1; n=$(ls $d/*.jpg | wc -l | tr -d ' ')
args=(); for k in 0 1 2 3 4; do i=$(( (n-1) * k / 4 )); args+=(-i $d/$(printf %05d $i).jpg); done
ffmpeg -v error -y $args -filter_complex "[0][1][2][3][4]hstack=5,scale=1500:-1" sheets/$1.jpg
