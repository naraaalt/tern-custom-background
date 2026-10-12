--!strict
-- Wallpaper: the window half. It owns one thing - the global style sheet that
-- paints the picture behind the whole window and clears the fills the app
-- draws over it - and the commands and routes that change it.
--
-- The host half (host.luau) renders the picture and writes `picture` and
-- `render_rev` to the plugin's kv store. This half polls those, so a window
-- that never runs a block still shows the wallpaper.

-- The picture is painted once, on the window itself. It used to go on the
-- window's four surfaces - main#panel, aside#sidebar, nav#tabbar,
-- footer#statusline - and each of them fitted it to its own box, so one
-- picture became four different crops side by side. The bars are the visible
-- half of that: the tab bar is 1920x49 and the status line 1920x32, so `cover`
-- on them scales the picture to the window's width - 5.2x - and keeps nine of
-- its 207 rows: 98.8 to 108.2 and 100.4 to 106.6. Those are the lit rows of
-- the portrait: 402 lit pixels across rows 99-108, 40 a row, against a mean of
-- 21 for the picture as a whole and 0 for its bottom rows. The pane beside
-- them showed rows 9 to 198 of the 207, 94% of the picture black. So the
-- wallpaper read as "it only shows at the top and the bottom", which is what
-- it did. One sheet on the window is one crop, and it is the crop `fit` names:
-- the screen.
local WINDOW = "body"

local POLL_MS = 150
local STEP = 0.05
local DEFAULT_OPACITY = 0.30

-- Where the settings block opens: `beside` splits the pane it was asked from,
-- so the window you are restyling stays in view while you change it. The other
-- placements are `below`, `tab`, `replace` and `preview`; `split` means the
-- same as `beside`.
local PLACEMENT = "beside"

-- The block kind, as `cx:new_block` and `PaneInfo.block` spell it: the
-- manifest id and the block id, not the `plugin.` surface prefix.
local BLOCK_KIND = "wallpaper.settings"

local IMAGE_EXT = {
	png = true, jpg = true, jpeg = true, gif = true, bmp = true, tif = true, tiff = true,
}

local applied = -1
local last_error: string? = nil
-- The last close request this window carried out, so a stale one in the kv
-- store is not acted on again after a reload.
local handled_close: number? = nil

local function kstr(key: string, fallback: string): string
	local v = tern.kv.get(key)
	return if type(v) == "string" then v else fallback
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

local function clamp(v: number, lo: number, hi: number): number
	return math.max(lo, math.min(hi, v))
end

local function is_image(path: string): boolean
	local ext = string.match(path, "%.([%a%d]+)$")
	return ext ~= nil and IMAGE_EXT[string.lower(ext)] == true
end

local function base(path: string): string
	return string.match(path, "([^/\\]+)$") or path
end

-- --- the sheet --------------------------------------------------------------

-- What the app paints over the picture. Its own surfaces are transparent, but
-- the content it draws on them is not: the transcript's code blocks, the
-- settings page's cards and its text fields all take `--code-bg`, the rest of
-- its cards take `--card`, and two containers (the composer, a note card)
-- carry fills no token reaches. Left alone they hide the picture everywhere
-- but the window's margins - 33% of the window's probe points, measured - so
-- the sheet clears them and the picture becomes the window's one background:
-- the app's own colours stay in its text, borders and icons, which is what
-- keeps it readable.
local CLEAR_FILLS = [[
:root {
	--tv-bg: transparent !important;
	--panel: transparent !important;
	--card: transparent !important;
	--code-bg: transparent !important;
}
.sf-col, .sf-card {
	background-color: transparent !important;
}
]]

-- --- the animation ----------------------------------------------------------

-- The frames of the animation that is up, or nil for a still. The host half
-- writes these from the engine's own report: how many frames it drew, the
-- grid they were laid out in, one cell's size in rendered pixels, and how
-- long each frame is held. None of it can be read off the picture, and the
-- grid is the one thing a sheet cannot be told about any other way: the
-- window is handed a single image of every frame, and the keyframes are what
-- turn it back into an animation.
--
-- nil for anything inconsistent, so a half-written or hand-edited store
-- leaves the still sheet up instead of a broken animation.
local function animation(): { frames: number, cols: number, rows: number, cw: number, ch: number, delays: { number } }?
	local frames = knum("anim_frames", 0)
	local cols = knum("anim_cols", 0)
	local rows = knum("anim_rows", 0)
	local cw = knum("anim_cell_w", 0)
	local ch = knum("anim_cell_h", 0)
	if frames < 2 or cols < 1 or rows < 1 or cw < 1 or ch < 1 or cols * rows < frames then
		return nil
	end

	local delays: { number } = {}
	for part in string.gmatch(kstr("anim_delays", ""), "%d+") do
		table.insert(delays, tonumber(part) :: number)
	end
	if #delays ~= frames then
		return nil
	end

	return { frames = frames, cols = cols, rows = rows, cw = cw, ch = ch, delays = delays }
end

-- One cell of the grid, sized for the cover crop: a frame fills the window,
-- so the cell is as wide as the window unless its own shape is wider than the
-- window's, in which case the height is what fills it. This is `cover` with
-- the picture being one cell rather than the whole grid, which is what keeps
-- the animation's crop identical to the still's.
local function cover_cell(a: number): (string, string)
	return string.format("max(100vw, calc(%.6f * 100vh))", a),
		string.format("max(calc(100vw / %.6f), 100vh)", a)
end

-- Where the image's top-left corner goes so that cell (col, row) is centred
-- on the window: the offsets are lengths, and `background-position` places
-- the image's corner at them. The still sheet gets the same result from
-- `center`, which is why the two line up.
local function cell_position(cw: string, ch: string, col: number, row: number): string
	return string.format("calc(50vw - %.4f * %s) calc(50vh - %.4f * %s)",
		col + 0.5, cw, row + 0.5, ch)
end

-- The sheet for an animated picture: the grid as one background image, and a
-- keyframe per frame that moves it so the next cell sits where the last one
-- was. `step-end` is what makes each frame hold for its own time rather than
-- sliding into the next one - a GIF's frames are stills, not a tween.
local function animated_sheet(
	anim: { frames: number, cols: number, rows: number, cw: number, ch: number, delays: { number } },
	picture: string,
	scrim: string,
	pixels: string
): string
	local total = 0
	for _, d in anim.delays do
		total += d
	end
	if total <= 0 then
		total = anim.frames * 100
	end

	local cw, ch = cover_cell(anim.cw / anim.ch)

	local frames: { string } = {}
	local at = 0
	local last = -1.0
	for i = 1, anim.frames do
		local pct = 0.0
		if i > 1 then
			-- The frames before this one are held first, so this one's
			-- keyframe sits where they end. Offsets must rise, and a frame
			-- held for a fraction of the total can round onto its
			-- predecessor's, which would drop it: nudge instead.
			at += anim.delays[i - 1]
			pct = at / total * 100
			if pct <= last then
				pct = last + 0.001
			end
			last = pct
		end
		local col = (i - 1) % anim.cols
		local row = math.floor((i - 1) / anim.cols)
		table.insert(frames, string.format("\t%.3f%% { background-position: center, %s; }",
			pct, cell_position(cw, ch, col, row)))
	end

	return string.format([[
%s {
	background-image: linear-gradient(rgb(0 0 0 / %s), rgb(0 0 0 / %s)), url("%s");
	background-size: 100%% 100%%, calc(%d * %s) calc(%d * %s);
	background-position: center, %s;
	background-repeat: no-repeat, no-repeat;%s
	animation: wallpaper-frames %dms step-end infinite;
}
@keyframes wallpaper-frames {
%s
}
@media (prefers-reduced-motion: reduce) {
	%s {
		animation: none;
	}
}
%s]], WINDOW, scrim, scrim, picture,
		anim.cols, cw, anim.rows, ch,
		cell_position(cw, ch, 0, 0), pixels, total,
		table.concat(frames, "\n"), WINDOW, CLEAR_FILLS)
end

local function sheet(): string
	local picture = kstr("picture", "")
	if picture == "" or not kbool("enabled", true) then
		-- Not "" - an empty source is at best a no-op. This sheet replaces the
		-- one that painted, so dropping the rule takes the picture away, leaves
		-- the window its own background and gives the app its fills back.
		return WINDOW .. " {\n\tbackground-image: none;\n}"
	end

	-- The knob is the wallpaper's opacity, so the scrim carries 1 - opacity:
	-- at 100% the picture is untouched, at 0% it is a black sheet.
	local scrim = string.format("%.3f", 1 - clamp(knum("opacity", DEFAULT_OPACITY), 0, 1))

	local pixels = if kbool("pixelated", false) then "\n\timage-rendering: pixelated;" else ""

	local anim = animation()
	if anim ~= nil then
		return animated_sheet(anim, picture, scrim, pixels)
	end

	-- The picture's size and repeat, by fit. The scrim is not part of the fit:
	-- it always covers the whole surface, or a letterboxed picture would leave
	-- its bars undimmed and the window's own colours would show through them.
	local fit = kstr("fit", "cover")
	local size, repeat_rule
	if fit == "contain" then
		size, repeat_rule = "contain", "no-repeat"
	elseif fit == "stretch" then
		size, repeat_rule = "100% 100%", "no-repeat"
	elseif fit == "tile" then
		size, repeat_rule = "auto", "repeat"
	else
		size, repeat_rule = "cover", "no-repeat"
	end

	return string.format([[
%s {
	background-image: linear-gradient(rgb(0 0 0 / %s), rgb(0 0 0 / %s)), url("%s");
	background-size: 100%% 100%%, %s;
	background-position: center, center;
	background-repeat: no-repeat, %s;%s
}
%s]], WINDOW, scrim, scrim, picture, size, repeat_rule, pixels, CLEAR_FILLS)
end

-- The sheet is installed when `render_rev` moves, so a new picture is a new
-- sheet. The picture arrives as a `data:` URL in the kv store, not as a file
-- this half could name: a sheet's `url()` cannot leave the plugin's directory.
local function apply()
	tern.css("wallpaper", sheet())
end

-- Installs the sheet when `render_rev` moves, or when `force` says so: the
-- entry, and the commands that change the sheet without a render. `applied`
-- starts at -1, so the first sync after a load installs unconditionally.
--
-- A failure leaves `applied` where it was, so the next tick tries again:
-- another process may be mid-rewrite of the kv file, and that must cost one
-- tick, not the wallpaper.
local function sync(force: boolean)
	local rev = knum("render_rev", 0)
	if not force and rev == applied then
		return
	end
	local ok, err = pcall(apply)
	if ok then
		applied = rev
		last_error = nil
	elseif err ~= last_error then
		last_error = err
		tern.log.warn("wallpaper: could not install the sheet:", err)
	end
end

-- Carries out a close the settings block asked for. The host half has no
-- `cx.layout`, so it leaves the pane id in `close_pane` and this half, which
-- does, closes the pane. Only the window actually showing that pane finds it
-- among its own, so a second window on the same session is left alone.
local function close_requested(cx: WindowCx?)
	if cx == nil then
		return
	end
	local want = tern.kv.get("close_pane")
	if type(want) ~= "number" or want == handled_close then
		return
	end
	handled_close = want
	for _, p in ipairs(cx.session:panes()) do
		if p.pane == want and p.block == BLOCK_KIND then
			cx.layout:close(want)
			return
		end
	end
end

local function tick(cx)
	-- Arm the next tick before anything else: an error in here must not end
	-- the loop, or the wallpaper would stay off until the window restarts.
	tern.timer(POLL_MS, tick)
	local ok, err = pcall(sync, false)
	if not ok and err ~= last_error then
		last_error = err
		tern.log.warn("wallpaper: could not read the settings:", err)
	end
	pcall(close_requested, cx)
end

-- --- commands ---------------------------------------------------------------

local function set_opacity(cx: WindowCx, value: number, note: string)
	local v = clamp(value, 0, 1)
	tern.kv.set("opacity", v)
	bump()
	sync(true)
	cx:toast("info", note .. string.format(" · %.0f%%", v * 100))
end

local function current_opacity(): number
	return clamp(knum("opacity", DEFAULT_OPACITY), 0, 1)
end

-- --- start ------------------------------------------------------------------

-- The sheet and the poll belong to the entry, not to `window_start`: a reload
-- builds this VM again and runs the entry again, but `window_start` only ever
-- runs once per window per plugin. Anything that must survive a reload has to
-- be set up here, or the window would be left with no sheet and no timer.
sync(true)
tern.timer(POLL_MS, tick)

-- The engine is a build artefact, not source: compile it on first run. The
-- check is cheap and runs again after a reload, so a deleted engine comes
-- back without the user having to run the script by hand.
if not tern.fs.exists("engine/dither.exe") then
	-- A spawn failure raises at the call; catching it keeps the window half
	-- alive (a raise here would mark the whole plugin Failed in this window).
	local ok, err = pcall(tern.process.run,
		{ "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", tern.plugin.dir .. "/engine/build.ps1" },
		{ timeout_ms = 180000 },
		function(r, fresh)
			if r.status == 0 then
				-- The host half gave up on a render while the engine was
				-- missing; moving `rev` tells it to try again now.
				tern.kv.set("rev", knum("rev", 0) + 1)
				if fresh then
					fresh:toast("info", "Wallpaper: built the dither engine")
				end
			elseif fresh then
				fresh:toast("error", "Wallpaper: could not build engine/dither.exe", (r.stderr or ""):sub(1, 200))
			end
		end
	)
	if not ok then
		tern.log.error("wallpaper: could not start the engine build:", err)
	end
end

-- Opens the settings block, or brings it forward when one is already up.
-- `beside` splits the pane it is asked from, so opening a second one would
-- shave another slice off the window every time the command is run.
local function open_settings(cx: WindowCx)
	for _, p in ipairs(cx.session:panes()) do
		if p.block == BLOCK_KIND then
			cx.layout:focus(p.pane)
			return
		end
	end
	cx:new_block(BLOCK_KIND, nil, PLACEMENT)
end

tern.command({
	id = "settings",
	title = "Wallpaper: settings",
	icon = "sliders",
	-- Verified free against this machine's effective keymap: `ctrl+shift+w`
	-- is close_tab and `ctrl+w` is the editor's, but `ctrl+alt+w` is unbound.
	keys = { "ctrl+alt+w" },
	run = function(cx)
		open_settings(cx)
	end,
})

tern.command({
	id = "brighten",
	title = "Wallpaper: brighter",
	icon = "sun",
	run = function(cx)
		set_opacity(cx, current_opacity() + STEP, "Wallpaper opacity")
	end,
})

tern.command({
	id = "dim",
	title = "Wallpaper: dimmer",
	icon = "moon",
	run = function(cx)
		set_opacity(cx, current_opacity() - STEP, "Wallpaper opacity")
	end,
})

tern.command({
	id = "reset",
	title = "Wallpaper: reset opacity",
	icon = "revert",
	run = function(cx)
		set_opacity(cx, DEFAULT_OPACITY, "Wallpaper opacity reset")
	end,
})

tern.command({
	id = "toggle",
	title = "Wallpaper: on or off",
	icon = "eye",
	run = function(cx)
		local on = not kbool("enabled", true)
		tern.kv.set("enabled", on)
		bump()
		sync(true)
		cx:toast("info", if on then "Wallpaper on" else "Wallpaper off")
	end,
})

-- --- dropping a picture -----------------------------------------------------

-- Off by default: claiming every image dropped anywhere in the window would
-- take the drop away from the terminal, which normally inserts the path.
tern.route.open(function(req, cx)
	if not kbool("claim", false) then
		return nil
	end
	if req.origin ~= "drop" or not is_image(req.path) then
		return nil
	end

	tern.kv.set("source", req.path)
	bump()
	open_settings(cx)
	cx:toast("info", "Wallpaper source", base(req.path))
	return { handled = true }
end)
