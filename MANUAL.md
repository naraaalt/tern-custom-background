# Wallpaper

A dithered image behind the whole Tern window, with a settings page.

The picture is rendered to a 1-bit (or small-palette) PNG by a small native
helper and painted under the whole window by a plugin style sheet, with the
fills the app draws on top of it cleared, so the terminal grid, sidebar, tab
bar and status line stay readable: the theme's colours are left alone and the
picture is scrimmed, not the other way round.

## Install

```
tern plugin link <this directory>
```

On the first window start the plugin builds `engine/dither.exe` from
`engine/Dither.cs` with the C# compiler that ships with Windows
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), against
`System.Drawing`. Nothing else is needed: no ImageMagick, no ffmpeg, no
runtime to install. To build it by hand:

```
powershell -ExecutionPolicy Bypass -File engine/build.ps1
```

A rebuild re-renders the wallpaper on the next setting you change; `tern
plugin reload` does it straight away (see the engine's stamp below).

## Use

- **Wallpaper: settings** in the palette (or the `plugin.wallpaper.settings`
  action) opens the settings block **beside the pane you asked from**, so the
  window you are restyling stays in view while you change it. It is a full
  page: an Image page, a Dither page, a Fit and opacity page, and a Status
  page that shows the render facts and a live preview of the dithered result.
  The placement is the `PLACEMENT` constant at the top of `window.luau`:
  `beside` (split), `below`, `tab`, `replace` or `preview`.
- **Closing the block.** The page's `Close` button (the one with the `esc`
  keycap) and Escape both close the pane, and Escape closes an open editor
  first, so one Escape never loses an edit. Asking for settings again while
  the block is open focuses it instead of splitting a second time.
- **`Ctrl+Alt+W`** opens it from anywhere in the window.
- **Palette: Image colours** reads the picture's own colours instead of taking
  a grey ramp off the shelf: pick how many (2–16) and whether they are the
  most used colours or median cut across the whole picture. The colours it
  settles on are filled into the Custom colours row, so switching to Custom
  continues from the picture rather than from the defaults.
- **Wallpaper: brighter / dimmer / reset opacity / on or off** are palette
  rows and bindable actions with no default chord, so they stay out of the
  keymap's way; add them to `settings.json` `keybinds` if you want them on
  keys too.
- **Use dropped images** (off by default) makes a dropped image file the
  source. It is off because claiming a drop takes it away from the terminal,
  which normally inserts the path.
- **Animate** (on by default) moves an animated source: an animated GIF's
  frames are dithered one by one into a grid image and the sheet steps through
  them at the GIF's own delays. **Frames** (2–48, 48 by default) caps how many
  are drawn, and the default is the cap, so a source with 48 frames or fewer
  keeps every frame it has and the pace the file set. Lowering it samples the
  source evenly and holds each drawn frame for the time of the ones it stands
  in for, which is how a long GIF is made cheap. Each frame is another cell,
  and the cells shrink until the grid fits inside Tern's 4096 px limit, so
  more frames cost resolution. A still image renders the same either way.
  While an animation is up the Fit row reads `cover` and is disabled, because
  the frames only line up as one grid; the stored choice comes back when the
  picture is still again.

## Settings

| Setting | Values | Default |
| --- | --- | --- |
| Image file | any path | none; the row starts empty and the wallpaper waits for one |
| Method | Error diffusion, Bayer matrix, Blue noise, Knoll, Riemersma, Threshold, Nearest | Error diffusion |
| Kernel | Floyd–Steinberg, Atkinson, Jarvis–Judice–Ninke, Stucki, Burkes, Sierra, Sierra two-row, Sierra lite, Fan, Shiau–Fan, Shiau–Fan 2, Simple 2D, Dizzy | Floyd–Steinberg |
| Matrix | 2×2, 4×4, 8×8, 16×16 (Bayer), or 2×2 … 16×16, blue noise (Knoll) | 8×8 |
| Block size | 1–25 px, blocks up the dithered result | 1 |
| Smooth blocks | on/off, average each block instead of sampling its middle | off |
| Palette | black and white, 4/8/16 greys, the image's own colours, custom `#rrggbb` list, or a retro preset: Game Boy, CGA / EGA, PICO-8, Commodore 64 | black and white |
| Colours to take | 2–16, how many the image's own palette reads | 8 |
| Chosen by | most used, median cut | most used |
| Pixel scale | 1–16, divides the source before the dither | 1 |
| Longest side | 1280/1920/2560/3840/4096 | 1920 |
| Animate | on/off, moves an animated source | on |
| Frames | 2–48, the most frames to draw | 48, the cap: a source with fewer keeps every frame it has |
| Threshold | 0–100% | 50% |
| Brightness | −100–100% | 0% |
| Contrast | 10–300% | 100% |
| Invert | on/off | off |
| Fit | `contain` (fit to screen), `cover` (fill the screen), `stretch` (stretch to screen), `tile` | `cover` |
| Opacity | 0–100% | 30% |
| Hard pixel edges | on/off (`image-rendering: pixelated`) | off |

The Kernel, Matrix and Block size rows are disabled while the method that does
not read them is running. A stored `bayer4`, `bayer8`, `threshold` or `none`
from the version that had them in `Algorithm` is moved onto `Method` once, when
the plugin first loads.

## How it fits together

```
window.luau  ── the style sheet: paints the picture once on the window itself
                (body), with a black scrim whose alpha is 1 - opacity, and
                clears the fills the app draws over it. Polls render_rev.
                With an animated render it paints one cell of the grid at a
                time instead, driven by @keyframes built from the delays.
host.luau    ── the render loop and the settings block. Polls rev, runs
                engine/dither.exe, writes picture, render, sig, render_rev and
                the animation's grid (anim_frames, anim_cols, anim_rows,
                anim_cell_w, anim_cell_h, anim_delays), and with the Image
                colours palette writes back the palette the engine read off
                the picture.
engine/      ── Dither.cs, built to dither.exe by build.ps1.
data/renders ── the rendered PNGs, in the plugin's data folder. The newest
                four are kept.
```

### One picture, one crop

The sheet used to name the window's four surfaces — `main#panel`,
`aside#sidebar`, `nav#tabbar`, `footer#statusline` — and paint the picture on
each. A background is fitted to the box it is painted on, so that was four
fits of one picture, and the four boxes are wildly different shapes: the
window is 1920×1032, the tab bar 1920×49, the status line 1920×32. Under
`cover` the two bars scaled the picture to the window's width — 5.2× — and
kept nine of its 207 rows: 98.8–108.2 in the tab bar and 100.4–106.6 in the
status line. Those are the lit rows of the portrait: 402 lit pixels across
rows 99–108, 40 a row, against a mean of 21 for the picture as a whole and 0
for its bottom rows. The pane beside them showed rows 9–198 of the 207, 94% of
the picture black. The result was a wallpaper that read as "it only shows at
the top and the bottom" — which is what it was doing.

The picture goes on `body` now, which is the whole window, so `fit` means
what it says: the screen. `background-attachment` is not a property Tern
parses, so a fixed-position layer behind four surfaces is not available;
one sheet on the window is the way to get one crop.

### An animation is one image

A GIF's frames are dithered separately and laid out in one grid — 31 frames as
6×6 — because a sheet can only be handed one image. The window half then walks
that grid with a `@keyframes` rule: one keyframe per frame, each moving
`background-position` by exactly one cell, with `animation-timing-function:
step-end` so each frame holds for its own time instead of sliding into the
next. The keyframe offsets are the delays the engine read out of the GIF and
summed, so the animation runs at the speed the file asked for; the same
numbers come back in the render's `delays=` field.

The grid is what makes the crop work. The sheet sizes the *whole grid* as if
it were one frame — `calc(cols * max(100vw, A * 100vh))` — and then positions
it so cell *k* sits where a single frame would have been; that is the same
`cover` crop the still sheet gets from `center`, which is why a still and an
animation of the same source line up.

Verified two ways, both with the timeline frozen (`tern ctl motion-at`, whose
own accuracy was checked first against a sheet with known keyframes).
Deterministically: a 4-frame render of a file whose frames are
1000/500/1000/500 ms of flat black, white, black, white reads black, white,
black, white at 100, 1200, 1800 and 2700 ms — the frames appear in the file's
order and are held for the file's own times. And against the atlas: seeking a
31-frame render to each frame's midpoint and asking which cell the window is
showing identifies 29 of the 31 frames — 3 to 30 exactly, and frame 0 — and
cannot separate frames 0, 1 and 2, which are 30 ms apart and nearly identical
in the source, so naming all three frame 0 is the closest the comparison can
get rather than evidence they were skipped. The comparison subtracts a
reference shot, so Tern's own interface — identical in both shots — cancels;
matching raw brightness instead does not work, because the interface over the
wallpaper contributes more to a pixel than the wallpaper does. Getting the cell
right also means the crop model is right: a crop off by one cell would name the
neighbouring frame.

The engine shrinks the cell until the grid fits: `ceil(sqrt(N))` columns and
`ceil(N / cols)` rows, scaled down by the same factor on both axes when either
side would pass 4096 px. The scaled size is **floored**, not rounded — six
columns of a cell rounded from 682.67 to 683 are 4098 px wide, and Tern
refuses the image — and clamped to `4096 / cols` as a second guarantee.

### What decides whether it reads as smooth

Two things, and neither is Tern: the frames drawn, and the dither.

**Frames.** The animation can only be as smooth as the file. A 31-frame GIF
asked for 12 frames is 11.7 fps and reads as a flip book, and the frames that
survive are the ones a sampling pattern picked, not the ones the file meant to
show. The default asks for the cap, so any source of 48 frames or fewer is
drawn whole and runs at its own rate — 30 fps for the GIF this was built
against. Tern has room for that pace: over a three-second sample it drew about
180 frames a second, with the paint at 0.32 ms at the median and 0.41 ms at
the 95th percentile against the 30 ms a slot lasts. Headroom, not a guarantee —
three seconds says nothing about the worst case, and this was measured with
the wallpaper as the only thing moving.

**Dither.** Ordered dithering ties its pattern to the pixel grid, so a pixel
changes only where the content under it changed. Error diffusion recomputes
its noise from its neighbours every frame, so pixels flip even where the source
barely moved, and the noise crawls over a moving picture. Measured on the same
31 frames as the share of pixels that flip between adjacent frames: Bayer 16×16
1.09%, blue noise 2.48%, Sierra 3-row 4.34%. A moving wallpaper reads cleanest
with an ordered mode — Bayer or Knoll — and it is the biggest visual difference
after the frame count. The measure is only meaningful while the content moves:
a still dithered picture does not change from frame to frame at all.

`animate` off, or a source with one frame, renders one cell and clears the six
`anim_*` keys, so the window half is back on the still path with nothing to
unlearn. `Fit` is forced to `cover` while an animation is up and the row says
so: `contain` and `tile` would show the neighbouring cells, because the cell's
edges are inside the image rather than at its edge.

Reduced motion is honoured: the sheet's `@media (prefers-reduced-motion:
reduce)` block drops the animation and leaves the first frame up. Tern does
not do this for you.

### The fills the app draws over the picture

Painting the window is not enough on its own: Tern's own surfaces are
transparent, but the content it draws on them is not. Measured on a live
window, opaque fills hid the picture under 33% of the window's probe points —
the transcript's code blocks, the settings page's cards and its text fields
all take `--code-bg`, more cards take `--card`, and two containers (the
composer, a note card) carry fills no token reaches. So the sheet clears
those four tokens and the two container classes, and the picture becomes the
window's one background. The app's own colours stay in its text, borders and
icons, which is what keeps it readable — the picture is scrimmed, not the
other way round.

With the picture off, the sheet is one `background-image: none` rule and
nothing else, so the app gets every fill back.

### Renders never go in the plugin folder

Tern watches every package directory **recursively** and reloads the whole
plugin on any change inside one. A render written to `renders/<n>.png` beside
the manifest therefore reloads the plugin on every value the user changes:
the settings block restarts, its view is rebuilt, and the page jumps back to
the top under the row being edited. So renders go to `tern.plugin.data`
instead, which is outside every watched folder.

The picture then has to reach the window half another way. A plugin sheet's
`url()` resolves against the package and refuses `..`, absolute paths and
links that leave it, so it cannot name a file in the data folder. The host
half reads the render back and publishes it as a `data:` URL — which a sheet
accepts anywhere — and the window half paints that. The PNG itself stays on
disk only for the block's own preview blob.

### The sheet and the poll are set up by the entry, not by `window_start`

A plugin reload drops the window half's VM and runs its entry again, but
`window_start` runs **once per window per plugin** — a reloaded VM never sees
it again. Tern also removes every `plugin:local:*` sheet on a reload. So the
install and the timer live at the top level of `window.luau`: put either of
them in `window_start` and the first `tern plugin reload` leaves the window
with no sheet and a dead poll, which no later kv write can revive. The same
applies to the engine build, which is why it is re-checked on every load.

The two halves share nothing but the plugin's kv store, because Tern gives a
plugin no other channel between its host and window halves:

| Key | Meaning |
| --- | --- |
| `rev` | bumped by anyone who changes a setting |
| `render` | the newest PNG's path, for the block's preview blob |
| `picture` | that PNG as a `data:` URL, for the window's sheet |
| `render_rev` | bumped when the windows must re-apply the sheet |
| `sig` | the render inputs that produced `render` |
| `render_seq` | the number the next render's file gets |
| `note` | the last render's one-line result, or why it failed |
| `close_pane` | a pane id the settings block asked to close |

`close_pane` is the one request that travels the other way. The host half owns
the page but has no `cx.layout` — that belongs to the window half — so the
block's Close button and Escape leave the pane id in the store and the window
half's poll carries it out. `PaneInfo.pane` and the block's `cx.pane` are the
same number, so the id names exactly one pane, and only the window actually
showing it finds it among its own panes; a second window on the same session is
left alone.

`rev` and `render_rev` are split so a display-only change (opacity, fit,
hard pixels, on/off) does not re-dither: the loop bumps `render_rev` alone and
the window repaints the picture it already has. `render_rev` is also what the
window half keys its install on, so any writer that moves it gets the sheet
re-installed — including the entry itself, which installs unconditionally on
load and so needs no help from the host half after a reload.

### Why every render is a new file

The block's preview blob is re-read only when `render` names a different file,
so a render that overwrote one fixed name would leave the preview showing the
first picture it ever sent. Each render therefore gets its own name and the
loop prunes all but the newest four.

The number in that name comes from `render_seq`, not from `rev`: `rev` only
moves when a *setting* does, and the engine being rebuilt is not a setting. A
render the settings did not ask for would otherwise land on a path that already
held a picture and overwrite it, and the preview would not re-read it because
its path had not changed.

`render_seq` is seeded from `rev` the first time it is used, because the files
already in the folder are numbered by `rev` and `prune` keeps the *highest*
numbers — starting from 1 would make the next render the oldest thing there and
delete it again immediately.

### The engine's stamp

`build.ps1` writes `engine/dither.stamp`, a hash of the `Dither.cs` it just
compiled, beside the binary. The render signature starts with it, so the
engine is an input to its own renders: after a rebuild the wallpaper re-renders
with the new binary, instead of leaving the picture the previous one made on
screen until the user happens to change a setting.

The stamp is read fresh on every settings change, so a rebuild is picked up
the next time anything is changed; `tern plugin reload` makes it immediate,
because a reload re-runs the first tick with no settings touched. The reload is
not otherwise needed — Tern does not reload a plugin just because a file in its
package changed.

```
powershell -ExecutionPolicy Bypass -File engine/build.ps1
tern plugin reload        # immediate; otherwise the next setting change does it
```

Measured: a stamp changed on its own re-rendered on the next settings change
with no reload (signature `feedface…`, new file r681); a stamp left unchanged
produced no new render; and the restored stamp's render matched a manual run of
the same settings byte for byte.

This used to be required for the sheet as well, because Tern caches a plugin
sheet's `url()` bytes **per path**: overwriting a file and re-installing the
sheet kept drawing the old bytes, and the cache survived clearing the sheet
and a plugin reload. The sheet paints a `data:` URL now, which has no path to
cache against, so that reason is gone — the preview is what keeps the names
unique.

## The engine

```
dither.exe --in <path> --out <path>
           [--mode diffusion|bayer|bluenoise|knoll|riemersma|threshold|none]
           [--algo fs|atkinson|jjn|stucki|burkes|sierra3|sierra2|sierra24a|
                   fan|shiaufan|shiaufan2|simple2d|dizzy]
           [--matrix 2|4|8|16] [--pattern 2|4|8|16|bluenoise]
           [--pixelate 1..25] [--smooth]
           [--palette bw|gray4|gray8|gray16|auto|custom:#rrggbb,...]
           [--extract N] [--extract-method popularity|median]
           [--scale 1..16] [--max N] [--frames 1..48]
           [--threshold 0..1] [--brightness -1..1] [--contrast 0..3]
           [--invert]
```

It prints `src=WxH out=WxH px=N ms=N bytes=N` and exits 1 with a message on
stderr for a bad option, a missing file or an image it cannot decode. A
two-colour palette is written as 1-bit PNG, up to 256 colours as 8-bit, and
anything larger as 24-bit. With `--palette auto` the colours it settled on
follow the status line as `palette=#rrggbb,...`, which is how the block reads
them back. With `--frames` above 1 it adds `frames=N cell=WxH cols=C rows=R
delays=ms,ms,...`, which is how the window half knows where one frame ends and
the next begins.

`--algo`, `--matrix` and `--pattern` are only read by the mode that uses them,
and the block disables the row of each while another mode is running.

**The modes.** `diffusion` hands each pixel's error to the neighbours its
kernel names. `bayer` and `bluenoise` are ordered: the matrix cell alone
decides which entry a pixel takes, so the pattern is the same at every tone.
`knoll` is ordered too, but the matrix does not choose between the two nearest
entries — for every colour it walks the palette the way an error diffusion
would and counts how often each entry wins, so a picture with six colours
comes out mixed rather than banded. `riemersma` diffuses along a Hilbert
curve, which keeps the error near where it belongs and leaves no directional
streaks. `threshold` and `none` do not dither at all.

**The ordered dither's amplitude is the palette's own step**, not the whole
range: the bias spans the gap between the two entries a pixel sits between, so
`bw` gets a full-amplitude checker and `gray16` a sixteenth of one. Biasing by
the whole range instead makes a sixteen-level palette spray its extremes over
every tone, which is more noise for no more tone.

**The blue-noise mask is made, not stored**: a 64×64 grid of ranks built by
void and cluster. It starts from a random half-density pattern, takes the
tightest cluster out and fills the largest void in what is left with it, and
stops when that void *is* the cell just emptied — the point at which no move
lowers the energy. Both ranking passes then start from that relaxed pattern:
the ones by tightest cluster first, the zeros by largest void, so rank 0 is the
cell with the most room around it.

**The image's own colours.** `--palette auto` reads the palette out of the
picture instead of taking one off the shelf, in `--extract` colours (2–256,
the block offers 2–16). `--extract-method popularity` counts colours in a
5-bit-per-channel histogram and keeps the fullest buckets, skipping any that
sit too close to one already kept so that a picture that is mostly one colour
does not spend its whole palette on that colour's neighbours; the filter is
dropped on a second pass so a picture with fewer distinct colours than asked
for still fills the palette. `--extract-method median` splits the colours into
boxes along each box's widest channel until there are as many boxes as
colours, and takes each box's average, which spreads the palette over the
whole picture rather than over its most common parts. Both sort the result
darkest-first, so it reads like `gray4` and `gray8` do.

The palette is read from its own downscale of the source, capped at 256 px on
the long side, so `--scale` and `--max` shape the dither without shaping the
palette. `--brightness`, `--contrast` and `--invert` do move it: the picture
and the palette have to agree about what the picture looks like.

The block publishes the colours it read into the Custom colours row, so
switching to that palette starts from the picture rather than from the
defaults.

**The retro presets are custom lists the block owns.** Game Boy (the DMG's
four greens), CGA / EGA (the IBM sixteen as post-CGA hardware and emulators
draw them), PICO-8 and Commodore 64 (the VIC-II's sixteen) travel to the engine
as an ordinary `custom:` list, so the engine has no notion of a preset and
nothing in it had to learn about them. They are the reference sets for those
machines, taken from what documents them rather than eyeballed.

While a preset is chosen the Custom colours row shows the preset's own list and
says so, and the user's custom list is left underneath it, untouched: switching
back to Custom colours brings their colours back. The engine's dithers are all
order-independent — `Pair` picks the two nearest entries by distance and
`Knoll` sorts its own walk by luminance — so the order the colours are written
in is only how they read in the row.

**The cut is the built-in palette's.** `bw` is quantized as a cut at
`--threshold`, not by distance, so `bw` plus `threshold` reads as a hard
black-and-white cut. That shortcut is only the same question as "which entry
is nearer" when the two tones are black and white in that order; a custom
palette names its own colours in its own order, and red and blue are not a
luminance at all, so `custom:` and `auto` always go through the weighted-RGB
distance loop and are quantized by colour. `--threshold` therefore moves `bw`
and `--mode threshold`, and does not move any other palette.

**Blocking up the result.** `--pixelate N` runs over the dithered picture, not
the source, so the dots grow while the picture keeps the detail the dither was
computed at; `--scale` is the other way round and takes the detail away first.
`--smooth` averages each block instead of taking its middle pixel, which reads
softer and less speckled.

Measured on a 736×736 JPEG, one core: 40–101 ms for every mode, and the
blue-noise mask is built once and cached for the life of the process. The
process spawn is the other half of the cost, which is why the block debounces
and the preview is the render itself rather than a second one.

## Notes and limits

- **Tern will not draw a picture longer than 4096 px on a side.** It logs
  `cannot load CSS image url()` and draws nothing, so the wallpaper would
  simply disappear. The plugin therefore treats "no cap" as 4096 and clamps
  anything above it; the engine's own `--max 0` stays uncapped for command-line
  use. Verified against a 5000×3000 source: uncapped fails, 4096 loads.
- **No native Settings page.** A plugin cannot add a page or a nav entry to
  Tern's own Preferences; the manifest has a closed key list and `cx.settings`
  rejects keys it does not know. The settings page here is a plugin block that
  is built from the same `prefs` kind, so it looks the part without pretending
  to be the real thing.
- **A text row's caret starts at the end.** The `prefs` kind reports that a
  text row was activated, not where it was clicked, and it has no selection
  model, so editing a path means pressing Backspace or Home first.
- **WebP needs a system codec.** GDI+ decodes JPEG, PNG, BMP, GIF and TIFF.
  A WebP without a codec fails with `cannot decode`.
- **Only GIF animates, and an APNG does not even fail.** An animated PNG is
  read as its first frame and rendered as a still, with no message: GDI+
  ignores the `acTL` chunk that makes it an animation. Anything animated that
  is not a GIF is a still here.
- **A delay of one centisecond plays at 10 ms, not the 100 ms browsers use.**
  Browsers treat anything under 2 cs as "as fast as possible" and clamp it to
  100 ms; this reads the file literally, so such a GIF runs ten times faster
  than it looks in a browser. A file that says 1 renders `delays=10`. The
  clamp is one line in `FrameDelays` if browser parity is wanted.
- **Each delay is a multiple of 10 ms, and only each delay.** A GIF stores
  hundredths of a second, so no single frame can be 16.7 ms. A uniform rate is
  still expressible as a repeating cycle — 60 fps as 10/20/20 ms, 30 fps as
  40/30/30 — and the plugin reproduces whatever cycle the file used, because it
  carries each frame's own delay rather than a rate. What the format rules out
  is an author who wanted a rate and wrote it into every frame: 16.7 ms per
  frame is not a value that exists. The 31-frame GIF this was built against
  runs its steady state at exactly 30 fps as a repeating 40/30/30 ms cycle,
  with a 30 ms first frame; the 31 frames sum to 1030 ms. That is one file's
  choice, not a rule about 30 fps GIFs.
- **A GIF is read as whole frames, and that holds.** Partial frames and
  disposal methods 1 (leave) and 2 (restore to background) composite the way a
  browser composites them, and a transparent index shows the frame below
  through. Verified by building GIFs for each case: a half-width partial frame
  over a white frame comes back black on the left and white on the right, and
  the same file with disposal 2 and a white background comes back white
  throughout.
- **An animation is always `cover`, and its frames share one palette.** The
  frames are one image, so only the crop that shows one cell at a time can be
  drawn; and the palette is read off the first frame, because a palette per
  frame would make the colours crawl as the picture moves.
- **Frames are sampled evenly, and their delays are added up.** Asking for
  fewer frames than the file has keeps the animation's length and pace: the
  skipped frames' delays go to the frame that stands in for them. The default
  asks for the cap, so a GIF of 48 frames or fewer is drawn whole; a GIF
  longer than that loses frames and gets choppier, which is the one thing
  raising the cap would fix at the cost of resolution.
- **One render at a time.** While a render runs the loop ignores new changes;
  the debounce picks up the newest settings when it fires.
- **The pane width decides the layout.** The `prefs` kind collapses its nav to
  an icon rail below 760px, so the settings page is easiest to read in a wide
  pane.
