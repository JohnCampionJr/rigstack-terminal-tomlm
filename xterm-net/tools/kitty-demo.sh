#!/bin/bash
# Kitty graphics walkthrough -- shell port of kitty-demo.ps1, step for step.
# Run it INSIDE the terminal under test.
#
#     ./kitty-demo.sh              every step
#     ./kitty-demo.sh --only place just that one
#     ./kitty-demo.sh --no-pause   straight through
#
# Everything is raw escape sequences; needs only bash, base64, od, perl (for
# the one flat RGBA rectangle) -- no PowerShell, no python.

ONLY=''; NOPAUSE=0
while [ $# -gt 0 ]; do
  case "$1" in
    --only) ONLY="$2"; shift 2 ;;
    --no-pause) NOPAUSE=1; shift ;;
    *) echo "unknown arg $1" >&2; exit 2 ;;
  esac
done

# Echo OFF for the whole run: the terminal's replies to the queries below arrive on stdin,
# and with echo on the tty driver prints them as ^[_G... junk the moment they land.
if [ -t 0 ]; then
  SAVED_STTY=$(stty -g)
  stty -echo
  trap 'stty "$SAVED_STTY"' EXIT
fi

ESC=$'\e'
ST="${ESC}\\"
DIR="$(cd "$(dirname "$0")" && pwd)"
GRASS="$DIR/kitten-grass.png"
DARK="$DIR/kitten-dark.png"
TILE="$DIR/kitten-tile.png"

emit() { printf '%s' "$1"; }

# Width/height straight out of the IHDR: two big-endian ints at offset 16.
png_size() {  # sets PNG_W PNG_H
  local b=($(od -An -tu1 -j16 -N8 "$1"))
  PNG_W=$(( (b[0]<<24) | (b[1]<<16) | (b[2]<<8) | b[3] ))
  PNG_H=$(( (b[4]<<24) | (b[5]<<16) | (b[6]<<8) | b[7] ))
}

png_b64() { base64 < "$1" | tr -d '\n'; }

# Base64 RGBA for a flat rectangle with an alpha -- the overlap step's tint.
solid_b64() {  # w h r g b a
  perl -e 'print pack("C4", $ARGV[2], $ARGV[3], $ARGV[4], $ARGV[5]) x ($ARGV[0] * $ARGV[1])' \
       "$1" "$2" "$3" "$4" "$5" "$6" | base64 | tr -d '\n'
}

# Chunked transmission: 4096 base64 chars per sequence, m=1 on all but the last.
transmit() {  # control payload
  local control="$1" payload="$2" size=4096 at=0 first=1 take chunk more
  if [ ${#payload} -le $size ]; then
    emit "${ESC}_G${control};${payload}${ST}"
    return
  fi
  while [ $at -lt ${#payload} ]; do
    chunk="${payload:$at:$size}"
    at=$((at + ${#chunk}))
    [ $at -lt ${#payload} ] && more=1 || more=0
    if [ $first -eq 1 ]; then
      emit "${ESC}_G${control},m=${more};${chunk}${ST}"
      first=0
    else
      emit "${ESC}_Gm=${more};${chunk}${ST}"
    fi
  done
}

send_photo() {  # id path
  transmit "a=t,i=$1,f=100,q=2" "$(png_b64 "$2")"
}

# CSI 16 t -> CSI 6 ; height ; width t. Sized in cells, turned into pixels.
CELL_W=10; CELL_H=20
get_cell_size() {
  [ -t 0 ] || return
  local reply='' chunk
  local old; old=$(stty -g) || return
  stty raw -echo min 0 time 3
  printf '\e[16t'
  # Bounded: at most ~1.5s in total, whether or not the terminal answers at all.
  local i
  for i in 1 2 3 4 5; do
    chunk=$(dd bs=64 count=1 2>/dev/null)
    reply+="$chunk"
    case "$reply" in *t) break ;; esac
  done
  stty "$old"
  if [[ "$reply" =~ 6\;([0-9]+)\;([0-9]+)t ]]; then
    CELL_H=${BASH_REMATCH[1]}; CELL_W=${BASH_REMATCH[2]}
  fi
}
# Every step starts from a blank screen; pictures and captions sit at FIXED rows.
step() {  # name description -> returns 0 to run the step
  if [ -n "$ONLY" ] && [ "$ONLY" != "$1" ]; then return 1; fi
  emit "${ESC}[0m${ESC}[2J${ESC}[H"
  emit "=== $1 === $2"$'\r\n'
  return 0
}

say() {  # row line...
  local row="$1"; shift
  emit "${ESC}[${row};1H${ESC}[0J"
  local line
  for line in "$@"; do emit "$line"$'\r\n'; done
}

# Swallows anything already sitting on the tty -- the terminal's replies to the kitty queries
# (q=2 suppresses most, a=q answers anyway) land on stdin and would otherwise be echoed into the
# prompt as ^[_G... junk, or worse, be read as the operator's enter.
drain_tty() {
  local old; old=$(stty -g 2>/dev/null) || return
  stty -icanon -echo min 0 time 1
  while [ -n "$(dd bs=256 count=1 2>/dev/null)" ]; do :; done
  stty "$old"
}

pause() {
  if [ -n "$ONLY" ] || [ "$NOPAUSE" -eq 1 ]; then emit $'\r\n'; return; fi
  drain_tty
  emit $'\r\n'"${ESC}[90m(enter)${ESC}[0m "
  read -r _ < /dev/tty
}

reset_images() { [ -z "$ONLY" ] && emit "${ESC}_Ga=d,d=A,q=2${ST}"; }

emit "${ESC}[2J${ESC}[H"
emit 'Kitty graphics walkthrough. Each step draws, then waits.'$'\r\n'
emit "${ESC}[90mSteps: detect place scale crop delete behind overlap reveal placeholder animate stack-anim${ESC}[0m"$'\r\n'
get_cell_size

send_picture() { send_photo 1 "$GRASS"; }
png_size "$GRASS"; PHOTO_W=$PNG_W; PHOTO_H=$PNG_H

if step detect 'does this terminal answer the query at all?'; then
  emit "${ESC}_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA${ST}"
  say 4 'If the terminal supports Kitty graphics it just replied OK, invisibly.' \
        'It must also have drawn NOTHING: that payload is a valid 1x1 image.'
  pause
fi

if step place 'transmit once, show twice'; then
  send_picture
  emit "${ESC}[8;3H";  emit "${ESC}_Ga=p,i=1,c=16,r=8,C=1,q=2${ST}"
  emit "${ESC}[8;24H"; emit "${ESC}_Ga=p,i=1,c=16,r=8,C=1,q=2${ST}"
  say 18 'Two placements of ONE transmitted picture -- the same kitten twice,' \
         'sent over the wire once. Both upright and identical.'
  pause; reset_images
fi

if step scale 'the same picture stretched into a cell box'; then
  send_picture
  emit "${ESC}[8;3H";  emit "${ESC}_Ga=p,i=1,c=10,r=3,C=1,q=2${ST}"
  emit "${ESC}[8;20H"; emit "${ESC}_Ga=p,i=1,c=30,r=8,C=1,q=2${ST}"
  say 18 'Same pixels, two cell boxes: c/r stretch rather than clip.'
  pause; reset_images
fi

if step crop 'show only part of it'; then
  send_picture
  halfW=$((PHOTO_W / 2)); halfH=$((PHOTO_H / 2))
  emit "${ESC}[8;3H"
  emit "${ESC}_Ga=p,i=1,x=0,y=0,w=${halfW},h=${halfH},c=16,r=8,C=1,q=2${ST}"
  emit "${ESC}[8;24H"
  emit "${ESC}_Ga=p,i=1,x=${halfW},y=${halfH},w=${halfW},h=${halfH},c=16,r=8,C=1,q=2${ST}"
  say 18 'Left: the top-left quarter. Right: the bottom-right quarter.' \
         'Two crops of one picture, each blown up to the same cell box.'
  pause; reset_images
fi

if step delete 'remove by column, leaving its neighbour'; then
  send_picture
  emit "${ESC}[8;3H";  emit "${ESC}_Ga=p,i=1,c=16,r=8,C=1,q=2${ST}"
  emit "${ESC}[8;24H"; emit "${ESC}_Ga=p,i=1,c=16,r=8,C=1,q=2${ST}"
  say 18 'Two pictures. Deleting column 4 takes only the left one...'
  pause
  emit "${ESC}_Ga=d,d=x,x=4,q=2${ST}"
  say 18 '...and the right one is untouched.'
  pause; reset_images
fi

if step behind 'a picture behind the text (negative z)'; then
  send_photo 2 "$DARK"
  png_size "$DARK"
  bandY=$((PNG_H * 70 / 100)); bandH=$((PNG_H * 25 / 100))
  emit "${ESC}[8;1H"
  emit "${ESC}_Ga=p,i=2,x=0,y=${bandY},w=${PNG_W},h=${bandH},c=60,r=5,z=-1,C=1,q=2${ST}"
  emit "${ESC}[8;2HText typed onto a background image stays readable,"
  emit "${ESC}[9;2Hand the picture stays put underneath it."
  say 14 'Negative z means behind the TEXT rather than in front of it.'
  pause; reset_images
fi

if step overlap 'two pictures over the same cells, blended by z'; then
  send_photo 1 "$GRASS"
  send_photo 2 "$DARK"
  emit "${ESC}[8;3H"
  emit "${ESC}_Ga=p,i=1,c=20,r=9,z=1,C=1,q=2${ST}"
  # Alpha 110 of 255, raw RGBA, so the blend is unmistakable.
  pw=$((10 * CELL_W)); ph=$((9 * CELL_H))
  transmit "a=t,i=3,f=32,s=${pw},v=${ph},q=2" "$(solid_b64 "$pw" "$ph" 250 240 90 110)"
  emit "${ESC}[8;13H"
  emit "${ESC}_Ga=p,i=3,c=10,r=9,z=5,C=1,q=2${ST}"
  say 18 'A translucent yellow panel over the right half of the picture.' \
         'The kitten shows THROUGH it: the cell kept both, and they are drawn' \
         'bottom-up so the panel blends over what was already there.'
  pause; reset_images
fi

if step reveal 'delete the front picture, and the one behind is whole'; then
  send_photo 1 "$GRASS"
  send_photo 2 "$DARK"
  emit "${ESC}[8;3H"
  emit "${ESC}_Ga=p,i=1,c=20,r=9,z=1,C=1,q=2${ST}"
  emit "${ESC}[10;9H"
  emit "${ESC}_Ga=p,i=2,c=8,r=5,z=5,C=1,q=2${ST}"
  say 18 'The dark kitten sits on top of the grass one, opaque, hiding its middle.'
  pause
  emit "${ESC}_Ga=d,d=i,i=2,q=2${ST}"
  say 18 'Now deleted -- and the picture underneath comes back WHOLE.' \
         'No hole where the two overlapped: being covered never destroyed it.'
  pause; reset_images
fi

if step placeholder 'U+10EEEE cells, the way image.nvim places pictures'; then
  send_photo 300 "$TILE"
  png_size "$TILE"
  cols=$(( (PNG_W + CELL_W - 1) / CELL_W ))
  rows=$(( (PNG_H + CELL_H - 1) / CELL_H ))
  # The image id travels in the cell's FOREGROUND colour.
  id=300
  r=$(( (id >> 16) & 255 )); g=$(( (id >> 8) & 255 )); b=$(( id & 255 ))
  emit "${ESC}[38;2;${r};${g};${b}m"
  PLACEHOLDER=$(printf '\xf4\x8e\xbb\xae')   # U+10EEEE in UTF-8
  rowchars=''
  for ((i=0; i<cols; i++)); do rowchars+="$PLACEHOLDER"; done
  for ((row=0; row<rows; row++)); do
    emit "${ESC}[$((8 + row));3H"
    emit "$rowchars"
  done
  emit "${ESC}[0m"
  say $((9 + rows)) 'No placement command: the CELLS say where the picture goes.' \
      "A ${cols}x${rows} grid of U+10EEEE, one tile each. A shuffled tile would be obvious" \
      'on a photograph in a way it never is on a flat rectangle.'
  pause; reset_images
fi

ANIM_GAP=80
anim_frames() { ls "$DIR"/anim/*.png | sort; }

send_animation() {  # id
  local id="$1" first=1 f
  for f in $(anim_frames); do
    if [ $first -eq 1 ]; then
      transmit "a=t,i=${id},f=100,q=2" "$(png_b64 "$f")"
      first=0
    else
      transmit "a=f,i=${id},z=${ANIM_GAP},f=100,q=2" "$(png_b64 "$f")"
    fi
  done
  # The root frame has no gap of its own until one is set.
  emit "${ESC}_Ga=a,i=${id},r=1,z=${ANIM_GAP},q=2${ST}"
}

if step animate 'frames, and the terminal running them'; then
  send_animation 4
  emit "${ESC}[8;3H"
  emit "${ESC}_Ga=p,i=4,c=30,r=6,C=1,q=2${ST}"
  emit "${ESC}_Ga=a,i=4,s=3,q=2${ST}"
  nframes=$(anim_frames | wc -l | tr -d ' ')
  say 17 "${nframes} frames at ${ANIM_GAP}ms, looping. The terminal is driving this," \
         'not this script -- it has already finished sending.'
  pause
  emit "${ESC}_Ga=a,i=4,s=1,q=2${ST}"
  say 17 'Stopped.'
  pause; reset_images
fi

if step stack-anim 'an animation layered over a still picture'; then
  send_photo 1 "$GRASS"
  send_animation 4
  emit "${ESC}[8;3H"
  emit "${ESC}_Ga=p,i=1,c=24,r=10,z=1,C=1,q=2${ST}"
  emit "${ESC}[11;7H"
  emit "${ESC}_Ga=p,i=4,c=16,r=4,z=5,C=1,q=2${ST}"
  emit "${ESC}_Ga=a,i=4,s=3,q=2${ST}"
  say 19 'A running animation stacked on a still picture: the two features' \
         'at once. The animation re-uploads its texture on every frame while the' \
         'picture under it must NOT be redrawn from the wrong layer or flicker.'
  pause
  emit "${ESC}_Ga=d,d=i,i=4,q=2${ST}"
  say 19 'Animation deleted -- the still picture behind it is whole and unmarked.'
  pause; reset_images
fi

emit "${ESC}[0m"$'\r\n''Done.'$'\r\n'
