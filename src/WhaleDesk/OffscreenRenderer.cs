using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhaleDesk;

/// <summary>
/// Renders the SAME PetVisual the live window uses, but fully offscreen: no
/// Window is created, nothing appears on the desktop. Two artefacts:
///   * a per-track contact sheet, composited over a dark/light split so any
///     scaling halo (bright fringe on dark, dark fringe on light) is visible;
///   * bubble layout samples at 100% and 150% to cover DPI and CJK wrapping.
/// </summary>
internal static class OffscreenRenderer
{
    private static readonly Brush BgDark = new SolidColorBrush(Color.FromRgb(0x14, 0x1C, 0x30));
    private static readonly Brush BgLight = new SolidColorBrush(Color.FromRgb(0xE9, 0xED, 0xF5));
    private static readonly Brush LabelBrush = Brushes.White;
    private static readonly Brush LabelDim = new SolidColorBrush(Color.FromRgb(0xB9, 0xC4, 0xDC));

    public static void Run(CliOptions opts, PetSpec spec, AtlasLoader atlas, Log log)
    {
        var dir = Path.Combine(opts.BuildDir, "verify");
        Directory.CreateDirectory(dir);
        var produced = new List<object>();

        produced.Add(RenderTracks(opts, spec, atlas, log, dir));
        produced.Add(RenderBubbles(opts, spec, atlas, log, dir, 1.0));
        produced.Add(RenderBubbles(opts, spec, atlas, log, dir, 1.5));

        log.Json("offscreen-artifacts", produced);
    }

    private static object RenderTracks(CliOptions opts, PetSpec spec, AtlasLoader atlas, Log log, string dir)
    {
        const double petHeight = 110;
        var petW = Math.Round(petHeight * atlas.CellW / atlas.CellH);
        const int pad = 4;
        var cw = (int)petW + pad * 2;
        var ch = (int)petHeight + pad * 2;
        const int labelW = 150, headerH = 30;
        var sheetW = labelW + cw * atlas.Columns;
        var sheetH = headerH + ch * spec.Tracks.Count;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(BgDark, null, new Rect(0, 0, sheetW / 2.0, sheetH));
            dc.DrawRectangle(BgLight, null, new Rect(sheetW / 2.0, 0, sheetW / 2.0, sheetH));

            for (var c = 0; c < atlas.Columns; c++)
                dc.DrawText(Text($"col {c}", 12, LabelDim), new Point(labelW + c * cw + 4, 8));

            for (var row = 0; row < spec.Tracks.Count; row++)
            {
                var track = spec.Tracks[row];
                var y = headerH + row * ch;
                dc.DrawText(Text($"{row} {track.Name}", 13, LabelBrush), new Point(8, y + 6));
                dc.DrawText(Text($"{track.Frames.Length}f {track.Durations[0]}ms {(track.Loop ? "loop" : "->" + (track.Fallback ?? "idle"))}", 11, LabelDim), new Point(8, y + 26));

                for (var f = 0; f < track.Frames.Length; f++)
                {
                    var pv = new PetVisual(atlas, spec, petHeight);
                    pv.SetAnchorRight(false);
                    pv.SetFrame(row, track.Frames[f]);
                    pv.SetBubble(null);
                    var rtb = RenderElement(pv, cw, ch, 96);
                    dc.DrawImage(rtb, new Rect(labelW + f * cw, y, cw, ch));
                    dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1),
                        new Rect(labelW + f * cw + 0.5, y + 0.5, cw - 1, ch - 1));
                }
            }
        }

        var path = Path.Combine(dir, "p1-tracks.png");
        Save(visual, sheetW, sheetH, path);
        log.Info($"offscreen tracks sheet -> {path} ({sheetW}x{sheetH})");
        return new { file = path, width = sheetW, height = sheetH, dpi = 96, petHeight, tracks = spec.Tracks.Count, frames = spec.Tracks.Sum(t => t.Frames.Length) };
    }

    private static object RenderBubbles(CliOptions opts, PetSpec spec, AtlasLoader atlas, Log log, string dir, double scale)
    {
        var samples = new[]
        {
            "pwsh 工作中",
            "老板，C 盘只剩 12% 了，要不要我扫一遍？",
            "npm 缓存 715.9 MB、pnpm store 390.1 MB、CrashDumps 109.6 MB —— 清理清单已备好，点我确认。",
        };

        const double hostW = 300, hostH = 320;
        var pxW = (int)Math.Round(hostW * scale);
        var pxH = (int)Math.Round(hostH * scale);
        var dpi = 96 * scale;
        const int labelW = 26;
        var sheetW = pxW + labelW;
        var sheetH = pxH * samples.Length;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(BgDark, null, new Rect(0, 0, sheetW / 2.0, sheetH));
            dc.DrawRectangle(BgLight, null, new Rect(sheetW / 2.0, 0, sheetW / 2.0, sheetH));

            for (var i = 0; i < samples.Length; i++)
            {
                var pv = new PetVisual(atlas, spec, opts.PetHeight);
                pv.SetAnchorRight(true);
                pv.SetFrame(spec.Track("idle").Row, 0);
                pv.SetBubble(samples[i]);
                var rtb = RenderElement(pv, pxW, pxH, dpi);
                var y = i * pxH;
                dc.DrawImage(rtb, new Rect(labelW, y, pxW, pxH));
                dc.DrawText(Text($"{i + 1}", 14 * scale, LabelBrush), new Point(6, y + 8));
                if (i > 0)
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), null,
                        new Rect(0, y - 1, sheetW, 2));
            }
        }

        var name = $"p1-bubbles-{(int)Math.Round(scale * 100)}.png";
        var path = Path.Combine(dir, name);
        // The sheet itself is always rasterised at 96 DPI, i.e. one sheet pixel is
        // one DIP: the numbers above (labelW, pxW, pxH) are pixel counts. Rendering
        // the sheet at `dpi` while drawing pixel coordinates made every cell 1.5x
        // too large at scale=1.5 and clipped the content (V1 round 1 bug). The
        // chosen density still applies to the inner pet, which was rasterised at
        // 96*scale and is blitted 1:1 here.
        Save(visual, sheetW, sheetH, path);
        log.Info($"offscreen bubble sheet -> {path} ({sheetW}x{sheetH} innerDpi={dpi} scale={scale})");
        return new { file = path, width = sheetW, height = sheetH, sheetDpi = 96, innerDpi = dpi, scale, samples = samples.Length, cellPx = new { w = pxW, h = pxH } };
    }

    /// <summary>Measure/arrange a visual at a device-pixel size and rasterise it.</summary>
    private static RenderTargetBitmap RenderElement(FrameworkElement element, int pxW, int pxH, double dpi)
    {
        var dipW = pxW * 96.0 / dpi;
        var dipH = pxH * 96.0 / dpi;
        element.Width = dipW;
        element.Height = dipH;
        element.Measure(new Size(dipW, dipH));
        element.Arrange(new Rect(0, 0, dipW, dipH));
        element.UpdateLayout();
        var rtb = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(element);
        return rtb;
    }

    private static FormattedText Text(string text, double size, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, 96);

    private static void Save(Visual visual, int w, int h, string path)
    {
        // Fixed 96 DPI: sheet coordinates are DIPs == pixels, so a sheet can never
        // be scaled twice by accident.
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
