using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhaleDesk;

/// <summary>
/// Loads the atlas and hands out frozen cells plus a per-pixel alpha mask.
///
/// Two deliberate choices:
///  * the decoded bitmap is always rebuilt at 96 DPI (BitmapSource.Create), so
///    one DIP equals one atlas pixel no matter what DPI chunk the PNG carries.
///    Skipping this makes CroppedBitmap rectangles drift on non-96-DPI files.
///  * pixel data is kept once (BGRA) and shared by every frame's hit test, so
///    click-through costs an array lookup instead of a bitmap read.
/// </summary>
internal sealed class AtlasLoader
{
    public BitmapSource Atlas { get; }
    public int CellW { get; }
    public int CellH { get; }
    public int Columns { get; }
    public int Rows { get; }

    private readonly byte[] _pixels;
    private readonly int _stride;
    private readonly Dictionary<(int row, int col), BitmapSource> _cache = new();

    private AtlasLoader(BitmapSource atlas, byte[] pixels, int stride, int cellW, int cellH, int columns, int rows)
    {
        Atlas = atlas; _pixels = pixels; _stride = stride;
        CellW = cellW; CellH = cellH; Columns = columns; Rows = rows;
    }

    public static AtlasLoader Load(string path, PetSpec spec)
    {
        using var fs = File.OpenRead(path);
        var decoded = BitmapFrame.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bgra = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        var normalized = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        normalized.Freeze();
        return new AtlasLoader(normalized, pixels, stride, spec.CellWidth, spec.CellHeight, spec.Columns, spec.AtlasRows);
    }

    public BitmapSource Frame(int row, int col)
    {
        if (_cache.TryGetValue((row, col), out var cached)) return cached;
        var rect = new Int32Rect(col * CellW, row * CellH, CellW, CellH);
        var cropped = new CroppedBitmap(Atlas, rect);
        cropped.Freeze();
        _cache[(row, col)] = cropped;
        return cropped;
    }

    /// <summary>Pixel-level hit test inside one cell. u/v are 0..1 within the cell.</summary>
    public bool IsOpaque(int row, int col, double u, double v, byte threshold = 16)
    {
        if (u < 0 || v < 0 || u >= 1 || v >= 1) return false;
        var x = col * CellW + Math.Clamp((int)(u * CellW), 0, CellW - 1);
        var y = row * CellH + Math.Clamp((int)(v * CellH), 0, CellH - 1);
        if (x < 0 || y < 0) return false;
        var index = y * _stride + x * 4 + 3;
        if (index < 0 || index >= _pixels.Length) return false;
        return _pixels[index] > threshold;
    }

    /// <summary>
    /// Hit test with a one-pixel dilation. The sprite's edge pixels alternate between
    /// opaque and transparent as the animation advances, which made the clickable area
    /// flicker; a dilated mask keeps the pet comfortably clickable there while moving
    /// the boundary by a single atlas pixel (about 0.8 screen px at 160 DIP).
    /// </summary>
    public bool IsOpaqueDilated(int row, int col, double u, double v, byte threshold = 16)
    {
        if (u < 0 || v < 0 || u >= 1 || v >= 1) return false;
        var baseX = col * CellW;
        var baseY = row * CellH;
        var cx = baseX + Math.Clamp((int)(u * CellW), 0, CellW - 1);
        var cy = baseY + Math.Clamp((int)(v * CellH), 0, CellH - 1);
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var x = cx + dx;
                var y = cy + dy;
                if (x < baseX || x >= baseX + CellW || y < baseY || y >= baseY + CellH) continue;
                var index = y * _stride + x * 4 + 3;
                if (index >= 0 && index < _pixels.Length && _pixels[index] > threshold) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A cell pixel that is opaque in EVERY frame of the given frames list, with an
    /// opaque 3x3 neighbourhood. The click-through probe needs this: a point taken
    /// from a single frame goes transparent a few hundred milliseconds later when
    /// the animation advances, which made the first probe inconclusive.
    /// </summary>
    public (int x, int y)? FindAlwaysOpaque(int row, int[] frames)
    {
        var cx = CellW / 2;
        var cy = CellH / 2;
        for (var radius = 0; radius < Math.Max(CellW, CellH); radius += 2)
        {
            for (var dy = -radius; dy <= radius; dy += 2)
            {
                for (var dx = -radius; dx <= radius; dx += 2)
                {
                    var x = cx + dx;
                    var y = cy + dy;
                    if (x < 1 || y < 1 || x >= CellW - 1 || y >= CellH - 1) continue;
                    if (OpaqueInAll(row, frames, x, y)) return (x, y);
                }
            }
        }
        return null;
    }

    private bool OpaqueInAll(int row, int[] frames, int x, int y)
    {
        foreach (var frame in frames)
        {
            for (var oy = -1; oy <= 1; oy++)
            {
                for (var ox = -1; ox <= 1; ox++)
                {
                    var gx = frame * CellW + x + ox;
                    var index = (row * CellH + y + oy) * _stride + gx * 4 + 3;
                    if (index < 0 || index >= _pixels.Length || _pixels[index] <= 16) return false;
                }
            }
        }
        return true;
    }

    /// <summary>Opaque pixel statistics of a cell; used by --selftest to prove the grid assumption.</summary>
    public (int opaque, int total, int minX, int minY, int maxX, int maxY) CellStats(int row, int col, byte threshold = 16)
    {
        int opaque = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < CellH; y++)
        {
            for (var x = 0; x < CellW; x++)
            {
                var gx = col * CellW + x;
                var index = (row * CellH + y) * _stride + gx * 4 + 3;
                if (index >= _pixels.Length || _pixels[index] <= threshold) continue;
                opaque++;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        return (opaque, CellW * CellH, minX, minY, maxX, maxY);
    }
}
