// dither.exe - decode an image, quantize it to a palette with an optional
// dither, and write a PNG.
//
// Built by build.ps1 with the .NET Framework compiler that ships with Windows
// (C# 5), against System.Drawing. No third-party tools: no ImageMagick, no
// ffmpeg, nothing to install.
//
//   dither.exe --in <path> --out <path>
//              [--mode diffusion|bayer|knoll|bluenoise|riemersma|threshold|none|ascii]
//              [--algo fs|atkinson|jjn|stucki|burkes|sierra3|sierra2|sierra24a|
//                      fan|shiaufan|shiaufan2|simple2d|dizzy]
//              [--matrix 2|4|8|16] [--pattern 2|4|8|16|bluenoise]
//              [--pixelate 1..25] [--smooth]
//              [--glyph 1..8] [--chars " .:-=+*#%@"]
//              [--palette bw|gray4|gray8|gray16|auto|custom:#rrggbb,#rrggbb,...]
//              [--extract N] [--extract-method popularity|median]
//              [--scale 1..16] [--max N]
//              [--threshold 0..1] [--brightness -1..1] [--contrast 0..3]
//              [--invert]
//
// Prints one line: src=WxH out=WxH px=N ms=N bytes=N, and with --palette auto
// the colours it took from the image after it: palette=#rrggbb,...
// Exits 1 and writes a message to stderr on any failure.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class Dither
{
	private static string inPath;
	private static string outPath;
	private static string mode = "diffusion";
	private static string algo = "fs";
	private static int matrix = 8;
	private static string pattern = "8";
	private static int pixelate = 1;
	private static bool smooth = false;
	private static string paletteName = "bw";
	private static double scale = 1.0;
	private static int maxSide = 0;
	private static double cut = 0.5;
	private static double brightness = 0.0;
	private static double contrast = 1.0;
	private static bool invert = false;
	private static int extract = 8;
	private static string extractMethod = "popularity";

	// The ascii mode's cell, as a multiple of the font's 8x8 square, and the
	// characters it draws with. The characters are passed in rather than
	// named: what makes a good ramp is a matter of taste, the block that
	// drives this has its own list of them, and a name here would be this
	// tool knowing a menu item.
	private static int glyph = 3;
	private static string chars = " .:-=+*#%@";

	// How many frames of an animated source to draw, 1 for a still. An image
	// with fewer frames uses what it has.
	private static int frames = 1;

	private static readonly List<Color> customColors = new List<Color>();

	// How far apart two entries of an extracted palette have to be, squared and
	// in the weights below, before the second one is worth keeping.
	private const double PALETTE_FLOOR = 0.02;

	// The palette is read from its own copy of the picture, not from the
	// dithered one: `--scale` and `--max` change the dither, and must not
	// change which colours come out of the image.
	private const int ANALYSIS_SIDE = 256;

	// One set of weights for everything that asks how different two colours
	// are - the tone, the quantizer and the palette - so they cannot drift
	// apart and disagree about what "nearest" means.
	private const double W_R = 0.30;
	private const double W_G = 0.59;
	private const double W_B = 0.11;

	private static int Main(string[] argv)
	{
		try
		{
			if (!Parse(argv))
			{
				Usage();
				return 1;
			}

			Stopwatch sw = Stopwatch.StartNew();

			// The image is held open for the whole run: GDI+ reads a frame out
			// of the stream on every SelectActiveFrame, so the stream has to
			// outlive the last frame read from it.
			using (FileStream fs = File.OpenRead(inPath))
			using (Image img = Image.FromStream(fs, true, true))
			{
				int sw0 = img.Width;
				int sh0 = img.Height;

				int count = FrameCount(img);
				int use = Math.Max(1, Math.Min(frames, count));
				int[] picked;
				int[] delays;
				PickFrames(FrameDelays(img, count), count, use, out picked, out delays);

				// The cell is one frame's dithered picture; the grid holds them
				// all, because the window can only be handed one image.
				Size cell = FitSize(sw0, sh0);
				int cols = 1;
				int rows = 1;
				if (use > 1)
				{
					cols = (int)Math.Ceiling(Math.Sqrt(use));
					rows = (int)Math.Ceiling((double)use / cols);
					cell = FitAtlas(cell, cols, rows);
				}

				// Characters come out whole or not at all, so the cell is cut
				// back to the largest number of them that fits inside it.
				if (mode == "ascii")
				{
					cell = FitAscii(cell);
				}

				// The palette comes off the first frame and is used for every
				// one: a palette per frame would make the colours crawl.
				List<Color> palette = paletteName == "auto"
					? PaletteFromImage(CompositeFrame(img, picked[0]), sw0, sh0)
					: BuildPalette();
				// ASCII is a two-tone render whatever palette is in hand: the
				// glyphs are one colour and the paper another, so the palette
				// is read as its two ends.
				if (mode == "ascii")
				{
					palette = AsciiPair(palette);
				}

				// The pixelate pass runs over the dithered result, so it blocks
				// up the dither pattern rather than the picture, and the size it
				// gives back is the size that gets written. It is computed here
				// because every cell has to come out the same size for the grid
				// to line up.
				//
				// ASCII is left out of it: its cell is already a block of the
				// glyph's own making, and blocking that up again would break the
				// characters apart rather than make them coarser. The glyph size
				// is the one knob for how big a character is.
				int w = cell.Width;
				int h = cell.Height;
				if (pixelate > 1 && mode != "ascii")
				{
					w = Math.Max(1, (w + pixelate - 1) / pixelate);
					h = Math.Max(1, (h + pixelate - 1) / pixelate);
				}

				int aw = w * cols;
				int ah = h * rows;
				int[] atlas = new int[aw * ah];

				for (int f = 0; f < use; f++)
				{
					using (Bitmap frame = CompositeFrame(img, picked[f]))
					using (Bitmap small = Downscale(frame, cell.Width, cell.Height))
					{
						double[] px = ReadPixels(small, cell.Width, cell.Height);
						ApplyTone(px);

						int[] index = new int[cell.Width * cell.Height];
						DitherCell(px, cell.Width, cell.Height, palette, index);

						if (pixelate > 1)
						{
							int pw;
							int ph;
							index = Pixelate(index, cell.Width, cell.Height, palette, out pw, out ph);
						}

						Blit(atlas, aw, (f % cols) * w, (f / cols) * h, w, h, index);
					}
				}

				WritePng(atlas, aw, ah, palette, outPath);

				long bytes = new FileInfo(outPath).Length;
				string line = string.Format(CultureInfo.InvariantCulture,
					"src={0}x{1} out={2}x{3} px={4} ms={5} bytes={6}",
					sw0, sh0, aw, ah, aw * ah, sw.ElapsedMilliseconds, bytes);
				// The palette is the render's own answer, and the only place
				// it can be read back: the caller asked for it by name.
				if (paletteName == "auto")
				{
					line += " palette=" + HexList(palette);
				}
				// An animation's grid and its per-frame times. The caller reads
				// these back to build the keyframes; the image alone does not
				// say where one frame ends and the next begins.
				if (use > 1)
				{
					string[] parts = new string[delays.Length];
					for (int i = 0; i < delays.Length; i++)
					{
						parts[i] = delays[i].ToString(CultureInfo.InvariantCulture);
					}
					line += string.Format(CultureInfo.InvariantCulture,
						" frames={0} cell={1}x{2} cols={3} rows={4} delays={5}",
						use, w, h, cols, rows, string.Join(",", parts));
				}
				Console.WriteLine(line);
			}

			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("dither: " + ex.Message);
			return 1;
		}
	}

	// The dither itself, on one frame's pixels. Split out of Main because an
	// animation runs it once per frame, into one cell of the grid.
	private static void DitherCell(double[] px, int w, int h, List<Color> palette, int[] index)
	{
		if (mode == "ascii")
		{
			Ascii(px, w, h, palette, index);
		}
		else if (mode == "bayer")
		{
			Ordered(px, w, h, palette, index, Bayer(matrix), matrix);
		}
		else if (mode == "bluenoise")
		{
			Ordered(px, w, h, palette, index, BlueNoise(), BLUE_SIDE);
		}
		else if (mode == "knoll")
		{
			Knoll(px, w, h, palette, index);
		}
		else if (mode == "riemersma")
		{
			Riemersma(px, w, h, palette, index);
		}
		else if (mode == "threshold")
		{
			Threshold(px, w, h, palette, index);
		}
		else if (mode == "none")
		{
			Nearest(px, w, h, palette, index);
		}
		else
		{
			double[] kernel = Kernel(algo);
			if (kernel == null)
			{
				Dizzy(px, w, h, palette, index);
			}
			else
			{
				Diffuse(px, w, h, palette, index, kernel);
			}
		}
	}

	private static void Usage()
	{
		Console.Error.WriteLine("usage: dither --in PATH --out PATH [options]");
		Console.Error.WriteLine("  --mode      diffusion | bayer | knoll | bluenoise | riemersma");
		Console.Error.WriteLine("              | threshold | none | ascii                        (diffusion)");
		Console.Error.WriteLine("  --algo      diffusion only: fs | atkinson | jjn | stucki | burkes");
		Console.Error.WriteLine("              | sierra3 | sierra2 | sierra24a | fan | shiaufan");
		Console.Error.WriteLine("              | shiaufan2 | simple2d | dizzy                      (fs)");
		Console.Error.WriteLine("  --matrix    bayer only: 2 | 4 | 8 | 16                          (8)");
		Console.Error.WriteLine("  --pattern   knoll only: 2 | 4 | 8 | 16 | bluenoise              (8)");
		Console.Error.WriteLine("  --pixelate  block the dithered result by N, 1..25               (1)");
		Console.Error.WriteLine("  --smooth    pixelate by averaging the block, not by sampling it");
		Console.Error.WriteLine("  --palette   bw | gray4 | gray8 | gray16 | custom:#rrggbb,...");
		Console.Error.WriteLine("              | auto, the colours taken from the image             (bw)");
		Console.Error.WriteLine("  --extract   colours to take from the image, 2..256               (8)");
		Console.Error.WriteLine("  --extract-method  popularity | median                            (popularity)");
		Console.Error.WriteLine("  --frames    frames of an animated source to draw, 1..48          (1)");
		Console.Error.WriteLine("              an atlas of ceil(sqrt(N)) columns is written instead");
		Console.Error.WriteLine("  --glyph     ascii only: character size, 1..8, in 8x8 font cells  (3)");
		Console.Error.WriteLine("  --chars     ascii only: the characters to draw with, lightest to");
		Console.Error.WriteLine("              densest, up to 95 of printable ASCII     ( .:-=+*#%@)");
		Console.Error.WriteLine("  --scale     integer downscale divisor, 1..16                     (1)");
		Console.Error.WriteLine("  --max       cap the longest output side at N pixels              (none)");
		Console.Error.WriteLine("  --threshold 0..1, the cut used by the bw palette                 (0.5)");
		Console.Error.WriteLine("  --brightness -1..1                                               (0)");
		Console.Error.WriteLine("  --contrast  0..3                                                 (1)");
		Console.Error.WriteLine("  --invert");
		Console.Error.WriteLine("With --palette auto the palette used is printed after the status line.");
	}

	private static bool Parse(string[] a)
	{
		for (int i = 0; i < a.Length; i++)
		{
			string k = a[i];
			string v = i + 1 < a.Length ? a[i + 1] : null;
			switch (k)
			{
				case "--in":
					if (v == null) return false;
					inPath = v; i++; break;
				case "--out":
					if (v == null) return false;
					outPath = v; i++; break;
				case "--algo":
					if (v == null) return false;
					algo = v.ToLowerInvariant(); i++; break;
				case "--mode":
					if (v == null) return false;
					mode = v.ToLowerInvariant(); i++; break;
				case "--matrix":
					if (v == null) return false;
					matrix = int.Parse(v, CultureInfo.InvariantCulture); i++; break;
				case "--pattern":
					if (v == null) return false;
					pattern = v.ToLowerInvariant(); i++; break;
				case "--pixelate":
					if (v == null) return false;
					pixelate = int.Parse(v, CultureInfo.InvariantCulture); i++; break;
				case "--smooth":
					smooth = true; break;
				case "--palette":
					if (v == null) return false;
					paletteName = v.ToLowerInvariant(); i++; break;
				case "--scale":
					if (v == null) return false;
					scale = double.Parse(v, CultureInfo.InvariantCulture); i++; break;
				case "--max":
					if (v == null) return false;
					maxSide = int.Parse(v, CultureInfo.InvariantCulture); i++; break;
				case "--threshold":
					if (v == null) return false;
					cut = double.Parse(v, CultureInfo.InvariantCulture); i++; break;
				case "--brightness":
					if (v == null) return false;
					brightness = double.Parse(v, CultureInfo.InvariantCulture); i++; break;
				case "--contrast":
					if (v == null) return false;
					contrast = double.Parse(v, CultureInfo.InvariantCulture); i++; break;
				case "--extract":
					if (v == null) return false;
					extract = int.Parse(v, CultureInfo.InvariantCulture); i++; break;
				case "--extract-method":
					if (v == null) return false;
					extractMethod = v.ToLowerInvariant(); i++; break;
				case "--glyph":
					if (v == null) return false;
					glyph = int.Parse(v, CultureInfo.InvariantCulture); i++; break;
				case "--chars":
					if (v == null) return false;
					chars = v; i++; break;
				case "--frames":
					if (v == null) return false;
					frames = int.Parse(v, CultureInfo.InvariantCulture); i++; break;
				case "--invert":
					invert = true; break;
				default:
					Console.Error.WriteLine("dither: unknown option " + k);
					return false;
			}
		}

		if (inPath == null || outPath == null)
		{
			Console.Error.WriteLine("dither: --in and --out are required");
			return false;
		}
		if (!File.Exists(inPath))
		{
			Console.Error.WriteLine("dither: no such file: " + inPath);
			return false;
		}

		if (scale < 1.0) scale = 1.0;
		if (scale > 16.0) scale = 16.0;
		if (cut < 0.0) cut = 0.0;
		if (cut > 1.0) cut = 1.0;
		if (extract < 2) extract = 2;
		if (extract > 256) extract = 256;
		if (frames < 1) frames = 1;
		if (frames > 48) frames = 48;

		if (extractMethod != "popularity" && extractMethod != "median")
		{
			Console.Error.WriteLine("dither: unknown --extract-method " + extractMethod);
			return false;
		}

		if (mode != "diffusion" && mode != "bayer" && mode != "knoll" && mode != "bluenoise"
			&& mode != "riemersma" && mode != "threshold" && mode != "none" && mode != "ascii")
		{
			Console.Error.WriteLine("dither: unknown --mode " + mode);
			return false;
		}

		// Checked whatever the mode, like the kernel: characters that cannot
		// be drawn are wrong whether or not this run draws them.
		if (glyph < 1) glyph = 1;
		if (glyph > 8) glyph = 8;
		if (chars.Length == 0)
		{
			Console.Error.WriteLine("dither: --chars needs at least one character");
			return false;
		}
		if (chars.Length > 95)
		{
			Console.Error.WriteLine("dither: --chars takes at most 95 characters");
			return false;
		}
		foreach (char c in chars)
		{
			if (c < 0x20 || c > 0x7E)
			{
				Console.Error.WriteLine("dither: --chars takes printable ASCII, not " + ((int)c));
				return false;
			}
		}
		ramp = Ramp(chars);

		// Checked whatever the mode: the name is wrong either way, and only
		// the diffusion mode reads it.
		if (!IsKernel(algo))
		{
			Console.Error.WriteLine("dither: unknown --algo " + algo);
			return false;
		}

		if (matrix != 2 && matrix != 4 && matrix != 8 && matrix != 16)
		{
			Console.Error.WriteLine("dither: --matrix takes 2, 4, 8 or 16");
			return false;
		}

		if (!IsPattern(pattern))
		{
			Console.Error.WriteLine("dither: unknown --pattern " + pattern);
			return false;
		}

		if (pixelate < 1) pixelate = 1;
		if (pixelate > 25) pixelate = 25;

		if (paletteName.StartsWith("custom:", StringComparison.Ordinal))
		{
			foreach (string part in paletteName.Substring("custom:".Length).Split(','))
			{
				string s = part.Trim();
				if (s.Length == 0)
				{
					continue;
				}
				try
				{
					customColors.Add(ParseHex(s));
				}
				catch (Exception)
				{
					Console.Error.WriteLine("dither: bad colour in --palette: " + s);
					return false;
				}
			}
			if (customColors.Count == 0)
			{
				Console.Error.WriteLine("dither: --palette custom: needs at least one colour");
				return false;
			}
			if (customColors.Count > 256)
			{
				Console.Error.WriteLine("dither: --palette custom: takes at most 256 colours");
				return false;
			}
		}
		else if (paletteName != "bw" && paletteName != "gray4" && paletteName != "gray8"
			&& paletteName != "gray16" && paletteName != "auto")
		{
			Console.Error.WriteLine("dither: unknown --palette " + paletteName);
			return false;
		}

		return true;
	}

	private static Color ParseHex(string s)
	{
		if (s[0] == '#')
		{
			s = s.Substring(1);
		}
		if (s.Length == 3)
		{
			s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
		}
		if (s.Length != 6)
		{
			throw new FormatException(s);
		}
		return Color.FromArgb(
			int.Parse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
			int.Parse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
			int.Parse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
	}

	// GDI+ decodes JPEG, PNG, BMP, GIF and TIFF. It has no WebP decoder unless
	// a system codec is installed, and that failure is reported rather than
	// guessed at.
	//
	// One frame, drawn onto a full-size 32bpp canvas. GDI+ hands back a
	// complete frame for a GIF rather than the partial update the file stores,
	// so no compositing of frames is needed here; the copy is what turns the
	// frame into a bitmap this code can read and scale.
	private static Bitmap CompositeFrame(Image img, int index)
	{
		try
		{
			Guid[] dims = img.FrameDimensionsList;
			if (dims != null && dims.Length > 0)
			{
				img.SelectActiveFrame(new FrameDimension(dims[0]), index);
			}

			Bitmap copy = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
			using (Graphics g = Graphics.FromImage(copy))
			{
				g.CompositingMode = CompositingMode.SourceCopy;
				g.InterpolationMode = InterpolationMode.NearestNeighbor;
				g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
			}
			return copy;
		}
		catch (Exception ex)
		{
			throw new Exception("cannot decode " + Path.GetFileName(inPath) + ": " + ex.Message);
		}
	}

	// How many frames the image holds. A still image, and anything GDI+ will
	// not answer for, is one frame.
	private static int FrameCount(Image img)
	{
		try
		{
			Guid[] dims = img.FrameDimensionsList;
			if (dims == null || dims.Length == 0)
			{
				return 1;
			}
			return Math.Max(1, img.GetFrameCount(new FrameDimension(dims[0])));
		}
		catch
		{
			return 1;
		}
	}

	// The delay of each frame in milliseconds. A GIF stores them as hundredths
	// of a second in property 0x5100, one little-endian 32-bit word per frame;
	// a frame that asks for no delay gets the 100 ms browsers fall back to, or
	// a GIF that sets none at all would animate at the frame rate of the
	// display.
	private static int[] FrameDelays(Image img, int count)
	{
		const int FALLBACK_MS = 100;
		int[] ms = new int[count];
		bool have = false;

		try
		{
			PropertyItem pi = img.GetPropertyItem(0x5100);
			int n = pi.Value.Length / 4;
			for (int i = 0; i < count; i++)
			{
				int hundredths = i < n ? BitConverter.ToInt32(pi.Value, i * 4) : 0;
				ms[i] = hundredths > 0 ? hundredths * 10 : FALLBACK_MS;
			}
			have = true;
		}
		catch
		{
			// No property: not an animation, or one that sets no delays.
		}

		if (!have)
		{
			for (int i = 0; i < count; i++)
			{
				ms[i] = FALLBACK_MS;
			}
		}
		return ms;
	}

	// Which frames to draw, and how long each is held. Asking for fewer frames
	// than the image has samples them evenly and adds the delays of the ones it
	// skips to the frame that stands in for them, so the animation keeps its
	// length and its pace rather than running fast.
	private static void PickFrames(int[] all, int count, int use, out int[] picked, out int[] delays)
	{
		picked = new int[use];
		delays = new int[use];

		for (int j = 0; j < use; j++)
		{
			int from = (int)((long)j * count / use);
			int to = (int)((long)(j + 1) * count / use);
			if (to <= from)
			{
				to = from + 1;
			}
			if (to > count)
			{
				to = count;
			}

			picked[j] = from;
			int sum = 0;
			for (int i = from; i < to; i++)
			{
				sum += all[i];
			}
			delays[j] = sum > 0 ? sum : 100;
		}
	}

	// An atlas of every frame is `cols` cells wide and `rows` cells tall, and
	// Tern refuses a CSS image longer than 4096 px on a side, so the cell
	// shrinks until the grid fits. Fewer, larger frames beat more, tiny ones,
	// which is why the caller picks the frame count and this only reacts.
	private const int ATLAS_SIDE = 4096;

	private static Size FitAtlas(Size cell, int cols, int rows)
	{
		double f = 1.0;
		if ((double)cell.Width * cols > ATLAS_SIDE)
		{
			f = Math.Min(f, (double)ATLAS_SIDE / (cell.Width * cols));
		}
		if ((double)cell.Height * rows > ATLAS_SIDE)
		{
			f = Math.Min(f, (double)ATLAS_SIDE / (cell.Height * rows));
		}
		if (f >= 1.0)
		{
			return cell;
		}

		// Floored, not rounded: rounding up a cell can put the grid back over
		// the limit it was just scaled to fit (six columns of a cell rounded
		// from 682.67 to 683 are 4098 px wide, and Tern refuses the image).
		// The clamp is the same guarantee for a cell so small that the scale
		// rounds it to zero.
		return new Size(
			Math.Max(1, Math.Min(ATLAS_SIDE / cols, (int)Math.Floor(cell.Width * f))),
			Math.Max(1, Math.Min(ATLAS_SIDE / rows, (int)Math.Floor(cell.Height * f))));
	}

	private static void Blit(int[] atlas, int aw, int ox, int oy, int w, int h, int[] cell)
	{
		for (int y = 0; y < h; y++)
		{
			Array.Copy(cell, y * w, atlas, (oy + y) * aw + ox, w);
		}
	}

	private static Size FitSize(int w, int h)
	{
		double f = 1.0 / scale;

		if (maxSide > 0)
		{
			double longest = Math.Max(w, h);
			if (longest > maxSide)
			{
				f = Math.Min(f, maxSide / longest);
			}
		}

		return new Size(Math.Max(1, (int)Math.Round(w * f)), Math.Max(1, (int)Math.Round(h * f)));
	}

	private static Bitmap Downscale(Bitmap src, int w, int h)
	{
		if (src.Width == w && src.Height == h)
		{
			return new Bitmap(src);
		}

		Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
		using (Graphics g = Graphics.FromImage(dst))
		{
			g.CompositingMode = CompositingMode.SourceCopy;
			g.InterpolationMode = InterpolationMode.HighQualityBicubic;
			g.PixelOffsetMode = PixelOffsetMode.HighQuality;
			g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
		}
		return dst;
	}

	// Flat float RGB in 0..1. Alpha is composited over black, so a transparent
	// source reads as a dark one instead of an arbitrary one.
	private static double[] ReadPixels(Bitmap bmp, int w, int h)
	{
		double[] px = new double[w * h * 3];

		BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
		try
		{
			int stride = data.Stride;
			byte[] row = new byte[stride];
			for (int y = 0; y < h; y++)
			{
				Marshal.Copy(IntPtr.Add(data.Scan0, y * stride), row, 0, stride);
				for (int x = 0; x < w; x++)
				{
					int i = x * 4;
					double a = row[i + 3] / 255.0;
					int o = (y * w + x) * 3;
					px[o] = row[i + 2] / 255.0 * a;
					px[o + 1] = row[i + 1] / 255.0 * a;
					px[o + 2] = row[i] / 255.0 * a;
				}
			}
		}
		finally
		{
			bmp.UnlockBits(data);
		}

		return px;
	}

	private static void ApplyTone(double[] px)
	{
		if (brightness == 0.0 && contrast == 1.0 && !invert)
		{
			return;
		}

		for (int i = 0; i < px.Length; i++)
		{
			double v = px[i];
			if (contrast != 1.0)
			{
				v = (v - 0.5) * contrast + 0.5;
			}
			v += brightness;
			if (invert)
			{
				v = 1.0 - v;
			}
			px[i] = Clamp01(v);
		}
	}

	private static List<Color> BuildPalette()
	{
		List<Color> p = new List<Color>();

		if (customColors.Count > 0)
		{
			p.AddRange(customColors);
			return p;
		}

		if (paletteName == "bw")
		{
			p.Add(Color.Black);
			p.Add(Color.White);
			return p;
		}

		int levels = paletteName == "gray4" ? 4 : paletteName == "gray8" ? 8 : 16;
		for (int i = 0; i < levels; i++)
		{
			int v = (int)Math.Round(255.0 * i / (levels - 1));
			p.Add(Color.FromArgb(v, v, v));
		}
		return p;
	}

	private static double Luma(double r, double g, double b)
	{
		return r * W_R + g * W_G + b * W_B;
	}

	private static double Luma(Color c)
	{
		return (c.R * W_R + c.G * W_G + c.B * W_B) / 255.0;
	}

	// ============================================================ from the image

	// The colours the picture is made of, in the order a ramp reads: darkest
	// first, so a grey palette comes out the way `gray4` and `gray8` do.
	private static List<Color> ExtractPalette(double[] px, int w, int h, int n, string method)
	{
		List<Color> picked = method == "median"
			? MedianCut(px, w, h, n)
			: Popularity(px, w, h, n);
		picked.Sort((a, b) => Luma(a).CompareTo(Luma(b)));
		return picked;
	}

	// A picture read back at its own size, so `--scale` and `--max` - which
	// shape the dither - do not shape the palette with it. The tone is applied
	// here too: a brightness or an invert has to move the palette and the
	// picture together, or the two disagree about what the picture looks like.
	private static List<Color> PaletteFromImage(Bitmap source, int w, int h)
	{
		Size size = AnalysisSize(w, h);
		using (Bitmap small = Downscale(source, size.Width, size.Height))
		{
			double[] px = ReadPixels(small, size.Width, size.Height);
			ApplyTone(px);
			return ExtractPalette(px, size.Width, size.Height, extract, extractMethod);
		}
	}

	private static Size AnalysisSize(int w, int h)
	{
		double longest = Math.Max(w, h);
		double f = longest > ANALYSIS_SIDE ? ANALYSIS_SIDE / longest : 1.0;
		return new Size(Math.Max(1, (int)Math.Round(w * f)), Math.Max(1, (int)Math.Round(h * f)));
	}

	// The colours a picture uses most. Counting is done in 5 bits a channel:
	// coarse enough that a gradient fills buckets instead of scattering one
	// pixel each, fine enough that two colours a shade apart stay apart.
	//
	// The most used bucket is not always worth a palette entry - a picture
	// that is 80% one colour has a run of near-identical buckets behind it -
	// so a bucket is only kept when it is far enough from the ones already
	// kept. That filter is dropped on a second pass, so a picture with fewer
	// distinct colours than asked for still fills the palette.
	private static List<Color> Popularity(double[] px, int w, int h, int n)
	{
		const int BITS = 5;
		const int SIDE = 1 << BITS;
		const int SHIFT = 8 - BITS;
		int buckets = SIDE * SIDE * SIDE;

		int[] count = new int[buckets];
		double[] sumR = new double[buckets];
		double[] sumG = new double[buckets];
		double[] sumB = new double[buckets];

		for (int i = 0; i < w * h; i++)
		{
			int o = i * 3;
			int r = (int)(px[o] * 255.0) >> SHIFT;
			int g = (int)(px[o + 1] * 255.0) >> SHIFT;
			int b = (int)(px[o + 2] * 255.0) >> SHIFT;
			int k = (r << (BITS * 2)) | (g << BITS) | b;
			count[k]++;
			sumR[k] += px[o];
			sumG[k] += px[o + 1];
			sumB[k] += px[o + 2];
		}

		// Used buckets ranked by count. The rank packs the count above the
		// bucket index, so one Array.Sort orders both and the walk reads the
		// index back out. 20 bits hold any bucket of 32768.
		int[] order = new int[buckets];
		int used = 0;
		for (int k = 0; k < buckets; k++)
		{
			if (count[k] > 0)
			{
				order[used++] = k;
			}
		}
		long[] rank = new long[used];
		for (int i = 0; i < used; i++)
		{
			rank[i] = ((long)count[order[i]] << 20) | (long)(uint)order[i];
		}
		Array.Sort(rank);

		List<Color> picked = new List<Color>();
		bool[] taken = new bool[buckets];
		for (int pass = 0; pass < 2 && picked.Count < n; pass++)
		{
			double floor = pass == 0 ? PALETTE_FLOOR : 0.0;
			for (int i = used - 1; i >= 0 && picked.Count < n; i--)
			{
				int k = (int)(rank[i] & 0xFFFFF);
				if (taken[k])
				{
					continue;
				}
				Color c = Average(sumR[k], sumG[k], sumB[k], count[k]);
				if (floor > 0.0 && TooClose(c, picked, floor))
				{
					continue;
				}
				taken[k] = true;
				picked.Add(c);
			}
		}
		return picked;
	}

	// Median cut: every colour starts in one box, and the box that spans the
	// most colour is split at the middle of its widest channel until there are
	// as many boxes as colours. Each box then contributes its average, which
	// is what spreads the palette over the whole picture instead of over the
	// parts of it that happen to be the most common.
	//
	// A box carries the channel it will split on, measured when it is made, so
	// choosing the box to split is a walk over the boxes rather than over the
	// pixels: at the top of the tree that is one pass over the picture instead
	// of one per colour.
	private static List<Color> MedianCut(double[] px, int w, int h, int n)
	{
		int total = w * h;
		int[] idx = new int[total];
		for (int i = 0; i < total; i++)
		{
			idx[i] = i;
		}

		List<Box> boxes = new List<Box>();
		Box all = new Box(0, total);
		Measure(px, idx, all);
		boxes.Add(all);

		while (boxes.Count < n)
		{
			int best = -1;
			for (int i = 0; i < boxes.Count; i++)
			{
				// A box of one pixel, or of one colour, has nothing to split.
				// The second is what stops a flat picture from filling the
				// palette with copies of its one colour.
				if (boxes[i].Hi - boxes[i].Lo < 2 || boxes[i].Range <= 0.0)
				{
					continue;
				}
				if (best < 0 || boxes[i].Range > boxes[best].Range)
				{
					best = i;
				}
			}
			if (best < 0)
			{
				break;
			}

			Box box = boxes[best];
			Array.Sort(idx, box.Lo, box.Hi - box.Lo, new ByChannel(px, box.Channel));
			int mid = box.Lo + (box.Hi - box.Lo) / 2;

			Box left = new Box(box.Lo, mid);
			Box right = new Box(mid, box.Hi);
			Measure(px, idx, left);
			Measure(px, idx, right);
			boxes[best] = left;
			boxes.Add(right);
		}

		List<Color> p = new List<Color>();
		foreach (Box box in boxes)
		{
			double sr = 0.0;
			double sg = 0.0;
			double sb = 0.0;
			for (int i = box.Lo; i < box.Hi; i++)
			{
				int o = idx[i] * 3;
				sr += px[o];
				sg += px[o + 1];
				sb += px[o + 2];
			}
			p.Add(Average(sr, sg, sb, box.Hi - box.Lo));
		}
		return p;
	}

	private sealed class Box
	{
		public readonly int Lo;
		public readonly int Hi;
		public int Channel;
		public double Range;

		public Box(int lo, int hi)
		{
			Lo = lo;
			Hi = hi;
		}
	}

	// The widest channel of a box, and how wide it is: the one it splits on.
	private static void Measure(double[] px, int[] idx, Box box)
	{
		double minR = 1.0, minG = 1.0, minB = 1.0;
		double maxR = 0.0, maxG = 0.0, maxB = 0.0;
		for (int i = box.Lo; i < box.Hi; i++)
		{
			int o = idx[i] * 3;
			double r = px[o];
			double g = px[o + 1];
			double b = px[o + 2];
			if (r < minR) minR = r;
			if (g < minG) minG = g;
			if (b < minB) minB = b;
			if (r > maxR) maxR = r;
			if (g > maxG) maxG = g;
			if (b > maxB) maxB = b;
		}

		box.Channel = 0;
		double widest = maxR - minR;
		if (maxG - minG > widest)
		{
			widest = maxG - minG;
			box.Channel = 1;
		}
		if (maxB - minB > widest)
		{
			widest = maxB - minB;
			box.Channel = 2;
		}
		box.Range = widest;
	}

	// Sorting a box by one channel is the step median cut repeats, and the
	// comparison needs the pixels, so it is a class rather than a lambda:
	// IComparer<int> is the overload Array.Sort takes a range with.
	private sealed class ByChannel : IComparer<int>
	{
		private readonly double[] px;
		private readonly int channel;

		public ByChannel(double[] px, int channel)
		{
			this.px = px;
			this.channel = channel;
		}

		public int Compare(int a, int b)
		{
			double va = px[a * 3 + channel];
			double vb = px[b * 3 + channel];
			return va < vb ? -1 : va > vb ? 1 : 0;
		}
	}

	private static Color Average(double r, double g, double b, int n)
	{
		return Color.FromArgb(
			(int)Math.Round(r / n * 255.0),
			(int)Math.Round(g / n * 255.0),
			(int)Math.Round(b / n * 255.0));
	}

	private static bool TooClose(Color c, List<Color> picked, double floor)
	{
		foreach (Color p in picked)
		{
			double dr = (c.R - p.R) / 255.0;
			double dg = (c.G - p.G) / 255.0;
			double db = (c.B - p.B) / 255.0;
			if (dr * dr * W_R + dg * dg * W_G + db * db * W_B < floor)
			{
				return true;
			}
		}
		return false;
	}

	private static string HexList(List<Color> p)
	{
		StringBuilder sb = new StringBuilder();
		for (int i = 0; i < p.Count; i++)
		{
			if (i > 0)
			{
				sb.Append(',');
			}
			sb.Append('#')
				.Append(p[i].R.ToString("x2", CultureInfo.InvariantCulture))
				.Append(p[i].G.ToString("x2", CultureInfo.InvariantCulture))
				.Append(p[i].B.ToString("x2", CultureInfo.InvariantCulture));
		}
		return sb.ToString();
	}

	// A two-colour palette is a cut, not a distance, so --threshold can move
	// it. That is only the same question as "which entry is nearer" for the
	// built-in black and white: a custom palette names its own colours in its
	// own order, and red and blue are not a luminance at all, so those go
	// through the distance loop and get quantized by colour. An extracted
	// palette is a distance too, whatever its size: two colours out of a
	// picture are still that picture's two colours, not black and white.
	private static bool IsCut(List<Color> p)
	{
		return p.Count == 2 && paletteName == "bw";
	}

	private static int NearestIndex(double[] px, int o, List<Color> palette)
	{
		return NearestIndex(px[o], px[o + 1], px[o + 2], palette);
	}

	// The weighted distance the quantizer measures with, in one place so the
	// ordered dither's amplitude and the snap cannot disagree about it.
	private static double Dist(double r, double g, double b, Color c)
	{
		double dr = r - c.R / 255.0;
		double dg = g - c.G / 255.0;
		double db = b - c.B / 255.0;
		return dr * dr * W_R + dg * dg * W_G + db * db * W_B;
	}

	// Takes the colour itself rather than a buffer and an offset, because the
	// pixelate pass asks about a colour that is not in any buffer.
	private static int NearestIndex(double r, double g, double b, List<Color> palette)
	{
		// The built-in black and white palette is a cut, not a distance:
		// --threshold moves it. Its entries are black then white, so index 1
		// is the light one.
		if (IsCut(palette))
		{
			return Luma(r, g, b) > cut ? 1 : 0;
		}

		int best = 0;
		double bestD = double.MaxValue;
		for (int i = 0; i < palette.Count; i++)
		{
			double d = Dist(r, g, b, palette[i]);
			if (d < bestD)
			{
				bestD = d;
				best = i;
			}
		}
		return best;
	}

	// The two palette entries a colour sits between, as the vector from the
	// second to the first: the span an ordered dither's matrix cell has to
	// cover. Half of it either way and the cell decides between exactly those
	// two entries.
	//
	// It is a vector, not a length, because the two entries need not differ in
	// luma: #bb8080 and #809e80 weigh the same, and a colour between them is
	// equally near both. Adding the same amount to all three channels moves it
	// along the greys, which is the direction in which those two are tied, so
	// no cell could ever pick between them - the dither would draw a flat
	// field. Moving along the line between the entries breaks the tie wherever
	// it is, and for a grey palette the two forms are the same thing, because
	// the weights add up to one.
	private static void Pair(double r, double g, double b, List<Color> palette,
		out double dr, out double dg, out double db)
	{
		dr = 0.0;
		dg = 0.0;
		db = 0.0;
		if (palette.Count < 2)
		{
			return;
		}

		int first = -1;
		int second = -1;
		double firstD = double.MaxValue;
		double secondD = double.MaxValue;
		for (int i = 0; i < palette.Count; i++)
		{
			double d = Dist(r, g, b, palette[i]);
			if (d < firstD)
			{
				secondD = firstD;
				second = first;
				firstD = d;
				first = i;
			}
			else if (d < secondD)
			{
				secondD = d;
				second = i;
			}
		}

		Color a = palette[first];
		Color c = palette[second];
		dr = (a.R - c.R) / 255.0;
		dg = (a.G - c.G) / 255.0;
		db = (a.B - c.B) / 255.0;
	}

	private static void Nearest(double[] px, int w, int h, List<Color> palette, int[] index)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				int n = y * w + x;
				index[n] = NearestIndex(px, n * 3, palette);
			}
		}
	}

	// A hard two-tone image from the palette's ends, whatever its size. Which
	// end is the light one comes from its luminance, not its position: a
	// custom palette is free to list its colours the other way round.
	//
	// --threshold is the black and white cut, and only that: two colours out of
	// a picture, or two named by hand, are not a light end and a dark end, and
	// a red and a blue are not a luminance at all. Those are quantized by
	// colour, the same as with no dither.
	private static void Threshold(double[] px, int w, int h, List<Color> palette, int[] index)
	{
		if (!IsCut(palette))
		{
			Nearest(px, w, h, palette, index);
			return;
		}

		int last = palette.Count - 1;
		int lightIdx = Luma(palette[last].R / 255.0, palette[last].G / 255.0, palette[last].B / 255.0)
			>= Luma(palette[0].R / 255.0, palette[0].G / 255.0, palette[0].B / 255.0) ? last : 0;
		int darkIdx = lightIdx == last ? 0 : last;

		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				int n = y * w + x;
				int o = n * 3;
				bool light = Luma(px[o], px[o + 1], px[o + 2]) > cut;
				index[n] = light ? lightIdx : darkIdx;
			}
		}
	}

	// ============================================================ error diffusion

	// The diffusion kernels: flat triples of { weight, dx, dy }, the pixel being
	// quantized at (0, 0). These are the published tables, and a kernel whose
	// weights do not add up to 1 (Atkinson's 6/8) is not a mistake: the error it
	// throws away is what gives it its character.
	private static readonly double[] K_FS = {
		7.0 / 16, 1, 0, 3.0 / 16, -1, 1, 5.0 / 16, 0, 1, 1.0 / 16, 1, 1,
	};

	private static readonly double[] K_ATKINSON = {
		1.0 / 8, 1, 0, 1.0 / 8, 2, 0, 1.0 / 8, -1, 1,
		1.0 / 8, 0, 1, 1.0 / 8, 1, 1, 1.0 / 8, 0, 2,
	};

	private static readonly double[] K_JJN = {
		7.0 / 48, 1, 0, 5.0 / 48, 2, 0,
		3.0 / 48, -2, 1, 5.0 / 48, -1, 1, 7.0 / 48, 0, 1, 5.0 / 48, 1, 1, 3.0 / 48, 2, 1,
		1.0 / 48, -2, 2, 3.0 / 48, -1, 2, 5.0 / 48, 0, 2, 3.0 / 48, 1, 2, 1.0 / 48, 2, 2,
	};

	private static readonly double[] K_STUCKI = {
		8.0 / 42, 1, 0, 4.0 / 42, 2, 0,
		2.0 / 42, -2, 1, 4.0 / 42, -1, 1, 8.0 / 42, 0, 1, 4.0 / 42, 1, 1, 2.0 / 42, 2, 1,
		1.0 / 42, -2, 2, 2.0 / 42, -1, 2, 4.0 / 42, 0, 2, 2.0 / 42, 1, 2, 1.0 / 42, 2, 2,
	};

	private static readonly double[] K_BURKES = {
		8.0 / 32, 1, 0, 4.0 / 32, 2, 0,
		2.0 / 32, -2, 1, 4.0 / 32, -1, 1, 8.0 / 32, 0, 1, 4.0 / 32, 1, 1, 2.0 / 32, 2, 1,
	};

	private static readonly double[] K_SIERRA3 = {
		5.0 / 32, 1, 0, 3.0 / 32, 2, 0,
		2.0 / 32, -2, 1, 4.0 / 32, -1, 1, 5.0 / 32, 0, 1, 4.0 / 32, 1, 1, 2.0 / 32, 2, 1,
		2.0 / 32, -1, 2, 3.0 / 32, 0, 2, 2.0 / 32, 1, 2,
	};

	private static readonly double[] K_SIERRA2 = {
		4.0 / 16, 1, 0, 3.0 / 16, 2, 0,
		1.0 / 16, -2, 1, 2.0 / 16, -1, 1, 3.0 / 16, 0, 1, 2.0 / 16, 1, 1, 1.0 / 16, 2, 1,
	};

	private static readonly double[] K_SIERRA24A = {
		2.0 / 4, 1, 0, 1.0 / 4, -1, 1, 1.0 / 4, 0, 1,
	};

	private static readonly double[] K_FAN = {
		7.0 / 16, 1, 0, 1.0 / 16, -2, 1, 3.0 / 16, -1, 1, 5.0 / 16, 0, 1,
	};

	private static readonly double[] K_SHIAUFAN = {
		4.0 / 8, 1, 0, 1.0 / 8, -2, 1, 1.0 / 8, -1, 1, 2.0 / 8, 0, 1,
	};

	private static readonly double[] K_SHIAUFAN2 = {
		8.0 / 16, 1, 0, 1.0 / 16, -3, 1, 1.0 / 16, -2, 1, 2.0 / 16, -1, 1, 4.0 / 16, 0, 1,
	};

	// Two neighbours, half the error each. The shortest kernel there is.
	private static readonly double[] K_SIMPLE2D = {
		1.0 / 2, 1, 0, 1.0 / 2, 0, 1,
	};

	// Dizzy walks the picture in a random order instead of along its rows, so it
	// has no kernel table in the usual sense: the four orthogonal neighbours
	// weigh ten times the four diagonal ones, and 44 is what they add up to.
	private static readonly double[] DIZZY = {
		10, 1, 0, 10, -1, 0, 10, 0, 1, 10, 0, -1,
		1, 1, 1, 1, -1, 1, 1, 1, -1, 1, -1, -1,
	};

	private const double DIZZY_TOTAL = 44.0;

	private static bool IsKernel(string name)
	{
		return Kernel(name) != null || name == "dizzy";
	}

	// The pattern is a matrix side, or the blue-noise mask that stands in for
	// one. Knoll is the only mode that reads it, and it parses the name, so the
	// check has to happen before the dither does.
	private static bool IsPattern(string name)
	{
		return name == "bluenoise" || name == "2" || name == "4" || name == "8" || name == "16";
	}

	// Null means "no table": only Dizzy, which is not a table but a walk order.
	private static double[] Kernel(string name)
	{
		switch (name)
		{
			case "fs": return K_FS;
			case "atkinson": return K_ATKINSON;
			case "jjn": return K_JJN;
			case "stucki": return K_STUCKI;
			case "burkes": return K_BURKES;
			case "sierra3": return K_SIERRA3;
			case "sierra2": return K_SIERRA2;
			case "sierra24a": return K_SIERRA24A;
			case "fan": return K_FAN;
			case "shiaufan": return K_SHIAUFAN;
			case "shiaufan2": return K_SHIAUFAN2;
			case "simple2d": return K_SIMPLE2D;
			default: return null;
		}
	}

	// One error diffusion, whatever the kernel: quantize the pixel, then hand the
	// error it could not represent to the neighbours the kernel names.
	//
	// The value is clamped to the range before the quantizer sees it. An
	// accumulated error can push a pixel past black or white, and the error it
	// then hands on would be measured from a value that no longer means anything.
	private static void Diffuse(double[] px, int w, int h, List<Color> palette, int[] index, double[] k)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				int n = y * w + x;
				int o = n * 3;
				double r = Clamp01(px[o]);
				double g = Clamp01(px[o + 1]);
				double b = Clamp01(px[o + 2]);
				px[o] = r;
				px[o + 1] = g;
				px[o + 2] = b;

				int i = NearestIndex(r, g, b, palette);
				index[n] = i;

				Color c = palette[i];
				double er = r - c.R / 255.0;
				double eg = g - c.G / 255.0;
				double eb = b - c.B / 255.0;

				for (int j = 0; j < k.Length; j += 3)
				{
					Spread(px, w, h, x + (int)k[j + 1], y + (int)k[j + 2], er, eg, eb, k[j]);
				}
			}
		}
	}

	// Dizzy: the pixels are quantized in a random order, and each one hands its
	// error to the eight pixels around it. A pixel that has already been
	// quantized is skipped, so the error only ever lands on a decision that has
	// not been made yet - which is what makes the order matter.
	private static void Dizzy(double[] px, int w, int h, List<Color> palette, int[] index)
	{
		int total = w * h;
		int[] order = new int[total];
		for (int i = 0; i < total; i++)
		{
			order[i] = i;
		}

		// A fixed seed, so the same picture and the same settings give the same
		// render: the settings block tells a re-render from a no-op by the bytes.
		uint seed = 2654435769u;
		for (int i = total - 1; i > 0; i--)
		{
			seed = NextRandom(seed);
			int j = (int)(seed % (uint)(i + 1));
			int swap = order[i];
			order[i] = order[j];
			order[j] = swap;
		}

		bool[] done = new bool[total];
		for (int v = 0; v < total; v++)
		{
			int n = order[v];
			int x = n % w;
			int y = n / w;
			int o = n * 3;
			double r = Clamp01(px[o]);
			double g = Clamp01(px[o + 1]);
			double b = Clamp01(px[o + 2]);

			int i = NearestIndex(r, g, b, palette);
			index[n] = i;
			done[n] = true;

			Color c = palette[i];
			double er = r - c.R / 255.0;
			double eg = g - c.G / 255.0;
			double eb = b - c.B / 255.0;

			for (int j = 0; j < DIZZY.Length; j += 3)
			{
				int nx = x + (int)DIZZY[j + 1];
				int ny = y + (int)DIZZY[j + 2];
				if (nx < 0 || nx >= w || ny < 0 || ny >= h)
				{
					continue;
				}
				int m = ny * w + nx;
				if (done[m])
				{
					continue;
				}
				double f = DIZZY[j] / DIZZY_TOTAL;
				int p = m * 3;
				px[p] += er * f;
				px[p + 1] += eg * f;
				px[p + 2] += eb * f;
			}
		}
	}

	// A small deterministic generator, seeded once per render: the shuffle has
	// to be the same every time the same picture is dithered.
	private static uint NextRandom(uint state)
	{
		uint t = state + 1831565813u;
		t = (t ^ (t >> 15)) * (t | 1u);
		t = t + ((t ^ (t >> 7)) * (t | 61u)) ^ t;
		return t ^ (t >> 14);
	}

	private static void Spread(double[] px, int w, int h, int x, int y, double er, double eg, double eb, double f)
	{
		if (x < 0 || x >= w || y < 0 || y >= h)
		{
			return;
		}
		int o = (y * w + x) * 3;
		px[o] += er * f;
		px[o + 1] += eg * f;
		px[o + 2] += eb * f;
	}

	// ============================================================ ordered dither

	// Bayer matrices are built rather than typed out. A 2x2 of 0..3 is the
	// seed, and each doubling turns every cell into four: itself, itself plus
	// two, itself plus three, itself plus one. That is the standard recursive
	// construction, and the crosshatch everyone recognises comes from it.
	private static readonly Dictionary<int, int[]> bayerCache = new Dictionary<int, int[]>();

	private static int[] Bayer(int n)
	{
		int[] cached;
		if (bayerCache.TryGetValue(n, out cached))
		{
			return cached;
		}

		int[] ranks = BayerRanks(n);
		int cells = n * n;
		int[] m = new int[cells];
		for (int i = 0; i < cells; i++)
		{
			// 0..cells-1 to 0..255, read at the middle of each rank's band so
			// that neither end is half a band wide.
			m[i] = Math.Min(255, (int)((ranks[i] + 0.5) / cells * 256.0));
		}
		bayerCache[n] = m;
		return m;
	}

	private static int[] BayerRanks(int n)
	{
		if (n == 2)
		{
			return new int[] { 0, 2, 3, 1 };
		}

		int t = n / 2;
		int[] half = BayerRanks(t);
		int[] m = new int[n * n];
		for (int y = 0; y < t; y++)
		{
			for (int x = 0; x < t; x++)
			{
				int i = half[y * t + x] * 4;
				m[y * n + x] = i;
				m[y * n + t + x] = i + 2;
				m[(t + y) * n + x] = i + 3;
				m[(t + y) * n + t + x] = i + 1;
			}
		}
		return m;
	}

	// Ordered dithering: bias each pixel by its matrix cell before the snap, so
	// the cell - not the error of the pixels before it - decides which palette
	// entry a pixel takes. The bias moves the pixel along the line between the
	// two entries it sits between, by up to half the distance to each: at the
	// top of the matrix the pixel lands on the lighter entry and at the bottom
	// on the darker one, so the matrix is a threshold between the two, tiled
	// over the picture.
	private static void Ordered(double[] px, int w, int h, List<Color> palette, int[] index, int[] m, int n)
	{
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				int o = (y * w + x) * 3;
				double r = px[o];
				double g = px[o + 1];
				double b = px[o + 2];

				double dr;
				double dg;
				double db;
				Pair(r, g, b, palette, out dr, out dg, out db);

				double bias = 0.5 - m[(y % n) * n + (x % n)] / 255.0;
				r = Clamp01(r + bias * dr);
				g = Clamp01(g + bias * dg);
				b = Clamp01(b + bias * db);
				px[o] = r;
				px[o + 1] = g;
				px[o + 2] = b;
				index[y * w + x] = NearestIndex(r, g, b, palette);
			}
		}
	}

	// ============================================================ blue noise

	// The blue-noise mask is made, not stored: a 64x64 grid of ranks built by
	// void and cluster, which is the usual way to get a threshold matrix whose
	// energy sits in the high frequencies - a dither with no pattern for the
	// eye to latch onto, where a Bayer matrix is all pattern. The filter wraps
	// at the edges, so the mask tiles.
	private const int BLUE_SIDE = 64;
	private const double BLUE_SIGMA = 1.5;
	private const int BLUE_RADIUS = 5;

	private static int[] blueNoise;

	private static int[] BlueNoise()
	{
		if (blueNoise != null)
		{
			return blueNoise;
		}

		int side = BLUE_SIDE;
		int cells = side * side;

		// The filter, as offsets from the cell it weighs, wrapped.
		int span = BLUE_RADIUS * 2 + 1;
		int[] fx = new int[span * span];
		int[] fy = new int[span * span];
		double[] fw = new double[span * span];
		int n = 0;
		for (int dy = -BLUE_RADIUS; dy <= BLUE_RADIUS; dy++)
		{
			for (int dx = -BLUE_RADIUS; dx <= BLUE_RADIUS; dx++)
			{
				fx[n] = dx;
				fy[n] = dy;
				fw[n] = Math.Exp(-(dx * dx + dy * dy) / (2.0 * BLUE_SIGMA * BLUE_SIGMA));
				n++;
			}
		}

		bool[] on = new bool[cells];
		double[] energy = new double[cells];
		int[] rank = new int[cells];

		// Half the cells on, at random: the density the relaxation starts from.
		// Any density relaxes to the same arrangement; this one is what the
		// algorithm is defined on.
		uint seed = 2654435769u;
		int placed = 0;
		while (placed < cells / 2)
		{
			seed = NextRandom(seed);
			int i = (int)(seed % (uint)cells);
			if (!on[i])
			{
				on[i] = true;
				placed++;
			}
		}
		BlueEnergy(energy, side, on, fx, fy, fw);

		// Relax: take the tightest cluster out, and if the largest void in what
		// is left is not the cell just emptied, fill that void with it. Repeat
		// until the cell taken out is the one put back, which is the point at
		// which there is no move left that lowers the energy.
		//
		// The order matters: comparing the cluster and the void in the pattern
		// that still holds the cluster compares two different patterns, and a
		// cell whose own weight keeps it the tightest cluster can then be taken
		// out and put straight back, for ever.
		while (true)
		{
			int tight = -1;
			double tightE = double.MinValue;
			for (int i = 0; i < cells; i++)
			{
				if (on[i] && energy[i] > tightE)
				{
					tightE = energy[i];
					tight = i;
				}
			}

			on[tight] = false;
			BlueAdd(energy, side, tight, fx, fy, fw, -1.0);

			int gap = -1;
			double gapE = double.MaxValue;
			for (int i = 0; i < cells; i++)
			{
				if (!on[i] && energy[i] < gapE)
				{
					gapE = energy[i];
					gap = i;
				}
			}

			if (gap == tight)
			{
				on[tight] = true;
				BlueAdd(energy, side, tight, fx, fy, fw, 1.0);
				break;
			}

			on[gap] = true;
			BlueAdd(energy, side, gap, fx, fy, fw, 1.0);
		}

		// The prototype: the relaxed half-density pattern every rank is
		// measured against. Both phases below start from it, so it is kept
		// rather than rebuilt.
		bool[] prototype = (bool[])on.Clone();

		// The ones are ranked by how tightly they are clustered, tightest
		// first: rank 0 is the cell left standing, the one with the most room
		// around it.
		for (int r = cells / 2 - 1; r >= 0; r--)
		{
			int tight = -1;
			double tightE = double.MinValue;
			for (int i = 0; i < cells; i++)
			{
				if (on[i] && energy[i] > tightE)
				{
					tightE = energy[i];
					tight = i;
				}
			}
			on[tight] = false;
			BlueAdd(energy, side, tight, fx, fy, fw, -1.0);
			rank[tight] = r;
		}

		// And the zeros by how large a void they are, largest first, so the
		// ranks continue where the ones left off. This starts from the
		// prototype again: the loop above emptied the pattern, and inserting
		// into an empty one would rank the cells in scan order.
		Array.Copy(prototype, on, cells);
		BlueEnergy(energy, side, on, fx, fy, fw);

		for (int r = cells / 2; r < cells; r++)
		{
			int gap = -1;
			double gapE = double.MaxValue;
			for (int i = 0; i < cells; i++)
			{
				if (!on[i] && energy[i] < gapE)
				{
					gapE = energy[i];
					gap = i;
				}
			}
			on[gap] = true;
			BlueAdd(energy, side, gap, fx, fy, fw, 1.0);
			rank[gap] = r;
		}

		int[] mask = new int[cells];
		for (int i = 0; i < cells; i++)
		{
			mask[i] = Math.Min(255, (int)((rank[i] + 0.5) / cells * 256.0));
		}
		blueNoise = mask;
		return mask;
	}

	private static void BlueEnergy(double[] energy, int side, bool[] on, int[] fx, int[] fy, double[] fw)
	{
		for (int i = 0; i < energy.Length; i++)
		{
			energy[i] = 0.0;
		}
		for (int i = 0; i < on.Length; i++)
		{
			if (on[i])
			{
				BlueAdd(energy, side, i, fx, fy, fw, 1.0);
			}
		}
	}

	private static void BlueAdd(double[] energy, int side, int cell, int[] fx, int[] fy, double[] fw, double sign)
	{
		int x = cell % side;
		int y = cell / side;
		for (int k = 0; k < fx.Length; k++)
		{
			int nx = ((x + fx[k]) % side + side) % side;
			int ny = ((y + fy[k]) % side + side) % side;
			energy[ny * side + nx] += sign * fw[k];
		}
	}

	// ============================================================ knoll

	// Knoll's ordered dither: the matrix does not choose between the two
	// nearest palette entries, it chooses between all of them. For every colour
	// in the picture the palette is walked the way an error diffusion would take
	// it - nearest entry, hand the error on, nearest again - and the entries are
	// counted. The counts say how many of the matrix's ranks each entry is
	// worth, and the ranks are laid out darkest first, so a matrix value from 0
	// to 255 reads as "this far up this colour's own mix".
	//
	// That is what makes it work with a palette read off a picture: a Bayer
	// matrix on its own can only choose between neighbouring entries, so a
	// six-colour palette comes out as six bands, while Knoll keeps the mix of
	// the colour it is standing in for.
	private const int KNOLL_RANKS = 64;

	private static readonly double[] linearRamp = BuildLinearRamp();

	private static double[] BuildLinearRamp()
	{
		double[] t = new double[256];
		for (int i = 0; i < 256; i++)
		{
			double v = i / 255.0;
			t[i] = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
		}
		return t;
	}

	private static void Knoll(double[] px, int w, int h, List<Color> palette, int[] index)
	{
		bool noise = pattern == "bluenoise";
		int n = noise ? BLUE_SIDE : int.Parse(pattern, CultureInfo.InvariantCulture);
		int[] m = noise ? BlueNoise() : Bayer(n);

		int count = palette.Count;
		// The walk happens in linear light, where averaging colours is a
		// physical thing to do, and the ranks are laid out by that luminance:
		// the entry that looks brighter gets the later ranks.
		double[] lin = new double[count * 3];
		for (int i = 0; i < count; i++)
		{
			lin[i * 3] = linearRamp[palette[i].R];
			lin[i * 3 + 1] = linearRamp[palette[i].G];
			lin[i * 3 + 2] = linearRamp[palette[i].B];
		}

		int[] order = new int[count];
		for (int i = 0; i < count; i++)
		{
			order[i] = i;
		}
		Array.Sort(order, delegate(int a, int b)
		{
			return Luma(lin[a * 3], lin[a * 3 + 1], lin[a * 3 + 2])
				.CompareTo(Luma(lin[b * 3], lin[b * 3 + 1], lin[b * 3 + 2]));
		});

		int ranks = Math.Min(noise ? 256 : n * n, KNOLL_RANKS);
		int[] counts = new int[count];
		Dictionary<int, byte[]> memo = new Dictionary<int, byte[]>();

		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				int o = (y * w + x) * 3;
				// The picture is quantized to six bits a channel for the walk.
				// The counts only have to be right to a couple of percent, and
				// this is what keeps one table per colour instead of per pixel.
				int qr = ((int)(px[o] * 255.0) & 252) | 2;
				int qg = ((int)(px[o + 1] * 255.0) & 252) | 2;
				int qb = ((int)(px[o + 2] * 255.0) & 252) | 2;
				int key = (qr << 16) | (qg << 8) | qb;

				byte[] table;
				if (!memo.TryGetValue(key, out table))
				{
					table = KnollTable(lin, order, counts, ranks, qr, qg, qb);
					memo[key] = table;
				}

				index[y * w + x] = table[m[(y % n) * n + (x % n)] * ranks >> 8];
			}
		}
	}

	// The walk: `ranks` steps of "the entry nearest to what is left to
	// represent", counting how often each entry wins. Laid out darkest first,
	// the table is a ramp through the entries this colour actually needs.
	private static byte[] KnollTable(double[] lin, int[] order, int[] counts, int ranks, int qr, int qg, int qb)
	{
		int count = order.Length;
		for (int i = 0; i < count; i++)
		{
			counts[i] = 0;
		}

		double r = linearRamp[qr];
		double g = linearRamp[qg];
		double b = linearRamp[qb];
		double er = 0.0;
		double eg = 0.0;
		double eb = 0.0;

		for (int step = 0; step < ranks; step++)
		{
			double tr = r + er;
			double tg = g + eg;
			double tb = b + eb;

			int best = 0;
			double bestD = double.MaxValue;
			for (int i = 0; i < count; i++)
			{
				double dr = tr - lin[i * 3];
				double dg = tg - lin[i * 3 + 1];
				double db = tb - lin[i * 3 + 2];
				double d = dr * dr + dg * dg + db * db;
				if (d < bestD)
				{
					bestD = d;
					best = i;
				}
			}

			counts[best]++;
			// The error carried to the next step is the source against what it
			// became. Not the biased value against it: the bias is that same
			// error, and adding it twice makes the walk diverge.
			er += r - lin[best * 3];
			eg += g - lin[best * 3 + 1];
			eb += b - lin[best * 3 + 2];
		}

		byte[] table = new byte[ranks];
		int at = 0;
		foreach (int i in order)
		{
			for (int k = counts[i]; k > 0; k--)
			{
				table[at++] = (byte)i;
			}
		}
		return table;
	}

	// ============================================================ riemersma

	// Riemersma's dither: the picture is walked along a Hilbert curve, and each
	// pixel is biased by the quantization errors of the last few pixels on that
	// curve, each weighed less than the one after it. The curve is what makes it
	// work - consecutive points on it are neighbours in the picture, so the
	// error stays where it belongs instead of streaking along a row the way an
	// error diffusion's does.
	private const int RIEM_Q = 16;
	private const double RIEM_RATIO = 16.0;

	// Weights decay from 1 at the newest entry to 1/16 at the oldest, which is
	// the curve and the ratio Riemersma's article gives. Index 0 is the pixel
	// just quantized.
	private static readonly double[] riemWeights = BuildRiemersmaWeights();

	private static double[] BuildRiemersmaWeights()
	{
		double[] w = new double[RIEM_Q];
		double b = Math.Exp(Math.Log(RIEM_RATIO) / (RIEM_Q - 1));
		for (int i = 0; i < RIEM_Q; i++)
		{
			w[i] = Math.Pow(b, RIEM_Q - 1 - i) / RIEM_RATIO;
		}
		return w;
	}

	private static void Riemersma(double[] px, int w, int h, List<Color> palette, int[] index)
	{
		int side = 1;
		while (side < Math.Max(w, h))
		{
			side <<= 1;
		}

		double[] queue = new double[RIEM_Q * 3];
		int head = 0;

		for (int d = 0; d < side * side; d++)
		{
			int x;
			int y;
			Hilbert(side, d, out x, out y);
			if (x >= w || y >= h)
			{
				continue;
			}

			int o = (y * w + x) * 3;
			double r = px[o];
			double g = px[o + 1];
			double b = px[o + 2];

			// Read newest first: weights[0] is the pixel just quantized,
			// weights[15] the one sixteen steps back along the curve.
			double sr = 0.0;
			double sg = 0.0;
			double sb = 0.0;
			for (int i = 0; i < RIEM_Q; i++)
			{
				int slot = ((head - 1 - i) % RIEM_Q + RIEM_Q) % RIEM_Q;
				sr += riemWeights[i] * queue[slot * 3];
				sg += riemWeights[i] * queue[slot * 3 + 1];
				sb += riemWeights[i] * queue[slot * 3 + 2];
			}

			double vr = Clamp01(r + sr);
			double vg = Clamp01(g + sg);
			double vb = Clamp01(b + sb);
			int pi = NearestIndex(vr, vg, vb, palette);
			index[y * w + x] = pi;

			// The error is the source pixel against what it became, not the
			// biased value against it: the bias is a sum of errors that have
			// already been counted once.
			Color c = palette[pi];
			queue[head * 3] = r - c.R / 255.0;
			queue[head * 3 + 1] = g - c.G / 255.0;
			queue[head * 3 + 2] = b - c.B / 255.0;
			head = (head + 1) % RIEM_Q;
		}
	}

	// The d-th point of a Hilbert curve over a side x side grid, side a power of
	// two: the usual d2xy, with the quarter turn folded in.
	private static void Hilbert(int side, int d, out int x, out int y)
	{
		x = 0;
		y = 0;
		int t = d;
		for (int s = 1; s < side; s *= 2)
		{
			int rx = 1 & (t / 2);
			int ry = 1 & (t ^ rx);
			if (ry == 0)
			{
				if (rx == 1)
				{
					x = s - 1 - x;
					y = s - 1 - y;
				}
				int swap = x;
				x = y;
				y = swap;
			}
			x += s * rx;
			y += s * ry;
			t /= 4;
		}
	}

	// ================================================================= ascii

	// The 8x8 bitmap font the characters are drawn from, one glyph to a line:
	// eight rows of eight pixels, the top row first, bit 7 the leftmost. It
	// covers 0x20..0x7E, so every character a ramp can name is one the font
	// has.
	//
	// Public domain: the 8x8 monochrome bitmap fonts for rendering by Daniel
	// Hepper, which are Marcel Sondaar's public-domain port of the IBM VGA
	// fonts. Embedded rather than drawn from an installed face: a wallpaper
	// that came out different on another machine would not be the same
	// wallpaper, and at this size a hinted system face is not the crisp
	// bitmap this wants anyway.
	private static readonly byte[] FONT8X8 = {
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // space
		0x18, 0x3C, 0x3C, 0x18, 0x18, 0x00, 0x18, 0x00, // !
		0x36, 0x36, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // "
		0x36, 0x36, 0x7F, 0x36, 0x7F, 0x36, 0x36, 0x00, // #
		0x0C, 0x3E, 0x03, 0x1E, 0x30, 0x1F, 0x0C, 0x00, // $
		0x00, 0x63, 0x33, 0x18, 0x0C, 0x66, 0x63, 0x00, // %
		0x1C, 0x36, 0x1C, 0x6E, 0x3B, 0x33, 0x6E, 0x00, // &
		0x06, 0x06, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, // apostrophe
		0x18, 0x0C, 0x06, 0x06, 0x06, 0x0C, 0x18, 0x00, // (
		0x06, 0x0C, 0x18, 0x18, 0x18, 0x0C, 0x06, 0x00, // )
		0x00, 0x66, 0x3C, 0xFF, 0x3C, 0x66, 0x00, 0x00, // *
		0x00, 0x0C, 0x0C, 0x3F, 0x0C, 0x0C, 0x00, 0x00, // +
		0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C, 0x06, // ,
		0x00, 0x00, 0x00, 0x3F, 0x00, 0x00, 0x00, 0x00, // -
		0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C, 0x00, // .
		0x60, 0x30, 0x18, 0x0C, 0x06, 0x03, 0x01, 0x00, // /
		0x3E, 0x63, 0x73, 0x7B, 0x6F, 0x67, 0x3E, 0x00, // 0
		0x0C, 0x0E, 0x0C, 0x0C, 0x0C, 0x0C, 0x3F, 0x00, // 1
		0x1E, 0x33, 0x30, 0x1C, 0x06, 0x33, 0x3F, 0x00, // 2
		0x1E, 0x33, 0x30, 0x1C, 0x30, 0x33, 0x1E, 0x00, // 3
		0x38, 0x3C, 0x36, 0x33, 0x7F, 0x30, 0x78, 0x00, // 4
		0x3F, 0x03, 0x1F, 0x30, 0x30, 0x33, 0x1E, 0x00, // 5
		0x1C, 0x06, 0x03, 0x1F, 0x33, 0x33, 0x1E, 0x00, // 6
		0x3F, 0x33, 0x30, 0x18, 0x0C, 0x0C, 0x0C, 0x00, // 7
		0x1E, 0x33, 0x33, 0x1E, 0x33, 0x33, 0x1E, 0x00, // 8
		0x1E, 0x33, 0x33, 0x3E, 0x30, 0x18, 0x0E, 0x00, // 9
		0x00, 0x0C, 0x0C, 0x00, 0x00, 0x0C, 0x0C, 0x00, // :
		0x00, 0x0C, 0x0C, 0x00, 0x00, 0x0C, 0x0C, 0x06, // ;
		0x18, 0x0C, 0x06, 0x03, 0x06, 0x0C, 0x18, 0x00, // <
		0x00, 0x00, 0x3F, 0x00, 0x00, 0x3F, 0x00, 0x00, // =
		0x06, 0x0C, 0x18, 0x30, 0x18, 0x0C, 0x06, 0x00, // >
		0x1E, 0x33, 0x30, 0x18, 0x0C, 0x00, 0x0C, 0x00, // ?
		0x3E, 0x63, 0x7B, 0x7B, 0x7B, 0x03, 0x1E, 0x00, // @
		0x0C, 0x1E, 0x33, 0x33, 0x3F, 0x33, 0x33, 0x00, // A
		0x3F, 0x66, 0x66, 0x3E, 0x66, 0x66, 0x3F, 0x00, // B
		0x3C, 0x66, 0x03, 0x03, 0x03, 0x66, 0x3C, 0x00, // C
		0x1F, 0x36, 0x66, 0x66, 0x66, 0x36, 0x1F, 0x00, // D
		0x7F, 0x46, 0x16, 0x1E, 0x16, 0x46, 0x7F, 0x00, // E
		0x7F, 0x46, 0x16, 0x1E, 0x16, 0x06, 0x0F, 0x00, // F
		0x3C, 0x66, 0x03, 0x03, 0x73, 0x66, 0x7C, 0x00, // G
		0x33, 0x33, 0x33, 0x3F, 0x33, 0x33, 0x33, 0x00, // H
		0x1E, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x1E, 0x00, // I
		0x78, 0x30, 0x30, 0x30, 0x33, 0x33, 0x1E, 0x00, // J
		0x67, 0x66, 0x36, 0x1E, 0x36, 0x66, 0x67, 0x00, // K
		0x0F, 0x06, 0x06, 0x06, 0x46, 0x66, 0x7F, 0x00, // L
		0x63, 0x77, 0x7F, 0x7F, 0x6B, 0x63, 0x63, 0x00, // M
		0x63, 0x67, 0x6F, 0x7B, 0x73, 0x63, 0x63, 0x00, // N
		0x1C, 0x36, 0x63, 0x63, 0x63, 0x36, 0x1C, 0x00, // O
		0x3F, 0x66, 0x66, 0x3E, 0x06, 0x06, 0x0F, 0x00, // P
		0x1E, 0x33, 0x33, 0x33, 0x3B, 0x1E, 0x38, 0x00, // Q
		0x3F, 0x66, 0x66, 0x3E, 0x36, 0x66, 0x67, 0x00, // R
		0x1E, 0x33, 0x07, 0x0E, 0x38, 0x33, 0x1E, 0x00, // S
		0x3F, 0x2D, 0x0C, 0x0C, 0x0C, 0x0C, 0x1E, 0x00, // T
		0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x3F, 0x00, // U
		0x33, 0x33, 0x33, 0x33, 0x33, 0x1E, 0x0C, 0x00, // V
		0x63, 0x63, 0x63, 0x6B, 0x7F, 0x77, 0x63, 0x00, // W
		0x63, 0x63, 0x36, 0x1C, 0x1C, 0x36, 0x63, 0x00, // X
		0x33, 0x33, 0x33, 0x1E, 0x0C, 0x0C, 0x1E, 0x00, // Y
		0x7F, 0x63, 0x31, 0x18, 0x4C, 0x66, 0x7F, 0x00, // Z
		0x1E, 0x06, 0x06, 0x06, 0x06, 0x06, 0x1E, 0x00, // [
		0x03, 0x06, 0x0C, 0x18, 0x30, 0x60, 0x40, 0x00, // backslash
		0x1E, 0x18, 0x18, 0x18, 0x18, 0x18, 0x1E, 0x00, // ]
		0x08, 0x1C, 0x36, 0x63, 0x00, 0x00, 0x00, 0x00, // ^
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, // _
		0x0C, 0x0C, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, // `
		0x00, 0x00, 0x1E, 0x30, 0x3E, 0x33, 0x6E, 0x00, // a
		0x07, 0x06, 0x06, 0x3E, 0x66, 0x66, 0x3B, 0x00, // b
		0x00, 0x00, 0x1E, 0x33, 0x03, 0x33, 0x1E, 0x00, // c
		0x38, 0x30, 0x30, 0x3E, 0x33, 0x33, 0x6E, 0x00, // d
		0x00, 0x00, 0x1E, 0x33, 0x3F, 0x03, 0x1E, 0x00, // e
		0x1C, 0x36, 0x06, 0x0F, 0x06, 0x06, 0x0F, 0x00, // f
		0x00, 0x00, 0x6E, 0x33, 0x33, 0x3E, 0x30, 0x1F, // g
		0x07, 0x06, 0x36, 0x6E, 0x66, 0x66, 0x67, 0x00, // h
		0x0C, 0x00, 0x0E, 0x0C, 0x0C, 0x0C, 0x1E, 0x00, // i
		0x30, 0x00, 0x30, 0x30, 0x30, 0x33, 0x33, 0x1E, // j
		0x07, 0x06, 0x66, 0x36, 0x1E, 0x36, 0x67, 0x00, // k
		0x0E, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x1E, 0x00, // l
		0x00, 0x00, 0x33, 0x7F, 0x7F, 0x6B, 0x63, 0x00, // m
		0x00, 0x00, 0x1F, 0x33, 0x33, 0x33, 0x33, 0x00, // n
		0x00, 0x00, 0x1E, 0x33, 0x33, 0x33, 0x1E, 0x00, // o
		0x00, 0x00, 0x3B, 0x66, 0x66, 0x3E, 0x06, 0x0F, // p
		0x00, 0x00, 0x6E, 0x33, 0x33, 0x3E, 0x30, 0x78, // q
		0x00, 0x00, 0x3B, 0x6E, 0x66, 0x06, 0x0F, 0x00, // r
		0x00, 0x00, 0x3E, 0x03, 0x1E, 0x30, 0x1F, 0x00, // s
		0x08, 0x0C, 0x3E, 0x0C, 0x0C, 0x2C, 0x18, 0x00, // t
		0x00, 0x00, 0x33, 0x33, 0x33, 0x33, 0x6E, 0x00, // u
		0x00, 0x00, 0x33, 0x33, 0x33, 0x1E, 0x0C, 0x00, // v
		0x00, 0x00, 0x63, 0x6B, 0x7F, 0x7F, 0x36, 0x00, // w
		0x00, 0x00, 0x63, 0x36, 0x1C, 0x36, 0x63, 0x00, // x
		0x00, 0x00, 0x33, 0x33, 0x33, 0x3E, 0x30, 0x1F, // y
		0x00, 0x00, 0x3F, 0x19, 0x0C, 0x26, 0x3F, 0x00, // z
		0x38, 0x0C, 0x0C, 0x07, 0x0C, 0x0C, 0x38, 0x00, // {
		0x18, 0x18, 0x18, 0x00, 0x18, 0x18, 0x18, 0x00, // |
		0x07, 0x0C, 0x0C, 0x38, 0x0C, 0x0C, 0x07, 0x00, // }
		0x6E, 0x3B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // ~
	};

	// The ramp in hand, as characters, already sorted by ink.
	private static int[] ramp;

	// The picture as characters: each cell of the output is one glyph, and
	// which glyph is decided by how much ink it puts down against the cell's
	// own tone. The cell is the font's 8x8 square scaled by `glyph`, so the
	// output is a whole number of characters across and down.
	private static void Ascii(double[] px, int w, int h, List<Color> palette, int[] index)
	{
		int cell = 8 * glyph;
		int cols = w / cell;
		int rows = h / cell;

		// Everything a glyph does not ink is the paper, which is also what
		// fills the strip the grid does not reach when the output is not a
		// whole number of cells across.
		for (int i = 0; i < index.Length; i++)
		{
			index[i] = 0;
		}

		for (int cy = 0; cy < rows; cy++)
		{
			for (int cx = 0; cx < cols; cx++)
			{
				// One character stands for the average of its own cell. The
				// glyph is the tone, and a tone read from anywhere but the
				// whole cell would make the characters depend on where the
				// picture happened to fall against the grid.
				double sum = 0.0;
				for (int y = cy * cell; y < (cy + 1) * cell; y++)
				{
					int o = (y * w + cx * cell) * 3;
					for (int x = 0; x < cell; x++)
					{
						sum += Luma(px[o], px[o + 1], px[o + 2]);
						o += 3;
					}
				}

				int k = (int)Math.Round(sum / (cell * cell) * (ramp.Length - 1));
				if (k < 0)
				{
					k = 0;
				}
				if (k > ramp.Length - 1)
				{
					k = ramp.Length - 1;
				}
				Glyph(ramp[k], index, w, cx * cell, cy * cell, cell);
			}
		}
	}

	// One character, at the cell's corner, at whatever integer scale the cell
	// is the font's 8x8 times.
	private static void Glyph(int ch, int[] index, int w, int ox, int oy, int cell)
	{
		int s = cell / 8;
		int g = (ch - 0x20) * 8;

		for (int y = 0; y < 8; y++)
		{
			int bits = FONT8X8[g + y];
			if (bits == 0)
			{
				continue;
			}
			for (int x = 0; x < 8; x++)
			{
				if ((bits & (0x80 >> x)) == 0)
				{
					continue;
				}
				// One pixel of the glyph is an s x s square of the output.
				for (int sy = 0; sy < s; sy++)
				{
					int o = (oy + y * s + sy) * w + ox + x * s;
					for (int sx = 0; sx < s; sx++)
					{
						index[o + sx] = 1;
					}
				}
			}
		}
	}

	// How much of a glyph's 64 pixels is ink, as a fraction: the tone the
	// character stands for.
	private static double Ink(int ch)
	{
		int g = (ch - 0x20) * 8;
		int n = 0;
		for (int i = 0; i < 8; i++)
		{
			int bits = FONT8X8[g + i];
			while (bits != 0)
			{
				n += bits & 1;
				bits >>= 1;
			}
		}
		return n / 64.0;
	}

	// The ramp's characters, sorted by ink. Sorting is what keeps the order a
	// ramp is written in from being a setting: in this font "-" is lighter
	// than ":", and "%" lighter than "*", so the classic ramp typed the way
	// everyone types it is not monotonic, and a ramp used as typed would run
	// backwards over part of its range.
	private static int[] Ramp(string chars)
	{
		int[] order = new int[chars.Length];
		double[] ink = new double[chars.Length];
		for (int i = 0; i < chars.Length; i++)
		{
			order[i] = chars[i];
			ink[i] = Ink(chars[i]);
		}

		// Insertion sort: a ramp is tens of characters, and this is the one
		// sort here that is stable, which two characters of equal ink need.
		for (int i = 1; i < order.Length; i++)
		{
			int ch = order[i];
			double v = ink[i];
			int j = i - 1;
			while (j >= 0 && ink[j] > v)
			{
				order[j + 1] = order[j];
				ink[j + 1] = ink[j];
				j--;
			}
			order[j + 1] = ch;
			ink[j + 1] = v;
		}
		return order;
	}

	// ASCII draws in two colours whatever palette is in hand: the glyphs in
	// the lightest entry and the paper in the darkest. Chosen by luminance
	// rather than by position, so a custom list typed light first still comes
	// out with the glyphs on the dark one.
	private static List<Color> AsciiPair(List<Color> p)
	{
		Color dark = p[0];
		Color light = p[0];
		foreach (Color c in p)
		{
			if (Luma(c) < Luma(dark))
			{
				dark = c;
			}
			if (Luma(c) > Luma(light))
			{
				light = c;
			}
		}

		List<Color> pair = new List<Color>();
		pair.Add(dark);
		pair.Add(light);
		return pair;
	}

	// The cell the characters are drawn in: the largest whole number of them
	// that fits the cell the rest of the engine would have used. Floored, so
	// the grid never grows past the size that was already fitted.
	private static Size FitAscii(Size cell)
	{
		int g = 8 * glyph;
		return new Size(
			Math.Max(1, cell.Width / g) * g,
			Math.Max(1, cell.Height / g) * g);
	}

	// ============================================================ pixelate

	// This runs over the dithered result rather than the picture, so it blocks
	// up the dither pattern itself: the dots grow while the picture keeps the
	// detail the dither was computed at. `--scale` is the other way round - it
	// takes the detail away before the dither - and the two read differently.
	private static int[] Pixelate(int[] index, int w, int h, List<Color> palette, out int ow, out int oh)
	{
		ow = Math.Max(1, (w + pixelate - 1) / pixelate);
		oh = Math.Max(1, (h + pixelate - 1) / pixelate);
		int[] small = new int[ow * oh];

		for (int y = 0; y < oh; y++)
		{
			for (int x = 0; x < ow; x++)
			{
				if (!smooth)
				{
					// The middle of the block, which is the pixel a nearest
					// neighbour reduction lands on.
					int sx = Math.Min(w - 1, x * pixelate + pixelate / 2);
					int sy = Math.Min(h - 1, y * pixelate + pixelate / 2);
					small[y * ow + x] = index[sy * w + sx];
					continue;
				}

				double sr = 0.0;
				double sg = 0.0;
				double sb = 0.0;
				int n = 0;
				int yEnd = Math.Min(h, (y + 1) * pixelate);
				int xEnd = Math.Min(w, (x + 1) * pixelate);
				for (int by = y * pixelate; by < yEnd; by++)
				{
					for (int bx = x * pixelate; bx < xEnd; bx++)
					{
						Color c = palette[index[by * w + bx]];
						sr += c.R;
						sg += c.G;
						sb += c.B;
						n++;
					}
				}
				small[y * ow + x] = NearestIndex(
					sr / (n * 255.0), sg / (n * 255.0), sb / (n * 255.0), palette);
			}
		}
		return small;
	}

	private static double Clamp01(double v)
	{
		return v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
	}

	private static bool IsGray(List<Color> p)
	{
		foreach (Color c in p)
		{
			if (c.R != c.G || c.G != c.B)
			{
				return false;
			}
		}
		return true;
	}

	private static void WritePng(int[] index, int w, int h, List<Color> palette, string path)
	{
		if (palette.Count == 2 && IsGray(palette))
		{
			WriteIndexed1(index, w, h, palette, path);
		}
		else if (palette.Count <= 256)
		{
			WriteIndexed8(index, w, h, palette, path);
		}
		else
		{
			WriteRgb(index, w, h, palette, path);
		}
	}

	private static void WriteIndexed1(int[] index, int w, int h, List<Color> palette, string path)
	{
		using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format1bppIndexed))
		{
			ColorPalette pal = bmp.Palette;
			pal.Entries[0] = palette[0];
			pal.Entries[1] = palette[1];
			bmp.Palette = pal;

			BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
			try
			{
				int stride = data.Stride;
				byte[] buf = new byte[stride * h];
				for (int y = 0; y < h; y++)
				{
					for (int x = 0; x < w; x++)
					{
						if (index[y * w + x] != 0)
						{
							buf[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
						}
					}
				}
				Marshal.Copy(buf, 0, data.Scan0, buf.Length);
			}
			finally
			{
				bmp.UnlockBits(data);
			}

			bmp.Save(path, ImageFormat.Png);
		}
	}

	private static void WriteIndexed8(int[] index, int w, int h, List<Color> palette, string path)
	{
		using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format8bppIndexed))
		{
			ColorPalette pal = bmp.Palette;
			for (int i = 0; i < 256; i++)
			{
				pal.Entries[i] = palette[i < palette.Count ? i : palette.Count - 1];
			}
			bmp.Palette = pal;

			BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
			try
			{
				int stride = data.Stride;
				byte[] buf = new byte[stride * h];
				for (int y = 0; y < h; y++)
				{
					for (int x = 0; x < w; x++)
					{
						buf[y * stride + x] = (byte)index[y * w + x];
					}
				}
				Marshal.Copy(buf, 0, data.Scan0, buf.Length);
			}
			finally
			{
				bmp.UnlockBits(data);
			}

			bmp.Save(path, ImageFormat.Png);
		}
	}

	private static void WriteRgb(int[] index, int w, int h, List<Color> palette, string path)
	{
		using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
		{
			BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
			try
			{
				int stride = data.Stride;
				byte[] buf = new byte[stride * h];
				for (int y = 0; y < h; y++)
				{
					for (int x = 0; x < w; x++)
					{
						Color c = palette[index[y * w + x]];
						int o = y * stride + x * 3;
						buf[o] = c.B;
						buf[o + 1] = c.G;
						buf[o + 2] = c.R;
					}
				}
				Marshal.Copy(buf, 0, data.Scan0, buf.Length);
			}
			finally
			{
				bmp.UnlockBits(data);
			}

			bmp.Save(path, ImageFormat.Png);
		}
	}
}
