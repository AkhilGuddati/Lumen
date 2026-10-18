using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Lumen;

public partial class MainWindow : Window
{
    readonly ObservableCollection<Game> games;
    readonly HashSet<string> busy = new();
    readonly HashSet<string> hltbRetried = new();
    readonly HashSet<string> descRetried = new();
    readonly Dictionary<string, Process> running = new();   // game id -> process we started
    readonly DispatcherTimer timer;

    Game? cur, musicFor;
    bool muted, musicPlaying, full, trailerPlaying, trailerShown, paused;
    double trailerTarget, musicTarget;
    double? scrollTarget, descTarget;
    ScrollViewer? sv;
    string? trailerPath, heroShown;
    int heroToken, trailerToken;

    // controller state
    ushort prevButtons;
    int dirHeld, vDirHeld;
    DateTime nextRepeat, vNextRepeat;
    bool padConnected;

    public MainWindow()
    {
        InitializeComponent();
        Store.Load();
        games = Store.Games;
        foreach (var g0 in games)   // retry missing trailers/music/art once per launch
        {
            if (g0.TrailerFile is null || !File.Exists(g0.TrailerFile)) { g0.TrailerFile = null; g0.TrailerTried = false; }
            if (g0.MusicFile is null || !File.Exists(g0.MusicFile)) { g0.MusicFile = null; g0.MusicTried = false; }
            else if (g0.MusicVer < 1) { Assets.DeleteMusic(g0); g0.MusicTried = false; }   // re-fetch old noisy DASH m4a
            // same for the cover/banner/logo: if a cached image vanished, fetch it again
            if (g0.Grid is not null && !File.Exists(g0.Grid)) { g0.Grid = null; g0.Fetched = false; }
            if (g0.Hero is not null && !File.Exists(g0.Hero)) { g0.Hero = null; g0.Fetched = false; }
            if (g0.Logo is not null && !File.Exists(g0.Logo)) { g0.Logo = null; g0.Fetched = false; }
        }
        SortFavs();
        List.ItemsSource = games;

        bool adopted = false;
        foreach (var g1 in games) adopted |= Assets.Adopt(g1);   // media/art files -> "<game title>…"
        if (adopted) Store.Save();
        Assets.Purge(games);                                     // drop media/art files no game claims any more

        if (Store.Settings.UiScale <= 0) Store.Settings.UiScale = 1.0;
        ApplyScale();
        ApplyTheme();
        SourceInitialized += (_, _) => Win.Mica(this, !Win.Light());
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            ApplyTheme();
            Win.Mica(this, !Win.Light());
        });

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) { ToggleFull(); e.Handled = true; }
            else if (e.Key == Key.Escape && full) { ToggleFull(); e.Handled = true; }
        };

        SizeChanged += (_, _) => SizeTrailer();
        // the real sizes only settle after layout (and change with the interface scale), so re-fit on those too
        Page.SizeChanged += (_, _) => SizeTrailer();
        HeroGrid.SizeChanged += (_, _) => SizeTrailer();
        Shelf.SizeChanged += (_, _) => SizeTrailer();
        Activated += (_, _) => { if (paused) { paused = false; SetTargets(); } };   // resume the theme when you come back

        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += Tick;
        timer.Start();

        Loaded += (_, _) =>
        {
            if (!full) ToggleFull();      // start in fullscreen (Esc / F11 to leave)
            SizeTrailer();
            Page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700))
            { EasingFunction = new QuadraticEase() });
            if (games.Count > 0) List.SelectedIndex = 0;
            else Hint.Visibility = Visibility.Visible;
        };
    }

    // ---------- Windows theme ----------
    static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    void ApplyTheme()
    {
        bool light = Win.Light();
        var bg = light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x1C, 0x1C, 0x1C);
        var accent = SystemParameters.WindowGlassColor;
        if (!light) accent = Blend(accent, Colors.White, .25);

        var r = Application.Current.Resources;
        r["Bg"] = new SolidColorBrush(bg);
        r["Fg"] = new SolidColorBrush(light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White);
        r["FgDim"] = new SolidColorBrush(light ? Color.FromRgb(0x5F, 0x5F, 0x5F) : Color.FromRgb(0xB0, 0xB0, 0xB0));
        r["Card"] = new SolidColorBrush(light ? Colors.White : Color.FromRgb(0x2B, 0x2B, 0x2B));
        r["Accent"] = new SolidColorBrush(Color.FromRgb(accent.R, accent.G, accent.B));
        // The play button keeps the theme accent it always had, just see-through;
        // it gains solidity (rather than losing it) the more active it becomes.
        r["PlayAccent"]     = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
        r["PlayAccentHover"] = new SolidColorBrush(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
        r["PlayAccentDown"] = new SolidColorBrush(Color.FromArgb(0xFF, accent.R, accent.G, accent.B));

        var card = light ? Colors.White : Color.FromRgb(0x2B, 0x2B, 0x2B);
        r["Glass"] = new SolidColorBrush(Color.FromArgb(light ? (byte)0xB8 : (byte)0x99, card.R, card.G, card.B));
        r["GlassLine"] = new SolidColorBrush(light ? Color.FromArgb(0x28, 0, 0, 0) : Color.FromArgb(0x30, 255, 255, 255));

        Color A(byte a) => Color.FromArgb(a, bg.R, bg.G, bg.B);
        ScrimH.Background = new LinearGradientBrush(
            new GradientStopCollection { new(A(0xF0), 0), new(A(0xA0), .45), new(A(0), 1) },
            new Point(0, .5), new Point(1, .5));
        ScrimV.Background = new LinearGradientBrush(
            new GradientStopCollection { new(A(0), .55), new(A(0xC0), 1) },
            new Point(.5, 0), new Point(.5, 1));
        ApplyShelf();
    }

    // The sheet the covers sit on: translucent, and tinted with the banner's colours so the
    // artwork above bleeds into it instead of ending in a flat grey band.
    Color? bannerTint;
    void ApplyShelf()
    {
        bool light = Win.Light();
        var bg = light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x1C, 0x1C, 0x1C);
        byte baseA = light ? (byte)0x84 : (byte)0x7C;     // a little more transparent than before
        var tint = bannerTint ?? bg;
        // The pane: a wash of the banner's colour at the top settling into the theme's background.
        Shelf.Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(0x33, tint.R, tint.G, tint.B), 0),
                new(Color.FromArgb(0x20, tint.R, tint.G, tint.B), .45),
                new(Color.FromArgb(baseA, bg.R, bg.G, bg.B), 1),
            },
            new Point(.5, 0), new Point(.5, 1));

        // A faint glow of the artwork's own colour along the top edge of the pane.
        byte glowA = light ? (byte)0x20 : (byte)0x33;
        Glow.Fill = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(glowA, tint.R, tint.G, tint.B), 0),
                new(Color.FromArgb((byte)(glowA / 3), tint.R, tint.G, tint.B), .35),
                new(Color.FromArgb(0, tint.R, tint.G, tint.B), 1),
            },
            new Point(.5, 0), new Point(.5, 1));
    }

    // Average colour of the banner (sampled from a thumbnail), pushed towards a mid tone so the
    // hue is actually visible on the dark pane.
    static Color? BannerTint(BitmapSource b)
    {
        if (b.PixelWidth <= 0 || b.PixelHeight <= 0) return null;
        var small = new TransformedBitmap(b, new ScaleTransform(48.0 / b.PixelWidth, 48.0 / b.PixelWidth));
        var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        if (w <= 0 || h <= 0) return null;
        var px = new byte[stride * h];
        conv.CopyPixels(px, stride, 0);
        long sr = 0, sg = 0, sb = 0;
        int n = w * h;
        for (int i = 0; i < px.Length; i += 4) { sr += px[i + 2]; sg += px[i + 1]; sb += px[i]; }
        if (n <= 0) return null;
        var avg = Color.FromRgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));

        // HSV: keep the hue, raise the saturation, park the brightness at the theme's tone.
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

        double c2 = val * sat, x2 = c2 * (1 - Math.Abs((hue / 60) % 2 - 1)), m2 = val - c2;
        double rr, gg, bb2;
        if (hue < 60) { rr = c2; gg = x2; bb2 = 0; }
        else if (hue < 120) { rr = x2; gg = c2; bb2 = 0; }
        else if (hue < 180) { rr = 0; gg = c2; bb2 = x2; }
        else if (hue < 240) { rr = 0; gg = x2; bb2 = c2; }
        else if (hue < 300) { rr = x2; gg = 0; bb2 = c2; }
        else { rr = c2; gg = 0; bb2 = x2; }
        return Color.FromRgb((byte)((rr + m2) * 255), (byte)((gg + m2) * 255), (byte)((bb2 + m2) * 255));
    }

    // ---------- Fullscreen ----------
    void Full_Click(object s, RoutedEventArgs e) => ToggleFull();

    void ToggleFull()
    {
        full = !full;
        if (full)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
        }
        FsBtn.Content = full ? "\uE73F" : "\uE740";
        FsBtn.ToolTip = full ? "Exit fullscreen (Esc)" : "Fullscreen (F11)";
        Dispatcher.BeginInvoke(new Action(() => Win.Mica(this, !Win.Light())));
    }

    // ---------- Display + transitions ----------
    static readonly Dictionary<string, BitmapImage> artCache = new();
    static BitmapImage? Img(string? p)
    {
        if (p is null || !File.Exists(p)) return null;
        try
        {
            var key = p! + "|" + File.GetLastWriteTimeUtc(p!).Ticks;
            if (artCache.TryGetValue(key, out var hit)) return hit;
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(p);
            b.EndInit();
            b.Freeze();
            artCache[key] = b;
            return b;
        }
        catch { return null; }
    }

    // Game logos usually come as PNGs with generous transparent padding *and* a wide, faint
    // dark halo baked into the alpha (a soft shadow). Left alone, the padding makes the title
    // look detached and the halo shows up as a grey box behind it, so crop to the artwork and
    // rescale the alpha so the halo fades to fully transparent instead of ending in a hard edge.
    const byte LogoHaze = 64;
    static readonly Dictionary<string, ImageSource> logoCache = new();
    static ImageSource? LogoImg(string? p)
    {
        var src = Img(p);
        if (src is not BitmapImage b) return src;
        // Keyed on the file's timestamp too: replacing the logo link re-downloads to the same file
        // name, and a path-only key would keep showing the old image.
        var key = p! + "|" + File.GetLastWriteTimeUtc(p!).Ticks;
        if (logoCache.TryGetValue(key, out var hit)) return hit;
        ImageSource result = b;
        try { result = TrimLogo(b) ?? b; } catch { }
        logoCache[key] = result;
        return result;
    }

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
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    if (px[row + x * 4 + 3] > t)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
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
                    if (a <= t) a = 0;
                    else a = (byte)((a - t) * 255 / (255 - t));
                    outPx[d] = px[s];
                    outPx[d + 1] = px[s + 1];
                    outPx[d + 2] = px[s + 2];
                    outPx[d + 3] = a;
                }
            }
            var bmp = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Bgra32, null, outPx, cw * 4);
            bmp.Freeze();
            return bmp;
        }
        return null;
    }

    // Same format as the "additional content" rows beside it, so the numbers line up.
    static string Hrs(double h) => Fmt.Hours(h);

    // Crossfade the banner: new image fades in on top, then becomes the base layer.
    void SetHero(string? p)
    {
        var img = Img(p);
        int tok = ++heroToken;

        // Let the banner's colours bleed into the shelf.
        var tint = img is null ? null : BannerTint(img);
        if (tint != bannerTint) { bannerTint = tint; ApplyShelf(); }

        if (img is null)
        {
            var o = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
            o.Completed += (_, _) => { if (tok == heroToken) Hero.Source = null; };
            Hero.BeginAnimation(OpacityProperty, o);
            return;
        }
        Hero2.Source = img;
        var f = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = new QuadraticEase() };
        f.Completed += (_, _) =>
        {
            if (tok != heroToken) return;
            Hero.BeginAnimation(OpacityProperty, null);
            Hero.Opacity = 1;
            Hero.Source = img;
            Hero2.BeginAnimation(OpacityProperty, null);
            Hero2.Opacity = 0;
        };
        Hero2.BeginAnimation(OpacityProperty, f);
    }

    void AnimateInfo()
    {
        var e = new CubicEase { EasingMode = EasingMode.EaseOut };
        InfoPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = e });
        InfoTr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = e });
    }

    // Keeps the trailer window proportional to the app window (16:9, bottom-right).
    void SizeTrailer()
    {
        double pw = Page.ActualWidth > 0 ? Page.ActualWidth : ActualWidth;
        double ph = Page.ActualHeight > 0 ? Page.ActualHeight : ActualHeight;
        double heroH = Math.Max(200, ph - Shelf.ActualHeight - 60);
        double w = Math.Clamp(pw * .26, 260, 640);
        w = Math.Max(220, Math.Min(w, heroH * .55 * 16 / 9));
        double h = w * 9 / 16;
        TrailerBox.Width = w;
        TrailerBox.Height = h;
        TrailerBox.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 12, 12);

        // Nothing may overlap: the info block (left) is shrunk to fit beside the trailer (right), below the top
        // buttons / status line and above the shelf. `pw` / `hh` are layout units, so this holds at any UI scale.
        bool trailerOn = trailerShown && TrailerBox.Visibility == Visibility.Visible;
        double right = trailerOn ? pw - 40 - w - 32 : pw - 24;       // right-hand limit of the info block
        double hh = HeroGrid.ActualHeight > 0 ? HeroGrid.ActualHeight : heroH;
        InfoFit.MaxWidth = Math.Max(200, right - 56);                 // 56 = its left margin
        InfoFit.MaxHeight = Math.Max(90, hh - 36 - 84);               // 36 bottom margin, 84 = top buttons / status line
        StatusPanel.MaxWidth = Math.Max(160, pw - 56 - 216);          // stays clear of the top-right buttons
    }

    // UI scale: the page lays out in a smaller coordinate space and the transform scales it back up
    // to fill the window, so everything (fonts, covers, spacing) grows together.
    void ApplyScale()
    {
        double s = Store.Settings.UiScale <= 0 ? 1.0 : Store.Settings.UiScale;
        Page.LayoutTransform = new ScaleTransform(s, s);
        SizeTrailer();
    }

    void ReleaseMedia()
    {
        Trailer.Stop(); Trailer.Close();
        Music.Stop(); Music.Close();
        trailerPath = null; trailerPlaying = false; musicPlaying = false;
        musicFor = null;
        ShowTrailer(false);
    }

    // The corner window is hidden by default: Y (or the film button) shows and hides it. A token
    // makes sure a fade-out that is still running never collapses a window shown again meanwhile.
    void ShowTrailer(bool on)
    {
        int tok = ++trailerToken;
        if (on)
        {
            if (trailerPath is null) return;   // nothing to reveal yet
            var e = new CubicEase { EasingMode = EasingMode.EaseOut };
            TrailerBox.Visibility = Visibility.Visible;
            TrailerBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
            var sc = new DoubleAnimation(.88, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = e };
            TrailerScale.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
            TrailerScale.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
        }
        else if (TrailerBox.Visibility == Visibility.Visible)
        {
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            a.Completed += (_, _) =>
            {
                if (tok != trailerToken) return;
                TrailerBox.BeginAnimation(OpacityProperty, null);
                TrailerBox.Visibility = Visibility.Collapsed;
            };
            TrailerBox.BeginAnimation(OpacityProperty, a);
        }
        SizeTrailer();      // the info block makes room for the trailer, or takes the space back
    }

    void Clear()
    {
        heroShown = null; SetHero(null);
        Logo.Source = null; Logo.Visibility = Visibility.Collapsed;
        TitleText.Text = ""; StatMain.Text = StatExtra.Text = StatFull.Text = ""; DescText.Text = ""; DescCard.Visibility = Visibility.Collapsed;
        MetaCard.Visibility = Visibility.Collapsed;
        PlayBtn.Visibility = Visibility.Collapsed;
        trailerPath = null; trailerShown = false; Trailer.Stop(); ShowTrailer(false);
        musicFor = null; musicPlaying = false; Music.Stop();
        Hint.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Render(Game g, bool animate)
    {
        Hint.Visibility = Visibility.Collapsed;
        PlayBtn.Visibility = Visibility.Visible;
        if (heroShown != g.Hero || animate) { heroShown = g.Hero; SetHero(g.Hero); }
        var logoImg = LogoImg(g.Logo);
        Logo.Source = logoImg;
        Logo.Visibility = logoImg is null ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Text = g.Name;
        TitleText.Visibility = Logo.Source is null ? Visibility.Visible : Visibility.Collapsed;
        bool wait = busy.Contains(g.Id) && g.Main <= 0;
        StatMain.Text = wait ? "…" : Hrs(g.Main);
        StatExtra.Text = wait ? "…" : Hrs(g.Extra);
        StatFull.Text = wait ? "…" : Hrs(g.Full);
        string nd = g.Description ?? "";
        bool descChanged = DescText.Text != nd;
        DescText.Text = nd;
        DescCard.Visibility = string.IsNullOrWhiteSpace(nd) ? Visibility.Collapsed : Visibility.Visible;
        RenderMeta(g);
        // New text in the fixed-size box: start reading it from the top.
        if (descChanged) { descTarget = null; DescCard.ScrollToVerticalOffset(0); }
        PlayLabel.Text = IsRunning(g) ? "Running" : "Play";
        if (!busy.Contains(g.Id))
        {
            var notes = new List<string>();
            if (g.HltbTried && !g.ManualHours && g.Main <= 0 && g.Extra <= 0 && g.Full <= 0)
                notes.Add("Play time not found — enter it manually in Edit…");
            if (g.TrailerTried && g.TrailerFile is null)
                notes.Add("No trailer found — set a link in Edit… (details: %AppData%\\Lumen\\log.txt)");
            // A logo link that didn't download is what leaves the title as plain text.
            if (!string.IsNullOrWhiteSpace(g.LogoUrl) && g.Logo is null)
                notes.Add("Logo link couldn't be loaded — check it in Edit… (details: %AppData%\\Lumen\\log.txt)");
            Status.Text = string.Join("    ", notes);
        }
        if (animate) AnimateInfo();
    }

    // Genres / developer / publisher / release date in two columns; "additional content" (DLC) spans both below.
    void RenderMeta(Game g)
    {
        bool genres = !string.IsNullOrWhiteSpace(g.Genres);
        bool dev = !string.IsNullOrWhiteSpace(g.Developer);
        bool pub = !string.IsNullOrWhiteSpace(g.Publisher);
        bool rel = g.ReleaseText.Length > 0;
        bool dlc = g.Dlc.Count > 0;

        MetaGenres.Text = g.Genres ?? "";
        string devText = (g.Developer ?? "").Trim();
        if (devText.Length > 80) devText = devText.Substring(0, 77) + "...";
        MetaDev.Text = devText;
        MetaPub.Text = g.Publisher ?? "";
        MetaRelease.Text = g.ReleaseText;
        MetaGenresRow.Visibility = genres ? Visibility.Visible : Visibility.Collapsed;
        MetaDevRow.Visibility = devText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        MetaPubRow.Visibility = pub ? Visibility.Visible : Visibility.Collapsed;
        MetaReleaseRow.Visibility = rel ? Visibility.Visible : Visibility.Collapsed;

        DlcBlock.Visibility = dlc ? Visibility.Visible : Visibility.Collapsed;
        DlcList.ItemsSource = dlc ? g.Dlc : null;

        // The description and the play time both live in this grid, so it stays visible
        // even when there is no other metadata.
        bool desc = !string.IsNullOrWhiteSpace(g.Description);
        DescCard.Visibility = desc ? Visibility.Visible : Visibility.Collapsed;
        MetaCard.Visibility = Visibility.Visible;
    }

    // ---------- Trailer (local file) ----------
    // Loads the trailer paused on a poster frame. The corner window stays hidden until the user
    // asks for it (Y), and the trailer only plays when asked (A, or clicking it).
    void LoadTrailer(Game g)
    {
        if (g.TrailerFile is null || !File.Exists(g.TrailerFile))
        {
            trailerPath = null; trailerPlaying = false; Trailer.Stop(); ShowTrailer(false);
            return;
        }
        if (trailerPath == g.TrailerFile)
        {
            if (trailerShown && TrailerBox.Visibility != Visibility.Visible) ShowTrailer(true);
            return;   // already loaded: keep whatever the user was watching
        }
        trailerPath = g.TrailerFile;
        trailerPlaying = false;
        BigPlay.Visibility = Visibility.Visible;
        Trailer.Stop();
        Trailer.Volume = 0;
        Trailer.Source = new Uri(g.TrailerFile);
        Trailer.Play();               // opens the file; Trailer_Opened then pauses on a poster frame
        ShowTrailer(trailerShown);    // hidden by default
        SetTargets();
    }

    void Trailer_Opened(object s, RoutedEventArgs e)
    {
        if (trailerPlaying) return;
        var d = Trailer.NaturalDuration.HasTimeSpan ? Trailer.NaturalDuration.TimeSpan.TotalSeconds : 0;
        Trailer.Position = TimeSpan.FromSeconds(Math.Min(3, d / 4));
        Trailer.Pause();
    }

    void TrailerToggle_Click(object s, RoutedEventArgs e) => ToggleTrailer();
    void Trailer_Click(object s, MouseButtonEventArgs e) => ToggleTrailer();
    void TrailerBox_Click(object s, RoutedEventArgs e) => ToggleTrailerBox();

    // A: play / pause the trailer, showing the corner window if it is hidden.
    void ToggleTrailer()
    {
        if (trailerPath is null) { TrailerPending(); return; }
        if (!trailerShown) { trailerShown = true; ShowTrailer(true); SyncTrailerBtn(); }
        trailerPlaying = !trailerPlaying;
        if (trailerPlaying) { Trailer.Play(); BigPlay.Visibility = Visibility.Collapsed; }
        else { Trailer.Pause(); BigPlay.Visibility = Visibility.Visible; }
        SetTargets();
    }

    // Y (or the film button): show / hide the corner window.
    void ToggleTrailerBox()
    {
        trailerShown = !trailerShown;
        if (!trailerShown && trailerPlaying)
        {
            trailerPlaying = false;               // don't leave sound playing from a hidden window
            Trailer.Pause();
            BigPlay.Visibility = Visibility.Visible;
        }
        ShowTrailer(trailerShown);
        SyncTrailerBtn();
        if (trailerShown && trailerPath is null) TrailerPending();
        SetTargets();
    }

    // Asked for a trailer that isn't on disk (yet): say which of the two it is.
    void TrailerPending() => Status.Text = cur is not null && !cur.TrailerTried
        ? "Trailer is still being prepared…"
        : "No trailer for this game — add one via Edit…";

    void SyncTrailerBtn() => TrBtn.ToolTip = trailerShown ? "Hide trailer (Y)" : "Show trailer (Y)";

    bool NeedsWork(Game g) =>
        !g.Fetched || !g.TrailerTried || !g.MusicTried
        || (g.Main <= 0 && !g.ManualHours && !g.HltbTried)
        || ((string.IsNullOrWhiteSpace(g.Description) || (g.DescVer < Meta.DescVersion && !g.DescManual) || g.MetaVer < Meta.MetaVersion)
            && descRetried.Add(g.Id));

    async Task Fetch(Game g)
    {
        if (!busy.Add(g.Id)) return;
        try
        {
            if (!g.Fetched) { Say(g, "Fetching artwork & play time…"); await Meta.Fill(g); }
            else
            {
                await Meta.Hltb(g);
                await Meta.Desc(g);
                Store.Save(); g.Refresh();
            }
            if (cur == g) Render(g, false);

            if (!g.TrailerTried) { Say(g, "Downloading trailer…"); await Assets.Trailer(g); if (cur == g) LoadTrailer(g); }
            if (!g.MusicTried) { Say(g, "Finding theme music…"); await Assets.Music(g); }
        }
        catch { }
        finally { busy.Remove(g.Id); Say(g, ""); if (cur == g) { Render(g, false); SetTargets(); } }
    }

    void Say(Game g, string t) { if (cur == g) Status.Text = t; }

    void List_Sel(object s, SelectionChangedEventArgs e)
    {
        cur = List.SelectedItem as Game;
        if (cur is null) { Clear(); return; }
        paused = false;
        Render(cur, true);
        LoadTrailer(cur);
        SetTargets();                       // keep the selected game's theme playing
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScrollToSel));
        if (NeedsWork(cur)) _ = Fetch(cur);
    }

    // ---------- Audio ----------
    // The theme of the selected game — the one whose art is in the banner — plays on a loop. Hovering a
    // cover or scrolling the description never changes it. The trailer (or mute) ducks the music while it
    // plays, and launching a game pauses it until you come back.
    void SetTargets()
    {
        bool trailer = trailerPlaying;
        trailerTarget = muted || !trailer ? 0 : .6;
        musicTarget = muted || trailer || paused ? 0 : .4;
        if (musicTarget > 0) SyncMusic();
    }

    static void Step(MediaElement m, double target)
    {
        var d = target - m.Volume;
        m.Volume = Math.Abs(d) < .04 ? target : m.Volume + Math.Sign(d) * .04;
    }

    // Loads (if needed) the selected game's theme, so the music always matches the banner.
    void SyncMusic()
    {
        if (cur is null || !PlayMusic(cur)) musicTarget = 0;
    }

    // Plays g's theme; returns false when the game has no playable theme file/link.
    bool PlayMusic(Game g)
    {
        var u = g.CustomTheme ?? g.MusicFile;
        if (string.IsNullOrEmpty(u)) return false;
        if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(u)) return false;
        if (musicFor is not null && musicFor.Id == g.Id && musicPlaying && Music.Source is not null) return true;
        try
        {
            Music.Stop();
            Music.Volume = 0;
            Music.Source = new Uri(u);
            Music.Play();
            musicPlaying = true;
            musicFor = g;
            return true;
        }
        catch { return false; }
    }

    void Music_Ended(object s, RoutedEventArgs e) { Music.Position = TimeSpan.Zero; Music.Play(); }

    // A theme that won't open (corrupt/unsupported) must not leave the app thinking it is playing.
    void Music_Failed(object s, ExceptionRoutedEventArgs e)
    {
        Store.Log("Music playback failed: " + (e.ErrorException?.Message ?? "unknown"));
        musicPlaying = false; musicFor = null; musicTarget = 0;
    }
    void Trailer_Ended(object s, RoutedEventArgs e)
    {
        trailerPlaying = false;
        BigPlay.Visibility = Visibility.Visible;
        Trailer.Position = TimeSpan.Zero;
        Trailer.Pause();
        SetTargets();
    }

    void Mute_Click(object s, RoutedEventArgs e) => ToggleMute();

    void ToggleMute()
    {
        muted = !muted;
        MuteBtn.Content = muted ? "\uE74F" : "\uE767";
        SetTargets();
    }

    // ---------- Main tick: volume fades, smooth scrolling, controller ----------
    void Tick(object? s, EventArgs e)
    {
        Step(Trailer, trailerTarget);
        Step(Music, musicTarget);
        if (musicTarget == 0 && Music.Volume == 0 && musicPlaying) { Music.Stop(); musicPlaying = false; }

        var v = Sv();
        if (v != null && scrollTarget is { } t)
        {
            var d = t - v.HorizontalOffset;
            if (Math.Abs(d) < .5) { v.ScrollToHorizontalOffset(t); scrollTarget = null; }
            else v.ScrollToHorizontalOffset(v.HorizontalOffset + d * .18);
        }

        // Smooth scrolling of the fixed-size description box (controller).
        if (descTarget is { } dt)
        {
            if (DescCard.Visibility != Visibility.Visible || DescCard.ScrollableHeight <= 0) descTarget = null;
            else
            {
                var d = dt - DescCard.VerticalOffset;
                if (Math.Abs(d) < .5) { DescCard.ScrollToVerticalOffset(dt); descTarget = null; }
                else DescCard.ScrollToVerticalOffset(DescCard.VerticalOffset + d * .25);
            }
        }

        PollPad();
    }

    ScrollViewer? Sv()
    {
        if (sv != null) return sv;
        if (VisualTreeHelper.GetChildrenCount(List) > 0)
            sv = (VisualTreeHelper.GetChild(List, 0) as Border)?.Child as ScrollViewer;
        return sv;
    }

    void ScrollToSel()
    {
        var v = Sv();
        if (v is null || cur is null) return;
        if (List.ItemContainerGenerator.ContainerFromItem(cur) is not FrameworkElement c) return;
        var x = c.TranslatePoint(new Point(0, 0), List).X + v.HorizontalOffset;
        scrollTarget = Math.Clamp(x - (v.ViewportWidth - c.ActualWidth) / 2, 0, v.ScrollableWidth);
    }

    // ---------- Controller ----------
    void PollPad()
    {
        if (!Pad.Poll(out var b, out var lx, out var ly))
        {
            if (padConnected)
            {
                padConnected = false;
                prevButtons = 0; dirHeld = 0; vDirHeld = 0;   // no stale presses on reconnect
                PadHint.Visibility = Visibility.Collapsed;
            }
            return;
        }
        if (!padConnected) { padConnected = true; PadHint.Visibility = Visibility.Visible; }
        // With an empty library there is nothing to launch, so Start offers the one thing that matters.
        PadHint.Text = games.Count == 0
            ? "START Add game"
            : "START Play    A Play Trailer    Y Show Trailer    X Mute Trailer    B Back";

        var pressed = (ushort)(b & ~prevButtons);
        prevButtons = b;
        if (!IsActive) { dirHeld = 0; vDirHeld = 0; return; }   // don't react while a game (or a dialog) has focus

        // Shelf: left/right, or the left stick.
        int dir = (b & Pad.Right) != 0 || lx > 16000 ? 1 : (b & Pad.Left) != 0 || lx < -16000 ? -1 : 0;
        var now = DateTime.Now;
        int move = 0;
        if (dir != 0)
        {
            if (dir != dirHeld) { move = dir; nextRepeat = now.AddMilliseconds(380); }
            else if (now >= nextRepeat) { move = dir; nextRepeat = now.AddMilliseconds(110); }
        }
        dirHeld = dir;
        if ((pressed & Pad.RB) != 0) move = 1;
        if ((pressed & Pad.LB) != 0) move = -1;
        if (move != 0) PadMove(move);

        // Description: up/down, or the left stick, reads through the fixed-size box.
        int vdir = (b & Pad.Down) != 0 || ly > 16000 ? 1 : (b & Pad.Up) != 0 || ly < -16000 ? -1 : 0;
        int vstep = 0;
        if (vdir != 0)
        {
            if (vdir != vDirHeld) { vstep = vdir; vNextRepeat = now.AddMilliseconds(380); }
            else if (now >= vNextRepeat) { vstep = vdir; vNextRepeat = now.AddMilliseconds(90); }
        }
        vDirHeld = vdir;
        if (vstep != 0) ScrollDesc(vstep);

        // START launches the game (or adds one when the library is empty); A plays the trailer,
        // Y shows/hides the trailer window, X mutes it.
        if ((pressed & Pad.Start) != 0)
        {
            if (games.Count == 0) Add_Click(this, new RoutedEventArgs());
            else Play_Click(null, null);
        }
        if ((pressed & Pad.A) != 0) ToggleTrailer();
        if ((pressed & Pad.Y) != 0) ToggleTrailerBox();
        if ((pressed & Pad.X) != 0) ToggleMute();
        if ((pressed & Pad.B) != 0 && full) ToggleFull();
    }

    // Scrolls the description by two lines at a time; Tick eases it towards this target.
    void ScrollDesc(int dir)
    {
        if (DescCard.Visibility != Visibility.Visible || DescCard.ScrollableHeight <= 0) return;
        descTarget = Math.Clamp((descTarget ?? DescCard.VerticalOffset) + dir * 40, 0, DescCard.ScrollableHeight);
    }

    void PadMove(int m)
    {
        if (games.Count == 0) return;   // Clamp would throw with an empty library
        var i = Math.Clamp(List.SelectedIndex + m, 0, games.Count - 1);
        if (i == List.SelectedIndex) return;
        List.SelectedIndex = i;            // List_Sel starts the newly selected game's theme
    }

    // ---------- Shelf interaction ----------
    void Cover_Right(object s, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is Game g) List.SelectedItem = g;
    }

    void Cover_Down(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Play_Click(null, null);
    }

    void List_Wheel(object s, MouseWheelEventArgs e)
    {
        var v = Sv();
        if (v is null) return;
        scrollTarget = Math.Clamp((scrollTarget ?? v.HorizontalOffset) - e.Delta * 1.2, 0, v.ScrollableWidth);
        e.Handled = true;
    }

    void List_Key(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Play_Click(null, null);
    }

    // ---------- Favorites ----------
    // Favorites first, everything else keeps its order.
    void SortFavs()
    {
        var sel = List.SelectedItem;
        var ordered = games.Select((g, i) => (g, i)).OrderByDescending(x => x.g.Favorite).ThenBy(x => x.i)
                           .Select(x => x.g).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int j = games.IndexOf(ordered[i]);
            if (j != i) games.Move(j, i);
        }
        if (sel != null && !ReferenceEquals(List.SelectedItem, sel)) List.SelectedItem = sel;
    }

    void List_Menu(object s, ContextMenuEventArgs e)
    {
        if (cur is null) { e.Handled = true; return; }
        foreach (var it in List.ContextMenu.Items)
            if (it is MenuItem m && (m.Tag as string) == "fav")
                m.Header = cur.Favorite ? "Remove from favorites" : "Add to favorites";
    }

    void Fav_Click(object s, RoutedEventArgs e)
    {
        if (cur is null) return;
        cur.Favorite = !cur.Favorite;
        Store.Save();
        SortFavs();
        cur.Refresh();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScrollToSel));
    }

    // ---------- Actions ----------
    void Play_Click(object? s, RoutedEventArgs? e)
    {
        if (cur is null) return;

        // The game is already running: never start a second instance of it.
        if (IsRunning(cur))
        {
            Status.Text = $"{cur.Name} is already running";
            return;
        }

        // A full path to a file that no longer exists: say so instead of failing silently.
        var path = cur.Path.Trim().Trim('"');
        if (!path.Contains("://") && System.IO.Path.IsPathRooted(path) && !File.Exists(path) && !Directory.Exists(path))
        {
            var r = MessageBox.Show(this,
                $"The game file for \"{cur.Name}\" was not found:\n\n{path}\n\nIt may have been moved, uninstalled, or its drive isn't connected.\n\nEdit the game to fix the path now?",
                "Game not found", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) Edit_Click(this, new RoutedEventArgs());
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(cur.Path) { UseShellExecute = true, Arguments = cur.Args };
            if (File.Exists(cur.Path)) psi.WorkingDirectory = System.IO.Path.GetDirectoryName(cur.Path)!;
            var p = Process.Start(psi);
            if (p is not null) TrackLaunch(cur, p);
            if (trailerPlaying) { trailerPlaying = false; Trailer.Pause(); BigPlay.Visibility = Visibility.Visible; }
            paused = true;                  // the game is starting: fade the theme out until they come back
            SetTargets();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't launch " + cur.Name); }
    }

    // ---- one instance per game: remember the process we started, and hold the button at
    //      "Running" until that process exits ----
    bool IsRunning(Game g) => running.TryGetValue(g.Id, out var p) && !p.HasExited;

    void TrackLaunch(Game g, Process p)
    {
        running[g.Id] = p;
        PlayLabel.Text = "Running";
        try
        {
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                try { Dispatcher.BeginInvoke(new Action(() => EndLaunch(g))); } catch { }
            };
            if (p.HasExited) EndLaunch(g);   // it was gone before we could even watch it
        }
        catch { }
    }

    void EndLaunch(Game g)
    {
        if (running.TryGetValue(g.Id, out var p)) { running.Remove(g.Id); try { p.Dispose(); } catch { } }
        if (cur == g) PlayLabel.Text = "Play";
    }

    static void ApplyDialog(Game g, GameDialog d)
    {
        g.Name = d.NameBox.Text.Trim();
        g.Path = d.PathBox.Text.Trim();
        g.Args = d.ArgsBox.Text.Trim();
        g.GridUrl = d.GridBox.Text.Trim();
        g.HeroUrl = d.HeroBox.Text.Trim();
        g.LogoUrl = d.LogoBox.Text.Trim();
        var nd = d.DescBox.Text.Trim();
        if (nd != (g.Description ?? "")) { g.Description = nd; g.DescManual = nd != ""; if (nd == "") g.DescVer = 0; }
        g.TrailerUrl = d.TrailerLinkBox.Text.Trim();
        g.MusicUrl = d.MusicLinkBox.Text.Trim();
        g.HltbUrl = d.HltbLinkBox.Text.Trim();
        double m = GameDialog.Num(d.MainBox.Text), x = GameDialog.Num(d.ExtraBox.Text), f = GameDialog.Num(d.FullBox.Text);
        bool edited = Math.Abs(m - Math.Round(g.Main, 1)) > .05 || Math.Abs(x - Math.Round(g.Extra, 1)) > .05
                      || Math.Abs(f - Math.Round(g.Full, 1)) > .05;
        if (m > 0 || x > 0 || f > 0) { if (edited) { g.Main = m; g.Extra = x; g.Full = f; g.ManualHours = true; } }
        else g.ManualHours = false;   // cleared -> auto-fill from HowLongToBeat again
    }

    void Add_Click(object s, RoutedEventArgs e)
    {
        var d = new GameDialog(null) { Owner = this };
        if (d.ShowDialog() != true) return;

        if (string.IsNullOrWhiteSpace(Store.Settings.SgdbKey) && !Store.Settings.KeyAsked)
        {
            Store.Settings.KeyAsked = true;
            var k = Dialogs.Ask(this, "Artwork",
                "Paste your free SteamGridDB API key to fetch covers and banners (steamgriddb.com → Preferences → API). Leave empty to skip — you can also give custom image links per game.", "");
            if (!string.IsNullOrWhiteSpace(k)) Store.Settings.SgdbKey = k;
        }

        var g = new Game();
        ApplyDialog(g, d);
        games.Add(g);
        Store.Save();
        List.SelectedItem = g;
    }

    void Edit_Click(object s, RoutedEventArgs e)
    {
        if (cur is null) return;
        string n0 = cur.Name, a0 = cur.GridUrl + "|" + cur.HeroUrl + "|" + cur.LogoUrl;
        bool manual0 = cur.ManualHours;
        string desc0 = cur.Description ?? "";
        string tu0 = cur.TrailerUrl ?? "", mu0 = cur.MusicUrl ?? "", hu0 = cur.HltbUrl ?? "";
        var d = new GameDialog(cur) { Owner = this };
        if (d.ShowDialog() != true) return;

        ApplyDialog(cur, d);
        if (n0 != cur.Name || hu0 != (cur.HltbUrl ?? "") || (manual0 && !cur.ManualHours))
        {
            cur.HltbTried = false; cur.HltbIdTried = false;
            if (n0 != cur.Name) cur.HltbGameId = null;
            if (!cur.DescManual) cur.DescVer = 0;
            cur.MetaVer = 0;
        }
        if (n0 != cur.Name) { cur.SteamId = null; if (cur.Description == desc0) { cur.Description = ""; cur.DescVer = 0; cur.DescManual = false; } }
        if (desc0 != "" && cur.Description == "") cur.Fetched = false;
        if (tu0 != (cur.TrailerUrl ?? "")) { ReleaseMedia(); Assets.DeleteTrailer(cur); cur.TrailerTried = false; }
        if (mu0 != (cur.MusicUrl ?? "")) { ReleaseMedia(); Assets.DeleteMusic(cur); cur.MusicTried = false; }
        if (hu0 != (cur.HltbUrl ?? "")) { cur.ManualHours = false; cur.Main = cur.Extra = cur.Full = 0; hltbRetried.Remove(cur.Id); }
        bool artChanged = a0 != cur.GridUrl + "|" + cur.HeroUrl + "|" + cur.LogoUrl;
        if (artChanged) { cur.Grid = cur.Hero = cur.Logo = null; }
        if (artChanged || n0 != cur.Name) { cur.Fetched = false; if (n0 != cur.Name) { Assets.Delete(cur); Assets.DeleteArt(cur); cur.TrailerTried = cur.MusicTried = false; trailerPath = null; } }
        if (manual0 && !cur.ManualHours) cur.Fetched = false;
        Store.Save();
        cur.Refresh();
        Render(cur, false);
        LoadTrailer(cur);
        musicFor = null; paused = false; SetTargets();
        if (NeedsWork(cur)) _ = Fetch(cur);
    }

    // Paste a link (or file path) for the cover / banner / logo; it is downloaded automatically.
    void ArtLink_Click(object s, RoutedEventArgs e)
    {
        if (cur is null) return;
        var r = Dialogs.ArtLinks(this, cur.GridUrl ?? "", cur.HeroUrl ?? "", cur.LogoUrl ?? "");
        if (r is null) return;
        var (c, h, l) = r.Value;
        if ((cur.GridUrl ?? "") == c && (cur.HeroUrl ?? "") == h && (cur.LogoUrl ?? "") == l) return;
        cur.GridUrl = c; cur.HeroUrl = h; cur.LogoUrl = l;
        cur.Grid = cur.Hero = cur.Logo = null;   // drop the old cached images
        cur.Fetched = false;
        Store.Save();
        cur.Refresh();
        Render(cur, false);
        _ = Fetch(cur);                          // downloads the pasted art now
    }

    void Refresh_Click(object s, RoutedEventArgs e)
    {
        if (cur is null) return;
        ReleaseMedia();
        Assets.Delete(cur);
        Assets.DeleteArt(cur);
        cur.Fetched = cur.TrailerTried = cur.MusicTried = false;
        cur.HltbTried = false; cur.HltbIdTried = false; cur.HltbGameId = null; cur.DescVer = 0; cur.MetaVer = 0;
        _ = Fetch(cur);
    }

    void ThemeFile_Click(object s, RoutedEventArgs e)
    {
        if (cur is null) return;
        var d = new OpenFileDialog { Filter = "Audio|*.mp3;*.wav;*.m4a;*.wma;*.flac|All files|*.*" };
        if (d.ShowDialog() != true) return;
        cur.CustomTheme = d.FileName;
        Store.Save();
        musicFor = null; paused = false;   // play the newly picked theme right away
        SetTargets();
    }

    void Remove_Click(object s, RoutedEventArgs e)
    {
        if (cur is null) return;
        if (MessageBox.Show(this, $"Remove {cur.Name} from the launcher? (The game itself is not touched.)",
                "Remove", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var g = cur;
        ReleaseMedia();
        games.Remove(g);
        Assets.Delete(g);
        Assets.DeleteArt(g);
        Store.Save();
        if (games.Count > 0) List.SelectedIndex = 0;
    }

    void Settings_Click(object s, RoutedEventArgs e)
    {
        if (Dialogs.AppSettings(this, PreviewScale) is not { } r) return;
        Store.Settings.SgdbKey = r.key;
        Store.Settings.UiScale = r.scale;
        Store.Save();
        ApplyScale();
    }

    // Live preview while the scale slider moves (not persisted until Save).
    void PreviewScale(double s)
    {
        if (s <= 0) s = 1.0;
        Page.LayoutTransform = new ScaleTransform(s, s);
        SizeTrailer();
    }
}
