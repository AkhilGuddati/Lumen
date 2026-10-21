using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumen;

// Decoded artwork cache, shared by the window and the model.
//
// Decoding a banner/logo/cover (and trimming a logo's alpha) is the slow part of switching games, and
// doing it on the UI thread is what made the crossfade hitch. So: everything here is keyed by file
// path + timestamp, decoding runs wherever the caller runs (background threads in practice), and every
// bitmap is frozen, which makes it safe to hand to the UI thread. The window only ever *reads* the
// cache on the UI thread (Cached / LogoCached) and lets Warm() fill it in the background.
public static class Art
{
    static readonly Dictionary<string, BitmapImage?> imgs = new();
    static readonly Dictionary<string, ImageSource?> logos = new();
    static readonly HashSet<string> pending = new();
    static readonly object gate = new();

    // Grid covers are shown small (even at the largest interface scale), so they are decoded at a
    // bounded width: a shelf of fifty 600x900 covers would otherwise hold ~100 MB of pixels.
    public const int CoverMaxW = 720;

    // Raised (on a background thread) when something finished decoding in the background.
    public static event Action<string>? Decoded;

    static string? Key(string? p, int maxW) =>
        p is null || !File.Exists(p) ? null : p + "|" + File.GetLastWriteTimeUtc(p).Ticks + "|" + maxW;

    // ---- cache-only lookups (cheap enough for the UI thread) ----
    public static BitmapImage? Cached(string? p, int maxW = 0)
    {
        var k = Key(p, maxW);
        if (k is null) return null;
        lock (gate) return imgs.GetValueOrDefault(k);
    }

    public static ImageSource? LogoCached(string? p)
    {
        var k = Key(p, 0);
        if (k is null) return null;
        lock (gate) return logos.GetValueOrDefault(k);
    }

    // ---- decode (call from a background thread) ----
    public static BitmapImage? Img(string? p, int maxW = 0)
    {
        var k = Key(p, maxW);
        if (k is null) return null;
        lock (gate) if (imgs.TryGetValue(k, out var hit)) return hit;

        BitmapImage? b = null;
        try
        {
            b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;   // decode now, so nothing touches the file later
            if (maxW > 0) b.DecodePixelWidth = maxW;    // bounded decode: small memory, fast
            b.UriSource = new Uri(p!);
            b.EndInit();
            b.Freeze();
        }
        catch { b = null; }
        lock (gate) imgs[k] = b;
        return b;
    }

    // Decodes the logo and trims its transparent padding / baked-in halo (see TrimLogo).
    public static ImageSource? Logo(string? p)
    {
        var k = Key(p, 0);
        if (k is null) return null;
        lock (gate) if (logos.TryGetValue(k, out var hit)) return hit;

        var b = Img(p);
        if (b is null) return null;
        ImageSource result = b;
        try { result = TrimLogo(b) ?? b; } catch { }
        lock (gate) logos[k] = result;
        return result;
    }

    // ---- background warm-up ----
    // Fire-and-forget: decode a game's art so the next switch finds it already warm. Each kind is decoded
    // the one way it is used (cover small, banner full size plus its tint, logo trimmed), nothing more.
    public static void Warm(string? grid, string? hero, string? logo)
    {
        Ensure(grid, CoverMaxW);
        EnsureHero(hero);
        EnsureLogo(logo);
    }

    // Starts a background decode of p at maxW unless it is already cached or under way, and raises
    // Decoded when it lands. This is what lets the cover tiles bind straight to the cache: a miss shows
    // for a moment, fills itself in off the UI thread, and the binding refreshes.
    public static void Ensure(string? p, int maxW)
    {
        var k = Key(p, maxW);
        if (k is null) return;
        lock (gate) if (imgs.ContainsKey(k)) return;
        Run("i|" + k, () => Img(p, maxW), p!);
    }

    // A banner is decoded and its tint worked out in one go, so ApplyHero never does pixel readback.
    public static void EnsureHero(string? p)
    {
        var k = Key(p, 0);
        if (k is null) return;
        lock (gate) if (imgs.ContainsKey(k)) return;
        Run("h|" + k, () => { if (Img(p) is { } b) Tint(b); }, p!);
    }

    // Same, for a trimmed logo (see Logo).
    public static void EnsureLogo(string? p)
    {
        var k = Key(p, 0);
        if (k is null) return;
        lock (gate) if (logos.ContainsKey(k)) return;
        Run("l|" + k, () => Logo(p), p!);
    }

    // At most two background decodes run at once. Without a cap, flicking through the shelf queued a decode
    // per game on every thread-pool thread, which starved the UI of CPU and flooded memory with pixel buffers.
    static readonly SemaphoreSlim decodeSlots = new(2);

    static void Run(string pendingKey, Action work, string path)
    {
        lock (gate) if (!pending.Add(pendingKey)) return;
        Task.Run(async () =>
        {
            await decodeSlots.WaitAsync();
            try { work(); } catch { }
            finally { decodeSlots.Release(); }
            lock (gate) pending.Remove(pendingKey);
            Decoded?.Invoke(path);
        });
    }

    // Game logos usually come as PNGs with generous transparent padding *and* a wide, faint
    // dark halo baked into the alpha (a soft shadow). Left alone, the padding makes the title
    // look detached and the halo shows up as a grey box behind it, so crop to the artwork and
    // rescale the alpha so the halo fades to fully transparent instead of ending in a hard edge.
    const byte LogoHaze = 64;
    static ImageSource? TrimLogo(BitmapSource src)
    {
        BitmapSource full = src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = full.PixelWidth, h = full.PixelHeight;
        if (w <= 0 || h <= 0) return null;
        int stride = w * 4;
        var px = new byte[stride * h];
        full.CopyPixels(px, stride, 0);

        // Try to ignore the halo; fall back to a gentler threshold for faint logos.
        foreach (byte t in new byte[] { LogoHaze, 16, 8, 4 })
        {
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride + 3;
                for (int x = 0; x < w; x++)
                {
                    if (px[row + x * 4] <= t) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX <= minX || maxY <= minY) continue;

            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            var outPx = new byte[cw * ch * 4];
            for (int y = 0; y < ch; y++)
            {
                int srow = (minY + y) * stride + minX * 4, drow = y * cw * 4;
                for (int x = 0; x < cw; x++)
                {
                    int s = srow + x * 4, d = drow + x * 4;
                    byte a = px[s + 3];
                    outPx[d] = px[s];
                    outPx[d + 1] = px[s + 1];
                    outPx[d + 2] = px[s + 2];
                    outPx[d + 3] = a <= t ? (byte)0 : (byte)((a - t) * 255 / (255 - t));
                }
            }
            var bmp = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Bgra32, null, outPx, cw * 4);
            bmp.Freeze();
            return bmp;
        }
        return null;
    }

    // ---- banner tint ----
    // The average colour is worked out once per image (weakly cached on the bitmap itself); the final
    // tint is derived from it on every call, so it follows the Windows light/dark theme when that changes.
    sealed class Avg { public Color? C; }
    static readonly ConditionalWeakTable<BitmapSource, Avg> avgs = new();

    // Average colour of a banner, pushed towards a mid tone so the hue shows on the dark pane.
    public static Color? Tint(BitmapSource b)
    {
        var avg = avgs.GetValue(b, s => new Avg { C = BannerAverage(s) }).C;
        return avg is { } c ? Shade(c) : null;
    }

    // Average colour of the banner, sampled from a 48px-wide thumbnail.
    static Color? BannerAverage(BitmapSource b)
    {
        if (b.PixelWidth <= 0 || b.PixelHeight <= 0) return null;
        double k = 48.0 / b.PixelWidth;
        var conv = new FormatConvertedBitmap(new TransformedBitmap(b, new ScaleTransform(k, k)), PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        if (w <= 0 || h <= 0) return null;
        var px = new byte[stride * h];
        conv.CopyPixels(px, stride, 0);
        long sr = 0, sg = 0, sb = 0;
        for (int i = 0; i < px.Length; i += 4) { sr += px[i + 2]; sg += px[i + 1]; sb += px[i]; }
        int n = w * h;
        return Color.FromRgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
    }

    // HSV: keep the hue, raise the saturation, park the brightness at the theme's tone.
    static Color Shade(Color avg)
    {
        double r = avg.R / 255.0, g = avg.G / 255.0, bl = avg.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, bl)), mn = Math.Min(r, Math.Min(g, bl)), d = mx - mn;
        double hue = 0;
        if (d > 0)
        {
            if (mx == r) hue = 60 * (((g - bl) / d) % 6);
            else if (mx == g) hue = 60 * (((bl - r) / d) + 2);
            else hue = 60 * (((r - g) / d) + 4);
            if (hue < 0) hue += 360;
        }
        double sat = mx <= 0 ? 0 : d / mx;
        sat = Math.Min(1, sat * 1.6 + 0.18);
        double val = Win.Light() ? 0.82 : 0.34;

        double c = val * sat, x = c * (1 - Math.Abs((hue / 60) % 2 - 1)), m = val - c;
        (double rr, double gg, double bb) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((rr + m) * 255), (byte)((gg + m) * 255), (byte)((bb + m) * 255));
    }
}
