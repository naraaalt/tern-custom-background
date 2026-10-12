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
- Reduced to black and white, 4/8/16 greys, the picture's own colours, or a
  custom list — by error diffusion (13 kernels), Bayer matrix, blue noise,
  Knoll, Riemersma, threshold or nearest.
- Fit (fit to screen, fill, stretch, tile), opacity, brightness, contrast,
  block size, pixel scale.
- **Palette: Image colours** reads the picture's own colours into the Custom
  colours row, so a colour wallpaper stays in colour.

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
