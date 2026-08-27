# Walks through the Kitty graphics protocol feature by feature, pausing between each so the screen
# can be looked at. Run it INSIDE the terminal under test.
#
#     pwsh -File kitty-demo.ps1              every step
#     pwsh -File kitty-demo.ps1 -Only place  just that one
#     pwsh -File kitty-demo.ps1 -NoPause     straight through
#
# Everything is emitted as raw escape sequences, so it depends on nothing but a terminal.

param(
    [string] $Only = '',
    [switch] $NoPause
)

$ESC = [char]27
$ST  = "$ESC\"

function Emit([string] $text) { [Console]::Out.Write($text) }

# The photographs, next to this script. Real pictures rather than flat rectangles because a solid
# colour hides most of what can go wrong -- a shuffled tile, a strip drawn from the wrong row, a
# picture stretched by a couple of pixels -- and hides the blend entirely, since a tint over one
# flat colour is just another flat colour.
#
# They are PNG and go over the wire as f=100, so the terminal decodes them and this script does no
# image work at all. That is also the only form here that exercises the PNG path -- Solid below is
# raw RGBA. Being 8-bit colormap PNGs, they walk the indexed and PLTE branches of the decoder too.
#
# Swapping in another picture needs no code change: the size is read out of the file.
$GrassKitten = Join-Path $PSScriptRoot 'kitten-grass.png'
$DarkKitten  = Join-Path $PSScriptRoot 'kitten-dark.png'

# A small one for the placeholder step, which cannot scale what it shows: a virtual placement's c
# and r are not honoured, so the picture covers its NATURAL number of cells and the only way to make
# it a sensible size on screen is to send a sensibly sized picture.
$TileKitten  = Join-Path $PSScriptRoot 'kitten-tile.png'

# Width and height straight out of the PNG header: an IHDR is always the first chunk, and its width
# and height are the two big-endian ints at offset 16.
#
# Cast to int BEFORE shifting. PowerShell's -shl on a [byte] shifts within eight bits, so
# `$bytes[18] -shl 8` is 0, not 256 -- the two high bytes of every dimension vanish and the size
# comes back as its own low byte. A 320x299 picture read as 64x43 still produces a valid crop, just
# a tiny one out of the corner, so nothing errors and the demo simply shows the wrong pixels. It
# only bites above 255, which is why a 120x112 image went on working while every larger one did not.
function Get-PngSize([string] $path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $w = ([int]$bytes[16] -shl 24) -bor ([int]$bytes[17] -shl 16) -bor ([int]$bytes[18] -shl 8) -bor [int]$bytes[19]
    $h = ([int]$bytes[20] -shl 24) -bor ([int]$bytes[21] -shl 16) -bor ([int]$bytes[22] -shl 8) -bor [int]$bytes[23]
    return @{ W = $w; H = $h }
}

function Get-PngBase64([string] $path) {
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
}

# Sends a photograph under an id. No s= or v=: a PNG carries its own dimensions and the terminal
# reads them, which is the point of the format.
function Send-Photo([int] $id, [string] $path) {
    Transmit "a=t,i=$id,f=100,q=2" (Get-PngBase64 $path)
}

# Base64 RGBA for a flat rectangle, with an alpha. The protocol carries RGBA; the terminal converts.
#
# The one place a flat colour is the right picture: the overlap step needs a TINT over a photograph,
# and a tint has to be featureless or you cannot tell which of the two you are looking through.
function Solid([int] $w, [int] $h, [byte] $r, [byte] $g, [byte] $b, [byte] $a = 255) {
    $bytes = New-Object byte[] ($w * $h * 4)
    for ($i = 0; $i -lt $w * $h; $i++) {
        $bytes[$i * 4]     = $r
        $bytes[$i * 4 + 1] = $g
        $bytes[$i * 4 + 2] = $b
        $bytes[$i * 4 + 3] = $a
    }
    [Convert]::ToBase64String($bytes)
}

# Chunked transmission: the protocol asks for 4096 base64 characters per escape sequence, with m=1
# on every chunk but the last. Sending one huge sequence usually works and is not what clients do.
function Transmit([string] $control, [string] $payload) {
    $size = 4096
    if ($payload.Length -le $size) {
        Emit "${ESC}_G$control;$payload$ST"
        return
    }

    $at = 0
    $first = $true
    while ($at -lt $payload.Length) {
        $take = [Math]::Min($size, $payload.Length - $at)
        $chunk = $payload.Substring($at, $take)
        $at += $take
        $more = if ($at -lt $payload.Length) { 1 } else { 0 }

        if ($first) {
            Emit "${ESC}_G$control,m=$more;$chunk$ST"
            $first = $false
        }
        else {
            Emit "${ESC}_Gm=$more;$chunk$ST"
        }
    }
}

# Asks the terminal how big a cell is, with CSI 16 t. Everything below is sized in CELLS and turned
# into pixels from the answer, so a picture lands on exact cell boundaries whatever the font is --
# otherwise a hardcoded pixel size covers a fractional number of columns and the placeholder step,
# which has to state its own width, writes past the edge of its own picture.
function Get-CellSize() {
    $fallback = @{ W = 10; H = 20 }
    if ([Console]::IsInputRedirected) { return $fallback }

    Emit "$ESC[16t"
    $deadline = [DateTime]::UtcNow.AddMilliseconds(500)
    $reply = ''

    while ([DateTime]::UtcNow -lt $deadline) {
        if ([Console]::KeyAvailable) {
            $reply += [Console]::ReadKey($true).KeyChar
            if ($reply.EndsWith('t')) { break }
        }
        else { Start-Sleep -Milliseconds 10 }
    }

    # CSI 6 ; height ; width t
    if ($reply -match '6;(\d+);(\d+)t') {
        return @{ W = [int]$Matches[2]; H = [int]$Matches[1] }
    }
    return $fallback
}

$cell = Get-CellSize

# Every step starts from a blank screen, and the header goes at the top of it.
#
# The clear is not cosmetic. Each step positions its pictures and its caption at FIXED rows -- row 8
# for the pictures, further down for the text -- because that is the only way to say where a picture
# should land. A header that merely scrolled down would leave the previous step's caption sitting on
# those same rows, and the next caption would overwrite it character by character: a short line over
# a long one leaves the tail of the long one behind, which reads as the terminal having drawn
# nonsense rather than as this script never having erased anything.
function Step([string] $name, [string] $description) {
    if ($Only -and $Only -ne $name) { return $false }
    Emit "$ESC[0m$ESC[2J$ESC[H"
    Emit "=== $name === $description`r`n"
    return $true
}

# The explanation under the pictures, from a fixed row.
#
# Erases from that row to the bottom before writing, so a caption replaces its predecessor whole
# instead of overwriting the part it happens to be long enough to reach. Every caller passes a row
# below its own pictures, which is what keeps the erase off them -- ED clears images along with the
# text, exactly as it does for a real program.
#
# CR LF rather than LF alone: a bare line feed moves down a row without returning to column one, so
# a two-line caption would start its second line under the end of the first.
function Say([int] $row, [string[]] $lines) {
    Emit "$ESC[$row;1H$ESC[0J"
    foreach ($line in $lines) { Emit "$line`r`n" }
}

function Pause() {
    if ($Only -or $NoPause) { Emit "`r`n"; return }
    Emit "`r`n$ESC[90m(enter)$ESC[0m "
    [void][Console]::ReadLine()
}

# Frees the pictures between steps. The next Step clears the screen, which takes the placements with
# it, but the images stay in the terminal's registry until something says otherwise. Skipped when a
# single step was asked for: the point of asking for one is to look at what it drew.
function Reset() { if (-not $Only) { Emit "${ESC}_Ga=d,d=A,q=2$ST" } }

Emit "$ESC[2J$ESC[H"
Emit "Kitty graphics walkthrough. Each step draws, then waits.`r`n"
Emit "$ESC[90mSteps: detect place scale crop delete behind overlap reveal placeholder animate stack-anim$ESC[0m`r`n"

# ---------------------------------------------------------------------------------------------

# Transmits the shared picture under id 1. Called by every step that shows it rather than once at
# the top, so that -Only <step> works: transmitting again under the same id simply replaces it.
function Send-Picture() { Send-Photo 1 $GrassKitten }

# The picture's own pixel size, which the crop step states its rectangle in.
$photo = Get-PngSize $GrassKitten
if (Step 'detect' 'does this terminal answer the query at all?') {
    Emit "${ESC}_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA$ST"
    Say 4 @(
        'If the terminal supports Kitty graphics it just replied OK, invisibly.',
        'It must also have drawn NOTHING: that payload is a valid 1x1 image.')
    Pause
}

if (Step 'place' 'transmit once, show twice') {
    Send-Picture
    Emit "$ESC[8;3H"
    Emit "${ESC}_Ga=p,i=1,c=16,r=8,C=1,q=2$ST"
    Emit "$ESC[8;24H"
    Emit "${ESC}_Ga=p,i=1,c=16,r=8,C=1,q=2$ST"
    Say 18 @(
        'Two placements of ONE transmitted picture -- the same kitten twice,',
        'sent over the wire once. Both upright and identical.')
    Pause
    Reset
}

if (Step 'scale' 'the same picture stretched into a cell box') {
    Send-Picture
    Emit "$ESC[8;3H"
    Emit "${ESC}_Ga=p,i=1,c=10,r=3,C=1,q=2$ST"
    Emit "$ESC[8;20H"
    Emit "${ESC}_Ga=p,i=1,c=30,r=8,C=1,q=2$ST"
    Say 18 @('Same pixels, two cell boxes: c/r stretch rather than clip.')
    Pause
    Reset
}

if (Step 'crop' 'show only part of it') {
    Send-Picture
    $halfW = [int]($photo.W / 2)
    $halfH = [int]($photo.H / 2)
    Emit "$ESC[8;3H"
    Emit "${ESC}_Ga=p,i=1,x=0,y=0,w=$halfW,h=$halfH,c=16,r=8,C=1,q=2$ST"
    Emit "$ESC[8;24H"
    Emit "${ESC}_Ga=p,i=1,x=$halfW,y=$halfH,w=$halfW,h=$halfH,c=16,r=8,C=1,q=2$ST"
    Say 18 @(
        'Left: the top-left quarter. Right: the bottom-right quarter.',
        'Two crops of one picture, each blown up to the same cell box.')
    Pause
    Reset
}

if (Step 'delete' 'remove by column, leaving its neighbour') {
    Send-Picture
    Emit "$ESC[8;3H";  Emit "${ESC}_Ga=p,i=1,c=16,r=8,C=1,q=2$ST"
    Emit "$ESC[8;24H"; Emit "${ESC}_Ga=p,i=1,c=16,r=8,C=1,q=2$ST"
    Say 18 @('Two pictures. Deleting column 4 takes only the left one...')
    Pause
    Emit "${ESC}_Ga=d,d=x,x=4,q=2$ST"
    Say 18 @('...and the right one is untouched.')
    Pause
    Reset
}

if (Step 'behind' 'a picture behind the text (negative z)') {
    Send-Photo 2 $DarkKitten

    # A horizontal band of the picture rather than the whole of it, stretched into a wide short box.
    # A background wants to be wide and shallow, and cropping to a strip first is what keeps it from
    # being squashed out of recognition -- and it is a real photograph, so text sitting on it is a
    # fair test of whether the glyphs stay legible rather than a flat colour that flatters them.
    # The lower part of the picture, which is a dark patterned cushion rather than the animal. A
    # background has to be dark enough for the text on it to stay readable -- the band across the
    # kitten's white face is the prettier picture and makes this step argue against itself -- and
    # patterned rather than flat, so a strip drawn from the wrong row still shows.
    $dark = Get-PngSize $DarkKitten
    $bandY = [int]($dark.H * 0.70)
    $bandH = [int]($dark.H * 0.25)
    Emit "$ESC[8;1H"
    Emit "${ESC}_Ga=p,i=2,x=0,y=$bandY,w=$($dark.W),h=$bandH,c=60,r=5,z=-1,C=1,q=2$ST"
    Emit "$ESC[8;2HText typed onto a background image stays readable,"
    Emit "$ESC[9;2Hand the picture stays put underneath it."
    Say 14 @('Negative z means behind the TEXT rather than in front of it.')
    Pause
    Reset
}

if (Step 'overlap' 'two pictures over the same cells, blended by z') {
    Send-Photo 1 $GrassKitten
    Send-Photo 2 $DarkKitten

    # The lower picture, and a translucent copy of the other one half over it. Two photographs
    # rather than a picture under a tint: a flat colour would blend to another flat colour and
    # prove nothing about WHICH pixels came through.
    Emit "$ESC[8;3H"
    Emit "${ESC}_Ga=p,i=1,c=20,r=9,z=1,C=1,q=2$ST"

    # Alpha 110 of 255, as raw RGBA, so the blend is unmistakable.
    #
    # Placed with c and r rather than left at its natural size. The pixel dimensions below are a
    # guess at the cell size, and a guess is all they can be when the terminal does not answer the
    # CSI 16 t query -- naming the cell box makes the panel cover exactly the right half of the
    # picture whatever the answer was.
    Transmit "a=t,i=3,f=32,s=$(10 * $cell.W),v=$(9 * $cell.H),q=2" (Solid (10 * $cell.W) (9 * $cell.H) 250 240 90 110)
    Emit "$ESC[8;13H"
    Emit "${ESC}_Ga=p,i=3,c=10,r=9,z=5,C=1,q=2$ST"

    Say 18 @(
        'A translucent yellow panel over the right half of the picture.',
        'The kitten shows THROUGH it: the cell kept both, and they are drawn',
        'bottom-up so the panel blends over what was already there.')
    Pause
    Reset
}

if (Step 'reveal' 'delete the front picture, and the one behind is whole') {
    Send-Photo 1 $GrassKitten
    Send-Photo 2 $DarkKitten

    Emit "$ESC[8;3H"
    Emit "${ESC}_Ga=p,i=1,c=20,r=9,z=1,C=1,q=2$ST"

    # Opaque, and squarely over the middle of the one below. This is the case that used to destroy
    # the covered cells outright.
    Emit "$ESC[10;9H"
    Emit "${ESC}_Ga=p,i=2,c=8,r=5,z=5,C=1,q=2$ST"

    Say 18 @('The dark kitten sits on top of the grass one, opaque, hiding its middle.')
    Pause

    Emit "${ESC}_Ga=d,d=i,i=2,q=2$ST"
    Say 18 @(
        'Now deleted -- and the picture underneath comes back WHOLE.',
        'No hole where the two overlapped: being covered never destroyed it.')
    Pause
    Reset
}

if (Step 'placeholder' 'U+10EEEE cells, the way image.nvim places pictures') {
    Send-Photo 300 $TileKitten

    # How many cells the picture covers at its own size, which is the grid of placeholders to write.
    # Computed rather than hardcoded because it depends on the cell size, and a grid that disagrees
    # with the picture is the whole failure mode here: too few cells crops it, too many address
    # tiles that do not exist.
    $tile = Get-PngSize $TileKitten
    $cols = [Math]::Ceiling($tile.W / $cell.W)
    $rows = [Math]::Ceiling($tile.H / $cell.H)

    # The image id travels in the cell's FOREGROUND colour, not in an escape sequence.
    $id = 300
    $r = ($id -shr 16) -band 0xFF
    $g = ($id -shr 8) -band 0xFF
    $b = $id -band 0xFF
    Emit "$ESC[38;2;$r;$g;${b}m"

    $placeholder = [char]::ConvertFromUtf32(0x10EEEE)
    for ($row = 0; $row -lt $rows; $row++) {
        Emit "$ESC[$(8 + $row);3H"
        Emit ($placeholder * $cols)
    }
    Emit "$ESC[0m"
    Say (9 + $rows) @(
        'No placement command: the CELLS say where the picture goes.',
        "A $($cols)x$($rows) grid of U+10EEEE, one tile each. A shuffled tile would be obvious",
        'on a photograph in a way it never is on a flat rectangle.')
    Pause
    Reset
}

# The frames of the animation, in order. A real one rather than four flat colours: a colour cycle
# looks identical whether the frames are played in order, backwards, or one of them is dropped.
# Muybridge's trotting cat is about as good as this gets -- a locomotion study is a sequence whose
# whole point is that every frame differs from its neighbours in a way the eye checks automatically.
# ms. Muybridge shot the plates faster than the GIF replays them; 80ms is a natural trot and makes
# a dropped or reordered frame obvious, which a slow flicker would hide.
$AnimGap = 80
function Get-AnimFrames() {
    Get-ChildItem (Join-Path $PSScriptRoot 'anim') -Filter '*.png' | Sort-Object Name
}

# Sends the animation under an id: the first PNG becomes the picture, the rest are added as frames.
function Send-Animation([int] $id) {
    $frames = Get-AnimFrames
    Transmit "a=t,i=$id,f=100,q=2" (Get-PngBase64 $frames[0].FullName)
    foreach ($f in $frames[1..($frames.Count - 1)]) {
        Transmit "a=f,i=$id,z=$AnimGap,f=100,q=2" (Get-PngBase64 $f.FullName)
    }
    # The root frame has no gap of its own until one is set, so without this it holds forever.
    Emit "${ESC}_Ga=a,i=$id,r=1,z=$AnimGap,q=2$ST"
}

if (Step 'animate' 'frames, and the terminal running them') {
    Send-Animation 4

    # A wide, shallow box: the plates are better than two to one, and squeezing them into a square
    # would make the cat look like a different animal.
    Emit "$ESC[8;3H"
    Emit "${ESC}_Ga=p,i=4,c=30,r=6,C=1,q=2$ST"

    Emit "${ESC}_Ga=a,i=4,s=3,q=2$ST"         # s=3 runs and loops

    Say 17 @(
        "$((Get-AnimFrames).Count) frames at ${AnimGap}ms, looping. The terminal is driving this,",
        'not this script -- it has already finished sending.')
    Pause

    Emit "${ESC}_Ga=a,i=4,s=1,q=2$ST"
    Say 17 @('Stopped.')
    Pause
    Reset
}

if (Step 'stack-anim' 'an animation layered over a still picture') {
    Send-Photo 1 $GrassKitten
    Send-Animation 4

    Emit "$ESC[8;3H"
    Emit "${ESC}_Ga=p,i=1,c=24,r=10,z=1,C=1,q=2$ST"

    Emit "$ESC[11;7H"
    Emit "${ESC}_Ga=p,i=4,c=16,r=4,z=5,C=1,q=2$ST"
    Emit "${ESC}_Ga=a,i=4,s=3,q=2$ST"

    Say 19 @(
        'A running animation stacked on a still picture: the two features',
        'at once. The animation re-uploads its texture on every frame while the',
        'picture under it must NOT be redrawn from the wrong layer or flicker.')
    Pause

    Emit "${ESC}_Ga=d,d=i,i=4,q=2$ST"
    Say 19 @('Animation deleted -- the still picture behind it is whole and unmarked.')
    Pause
    Reset
}

Emit "$ESC[0m`r`nDone.`r`n"
