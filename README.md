# Tern Custom Background

A plugin for [Tern](https://stencil.so/tern), the terminal app by Stencil: it
puts a dithered picture behind the whole window — under the terminal, the
sidebar, the tab bar and the status line — with a settings page to pick the
image and the way it is drawn.

## Install

```
tern plugin install github.com/naraaalt/tern-custom-background
```

Or drop the folder into your plugins folder (`tern plugin dir`) and let Tern
reload; it is enabled by default. Then open **Wallpaper: settings** from the
palette, or press `Ctrl+Alt+W`. The page opens beside the pane you asked from
and shows a live preview of the dithered result.

## What it does

- Any image becomes the window's background. Choose it with the `Choose…`
  button or paste a path into the Image file row.
- Reduced to black and white, 4/8/16 greys, the picture's own colours, a
  custom list, or a retro preset — Game Boy, CGA / EGA, PICO-8, Commodore 64 —
  by error diffusion (13 kernels), Bayer matrix, blue noise, Knoll, Riemersma,
  threshold or nearest.
- Or drawn as characters: an 8×8 bitmap font at 1–8× cell size, on a ramp from
  two glyphs to seventy, in two colours taken from whatever palette is set.
- Fit (fit to screen, fill, stretch, tile), opacity, brightness, contrast,
  block size, pixel scale.
- **An animated GIF moves.** The frames are rendered into one grid image and
  the sheet turns it back into an animation with a keyframe per frame, at the
  GIF's own delays; a still source is unaffected. Off with the Animate row.
  The default draws up to 48 frames — the cap — so a GIF of 48 frames or fewer
  keeps every frame it has and runs at the file's own rate. For the smoothest
  motion pick an ordered mode — Bayer or Knoll: error diffusion recomputes its
  noise each frame, so the dither crawls over the moving picture.
- **Palette: Image colours** reads the picture's own colours into the Custom
  colours row, so a colour wallpaper stays in colour.

## Animated GIFs: known issues

Animation works, and it is the newest and least travelled part of the plugin.
What is known to be rough, with the ones that are defects marked:

| Issue | Effect | Status |
| --- | --- | --- |
| **Over 48 frames loses frames** | A 60 fps clip longer than 0.8 s, or any GIF past 48 frames, is sampled down to 48: the delays are preserved so the pace is right, but the motion is not | Cap, not a defect — the grid is one image and Tern refuses anything past 4096 px a side. Raising it costs resolution in every frame |
| **A 1-centisecond delay plays ~10× too fast** | A GIF whose frames say `1` in the delay field (10 ms) runs at 100 fps here, where a browser clamps anything under 2 cs to 100 ms and shows 10 fps | **Defect.** The file's literal value is used. One line in `Dither.cs` to match browsers, if that is wanted |
| **Animated PNG is ignored** | An APNG renders as its first frame, with no error | Gap. Only GIF animates; APNG would need its own reader |
| **Delays are quantized to 10 ms** | A frame cannot be 16.7 ms: the format stores hundredths of a second. A uniform rate is still expressible as a repeating cycle — 60 fps as 10/20/20 ms, 30 fps as 40/30/30 — but only if the file's author wrote it that way | Format limit |
| **Colour GIFs use the first frame's palette** | With `Palette: Image colours`, a scene that changes colour keeps frame 0's colours | Deliberate: a palette per frame makes the colours crawl |
| **Fit is forced to `cover`** | The Fit row is disabled while animating | Deliberate: the frames only line up as one grid |

Verified by building the files: partial frames and disposal methods 1 and 2
composite correctly, transparency shows the previous frame through, and a
1 cs file really does render `delays=10,10`.

## Requirements

Windows only, for now. The dither runs in a small C# helper (`engine/Dither.cs`)
that the plugin compiles on first run with the C# compiler that ships with
Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, from .NET
Framework 4.x) — there is nothing else to install. On macOS or Linux the
settings page loads but no picture is ever rendered, and the `Choose…` button
has no dialog; paste the path instead.

## More

[MANUAL.md](MANUAL.md) has the full settings table, the engine's command line,
and how the two halves of the plugin fit together.
