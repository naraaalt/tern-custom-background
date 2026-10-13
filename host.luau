--!strict
-- Wallpaper: a dithered image behind the whole Tern window.
--
-- This half owns the render pipeline and the settings block. The window half
-- (window.luau) owns the style sheet. They share nothing but the plugin's kv
-- store, which is the only channel Tern gives the two halves:
--
--   rev         bumped by anyone who changes a setting
--   render      the newest PNG, for the block's own preview blob
--   picture     the newest PNG as a `data:` URL, for the window's sheet
--   render_rev  bumped when a window must re-apply the sheet
--   sig         the render inputs that produced `render`
--
-- With the Image colours palette, `colors` also travels this way, in the other
-- direction: the engine prints the palette it read off the picture, the render
-- callback publishes it, and the settings block shows it in the Custom colours
-- row. It is a render result, not a render input, which is why writing it does
-- not change `sig` and does not start another render.
--
-- The loop below watches `rev`. When the render inputs changed it debounces
-- and runs engine/dither.exe; when only the display settings changed (opacity,
-- fit, enabled) it reuses the picture it has and just bumps `render_rev`.

-- Renders go to the plugin's data folder, never the package folder. Tern
-- watches every package directory recursively and reloads the whole plugin on
-- any change in one, which restarts the settings block and rebuilds its view
-- - so a render written beside the manifest would reset the page's scroll on
-- every value the user changed.
--
-- A sheet can't reach the data folder either: `url()` takes a path relative to
-- the package and refuses `..` and absolute paths. So the window half gets the
-- bytes as a `data:` URL instead, which a sheet accepts anywhere.
local RENDER_DIR = tern.plugin.data .. "/renders"
local POLL_MS = 150
local DEBOUNCE_MS = 200
local KEEP_RENDERS = 4

-- `tern.fs` resolves a relative path against the package, but a spawned
-- process does not: every path that reaches dither.exe is made absolute here.
local ROOT = tern.plugin.dir

local function abs(path: string): string
	return ROOT .. "/" .. path
end

local function is_absolute(path: string): boolean
	return string.match(path, "^%a:[\\/]") ~= nil
		or string.sub(path, 1, 1) == "/"
		or string.sub(path, 1, 2) == "\\\\"
end

local function absolute(path: string): string
	return if is_absolute(path) then path else abs(path)
end

-- The most frames the engine will draw. The ceiling is the grid, not the
-- count: every frame is another cell in one image, and the cells shrink until
-- the grid fits inside Tern's 4096 px limit, so more frames cost resolution.
-- At 48 the grid is 7x7 and a cell is at most 585 px.
local MAX_FRAMES = 48

local DEFAULTS = {
	mode = "diffusion",
	algo = "fs",
	matrix = "8",
	pattern = "8",
	pixelate = 1,
	smooth = false,
	palette = "bw",
	colors = "#111111,#eeeeee",
	extract = 8,
	extract_method = "popularity",
	scale = 1,
	cut = 0.5,
	brightness = 0,
	contrast = 1,
	invert = false,
	maxside = 1920,
	animate = true,
	frames = MAX_FRAMES,
	opacity = 0.30,
	fit = "cover",
	pixelated = false,
	enabled = true,
	claim = false,
}

local KEYS = {
	"source", "mode", "algo", "matrix", "pattern", "pixelate", "smooth",
	"palette", "colors", "extract", "extract_method", "scale", "cut",
	"brightness", "contrast", "invert", "maxside", "animate", "frames",
	"opacity", "fit", "pixelated", "enabled", "claim",
}

-- The animation the render in hand came out as, written by the render loop
-- from the engine's own report and read by both halves. They are separate
-- keys rather than one table because the window half reads them with the
-- same typed accessors as everything else.
local ANIM_KEYS = {
	"anim_frames", "anim_cols", "anim_rows", "anim_cell_w", "anim_cell_h", "anim_delays",
}

-- The engine's modes, in the order the menu lists them: the error diffusions
-- first, then the ordered ones, then the two that do not dither at all.
local MODES = { "diffusion", "bayer", "bluenoise", "knoll", "riemersma", "threshold", "none" }
local ALGOS = {
	"fs", "atkinson", "jjn", "stucki", "burkes", "sierra3", "sierra2", "sierra24a",
	"fan", "shiaufan", "shiaufan2", "simple2d", "dizzy",
}
local MATRICES = { "2", "4", "8", "16" }
local PATTERNS = { "2", "4", "8", "16", "bluenoise" }

-- Retro palettes, in the order the menu lists them. Each is the reference set
-- for the machine, taken from what documents it rather than eyeballed: the
-- DMG's four greens, the canonical CGA/EGA sixteen that post-CGA hardware and
-- emulators draw, PICO-8's sixteen, and the sixteen c64-wiki lists for the
-- VIC-II. They travel to the engine as an ordinary `custom:` list, so the
-- engine has no notion of a preset and nothing in it has to know about them.
--
-- The engine's dithers are all order-independent - `Pair` picks the two
-- nearest entries by distance and `Knoll` sorts its own walk by luminance - so
-- the order here is only how the colours read in the row.
local PRESETS = {
	gb = {
		label = "Game Boy",
		detail = "The DMG's four greens.",
		colors = "#0f380f,#306230,#8bac0f,#9bbc0f",
	},
	cga = {
		label = "CGA / EGA",
		detail = "The IBM sixteen, as emulators draw them.",
		colors = "#000000,#0000aa,#00aa00,#00aaaa,#aa0000,#aa00aa,#aa5500,#aaaaaa"
			.. ",#555555,#5555ff,#55ff55,#55ffff,#ff5555,#ff55ff,#ffff55,#ffffff",
	},
	pico8 = {
		label = "PICO-8",
		detail = "The fantasy console's sixteen.",
		colors = "#000000,#1d2b53,#7e2553,#008751,#ab5236,#5f574f,#c2c3c7,#fff1e8"
			.. ",#ff004d,#ffa300,#ffec27,#00e436,#29adff,#83769c,#ff77a8,#ffccaa",
	},
	c64 = {
		label = "Commodore 64",
		detail = "The VIC-II's sixteen.",
		colors = "#000000,#ffffff,#880000,#aaffee,#cc44cc,#00cc55,#0000aa,#eeee77"
			.. ",#dd8855,#664400,#ff7777,#333333,#777777,#aaff66,#0088ff,#bbbbbb",
	},
}
local PRESET_NAMES = { "gb", "cga", "pico8", "c64" }

local PALETTES = { "bw", "gray4", "gray8", "gray16", "auto", "custom" }
for _, name in PRESET_NAMES do
	table.insert(PALETTES, name)
end

-- The menu, built once: the built-in palettes, then the presets. Kept beside
-- PRESETS so a preset cannot be added without appearing in the row.
local PALETTE_OPTIONS = {
	{ value = "bw", label = "Black and white" },
	{ value = "gray4", label = "4 greys" },
	{ value = "gray8", label = "8 greys" },
	{ value = "gray16", label = "16 greys" },
	{ value = "auto", label = "Image colours", detail = "The colours the picture is made of." },
	{ value = "custom", label = "Custom colours" },
}
for _, name in PRESET_NAMES do
	local p = PRESETS[name]
	table.insert(PALETTE_OPTIONS, { value = name, label = p.label, detail = p.detail })
end

-- The colours a preset palette stands for, or nil for the palettes that are
-- not presets. The Custom colours row shows these rather than the user's own
-- list while a preset is in use, so the row says which colours are in effect
-- without the preset overwriting a custom list the user typed.
local function preset_colors(name: string): string?
	local p = PRESETS[name]
	return if p ~= nil then p.colors else nil
end

local METHODS = { "popularity", "median" }
local FITS = { "cover", "contain", "stretch", "tile" }

local IMAGE_EXT = {
	png = true, jpg = true, jpeg = true, gif = true, bmp = true, tif = true, tiff = true,
}

-- ============================================================ kv and settings

-- A number in a string slot is a value an older build wrote (the migration
-- that moved `bayer8` onto `mode` stored `matrix` as a number). Reading it as
-- the fallback would silently reset the user's choice, so it is read as its
-- own digits instead.
local function kstr(key: string, fallback: string): string
	local v = tern.kv.get(key)
	if type(v) == "string" then
		return v
	end
	if type(v) == "number" then
		return tostring(v)
	end
	return fallback
end

local function knum(key: string, fallback: number): number
	local v = tern.kv.get(key)
	return if type(v) == "number" then v else fallback
end

local function kbool(key: string, fallback: boolean): boolean
	local v = tern.kv.get(key)
	return if type(v) == "boolean" then v else fallback
end

local function bump()
	tern.kv.set("rev", knum("rev", 0) + 1)
end

local function one_of(v: string, allowed: { string }, fallback: string): string
	for _, a in allowed do
		if v == a then
			return v
		end
	end
	return fallback
end

local function clamp(v: number, lo: number, hi: number): number
	return math.max(lo, math.min(hi, v))
end

-- Whether the render that is up is an animation. The key is the render's own
-- answer, not a setting: a still source with `animate` on renders one cell
-- and leaves it unset.
local function animated(): boolean
	return knum("anim_frames", 0) > 1
end

-- The window half gets its picture through the kv store, which holds JSON
-- text, so the render's bytes travel base64-encoded. A dithered PNG is a
-- handful of kilobytes, which the store and a style sheet both carry.
local B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"

local function base64(bytes: string): string
	local out: { string } = table.create(math.ceil(#bytes / 3) * 4)
	local n = #bytes
	local i = 1
	while i <= n do
		local b1 = string.byte(bytes, i) or 0
		local b2 = if i + 1 <= n then string.byte(bytes, i + 1) else nil
		local b3 = if i + 2 <= n then string.byte(bytes, i + 2) else nil
		local c1 = math.floor(b1 / 4)
		local c2 = (b1 % 4) * 16 + (if b2 then math.floor(b2 / 16) else 0)
		local c3 = if b2 then (b2 % 16) * 4 + (if b3 then math.floor(b3 / 64) else 0) else nil
		local c4 = if b3 then b3 % 64 else nil
		table.insert(out, string.sub(B64, c1 + 1, c1 + 1))
		table.insert(out, string.sub(B64, c2 + 1, c2 + 1))
		table.insert(out, if c3 then string.sub(B64, c3 + 1, c3 + 1) else "=")
		table.insert(out, if c4 then string.sub(B64, c4 + 1, c4 + 1) else "=")
		i += 3
	end
	return table.concat(out)
end

-- Tern refuses a CSS image with a side longer than this - it logs "cannot load
-- CSS image url()" and draws nothing - so a render past it is a wallpaper that
-- silently disappears. Verified against a 5000x3000 source: uncapped fails,
-- 4096 loads. The engine keeps its own uncapped mode for command-line use; the
-- plugin is what must not hand Tern something it will not draw.
local MAX_SIDE = 4096

-- 0 is the engine's "no cap", and the most Tern takes is MAX_SIDE, so that is
-- what it means here. Anything above the ceiling comes down to it.
local function clamp_maxside(n: number): number
	local v = math.floor(n)
	if v <= 0 then
		return MAX_SIDE
	end
	return math.min(v, MAX_SIDE)
end

-- The settings as the rest of this half wants them: every value present and in
-- range, whatever the kv store happens to hold.
local function settings(): { [string]: any }
	local s = {}
	s.source = kstr("source", "")
	s.mode = one_of(kstr("mode", DEFAULTS.mode), MODES, DEFAULTS.mode)
	s.algo = one_of(kstr("algo", DEFAULTS.algo), ALGOS, DEFAULTS.algo)
	s.matrix = one_of(kstr("matrix", DEFAULTS.matrix), MATRICES, DEFAULTS.matrix)
	s.pattern = one_of(kstr("pattern", DEFAULTS.pattern), PATTERNS, DEFAULTS.pattern)
	s.pixelate = clamp(math.floor(knum("pixelate", DEFAULTS.pixelate)), 1, 25)
	s.smooth = kbool("smooth", DEFAULTS.smooth)
	s.palette = one_of(kstr("palette", DEFAULTS.palette), PALETTES, DEFAULTS.palette)
	s.colors = kstr("colors", DEFAULTS.colors)
	s.extract = clamp(math.floor(knum("extract", DEFAULTS.extract)), 2, 16)
	s.extract_method = one_of(kstr("extract_method", DEFAULTS.extract_method), METHODS, DEFAULTS.extract_method)
	s.scale = clamp(math.floor(knum("scale", DEFAULTS.scale)), 1, 16)
	s.cut = clamp(knum("cut", DEFAULTS.cut), 0, 1)
	s.brightness = clamp(knum("brightness", DEFAULTS.brightness), -1, 1)
	s.contrast = clamp(knum("contrast", DEFAULTS.contrast), 0.1, 3)
	s.invert = kbool("invert", DEFAULTS.invert)
	s.maxside = clamp_maxside(knum("maxside", DEFAULTS.maxside))
	s.animate = kbool("animate", DEFAULTS.animate)
	s.frames = clamp(math.floor(knum("frames", DEFAULTS.frames)), 2, MAX_FRAMES)
	s.opacity = clamp(knum("opacity", DEFAULTS.opacity), 0, 1)
	s.fit = one_of(kstr("fit", DEFAULTS.fit), FITS, DEFAULTS.fit)
	s.pixelated = kbool("pixelated", DEFAULTS.pixelated)
	s.enabled = kbool("enabled", DEFAULTS.enabled)
	s.claim = kbool("claim", DEFAULTS.claim)
	return s
end

local function write_settings(s: { [string]: any })
	for _, key in KEYS do
		local v = s[key]
		if v == nil or v == "" then
			tern.kv.set(key, nil)
		else
			tern.kv.set(key, v)
		end
	end
end

-- Before the engine split the ordered dithers out of `--algo`, `algo` held
-- bayer4, bayer8, threshold and none as if they were diffusions. Move a stored
-- one onto `mode` once, so an existing choice survives the upgrade instead of
-- reading back as Floyd-Steinberg.
local function migrate()
	if type(tern.kv.get("mode")) == "string" then
		return
	end

	local algo = tern.kv.get("algo")
	if algo == "bayer4" or algo == "bayer8" then
		tern.kv.set("mode", "bayer")
		tern.kv.set("matrix", string.sub(algo, 6))
		tern.kv.set("algo", DEFAULTS.algo)
	elseif algo == "threshold" or algo == "none" then
		tern.kv.set("mode", algo)
		tern.kv.set("algo", DEFAULTS.algo)
	end
end

-- ============================================================ render pipeline

local function fixed(n: number): string
	return string.format("%.4f", n)
end

-- The engine's identity: the hash of the source it was built from, which
-- build.ps1 writes beside the binary. A render may only be reused while the
-- engine that made it is still the one on disk, and no setting records a
-- rebuild - so it belongs in the signature, or rebuilding the engine would
-- leave the old picture up until the user happened to change something.
--
-- Read fresh every time rather than once per load: `signature` only runs when
-- a setting moves, so this is one small read per edit, and caching it would
-- record "none" for the renders made on a first run, where the engine is built
-- after the first signature is taken.
local function engine_stamp(): string
	local ok, text = pcall(tern.fs.read, abs("engine/dither.stamp"), 256)
	if ok and type(text) == "string" and text ~= "" then
		return text
	end
	return "none"
end

-- How many frames this render asks the engine for. One is the engine's
-- still: an animated source then comes out as its first frame, in one cell.
local function use_frames(s: { [string]: any }): number
	return if s.animate then s.frames else 1
end

-- The render inputs, as one string. Display-only settings are deliberately
-- absent: changing the opacity must not re-dither the picture. A setting is
-- only an input while the palette in use reads it: the colours are the Custom
-- palette's, the count and method the Image palette's, so editing one of them
-- under another palette is not a render.
local function signature(s: { [string]: any }): string
	local parts = {
		engine_stamp(), tostring(s.source), tostring(s.mode), tostring(s.palette),
		fixed(s.scale), fixed(s.cut), fixed(s.brightness), fixed(s.contrast),
		tostring(s.invert), fixed(s.maxside), tostring(use_frames(s)),
	}
	-- Only the setting the mode reads goes in: a matrix while the diffusions
	-- are running is not a render, and neither is a pattern under Bayer.
	if s.mode == "diffusion" then
		table.insert(parts, tostring(s.algo))
	elseif s.mode == "bayer" then
		table.insert(parts, tostring(s.matrix))
	elseif s.mode == "knoll" then
		table.insert(parts, tostring(s.pattern))
	end
	if s.pixelate > 1 then
		table.insert(parts, tostring(s.pixelate))
		table.insert(parts, tostring(s.smooth))
	end
	if s.palette == "custom" then
		table.insert(parts, tostring(s.colors))
	end
	if s.palette == "auto" then
		table.insert(parts, tostring(s.extract))
		table.insert(parts, tostring(s.extract_method))
	end
	return table.concat(parts, "|")
end

local function render_argv(s: { [string]: any }, out: string): { string }
	local palette = s.palette
	if palette == "custom" then
		palette = "custom:" .. s.colors
	elseif PRESETS[palette] ~= nil then
		-- A preset is a custom list that the block owns, so the engine keeps
		-- reading one kind of palette and the colours stay in one place.
		palette = "custom:" .. PRESETS[palette].colors
	end

	local argv: { string } = {
		abs("engine/dither.exe"),
		"--in", absolute(s.source),
		"--out", absolute(out),
		"--mode", s.mode,
		"--palette", palette,
		"--scale", fixed(s.scale),
		"--threshold", fixed(s.cut),
		"--brightness", fixed(s.brightness),
		"--contrast", fixed(s.contrast),
	}
	if s.mode == "diffusion" then
		table.insert(argv, "--algo")
		table.insert(argv, s.algo)
	elseif s.mode == "bayer" then
		table.insert(argv, "--matrix")
		table.insert(argv, s.matrix)
	elseif s.mode == "knoll" then
		table.insert(argv, "--pattern")
		table.insert(argv, s.pattern)
	end
	if s.pixelate > 1 then
		table.insert(argv, "--pixelate")
		table.insert(argv, tostring(s.pixelate))
		if s.smooth then
			table.insert(argv, "--smooth")
		end
	end
	if s.palette == "auto" then
		table.insert(argv, "--extract")
		table.insert(argv, tostring(s.extract))
		table.insert(argv, "--extract-method")
		table.insert(argv, s.extract_method)
	end
	if s.invert then
		table.insert(argv, "--invert")
	end
	-- Always passed: `settings()` keeps it in 1..MAX_SIDE, and Tern will not
	-- draw a picture whose side is longer than that.
	table.insert(argv, "--max")
	table.insert(argv, tostring(s.maxside))
	-- Always passed too: 1 is the engine's still, which is what a source with
	-- one frame renders as whatever this says.
	table.insert(argv, "--frames")
	table.insert(argv, tostring(use_frames(s)))
	return argv
end

local function render_path(rev: number): string
	return string.format("%s/r%d.png", RENDER_DIR, rev)
end

-- The number the next render's file gets. `rev` is the settings revision and
-- only moves when a setting does, so a render that the settings did not ask
-- for - the engine having been rebuilt - would land on a path that already
-- holds a picture, overwrite it, and leave the preview re-reading nothing
-- because its path did not change. Every render therefore takes its own
-- number, which is what the "a new path per render" rule above needs.
local function next_render(): number
	local n = knum("render_seq", 0)
	if n == 0 then
		-- Seeded from the settings revision, because the files already on disk
		-- are numbered by it and `prune` keeps the highest numbers: starting
		-- from 1 would make the next render the oldest thing in the folder and
		-- it would delete it again straight away.
		n = knum("rev", 0)
	end
	n += 1
	tern.kv.set("render_seq", n)
	return n
end

-- Keeps the folder from growing without bound. Every render is a new path,
-- because Tern caches a sheet's url() bytes per path and would otherwise keep
-- drawing the old picture.
local function prune(keep: string)
	local ok, names = pcall(tern.fs.list, RENDER_DIR)
	if not ok then
		return
	end

	local found: { string } = {}
	for _, name in names do
		if string.match(name, "^r%d+%.png$") then
			table.insert(found, name)
		end
	end
	table.sort(found, function(a, b)
		return (tonumber(string.match(a, "%d+")) or 0) < (tonumber(string.match(b, "%d+")) or 0)
	end)

	for i = 1, #found - KEEP_RENDERS do
		local path = RENDER_DIR .. "/" .. found[i]
		if path ~= keep then
			pcall(tern.fs.remove, path)
		end
	end
end

local seen = -1
local inflight = false
local debounce: TimerHandle? = nil

local function note(text: string)
	tern.kv.set("note", text)
end

-- The host half has no way to close a pane - `cx.layout` belongs to the window
-- half - so a close leaves a request in the kv store and the window half's
-- poll carries it out. `PaneInfo.pane` is the same number as the block's
-- `cx.pane`, so the id in the request names exactly one pane.
local function request_close(cx: BlockCx)
	tern.kv.set("close_pane", cx.pane)
end

-- The sheet's copy of the render. `url()` in a plugin sheet resolves against
-- the package and refuses anything outside it, so the picture cannot be named
-- by path; a `data:` URL carries it instead and a sheet accepts one anywhere.
--
-- nil when the file cannot be read. The caller must not store a nil under
-- `picture`: `kv.set` deletes the key, which would blank the wallpaper rather
-- than leave the picture that is already up.
local function picture_url(path: string): string?
	local ok, bytes = pcall(tern.fs.read, path)
	if not ok then
		return nil
	end
	return "data:image/png;base64," .. base64(bytes)
end

-- The engine prints the palette it resolved after its status line, and with
-- the Image palette that is the only copy of it: the colours are the render's
-- answer, not a setting. They are read back out so the Custom colours row can
-- show them, and so switching to the Custom palette starts from the picture
-- rather than from the defaults.
local function extracted_palette(text: string): string?
	return string.match(text, "palette=([#%x,]+)")
end

-- The grid and per-frame times of an animated render, as the engine reports
-- them: "frames=8 cell=640x360 cols=3 rows=3 delays=100,100,...". Nothing
-- else in the render says where one frame ends and the next begins, so this
-- is what the window half turns into keyframes. nil for a still.
local function render_animation(text: string): { [string]: any }?
	local n, cw, ch, cols, rows, delays =
		string.match(text, "frames=(%d+) cell=(%d+)x(%d+) cols=(%d+) rows=(%d+) delays=([%d,]+)")
	if n == nil then
		return nil
	end
	local frames = tonumber(n) :: number
	if frames < 2 then
		return nil
	end
	return {
		frames = frames,
		cols = tonumber(cols) :: number,
		rows = tonumber(rows) :: number,
		cell_w = tonumber(cw) :: number,
		cell_h = tonumber(ch) :: number,
		delays = delays :: string,
	}
end

-- The animation the window half should draw, or nil for a still. `kv.set`
-- deletes a key when the value is nil, so clearing is the same call.
local function publish_animation(anim: { [string]: any }?)
	for _, key in ANIM_KEYS do
		tern.kv.set(key, if anim == nil then nil else anim[string.sub(key, 6)])
	end
end

local function apply_render(rev: number, s: { [string]: any })
	inflight = true
	local out = render_path(next_render())
	local sig = signature(s)

	-- A spawn failure raises at the call rather than arriving in the callback,
	-- so the raise has to be caught here: without it `inflight` would stay
	-- true and no render would ever be scheduled again. `seen` moves too, so a
	-- missing engine costs one failed attempt instead of one every 150 ms; the
	-- window half bumps `rev` once it has built the engine.
	local ok, err = pcall(tern.process.run, render_argv(s, out), { timeout_ms = 120000 }, function(r)
		if r.status == 0 then
			-- `kv.set` deletes a key when the value is nil, so the read has to
			-- be checked before publishing: `picture_url` is nil when the file
			-- cannot be read back, and storing that would delete the picture
			-- key and blank the wallpaper. On a failed read the picture that
			-- is already up stays up, and the failure is what gets reported.
			local url = picture_url(out)
			if url then
				local text = (r.stdout or ""):gsub("%s+$", "")
				if s.palette == "auto" then
					local hexes = extracted_palette(text)
					if hexes then
						tern.kv.set("colors", hexes)
						-- The block reads the settings when `rev` moves, so
						-- this is what puts the new colours in the row. It is
						-- not a render input while the palette is the image's,
						-- so the signature is unmoved and nothing renders
						-- again: the row catches up on the next draw.
						bump()
					end
					text = text:gsub("%s*palette=[#%x,]+", "")
				end
				-- Read before the note drops it: the per-frame times are a list
				-- as long as the frame count, and the Frames row already says
				-- the count, so the note keeps the grid and loses the list.
				local anim = render_animation(text)
				text = text:gsub("%s*delays=[%d,]+", "")
				tern.kv.set("render", out)
				tern.kv.set("picture", url)
				tern.kv.set("sig", sig)
				publish_animation(anim)
				note(text)
				prune(out)
			else
				-- The candidate is no use to anyone, so it goes; the renders
				-- that are up stay, and `prune` is not run here because it
				-- keeps the newest four and would be protecting this file.
				note("render failed: cannot read " .. out)
				pcall(tern.fs.remove, out)
			end
		else
			local why = ((r.stderr or "") .. " " .. (r.stdout or "")):gsub("%s+$", "")
			if r.timed_out then
				why = "timed out"
			end
			note("render failed: " .. why)
		end
		tern.kv.set("render_rev", knum("render_rev", 0) + 1)
		inflight = false
		seen = rev
	end)

	if not ok then
		note("render failed: " .. tostring(err))
		inflight = false
		seen = rev
	end
end

-- One pending timer, not a re-armed one: the poll interval is shorter than
-- the debounce, so cancelling and re-arming on every poll would keep pushing
-- the render out forever. The timer reads the settings when it fires, so
-- changes made while it waits are picked up anyway.
local function schedule()
	if debounce ~= nil then
		return
	end
	debounce = tern.timer(DEBOUNCE_MS, function()
		debounce = nil
		local rev = knum("rev", 0)
		local s = settings()
		if s.source == "" then
			seen = rev
			return
		end
		apply_render(rev, s)
	end)
end

local function tick()
	local rev = knum("rev", 0)
	if rev ~= seen and not inflight then
		local s = settings()
		local have = kstr("render", "")
		if s.source == "" then
			seen = rev
		elseif signature(s) == kstr("sig", "") and have ~= "" and tern.fs.exists(have) then
			-- Only a display setting moved: the picture stands, the windows
			-- just have to re-apply it.
			tern.kv.set("render_rev", knum("render_rev", 0) + 1)
			seen = rev
		else
			schedule()
		end
	end
	tern.timer(POLL_MS, tick)
end

-- ============================================================ the block's UI

local ui = tern.ui

type State = {
	pane: number,
	s: { [string]: any },
	picking: boolean,
	engine: boolean,
	seen_rev: number,
	seen_render_rev: number,
	preview: string?,
	preview_for: string?,
	preview_error: string?,
}

-- Which page is open, which row wears the ring, and what is being edited.
--
-- These live here rather than on the block's state because Tern hands every
-- hook its own copy of that state: a copy taken when the block started is what
-- the polling loop below holds, so anything the event handler writes there is
-- gone the moment the loop redraws. A redraw without the ring would leave the
-- page back at the top, since `focus` is what scrolls the changed row into
-- view. One entry per pane, so two open pages don't share a page number.
type UiState = {
	page: string,
	focus: string?,
	-- The `focus` value the last view carried. Tern acts on the prop only when
	-- it differs from the one before, so this is what tells the event handler
	-- whether it has to blank the ring to re-anchor the page.
	focus_sent: string?,
	editing: { row: string, draft: string, cursor: number }?,
}

local UI: { [number]: UiState } = {}

local function ui_state(state: State): UiState
	local u = UI[state.pane]
	if u == nil then
		u = { page = "image", focus = nil, focus_sent = nil, editing = nil }
		UI[state.pane] = u
	end
	return u
end

local TEXT_ROWS = { source = true, colors = true }
-- A choice row with more than four options draws as a popup button, which only
-- opens its menu while the row is the one being edited.
local CHOICE_ROWS = {
	mode = true, algo = true, matrix = true, pattern = true,
	palette = true, maxside = true, fit = true,
}

local function row_value(state: State, row: string): string
	local s = state.s
	if row == "mode" then
		return tostring(s.mode)
	elseif row == "algo" then
		return tostring(s.algo)
	elseif row == "matrix" then
		return tostring(s.matrix)
	elseif row == "pattern" then
		return tostring(s.pattern)
	elseif row == "palette" then
		return tostring(s.palette)
	elseif row == "maxside" then
		return tostring(s.maxside)
	elseif row == "fit" then
		return tostring(s.fit)
	elseif row == "source" then
		return tostring(s.source)
	elseif row == "colors" then
		return tostring(s.colors)
	end
	return ""
end

local function prev_boundary(s: string, i: number): number
	local j = i - 1
	while j > 1 and bit32.band(string.byte(s, j), 0xC0) == 0x80 do
		j -= 1
	end
	return math.max(1, j)
end

local function next_boundary(s: string, i: number): number
	local n = #s
	local j = i + 1
	while j <= n and bit32.band(string.byte(s, j), 0xC0) == 0x80 do
		j += 1
	end
	return math.min(n + 1, j)
end

-- The prefs kind takes the caret in UTF-16 units; the editing state keeps a
-- byte offset, which is what string.sub needs.
local function utf16_caret(s: string, bytes: number): number
	local units = 0
	local j = 1
	while j < bytes do
		local cp = utf8.codepoint(s, j)
		units += if cp > 0xFFFF then 2 else 1
		j = next_boundary(s, j)
	end
	return units
end

local function is_image(path: string): boolean
	local ext = string.match(path, "%.([%a%d]+)$")
	return ext ~= nil and IMAGE_EXT[string.lower(ext)] == true
end

local function base(path: string): string
	return string.match(path, "([^/\\]+)$") or path
end

-- The folder a path sits in, for hints: the row shows the name, the hint says
-- where it is, so a long path never has to wrap across the row.
local function dir(path: string): string
	return string.match(path, "^(.*)[/\\][^/\\]+$") or ""
end

-- --- the render loop the block runs for its own preview ----------------------

local function refresh_preview(state: State, cx: BlockCx)
	local path = kstr("render", "")
	if path == "" or path == state.preview_for then
		return
	end
	if not tern.fs.exists(path) then
		return
	end

	local ok, bytes = pcall(tern.fs.read, path)
	if not ok then
		state.preview_error = "cannot read " .. path
		return
	end

	local blob_ok, blob = pcall(cx.blob, cx, bytes, "image/png")
	if not blob_ok then
		state.preview_error = "cannot send the preview"
		return
	end

	state.preview = blob
	state.preview_for = path
	state.preview_error = nil
end

local function watch(state: State, cx: BlockCx)
	local function alive(): boolean
		for _, p in tern.pane.list() do
			if p.pane == cx.pane then
				return true
			end
		end
		return false
	end

	local function beat()
		if not alive() then
			return
		end

		local rev = knum("rev", 0)
		local rr = knum("render_rev", 0)
		local dirty = false

		if rev ~= state.seen_rev then
			state.seen_rev = rev
			state.s = settings()
			dirty = true
		end
		if rr ~= state.seen_render_rev then
			state.seen_render_rev = rr
			refresh_preview(state, cx)
			dirty = true
		end

		if dirty then
			cx:render()
		end
		tern.timer(POLL_MS, beat)
	end

	beat()
end

-- --- rows -------------------------------------------------------------------

local function status_rows(state: State): { { [string]: any } }
	local s = state.s
	local note_text = kstr("note", "")
	if note_text == "" then
		note_text = "nothing rendered yet"
	end
	return {
		{
			id = "engine",
			label = "Dither engine",
			hint = "engine/dither.exe, built from Dither.cs by build.ps1",
			control = { k = "text", value = if state.engine then "ready" else "missing", disabled = nil },
			disabled = if state.engine then nil else "run engine/build.ps1",
		},
		{
			id = "source",
			label = "Source",
			control = { k = "text", value = if s.source == "" then "none" else base(s.source) },
			disabled = "read-only",
		},
		{
			id = "render",
			label = "Rendered file",
			control = { k = "text", value = if kstr("render", "") == "" then "none" else kstr("render", "") },
			disabled = "read-only",
		},
		{
			id = "note",
			label = "Last render",
			control = { k = "text", value = note_text, mono = true },
			disabled = "read-only",
		},
		{
			id = "revs",
			label = "Revisions",
			hint = "settings changed, then applied to the windows",
			control = {
				k = "text",
				value = string.format("rev %d · applied %d", knum("rev", 0), knum("render_rev", 0)),
			},
			disabled = "read-only",
		},
	}
end

local function rows_for(state: State): { { [string]: any } }
	local s = state.s

	if ui_state(state).page == "image" then
		return {
			{
				id = "source",
				label = "Image file",
				-- The path itself is in the field beside this. The hint names the
				-- folder it came from: a full path here wrapped to a dozen lines
				-- in a narrow pane and pushed the page far past it.
				hint = if s.source == ""
					then "Nothing chosen yet. Pick a file, or paste a path here."
					else dir(s.source),
				control = { k = "text", value = s.source, placeholder = "no file yet", mono = true },
			},
			{
				id = "pick",
				label = "Choose a file",
				hint = "Opens the system file dialog.",
				control = { k = "action", act = "pick", label = "Choose…" },
			},
			{
				id = "claim",
				label = "Use dropped images",
				hint = "An image dropped anywhere on the window becomes the wallpaper.",
				control = { k = "switch", on = s.claim },
			},
			{
				id = "enabled",
				label = "Show the wallpaper",
				hint = "Off leaves the window's own colours alone.",
				control = { k = "switch", on = s.enabled },
			},
		}
	end

	if ui_state(state).page == "dither" then
		return {
			{
				id = "mode",
				label = "Method",
				hint = "How the picture is reduced to the palette.",
				control = { k = "choice", value = s.mode, options = {
					{ value = "diffusion", label = "Error diffusion",
						detail = "The error each pixel cannot place is handed to the ones after it." },
					{ value = "bayer", label = "Bayer matrix",
						detail = "An ordered crosshatch, the same at every tone." },
					{ value = "bluenoise", label = "Blue noise",
						detail = "An ordered dither with no pattern to see." },
					{ value = "knoll", label = "Knoll",
						detail = "Ordered, but it mixes the colours the picture is made of." },
					{ value = "riemersma", label = "Riemersma",
						detail = "Diffusion along a Hilbert curve: even, with no streaks." },
					{ value = "threshold", label = "Threshold",
						detail = "No dither: a hard two-tone cut." },
					{ value = "none", label = "Nearest",
						detail = "No dither: snap to the nearest level." },
				} },
			},
			{
				id = "algo",
				label = "Kernel",
				hint = "Which neighbours take the error, and how much of it.",
				disabled = if s.mode == "diffusion" then nil else "Used by Error diffusion.",
				control = { k = "choice", value = s.algo, options = {
					{ value = "fs", label = "Floyd–Steinberg", detail = "The classic: the most detail." },
					{ value = "atkinson", label = "Atkinson", detail = "Lighter and cleaner; blown highlights." },
					{ value = "jjn", label = "Jarvis–Judice–Ninke", detail = "Wider and smoother than Floyd–Steinberg." },
					{ value = "stucki", label = "Stucki", detail = "Jarvis's spread, cheaper and crisper." },
					{ value = "burkes", label = "Burkes", detail = "Stucki without the far row." },
					{ value = "sierra3", label = "Sierra", detail = "Three rows, gentler than Stucki." },
					{ value = "sierra2", label = "Sierra two-row", detail = "Two rows: finer texture." },
					{ value = "sierra24a", label = "Sierra lite", detail = "Two neighbours: coarse and clean." },
					{ value = "fan", label = "Fan", detail = "Floyd–Steinberg with the error kept nearer." },
					{ value = "shiaufan", label = "Shiau–Fan", detail = "Flat and even, good for greys." },
					{ value = "shiaufan2", label = "Shiau–Fan 2", detail = "The same, spread over three rows." },
					{ value = "simple2d", label = "Simple 2D", detail = "Half the error to each of two neighbours." },
					{ value = "dizzy", label = "Dizzy", detail = "Pixels taken in a random order." },
				} },
			},
			{
				id = "matrix",
				label = "Matrix",
				hint = "The size of the threshold grid: larger is finer.",
				disabled = if s.mode == "bayer" then nil else "Used by Bayer matrix.",
				control = { k = "choice", value = s.matrix, options = {
					{ value = "2", label = "2×2", detail = "The coarsest crosshatch there is." },
					{ value = "4", label = "4×4", detail = "Coarse." },
					{ value = "8", label = "8×8", detail = "Fine." },
					{ value = "16", label = "16×16", detail = "The finest, and the least visible grid." },
				} },
			},
			{
				id = "pattern",
				label = "Matrix",
				hint = "The threshold grid Knoll mixes through.",
				disabled = if s.mode == "knoll" then nil else "Used by Knoll.",
				control = { k = "choice", value = s.pattern, options = {
					{ value = "2", label = "2×2" },
					{ value = "4", label = "4×4" },
					{ value = "8", label = "8×8" },
					{ value = "16", label = "16×16" },
					{ value = "bluenoise", label = "Blue noise", detail = "No grid to see at all." },
				} },
			},
			{
				id = "pixelate",
				label = "Block size",
				hint = "Blocks the dithered result, so the dots grow and the picture keeps its detail.",
				control = { k = "number", value = s.pixelate, min = 1, max = 25, unit = "px" },
			},
			{
				id = "smooth",
				label = "Smooth blocks",
				hint = "Averages each block instead of taking its middle pixel.",
				disabled = if s.pixelate > 1 then nil else "Used once the block size is above 1.",
				control = { k = "switch", on = s.smooth },
			},
			{
				id = "palette",
				label = "Palette",
				changed = s.palette ~= DEFAULTS.palette,
				defaultLabel = "Black and white",
				control = { k = "choice", value = s.palette, options = PALETTE_OPTIONS },
			},
			{
				id = "extract",
				label = "Colours to take",
				hint = "How many the Image colours palette reads.",
				disabled = if s.palette == "auto" then nil else "Used by the Image colours palette.",
				control = { k = "number", value = s.extract, min = 2, max = 16 },
			},
			{
				id = "extract_method",
				label = "Chosen by",
				hint = "Counting picks what covers the most; median cut spreads over the whole picture.",
				disabled = if s.palette == "auto" then nil else "Used by the Image colours palette.",
				control = { k = "choice", value = s.extract_method, options = {
					{ value = "popularity", label = "Most used" },
					{ value = "median", label = "Median cut" },
				} },
			},
			{
				id = "colors",
				label = "Custom colours",
				hint = "#rrggbb, comma separated. Used by the Custom palette; a "
					.. "retro palette above supplies its own list, shown here.",
				-- The Image colours palette fills this row in from the render,
				-- so under it the row shows the render's answer rather than a
				-- setting to edit; a retro palette shows the colours it stands
				-- for, and its own list is left alone underneath.
				disabled = if s.palette == "auto" then "Read from the image."
					elseif preset_colors(s.palette) ~= nil then "Set by the palette."
					else nil,
				control = { k = "text", value = preset_colors(s.palette) or s.colors, mono = true },
			},
			{
				id = "scale",
				label = "Pixel scale",
				hint = "Divides the source before the dither, so the dots grow.",
				control = { k = "number", value = s.scale, min = 1, max = 16, unit = "×" },
			},
			{
				id = "maxside",
				label = "Longest side",
				hint = "Caps the rendered pixels. Tern will not draw a picture past 4096.",
				control = { k = "choice", value = tostring(s.maxside), options = {
					{ value = "1280", label = "1280" },
					{ value = "1920", label = "1920" },
					{ value = "2560", label = "2560" },
					{ value = "3840", label = "3840" },
					{ value = tostring(MAX_SIDE), label = tostring(MAX_SIDE) },
				} },
			},
			{
				id = "animate",
				label = "Animate",
				hint = "An animated source moves. A still one renders the same either way.",
				control = { k = "switch", on = s.animate },
			},
			{
				id = "frames",
				label = "Frames",
				hint = "The most frames to draw. The default is the cap, so a "
					.. "source keeps its own pace; lowering it samples the "
					.. "source evenly and holds each drawn frame for the time "
					.. "of the ones it stands in for.",
				disabled = if s.animate then nil else "Used once Animate is on.",
				control = { k = "number", value = s.frames, min = 2, max = MAX_FRAMES },
			},
		}
	end

	if ui_state(state).page == "tone" then
		return {
			{
				id = "cut",
				label = "Threshold",
				hint = "Where black becomes white.",
				control = { k = "number", value = math.floor(s.cut * 100 + 0.5), min = 0, max = 100, step = 5, unit = "%" },
			},
			{
				id = "brightness",
				label = "Brightness",
				control = { k = "number", value = math.floor(s.brightness * 100 + 0.5), min = -100, max = 100, step = 5, unit = "%" },
			},
			{
				id = "contrast",
				label = "Contrast",
				control = { k = "number", value = math.floor(s.contrast * 100 + 0.5), min = 10, max = 300, step = 10, unit = "%" },
			},
			{
				id = "invert",
				label = "Invert",
				hint = "Swaps light and dark before the dither.",
				control = { k = "switch", on = s.invert },
			},
			{
				id = "reset-dither",
				label = "Reset the dither",
				hint = "Back to Floyd–Steinberg in black and white, with no tone change.",
				control = { k = "action", act = "reset-dither", label = "Reset" },
			},
		}
	end

	if ui_state(state).page == "look" then
		return {
			{
				id = "fit",
				label = "Fit",
				hint = if animated()
					then "An animation fills the screen: the frames only line up as one grid."
					else "How the picture meets the window.",
				-- The value is what is drawn, not what is stored: an animation
				-- is always the cover crop, and the stored choice comes back
				-- when the picture is still again.
				disabled = if animated() then "An animation always fills the screen." else nil,
				control = { k = "choice", value = if animated() then "cover" else s.fit, options = {
					{ value = "contain", label = "Fit to screen", detail = "The whole picture, letterboxed." },
					{ value = "cover", label = "Fill the screen", detail = "Fills the window, cropping the overflow." },
					{ value = "stretch", label = "Stretch to screen", detail = "Fills the window exactly, so the picture's shape changes." },
					{ value = "tile", label = "Tile", detail = "One image pixel per pixel, repeated." },
				} },
			},
			{
				id = "opacity",
				label = "Opacity",
				hint = "The rest is a black scrim, so the window's own colours stay readable.",
				control = { k = "number", value = math.floor(s.opacity * 100 + 0.5), min = 0, max = 100, step = 5, unit = "%" },
			},
			{
				id = "pixelated",
				label = "Hard pixel edges",
				hint = "Scales with the nearest pixel instead of smoothing.",
				control = { k = "switch", on = s.pixelated },
			},
			{
				id = "reset-look",
				label = "Reset the fit",
				hint = "Back to filling the screen at 30%.",
				control = { k = "action", act = "reset-look", label = "Reset" },
			},
		}
	end

	return status_rows(state)
end

-- --- writing a setting back -------------------------------------------------

local function apply_change(state: State, item: string, value: any)
	local s = state.s
	local n = tonumber(value)

	if item == "enabled" then
		s.enabled = value == true
	elseif item == "claim" then
		s.claim = value == true
	elseif item == "invert" then
		s.invert = value == true
	elseif item == "pixelated" then
		s.pixelated = value == true
	elseif item == "smooth" then
		s.smooth = value == true
	elseif item == "animate" then
		s.animate = value == true
	elseif item == "frames" then
		s.frames = clamp(math.floor(n or s.frames), 2, MAX_FRAMES)
	elseif item == "mode" then
		s.mode = one_of(tostring(value), MODES, s.mode)
	elseif item == "algo" then
		s.algo = one_of(tostring(value), ALGOS, s.algo)
	elseif item == "matrix" then
		s.matrix = one_of(tostring(value), MATRICES, s.matrix)
	elseif item == "pattern" then
		s.pattern = one_of(tostring(value), PATTERNS, s.pattern)
	elseif item == "pixelate" then
		s.pixelate = clamp(math.floor(n or s.pixelate), 1, 25)
	elseif item == "palette" then
		s.palette = one_of(tostring(value), PALETTES, s.palette)
	elseif item == "extract" then
		s.extract = clamp(math.floor(n or s.extract), 2, 16)
	elseif item == "extract_method" then
		s.extract_method = one_of(tostring(value), METHODS, s.extract_method)
	elseif item == "fit" then
		s.fit = one_of(tostring(value), FITS, s.fit)
	elseif item == "scale" then
		s.scale = clamp(math.floor(n or s.scale), 1, 16)
	elseif item == "maxside" then
		s.maxside = clamp_maxside(n or s.maxside)
	elseif item == "cut" then
		s.cut = clamp((n or 50) / 100, 0, 1)
	elseif item == "brightness" then
		s.brightness = clamp((n or 0) / 100, -1, 1)
	elseif item == "contrast" then
		s.contrast = clamp((n or 100) / 100, 0.1, 3)
	elseif item == "opacity" then
		s.opacity = clamp((n or 30) / 100, 0, 1)
	else
		return
	end

	write_settings(s)
	bump()
	state.seen_rev = knum("rev", 0)
	-- Tern redraws the page's document when a row's value changes, which puts
	-- the scroll back at the top. `focus` is the one scroll the page takes
	-- from us: it brings a row back into view, so the row the user just
	-- changed is where they left it.
	ui_state(state).focus = item
end

local function commit_text(state: State, row: string, text: string): boolean
	if row == "source" then
		if text ~= "" and not tern.fs.exists(text) then
			state.preview_error = "no such file: " .. text
			return false
		end
		if text ~= "" and not is_image(text) then
			state.preview_error = "not an image file: " .. base(text)
			return false
		end
		state.preview_error = nil
		state.s.source = text
	elseif row == "colors" then
		state.s.colors = text
		state.preview_error = nil
	end
	write_settings(state.s)
	bump()
	state.seen_rev = knum("rev", 0)
	ui_state(state).focus = row
	return true
end

-- The dialog is spawned rather than drawn: Tern has no file-picker API, and
-- the host half runs on the machine the files are on.
local DIALOG = table.concat({
	"Add-Type -AssemblyName System.Windows.Forms;",
	"$f = New-Object System.Windows.Forms.Form;",
	"$f.TopMost = $true; $f.ShowInTaskbar = $false; $f.Opacity = 0;",
	"$f.StartPosition = 'CenterScreen'; $f.Size = New-Object System.Drawing.Size(1, 1);",
	"$d = New-Object System.Windows.Forms.OpenFileDialog;",
	"$d.Title = 'Choose a wallpaper image';",
	"$d.Filter = 'Images (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff|All files (*.*)|*.*';",
	"$d.CheckFileExists = $true;",
	"if ($d.ShowDialog($f) -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Out.Write($d.FileName) }",
	"$f.Dispose()",
}, " ")

local function pick(state: State, cx: BlockCx)
	if state.picking then
		return
	end
	state.picking = true
	cx:render()

	-- As in the render loop: a spawn failure raises at the call, and leaving
	-- `picking` set would keep the Choose button dead for the session.
	local ok, err = pcall(tern.process.run, { "powershell.exe", "-NoProfile", "-STA", "-Command", DIALOG }, { timeout_ms = 600000 }, function(r)
		state.picking = false
		local path = (r.stdout or ""):gsub("^%s+", ""):gsub("%s+$", "")
		if r.status == 0 and path == "" then
			-- The dialog closed without a pick: that is a cancel, not an error.
			state.preview_error = nil
		elseif r.status ~= 0 or path == "" then
			-- Anything else is a failure the user must see: no powershell.exe,
			-- a script error, or the dialog still open when the deadline passed.
			local why = if r.timed_out
				then "the file dialog timed out"
				else "the file dialog failed: " .. ((r.stderr or ""):gsub("%s+$", ""):sub(1, 160))
			state.preview_error = why
		elseif not is_image(path) then
			state.preview_error = "not an image file: " .. base(path)
		else
			state.preview_error = nil
			state.s.source = path
			write_settings(state.s)
			bump()
			state.seen_rev = knum("rev", 0)
		end
		cx:render()
	end)

	if not ok then
		state.picking = false
		state.preview_error = "the file dialog failed: " .. tostring(err)
		cx:render()
	end
end

-- --- the view ---------------------------------------------------------------

local function build(state: State): { main: Node, dock: Node }
	local s = state.s
	local pages = {
		{ id = "image", label = "Image", group = "Wallpaper", icon = "image" },
		{ id = "dither", label = "Dither", group = "Wallpaper", icon = "wand" },
		{ id = "tone", label = "Tone", group = "Wallpaper", icon = "sliders" },
		{ id = "look", label = "Fit and opacity", group = "Wallpaper", icon = "eye" },
		{ id = "status", label = "Status", group = "About", icon = "info" },
	}

	local lead = if s.source == ""
		then "Pick an image, then choose how it is dithered and how far it shows through."
		else base(s.source)

	-- `editing` is what makes a popup choice row show its menu and a text row
	-- show its draft; the two want different fields.
	local editing = nil
	local u = ui_state(state)
	if u.editing ~= nil then
		local row = u.editing.row
		if TEXT_ROWS[row] then
			local draft = u.editing.draft or ""
			editing = {
				row = row,
				draft = draft,
				cursor = utf16_caret(draft, u.editing.cursor or (#draft + 1)),
			}
		else
			editing = { row = row, option = u.editing.option }
		end
	end

	-- The preview is a child of the page, so it sits at the end of the
	-- document and scrolls with it. As a second child of `main` it would sit
	-- above the page instead.
	local children: { Node } = {}
	if state.preview ~= nil then
		-- The preview is the rendered file, and an animated render is every
		-- frame in one image, so the row says so: the picture above is a grid
		-- of cells, not the wallpaper's own layout.
		local lines: { { any } } = {
			{ ui.span("source", "muted"), ui.span(if s.source == "" then "none" else base(s.source)) },
			{ ui.span("render", "muted"), ui.span(if kstr("render", "") == "" then "none" else base(kstr("render", ""))) },
		}
		if animated() then
			table.insert(lines, {
				ui.span("frames", "muted"),
				ui.span(string.format("%d, one per cell of %d×%d",
					knum("anim_frames", 0), knum("anim_cols", 1), knum("anim_rows", 1))),
			})
		end
		table.insert(lines, { ui.span("note", "muted"), ui.span(kstr("note", "nothing rendered yet")) })

		local row = ui.row({
			ui.node("image", {
				blob = state.preview,
				alt = if animated() then "Wallpaper frames, one per cell" else "Wallpaper preview",
				max = { h = "18lines", w = "36ch" },
			}),
			ui.kv(lines),
		})
		local p: { [string]: any } = row.p or {}
		p.role = "wallpaper.preview"
		p.key = "preview"
		row.p = p
		table.insert(children, row)
	end

	local prefs = ui.node("prefs", {
		key = "prefs",
		title = "Wallpaper",
		lead = lead,
		pages = pages,
		page = u.page,
		focus = u.focus,
		editing = editing,
		sections = {
			{
				id = u.page,
				title = if u.page == "status" then "Render" else nil,
				rows = rows_for(state),
			},
		},
	}, children)


	-- The prefs node needs a wrapper: a region's root node is drawn as its
	-- children, so a prefs node returned as `main` itself would never get its
	-- own element.
	local main: Node = ui.col({ prefs })

	-- What this view carries is what Tern will diff the next one against.
	u.focus_sent = u.focus


	local dock: { Node } = {}
	if state.picking then
		table.insert(dock, ui.text({ ui.span("waiting for the file dialog…", "pending") }))
	end
	if state.preview_error ~= nil then
		table.insert(dock, ui.text({ ui.span(state.preview_error, "error") }))
	end
	if #dock == 0 then
		table.insert(dock, ui.text({
			ui.span(if s.source == "" then "no image chosen" else base(s.source), "muted"),
		}))
	end

	return { main = main, dock = ui.row(dock) }
end

-- --- the block --------------------------------------------------------------

local block: BlockDef<State> = {
	init = function(cx, args, saved)
		-- A drop can open this block with the file as its argument.
		if args ~= nil and args[1] ~= nil and args[1] ~= "" and is_image(args[1]) then
			local s = settings()
			s.source = args[1]
			write_settings(s)
			bump()
		end

		local page = "image"
		if type(saved) == "table" and type(saved.page) == "string" then
			page = saved.page
		end

		local state: State = {
			pane = cx.pane,
			s = settings(),
			picking = false,
			engine = tern.fs.exists("engine/dither.exe"),
			seen_rev = knum("rev", 0),
			seen_render_rev = -1,
			preview = nil,
			preview_for = nil,
			preview_error = nil,
		}

		-- The page is the one thing worth restoring: every setting lives in the
		-- kv store, so a restart reads them from there.
		local u = ui_state(state)
		u.page = page
		u.focus = nil
		u.focus_sent = nil
		u.editing = nil

		refresh_preview(state, cx)
		watch(state, cx)
		return state
	end,

	title = function(state)
		local s = state.s
		return if s.source == "" then "Wallpaper" else "Wallpaper · " .. base(s.source)
	end,

	view = function(state, _cx)
		return build(state)
	end,

	-- Only the block's own place on the page is worth restoring: every setting
	-- lives in the kv store, so a restart reads them from there.
	save = function(state)
		return { page = ui_state(state).page, focus = ui_state(state).focus }
	end,

	event = function(state, ev, cx)
		local e: any = ev
		local kind = tostring(e.ev or "")
		local item = tostring(e.item or "")
		if kind == "change" then
			apply_change(state, item, e.value)
			-- A pick from an open menu is the end of that menu: `editing` is
			-- what draws it, and nothing else would clear it until the user
			-- clicked the button again or clicked away. Cleared here for every
			-- change: on a segment or a stepper no menu was open to begin with.
			ui_state(state).editing = nil
			-- Tern only acts on `focus` when the value it receives differs from
			-- the one before, so a second change to the same row would leave
			-- the page at the top. Blank the ring for one render, then
			-- re-assert it: the row the user just changed is where they left it.
			local u = ui_state(state)
			if u.focus ~= nil and u.focus_sent == u.focus then
				u.focus = nil
				cx:render()
				u.focus = item
			end
		elseif kind == "action" then
			local act = tostring(e.act or "")
			if act == "page" then
				local u = ui_state(state)
				u.page = tostring(e.value)
				u.focus = nil
				u.editing = nil
			elseif act == "pick" then
				pick(state, cx)
			elseif act == "reset-dither" then
				local s = state.s
				s.mode = DEFAULTS.mode
				s.algo = DEFAULTS.algo
				s.matrix = DEFAULTS.matrix
				s.pattern = DEFAULTS.pattern
				s.pixelate = DEFAULTS.pixelate
				s.smooth = DEFAULTS.smooth
				s.palette = DEFAULTS.palette
				s.colors = DEFAULTS.colors
				s.extract = DEFAULTS.extract
				s.extract_method = DEFAULTS.extract_method
				s.scale = DEFAULTS.scale
				s.cut = DEFAULTS.cut
				s.brightness = DEFAULTS.brightness
				s.contrast = DEFAULTS.contrast
				s.invert = DEFAULTS.invert
				s.maxside = DEFAULTS.maxside
				s.animate = DEFAULTS.animate
				s.frames = DEFAULTS.frames
				write_settings(s)
				bump()
				state.seen_rev = knum("rev", 0)
			elseif act == "reset-look" then
				local s = state.s
				s.fit = DEFAULTS.fit
				s.opacity = DEFAULTS.opacity
				s.pixelated = DEFAULTS.pixelated
				write_settings(s)
				bump()
				state.seen_rev = knum("rev", 0)
			elseif act == "close" then
				-- Two different things arrive with this act. The page's Close
				-- button sends `action { act = "close" }`; dismissing an open
				-- menu sends `action { act = "close", value = <row> }`. Only
				-- the first one means the block should go away.
				if e.value == nil then
					request_close(cx)
				else
					ui_state(state).editing = nil
				end
			end
		elseif kind == "activate" then
			local u = ui_state(state)
			if TEXT_ROWS[item] then
				local current = row_value(state, item)
				u.editing = { row = item, draft = current, cursor = #current + 1 }
				-- Only a row being typed in gets the ring: the prefs page
				-- scrolls a focused row into view, and doing that for a row
				-- the pointer just clicked yanks the page out from under it.
				u.focus = item
			elseif CHOICE_ROWS[item] then
				u.editing = { row = item, option = row_value(state, item) }
			end
		elseif kind == "select" then
			-- A click on a row's own surface needs no ring, and no scroll.
		end

		cx:render()
	end,
	key = function(state, key, cx)
		local ed = ui_state(state).editing
		if ed == nil then
			-- Nothing to close but the page: Escape is what the Close button's
			-- "esc" keycap promises, so it closes the block.
			if key.name == "escape" then
				request_close(cx)
				return true
			end
			return false
		end

		-- A choice row's menu is Tern's; the only key it needs from us is the
		-- one that closes it.
		if not TEXT_ROWS[ed.row] then
			if key.name == "escape" then
				ui_state(state).editing = nil
				return true
			end
			return false
		end

		local draft = ed.draft
		local cursor = ed.cursor
		local name = key.name
		local text = key.text or ""

		local function put(d: string, c: number)
			ed.draft = d
			ed.cursor = c
		end

		if name == "escape" then
			ui_state(state).editing = nil
		elseif name == "enter" then
			local row = ed.row
			local committed = commit_text(state, row, draft)
			local u = ui_state(state)
			u.editing = nil
			-- The row was already the ring, so Tern sees the same `focus` value
			-- and leaves the page where the change put it. Blank it for one
			-- render to make the next one count.
			if committed and u.focus_sent == u.focus then
				u.focus = nil
				cx:render()
				u.focus = row
			end
		elseif name == "backspace" then
			if cursor > 1 then
				local at = prev_boundary(draft, cursor)
				put(draft:sub(1, at - 1) .. draft:sub(cursor), at)
			end
		elseif name == "delete" then
			if cursor <= #draft then
				put(draft:sub(1, cursor - 1) .. draft:sub(next_boundary(draft, cursor)), cursor)
			end
		elseif name == "left" then
			put(draft, math.max(1, prev_boundary(draft, cursor)))
		elseif name == "right" then
			put(draft, math.min(#draft + 1, next_boundary(draft, cursor)))
		elseif name == "home" then
			put(draft, 1)
		elseif name == "end" then
			put(draft, #draft + 1)
		elseif name == "paste" then
			put(draft:sub(1, cursor - 1) .. text .. draft:sub(cursor), cursor + #text)
		elseif #text > 0 and not key.ctrl and not key.meta and not key.alt then
			put(draft:sub(1, cursor - 1) .. text .. draft:sub(cursor), cursor + #text)
		else
			return false
		end

		return true
	end,
}

tern.block.define("settings", block)

-- ============================================================ start

pcall(tern.fs.mkdir, RENDER_DIR)

migrate()

tern.timer(POLL_MS, tick)
