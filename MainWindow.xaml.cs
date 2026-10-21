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
    // ---------- state ----------
    readonly ObservableCollection<Game> games;
    readonly HashSet<string> busy = new();
    readonly HashSet<string> descRetried = new();
    readonly Dictionary<string, Process> running = new();   // game id -> process we started

    Game? cur, musicFor;
    bool muted, musicPlaying, full, trailerPlaying, trailerShown, paused;
    double trailerTarget, musicTarget;
    double? shelfTarget, gridTarget, descTarget;            // scroll positions the frame loop glides towards
    string? trailerPath, heroShown;
    int heroToken, trailerToken, trailerLoadToken, infoToken;
    bool infoFilled;                                        // the title/metadata block has content to fade out
    TimeSpan lastFrame;

    // controller state
    ushort prevButtons;
    int dirHeld, vDirHeld;
    DateTime nextRepeat, vNextRepeat;
    bool padConnected;

    // ---------- motion vocabulary ----------
    // One set of curves for the whole app, so every move has the same feel.
    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    static readonly IEasingFunction EaseIn = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    static readonly IEasingFunction EaseInOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });
    static readonly IEasingFunction Decel = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });   // long, soft landing
    static readonly IEasingFunction Settle = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = .25 });

    // Animates one property. `from` null = carry on from wherever it is right now (so an interrupted
    // move never jumps). `done` runs when it finishes (not when it is replaced by a newer animation).
    static void Anim(IAnimatable target, DependencyProperty prop, double? from, double to, int ms,
                     IEasingFunction ease, int delayMs = 0, Action? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        if (from is { } f) a.From = f;
        if (delayMs > 0) a.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        if (done is not null) a.Completed += (_, _) => done();
        target.BeginAnimation(prop, a);
    }

    // Drops any running animation and sets the value outright.
    static void Snap(IAnimatable target, DependencyProperty prop, double value)
    {
        target.BeginAnimation(prop, null);
        ((DependencyObject)target).SetValue(prop, value);
    }

    public MainWindow()
    {
        InitializeComponent();
        Store.Load();
        games = Store.Games;
        foreach (var g in games)   // retry missing trailers/music/art once per launch
        {
            if (g.TrailerFile is null || !File.Exists(g.TrailerFile)) { g.TrailerFile = null; g.TrailerTried = false; }
            if (g.MusicFile is null || !File.Exists(g.MusicFile)) { g.MusicFile = null; g.MusicTried = false; }
            else if (g.MusicVer < 1) { Assets.DeleteMusic(g); g.MusicTried = false; }   // re-fetch old noisy DASH m4a
            // same for the cover/banner/logo: if a cached image vanished, fetch it again
            if (g.Grid is not null && !File.Exists(g.Grid)) { g.Grid = null; g.Fetched = false; }
            if (g.Hero is not null && !File.Exists(g.Hero)) { g.Hero = null; g.Fetched = false; }
            if (g.Logo is not null && !File.Exists(g.Logo)) { g.Logo = null; g.Fetched = false; }
        }
        SortFavs();
        List.ItemsSource = games;
        NavList.ItemsSource = games;   // the all-games pane shows the same library as a grid

        // A cover finished decoding in the background: repaint the game that owns it, so its tile
        // (shelf or grid) swaps from empty to art without any decode ever touching the UI thread.
        Art.Decoded += path => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            foreach (var g in games) if (g.Grid == path) g.Refresh();   // Background: never ahead of animation frames
        }));

        bool adopted = false;
        foreach (var g in games) adopted |= Assets.Adopt(g);   // media/art files -> "<game title>…"
        if (adopted) Store.Save();
        Assets.Purge(games);                                   // drop media/art files no game claims any more

        ApplyScale();
        ApplyTheme();
        SourceInitialized += (_, _) => Win.Mica(this, !Win.Light());
        SystemEvents.UserPreferenceChanged += OnPrefsChanged;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) { ToggleFull(); e.Handled = true; }
            else if (e.Key == Key.G) { ToggleNav(); e.Handled = true; }   // same grid as SELECT on the pad
            else if (e.Key == Key.Escape && navOpen) { CloseNav(false); e.Handled = true; }
            else if (e.Key == Key.Escape && full) { ToggleFull(); e.Handled = true; }
        };

        // The real sizes only settle after layout (and change with the interface scale), so re-fit on all of these.
        SizeChanged += (_, _) => SizeTrailer();
        InfoPanel.SizeChanged += (_, _) => SizeTrailer();
        TopBar.SizeChanged += (_, _) => SizeTrailer();
        Page.SizeChanged += (_, _) => SizeTrailer();
        HeroGrid.SizeChanged += (_, _) => SizeTrailer();
        Shelf.SizeChanged += (_, _) => SizeTrailer();
        Activated += (_, _) => { if (paused) { paused = false; SetTargets(); } };   // resume the theme when you come back

        // One per-frame loop for everything that glides (scrolling, volume, controller): it runs in step
        // with the display instead of a fixed 30 ms timer, and all speeds are time-based.
        CompositionTarget.Rendering += OnFrame;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            SystemEvents.UserPreferenceChanged -= OnPrefsChanged;
            Store.SaveNow();
        };

        Loaded += (_, _) =>
        {
            if (!full) ToggleFull();      // start in fullscreen (Esc / F11 to leave)
            SizeTrailer();
            Anim(Page, OpacityProperty, 0, 1, 800, EaseOut);
            if (games.Count > 0) List.SelectedIndex = 0;
            else Hint.Visibility = Visibility.Visible;
        };
    }

    void OnPrefsChanged(object? s, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyTheme();
            Win.Mica(this, !Win.Light());
        }));
    }

    // ---------- Windows theme ----------
    static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static Color WithAlpha(byte a, Color c) => Color.FromArgb(a, c.R, c.G, c.B);
    static Color ThemeBg(bool light) => light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x1C, 0x1C, 0x1C);

    void ApplyTheme()
    {
        bool light = Win.Light();
        var bg = ThemeBg(light);
        var card = light ? Colors.White : Color.FromRgb(0x2B, 0x2B, 0x2B);
        var accent = SystemParameters.WindowGlassColor;
        if (!light) accent = Blend(accent, Colors.White, .25);

        var r = Application.Current.Resources;
        r["Bg"] = new SolidColorBrush(bg);
        r["Fg"] = new SolidColorBrush(light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White);
        r["FgDim"] = new SolidColorBrush(light ? Color.FromRgb(0x5F, 0x5F, 0x5F) : Color.FromRgb(0xB0, 0xB0, 0xB0));
        r["Card"] = new SolidColorBrush(card);
        r["Accent"] = new SolidColorBrush(Color.FromRgb(accent.R, accent.G, accent.B));
        // The play button keeps the theme accent, just see-through; it gains solidity the more active it becomes.
        r["PlayAccent"] = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
        r["PlayAccentHover"] = new SolidColorBrush(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
        r["PlayAccentDown"] = new SolidColorBrush(Color.FromArgb(0xFF, accent.R, accent.G, accent.B));
        r["Glass"] = new SolidColorBrush(Color.FromArgb(light ? (byte)0xB8 : (byte)0x99, card.R, card.G, card.B));
        r["GlassLine"] = new SolidColorBrush(light ? Color.FromArgb(0x28, 0, 0, 0) : Color.FromArgb(0x30, 255, 255, 255));

        ScrimH.Background = new LinearGradientBrush(
            new GradientStopCollection { new(WithAlpha(0xF0, bg), 0), new(WithAlpha(0xA0, bg), .45), new(WithAlpha(0, bg), 1) },
            new Point(0, .5), new Point(1, .5));
        ScrimV.Background = new LinearGradientBrush(
            new GradientStopCollection { new(WithAlpha(0, bg), .55), new(WithAlpha(0xC0, bg), 1) },
            new Point(.5, 0), new Point(.5, 1));
        ApplyShelf();
    }

    // The sheet the covers sit on: translucent, and tinted with the banner's colours so the artwork
    // above bleeds into it instead of ending in a flat grey band. The brushes are created once and
    // their stops animated, so the tint drifts to the new game's colour instead of snapping.
    Color? bannerTint;
    LinearGradientBrush? shelfBrush, glowBrush;

    static LinearGradientBrush Vertical(Color[] colors, double[] offsets) => new(
        new GradientStopCollection(colors.Select((c, i) => new GradientStop(c, offsets[i]))),
        new Point(.5, 0), new Point(.5, 1));

    void ApplyShelf()
    {
        bool light = Win.Light();
        var bg = ThemeBg(light);
        var tint = bannerTint ?? bg;
        byte baseA = light ? (byte)0x84 : (byte)0x7C;
        byte glowA = light ? (byte)0x20 : (byte)0x33;

        // The pane: a wash of the banner's colour at the top settling into the theme's background.
        Color[] pane = { WithAlpha(0x33, tint), WithAlpha(0x20, tint), WithAlpha(baseA, bg) };
        // A faint glow of the artwork's own colour along the top edge (shelf, and the all-games pane).
        Color[] glow = { WithAlpha(glowA, tint), WithAlpha((byte)(glowA / 3), tint), WithAlpha(0, tint) };

        if (shelfBrush is null || glowBrush is null)
        {
            shelfBrush = Vertical(pane, new[] { 0, .45, 1 });
            glowBrush = Vertical(glow, new[] { 0, .35, 1 });
            Shelf.Background = shelfBrush;
            Glow.Fill = glowBrush;
            PaneGlow.Fill = glowBrush;
            return;
        }

        var dur = TimeSpan.FromMilliseconds(500);
        for (int i = 0; i < 3; i++)
        {
            TintStop(shelfBrush, i, pane[i], dur);
            TintStop(glowBrush, i, glow[i], dur);
        }
    }

    static void TintStop(LinearGradientBrush b, int i, Color c, TimeSpan dur) =>
        b.GradientStops[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(c, dur) { EasingFunction = EaseInOut });

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

    // UI scale: the page lays out in a smaller coordinate space and the transform scales it back up
    // to fill the window, so everything (fonts, covers, spacing) grows together.
    void ApplyScale() => PreviewScale(Store.Settings.UiScale, Store.Settings.TextScale);

    // Live preview while a scale slider moves (not persisted until Save). `t` scales the info text only.
    void PreviewScale(double s, double t)
    {
        if (s <= 0) s = 1.0;
        if (t <= 0) t = 1.0;
        Page.LayoutTransform = new ScaleTransform(s, s);
        MetaCard.LayoutTransform = new ScaleTransform(t, t);
        SizeTrailer();
    }

    // ---------- Banner ----------
    // Images are decoded off the UI thread (see Art); on the UI thread we only ever read the cache,
    // and a miss fills itself in the background and repaints when it is ready.

    // Crossfade the banner: the new image fades in on top, then becomes the base layer.
    void SetHero(string? p)
    {
        int tok = ++heroToken;
        var img = Art.Cached(p);
        if (img is not null) { ApplyHero(img, tok); return; }
        if (p is null || !File.Exists(p)) { ApplyHero(null, tok); return; }
        // Decode AND work out the tint here, off the UI thread: Art.Tint caches its result on the bitmap,
        // so ApplyHero's call is then free (otherwise the pixel readback happens mid-transition).
        // The short delay lets a fast flick through the shelf drop banners it has already passed
        // (a stale token means nobody wants this one) instead of decoding every one of them.
        _ = Task.Run(async () =>
        {
            await Task.Delay(90);
            if (tok != heroToken) return null;
            var b = Art.Img(p);
            if (b is not null) Art.Tint(b);
            return b;
        }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (tok == heroToken) ApplyHero(t.Result, tok);
        })), TaskScheduler.Default);
    }

    void ApplyHero(BitmapImage? img, int tok)
    {
        // Let the banner's colours bleed into the shelf (faded, not snapped).
        var tint = img is null ? null : Art.Tint(img);
        if (tint != bannerTint) { bannerTint = tint; ApplyShelf(); }

        if (img is null)
        {
            Anim(Hero2, OpacityProperty, null, 0, 300, EaseOut);
            Anim(Hero, OpacityProperty, null, 0, 300, EaseOut, 0, () => { if (tok == heroToken) Hero.Source = null; });
            return;
        }

        // The incoming banner drifts in from a hair larger while it fades up, so the swap reads as one
        // continuous move instead of two layers blinking at each other. If a previous swap is still
        // running, this one carries on from where it is rather than restarting from nothing.
        bool idle = Hero2.Opacity < .02;
        Hero2.Source = img;
        Anim(HeroZoom, ScaleTransform.ScaleXProperty, idle ? 1.04 : null, 1, 800, EaseOut);
        Anim(HeroZoom, ScaleTransform.ScaleYProperty, idle ? 1.04 : null, 1, 800, EaseOut);
        Anim(Hero2, OpacityProperty, idle ? 0 : null, 1, 650, EaseInOut, 0, () =>
        {
            if (tok != heroToken) return;
            Snap(Hero, OpacityProperty, 1);
            Hero.Source = img;
            Snap(Hero2, OpacityProperty, 0);
        });
    }

    // ---------- Title + metadata ----------
    // The old title/metadata fade out quickly, the new content is swapped in while nothing is visible,
    // then it rises in: title first, the metadata a beat later. The play button lives outside this block,
    // so it never blinks along with the switch.
    void Render(Game g, bool swap)
    {
        Hint.Visibility = Visibility.Collapsed;
        PlayBtn.Visibility = Visibility.Visible;
        if (swap || heroShown != g.Hero) { heroShown = g.Hero; SetHero(g.Hero); }

        int tok = ++infoToken;
        if (swap && infoFilled && InfoBody.Opacity > .05)
        {
            Anim(InfoBody, OpacityProperty, null, 0, 110, EaseIn, 0, () =>
            {
                if (tok != infoToken) return;   // another game was selected meanwhile
                FillInfo(g);
                RevealInfo(true);
            });
            return;
        }
        FillInfo(g);
        if (swap || InfoBody.Opacity < .99) RevealInfo(swap);   // a silent re-render just shows everything
    }

    void RevealInfo(bool fresh)
    {
        if (fresh)
        {
            // Base values first: the delayed metadata animation would otherwise leave it at its last value.
            Snap(InfoBody, OpacityProperty, 0);
            Snap(MetaCard, OpacityProperty, 0);
            Snap(InfoShift, TranslateTransform.YProperty, 14);
            Snap(MetaShift, TranslateTransform.YProperty, 18);
        }
        double? o = fresh ? 0 : null;
        Anim(InfoBody, OpacityProperty, o, 1, 360, EaseOut);
        Anim(InfoShift, TranslateTransform.YProperty, fresh ? 14 : null, 0, 480, EaseOut);
        Anim(MetaCard, OpacityProperty, o, 1, 360, EaseOut, fresh ? 80 : 0);
        Anim(MetaShift, TranslateTransform.YProperty, fresh ? 18 : null, 0, 520, EaseOut, fresh ? 80 : 0);
    }

    void FillInfo(Game g)
    {
        infoFilled = true;

        // The trimmed logo is shown from the cache; a first-time decode (logo scan included) happens
        // in the background and repaints the title area when it is ready, so it never blocks the switch.
        var logoImg = Art.LogoCached(g.Logo);
        if (logoImg is not null) ApplyLogo(g, logoImg);
        else if (g.Logo is not null && File.Exists(g.Logo))
            // Short grace period first: if the selection has already moved on (fast navigation) the
            // expensive decode + trim is skipped altogether instead of running for nothing.
            _ = Task.Run(async () =>
            {
                await Task.Delay(90);
                return cur == g ? Art.Logo(g.Logo) : null;
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (cur == g && t.Result is not null) ApplyLogo(g, t.Result);
            })), TaskScheduler.Default);
        else ApplyLogo(g, null);

        TitleText.Text = g.Name;
        bool wait = busy.Contains(g.Id) && g.Main <= 0;
        StatMain.Text = wait ? "…" : Fmt.Hours(g.Main);
        StatExtra.Text = wait ? "…" : Fmt.Hours(g.Extra);
        StatFull.Text = wait ? "…" : Fmt.Hours(g.Full);

        string desc = g.Description ?? "";
        bool descChanged = DescText.Text != desc;
        DescText.Text = desc;
        RenderMeta(g, desc);
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
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SizeTrailer));   // content changed: re-fit
    }

    // Genres / developer / publisher / release date in two columns; "additional content" (DLC) spans both below.
    void RenderMeta(Game g, string desc)
    {
        static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

        string dev = (g.Developer ?? "").Trim();
        if (dev.Length > 80) dev = dev[..77] + "...";

        MetaGenres.Text = g.Genres ?? "";
        MetaDev.Text = dev;
        MetaPub.Text = g.Publisher ?? "";
        MetaRelease.Text = g.ReleaseText;
        MetaGenresRow.Visibility = Vis(!string.IsNullOrWhiteSpace(g.Genres));
        MetaDevRow.Visibility = Vis(dev.Length > 0);
        MetaPubRow.Visibility = Vis(!string.IsNullOrWhiteSpace(g.Publisher));
        MetaReleaseRow.Visibility = Vis(g.ReleaseText.Length > 0);

        DlcBlock.Visibility = Vis(g.Dlc.Count > 0);
        DlcList.ItemsSource = g.Dlc.Count > 0 ? g.Dlc : null;

        DescCard.Visibility = Vis(!string.IsNullOrWhiteSpace(desc));
        // The description and the play time both live in this grid, so it stays visible
        // even when there is no other metadata.
        MetaCard.Visibility = Visibility.Visible;
    }

    // Sizes the logo to fill the title area (up to 420 x 240) while keeping its aspect ratio. It scales
    // down further when the rest of the info block leaves less height (logoMaxH, set in SizeTrailer), so it
    // is never cut off. Without an explicit size the image would stay at its natural pixel size.
    double logoMaxH = 240;

    // Shows (or hides) the game's logo over the banner; the plain title takes over when there is none.
    void ApplyLogo(Game g, ImageSource? src)
    {
        Logo.Source = src;
        Logo.Visibility = src is null ? Visibility.Collapsed : Visibility.Visible;
        FitLogo();
        TitleText.Visibility = src is null ? Visibility.Visible : Visibility.Collapsed;
        if (cur == g) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SizeTrailer));
    }

    void FitLogo()
    {
        const double maxW = 420;
        var img = Logo.Source;
        if (img is null || img.Width <= 0 || img.Height <= 0) { Logo.ClearValue(WidthProperty); Logo.ClearValue(HeightProperty); return; }
        double aspect = img.Width / img.Height;
        double w = Math.Min(maxW, Math.Min(240, logoMaxH) * aspect);
        double h = w / aspect;
        if (double.IsNaN(Logo.Width) || Math.Abs(Logo.Width - w) > .5 || Math.Abs(Logo.Height - h) > .5)
        {
            Logo.Width = w;
            Logo.Height = h;
        }
    }

    void Clear()
    {
        ++infoToken; infoFilled = false;
        heroShown = null; SetHero(null);
        Logo.Source = null; Logo.Visibility = Visibility.Collapsed;
        TitleText.Text = ""; StatMain.Text = StatExtra.Text = StatFull.Text = ""; DescText.Text = "";
        DescCard.Visibility = Visibility.Collapsed;
        MetaCard.Visibility = Visibility.Collapsed;
        PlayBtn.Visibility = Visibility.Collapsed;
        trailerPath = null; trailerShown = false; Trailer.Stop(); ShowTrailer(false);
        musicFor = null; musicPlaying = false; Music.Stop();
        Hint.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Keeps the trailer window proportional to the app window (16:9, bottom-right), and fits the info
    // block around it. Layout units throughout, so it holds at any interface scale.
    void SizeTrailer()
    {
        double pw = Page.ActualWidth > 0 ? Page.ActualWidth : ActualWidth;
        double ph = Page.ActualHeight > 0 ? Page.ActualHeight : ActualHeight;
        double heroH = Math.Max(200, ph - Shelf.ActualHeight - 60);
        double w = Math.Clamp(pw * .26, 260, 640);
        w = Math.Max(220, Math.Min(w, heroH * .55 * 16 / 9));
        double h = w * 9 / 16;
        TrailerBox.Width = TrailerSlot.Width = w;
        TrailerBox.Height = TrailerSlot.Height = h;
        TrailerBox.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 12, 12);

        // Nothing may overlap: the info block (left) is shrunk to fit beside the trailer (right), below the top
        // buttons / status line and above the shelf.
        double right = pw - 40 - w - 32;                              // right-hand limit: the trailer's slot is always reserved
        double hh = HeroGrid.ActualHeight > 0 ? HeroGrid.ActualHeight : heroH;
        InfoFit.MaxWidth = Math.Max(200, right - 56);                 // 56 = its left margin
        InfoFit.MaxHeight = Math.Max(90, hh - 36 - 60);               // 36 bottom margin, 60 = top buttons / status line
        PadHint.MaxWidth = Math.Max(200, pw * .45);                   // the hint wraps rather than crowding the status line
        StatusPanel.MaxWidth = Math.Max(160, pw - 56 - 24 - TopBar.ActualWidth - 24);   // stays clear of the hint + round buttons

        // The "additional content" list takes whatever height the rest of the block leaves, and scrolls beyond
        // that, so the block can never grow past the shelf. Sizes are in layout units (text scale `t` included).
        double t = Store.Settings.TextScale <= 0 ? 1.0 : Store.Settings.TextScale;
        double title = TitleBox.ActualHeight;
        double others = InfoPanel.ActualHeight - DlcScroll.ActualHeight * t - title;   // everything except title and DLC list
        // keep room for a readable logo (110 + its 18 gap); the list scrolls inside what is left
        DlcScroll.MaxHeight = Math.Max(40, (InfoFit.MaxHeight - others - 128) / t);

        // The logo gets whatever height the rest of the block leaves, so it scales down instead of being cut off.
        logoMaxH = Math.Max(60, InfoFit.MaxHeight - (InfoPanel.ActualHeight - title));
        FitLogo();

        // The description column takes the width left beside the metadata (up to 340), so its text is never
        // cut off at the right edge; the text just wraps onto more lines inside its fixed-height box.
        double colsW = MetaCard.ColumnDefinitions[0].ActualWidth + MetaCard.ColumnDefinitions[1].ActualWidth;
        double availW = (InfoFit.MaxWidth - MetaCard.Margin.Left) / t - colsW - DescCard.Margin.Left;
        double dw = Math.Clamp(availW, 160, 340);
        if (Math.Abs(DescCard.Width - dw) > .5) DescCard.Width = dw;
    }

    // ---------- Trailer (local file) ----------
    // Loads the trailer paused on a poster frame. The corner window stays hidden until the user
    // asks for it (Y), and the trailer only plays when asked (A, or clicking it).
    // Opening a video file builds a whole media graph, which is by far the slowest thing that can
    // happen while switching games — so the open is pushed behind the transition (Background priority
    // only runs once the frame has rendered) and dropped if the selection has moved on meanwhile.
    void LoadTrailer(Game g)
    {
        if (g.TrailerFile is null || !File.Exists(g.TrailerFile))
        {
            trailerPath = null; trailerPlaying = false; ++trailerLoadToken; Trailer.Stop(); ShowTrailer(false);
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
        int tok = ++trailerLoadToken;
        string file = g.TrailerFile;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (tok != trailerLoadToken) return;   // another game was selected meanwhile
            try
            {
                Trailer.Stop();
                Trailer.Volume = 0;
                Trailer.Source = new Uri(file);
                Trailer.Play();               // opens the file; Trailer_Opened then pauses on a poster frame
            }
            catch { }
        }));
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

    void ReleaseMedia()
    {
        Trailer.Stop(); Trailer.Close();
        Music.Stop(); Music.Close();
        trailerPath = null; trailerPlaying = false; musicPlaying = false;
        musicFor = null;
        ShowTrailer(false);
    }

    // The corner window is hidden by default: Y (or the film button) shows and hides it. It grows in
    // from its corner and shrinks back out; a token makes sure a fade-out that is still running never
    // collapses a window shown again meanwhile.
    void ShowTrailer(bool on)
    {
        int tok = ++trailerToken;
        if (on)
        {
            if (trailerPath is null) return;   // nothing to reveal yet
            bool wasHidden = TrailerBox.Visibility != Visibility.Visible;
            TrailerBox.Visibility = Visibility.Visible;
            Anim(TrailerBox, OpacityProperty, wasHidden ? 0 : null, 1, 320, EaseOut);
            Anim(TrailerScale, ScaleTransform.ScaleXProperty, wasHidden ? .9 : null, 1, 460, Settle);
            Anim(TrailerScale, ScaleTransform.ScaleYProperty, wasHidden ? .9 : null, 1, 460, Settle);
        }
        else if (TrailerBox.Visibility == Visibility.Visible)
        {
            Anim(TrailerScale, ScaleTransform.ScaleXProperty, null, .94, 220, EaseIn);
            Anim(TrailerScale, ScaleTransform.ScaleYProperty, null, .94, 220, EaseIn);
            Anim(TrailerBox, OpacityProperty, null, 0, 220, EaseIn, 0, () =>
            {
                if (tok != trailerToken) return;
                TrailerBox.BeginAnimation(OpacityProperty, null);
                TrailerBox.Visibility = Visibility.Collapsed;
            });
        }
        SizeTrailer();      // the info block makes room for the trailer, or takes the space back
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

    void Trailer_Ended(object s, RoutedEventArgs e)
    {
        trailerPlaying = false;
        BigPlay.Visibility = Visibility.Visible;
        Trailer.Position = TimeSpan.Zero;
        Trailer.Pause();
        SetTargets();
    }

    // ---------- Fetching ----------
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

    // ---------- Selection ----------
    void List_Sel(object s, SelectionChangedEventArgs e)
    {
        cur = List.SelectedItem as Game;
        if (cur is null) { Clear(); return; }
        paused = false;
        Render(cur, true);
        LoadTrailer(cur);
        SetTargets();                       // keep the selected game's theme playing
        ScheduleWarm(cur);                  // pre-decode the neighbours once the selection settles
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScrollToSel));
        if (NeedsWork(cur)) _ = Fetch(cur);
    }

    // Pre-decodes the art of the games either side of g on a background thread, so stepping along the
    // shelf finds the next banner already in Art's cache. It is debounced: while the selection is racing
    // along (held button, fast scroll) nothing is queued, and only the spot where it comes to rest is warmed,
    // so quick navigation never competes with the UI for CPU and memory. Art.Run also caps how many decodes
    // run at once.
    DispatcherTimer? warmTimer;
    Game? warmFor;

    void ScheduleWarm(Game g)
    {
        warmFor = g;
        if (warmTimer is null)
        {
            warmTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
            warmTimer.Tick += (_, _) =>
            {
                warmTimer!.Stop();
                if (warmFor is { } w && ReferenceEquals(w, cur)) WarmNear(w);
            };
        }
        warmTimer.Stop();
        warmTimer.Start();   // restarts the countdown on every move
    }

    void WarmNear(Game g)
    {
        int i = games.IndexOf(g);
        if (i < 0) return;
        for (int k = Math.Max(0, i - 1); k <= Math.Min(games.Count - 1, i + 1); k++)
            if (k != i) Art.Warm(games[k].Grid, games[k].Hero, games[k].Logo);
    }

    // ---------- Audio ----------
    // The theme of the selected game — the one whose art is in the banner — plays on a loop. Hovering a
    // cover or scrolling the description never changes it. The trailer (or mute) ducks the music while it
    // plays, and launching a game pauses it until you come back.
    void SetTargets()
    {
        trailerTarget = muted || !trailerPlaying ? 0 : .6;
        musicTarget = muted || trailerPlaying || paused ? 0 : .4;
        if (musicTarget > 0) SyncMusic();
    }

    // Eases a volume towards its target at a steady rate (per second), so fades don't depend on frame rate.
    // Fades in are gentler than fades out, so a new theme swells in rather than appearing.
    static void Ramp(MediaElement m, double target, double dt)
    {
        double v = m.Volume, d = target - v;
        if (d == 0) return;
        double step = (d > 0 ? .9 : 2.2) * dt;
        m.Volume = Math.Abs(d) <= step ? target : v + Math.Sign(d) * step;
    }

    // Loads (if needed) the selected game's theme, so the music always matches the banner.
    void SyncMusic()
    {
        if (cur is null || !PlayMusic(cur)) musicTarget = 0;
    }

    // Plays g's theme; returns false when the game has no playable theme file/link.
    // Switching themes is staged so moving between games never stalls or clicks: the current theme
    // fades out (target 0), and the frame loop opens the new file only once the old one is silent —
    // opening audio builds a media graph, which is exactly the kind of work that must not happen in the
    // middle of the banner crossfade. The new theme then fades back in.
    Game? musicWant;
    string? musicWantFile;

    bool PlayMusic(Game g)
    {
        var u = g.CustomTheme ?? g.MusicFile;
        if (string.IsNullOrEmpty(u) || (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(u)))
        { musicWant = null; musicWantFile = null; return false; }
        if (musicFor is not null && musicFor.Id == g.Id && musicPlaying && Music.Source is not null
            && musicWant is null) return true;
        if (musicWant == g && musicWantFile == u) return true;   // already on its way in

        musicWant = g;
        musicWantFile = u;
        musicTarget = 0;                     // fade the current theme out; the frame loop opens the new one
        return true;
    }

    // The staged theme swap: only once the outgoing theme is silent, and only for the game that is
    // still selected (a fast flick through the shelf must not open every file it passed over).
    void OpenWantedTheme()
    {
        if (musicWant is not { } g || musicWantFile is not { } u) return;
        if (Music.Volume > .02) return;      // still fading the old one out
        if (cur != g) { musicWant = null; musicWantFile = null; return; }
        musicWant = null;
        try
        {
            Music.Stop();
            Music.Volume = 0;
            Music.Source = new Uri(u);
            Music.Play();
            musicPlaying = true;
            musicFor = g;
        }
        catch { musicPlaying = false; musicFor = null; musicTarget = 0; }
        SetTargets();                        // fade the new theme in
    }

    void Music_Ended(object s, RoutedEventArgs e) { Music.Position = TimeSpan.Zero; Music.Play(); }

    // A theme that won't open (corrupt/unsupported) must not leave the app thinking it is playing.
    void Music_Failed(object s, ExceptionRoutedEventArgs e)
    {
        Store.Log("Music playback failed: " + (e.ErrorException?.Message ?? "unknown"));
        musicPlaying = false; musicFor = null; musicTarget = 0;
    }

    void Mute_Click(object s, RoutedEventArgs e) => ToggleMute();

    void ToggleMute()
    {
        muted = !muted;
        MuteBtn.Content = muted ? "\uE74F" : "\uE767";
        SetTargets();
    }

    // ---------- Frame loop: volume fades, smooth scrolling, controller ----------
    void OnFrame(object? s, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == lastFrame) return;                       // WPF can raise this twice for one frame
        double dt = lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min(.1, (now - lastFrame).TotalSeconds);
        lastFrame = now;

        Ramp(Trailer, trailerTarget, dt);
        Ramp(Music, musicTarget, dt);
        if (musicTarget == 0 && Music.Volume == 0 && musicPlaying) { Music.Stop(); musicPlaying = false; }
        OpenWantedTheme();   // staged theme swap: open the new file only once the old one is silent

        GlideScroll(ScrollOf(List), ref shelfTarget, true, dt, 14);
        GlideScroll(navOpen ? ScrollOf(NavList) : null, ref gridTarget, false, dt, 16);

        // Smooth scrolling of the fixed-size description box (controller).
        if (descTarget is not null)
        {
            if (DescCard.Visibility != Visibility.Visible || DescCard.ScrollableHeight <= 0) descTarget = null;
            else GlideScroll(DescCard, ref descTarget, false, dt, 18);
        }

        PollPad();
    }

    // Eases `now` towards `target`; the share of the remaining distance covered per second is set by
    // `rate`, so it looks the same at 60 and at 144 Hz. Returns false once it has arrived.
    static bool Glide(double now, double target, double dt, double rate, out double next)
    {
        double d = target - now;
        if (Math.Abs(d) < .5) { next = target; return false; }
        next = now + d * (1 - Math.Exp(-rate * dt));
        return true;
    }

    static void GlideScroll(ScrollViewer? v, ref double? target, bool horizontal, double dt, double rate)
    {
        if (v is null || target is not { } t) return;
        bool moving = Glide(horizontal ? v.HorizontalOffset : v.VerticalOffset, t, dt, rate, out var next);
        if (horizontal) v.ScrollToHorizontalOffset(next); else v.ScrollToVerticalOffset(next);
        if (!moving) target = null;
    }

    // The ScrollViewer inside a ListBox's default template.
    static ScrollViewer? ScrollOf(ListBox lb) =>
        VisualTreeHelper.GetChildrenCount(lb) > 0 && VisualTreeHelper.GetChild(lb, 0) is Border { Child: ScrollViewer sv } ? sv : null;

    void ScrollToSel()
    {
        if (ScrollOf(List) is not { } v || cur is null) return;
        if (List.ItemContainerGenerator.ContainerFromItem(cur) is not FrameworkElement c) return;
        var x = c.TranslatePoint(new Point(0, 0), List).X + v.HorizontalOffset;
        shelfTarget = Math.Clamp(x - (v.ViewportWidth - c.ActualWidth) / 2, 0, v.ScrollableWidth);
    }

    // ---------- Controller ----------
    const string ShelfHint = "SELECT Grid    START Play    Trailer:    A Play    Y Show    X Mute";

    void PollPad()
    {
        if (!Pad.Poll(out var b, out var lx, out var ly))
        {
            if (padConnected)
            {
                padConnected = false;
                prevButtons = 0; dirHeld = 0; vDirHeld = 0;   // no stale presses on reconnect
                PadHint.Visibility = NavHint.Visibility = Visibility.Collapsed;
            }
            return;
        }
        if (!padConnected) { padConnected = true; PadHint.Visibility = NavHint.Visibility = Visibility.Visible; }

        var pressed = (ushort)(b & ~prevButtons);
        prevButtons = b;

        // SELECT slides the all-games pane up / away.
        if ((pressed & Pad.Select) != 0) ToggleNav();

        // With an empty library there is nothing to launch, so Start offers the one thing that matters.
        PadHint.Text = games.Count == 0 ? "START Add game" : ShelfHint;

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
        if (move != 0)
        {
            if (navOpen) NavMove(move);     // inside the grid: step one cover
            else PadMove(move);
        }

        // Description: up/down, or the left stick, reads through the fixed-size box.
        // Inside the grid the same buttons step a whole row instead.
        int vdir = (b & Pad.Down) != 0 || ly < -16000 ? 1 : (b & Pad.Up) != 0 || ly > 16000 ? -1 : 0;   // XInput's stick Y is up-positive
        int vstep = 0;
        if (vdir != 0)
        {
            if (vdir != vDirHeld) { vstep = vdir; vNextRepeat = now.AddMilliseconds(380); }
            else if (now >= vNextRepeat) { vstep = vdir; vNextRepeat = now.AddMilliseconds(90); }
        }
        vDirHeld = vdir;
        if (vstep != 0)
        {
            if (navOpen) NavMove(vstep * NavCols());
            else ScrollDesc(vstep);
        }

        // START launches the game (or adds one when the library is empty); A plays the trailer,
        // Y shows/hides the trailer window, X mutes it. With the grid open: B backs out of it
        // (the selection stays where it was), A / START keep the highlighted cover.
        if ((pressed & Pad.B) != 0 && navOpen) { CloseNav(false); return; }
        if ((pressed & Pad.Start) != 0)
        {
            if (navOpen) CloseNav(true);
            if (games.Count == 0) Add_Click(this, new RoutedEventArgs());
            else Play_Click(null, null);
        }
        if ((pressed & Pad.A) != 0)
        {
            if (navOpen) CloseNav(true);
            else ToggleTrailer();
        }
        if ((pressed & Pad.Y) != 0) ToggleTrailerBox();
        if ((pressed & Pad.X) != 0) ToggleMute();
    }

    // Scrolls the description by two lines at a time; the frame loop eases it towards this target.
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

    // ---------- All-games pane ----------
    // A full-page pane that slides up over everything: the banner, the info block and the shelf stay
    // exactly where they are and are simply covered, so nothing is resized or re-laid-out while it moves
    // (it only translates, which the GPU does without touching layout). It sits parked below the window,
    // Hidden but still laid out, so its covers are ready and the first open doesn't hitch.
    //
    // The page behind does not follow the highlight while the pane is up (it is hidden anyway); the pick
    // is applied as the pane starts to leave, so the new banner fades in underneath it as it slides down.
    bool navOpen;
    bool paneParked = true;                          // fully off-screen (so the next open starts from below)

    const double NavTileW = 142;                     // 132px cover + the item's 5px margin on each side

    void ToggleNav() { if (navOpen) CloseNav(true); else OpenNav(); }

    double PageHeight => Page.ActualHeight > 0 ? Page.ActualHeight : ActualHeight;

    void OpenNav()
    {
        if (games.Count == 0 || navOpen) return;
        NavList.SelectedItem = cur ?? games[0];      // (before navOpen: Nav_Sel must not animate this)
        navOpen = true;
        gridTarget = null;

        GridPane.Visibility = Visibility.Visible;
        NavList.UpdateLayout();
        CenterOnSelection();                          // already in view when the pane arrives
        NavList.Focus();
        NavDim.IsHitTestVisible = true;

        double from = paneParked ? PageHeight : PaneShift.Y;
        paneParked = false;
        Anim(PaneShift, TranslateTransform.YProperty, from, 0, 520, Decel);
        Anim(NavDim, OpacityProperty, null, .55, 380, EaseOut);
    }

    // keepSelection: the highlighted cover becomes the selected game (SELECT / A / Enter / a click).
    // Otherwise the pane just leaves and nothing changes (Esc / B / a click outside).
    void CloseNav(bool keepSelection)
    {
        if (!navOpen) return;
        navOpen = false;
        gridTarget = null;
        var pick = keepSelection ? NavList.SelectedItem as Game : null;
        if (NavList.IsKeyboardFocusWithin) List.Focus();   // hand the keys back to the row

        double h = PageHeight;
        Anim(NavDim, OpacityProperty, null, 0, 340, EaseOut);
        Anim(PaneShift, TranslateTransform.YProperty, null, h, 360, EaseInOut, 0, () =>
        {
            if (navOpen) return;                           // reopened meanwhile
            Snap(PaneShift, TranslateTransform.YProperty, h);
            GridPane.Visibility = Visibility.Hidden;
            NavDim.IsHitTestVisible = false;
            paneParked = true;

            // The switch itself (banner crossfade, info fade, trailer + music load) runs only now that
            // the slide is over, so none of that work competes with the pane's animation frames.
            if (pick is not null && !ReferenceEquals(List.SelectedItem, pick)) List.SelectedItem = pick;
        });

        // Meanwhile, decode the pick's art on a background thread so it is already warm when the switch happens.
        if (pick is not null && !ReferenceEquals(List.SelectedItem, pick))
            Art.Warm(pick.Grid, pick.Hero, pick.Logo);
    }

    // Brings the highlighted tile to the middle of the grid at once (used while the pane is still off-screen).
    void CenterOnSelection()
    {
        if (TileTop(NavList.SelectedItem) is not { } top || ScrollOf(NavList) is not { } v) return;
        double mid = top + 222 / 2.0 - v.ViewportHeight / 2;
        v.ScrollToVerticalOffset(Math.Clamp(mid, 0, v.ScrollableHeight));
    }

    // Eases the grid just far enough to show the highlighted tile (with a little room for its lift).
    void RevealSelection()
    {
        if (TileTop(NavList.SelectedItem) is not { } top || ScrollOf(NavList) is not { } v) return;
        const double room = 24;
        double t = v.VerticalOffset, bottom = top + 222;
        if (top - room < t) t = top - room;
        else if (bottom + room > t + v.ViewportHeight) t = bottom + room - v.ViewportHeight;
        gridTarget = Math.Clamp(t, 0, v.ScrollableHeight);
    }

    // Top edge of a tile in the grid's scrolled content, or null when it has no container (yet).
    double? TileTop(object? item)
    {
        if (item is null || ScrollOf(NavList) is not { } v) return null;
        if (NavList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement c || c.ActualHeight <= 0) return null;
        return c.TranslatePoint(new Point(0, 0), v).Y + v.VerticalOffset;
    }

    // How many covers fit on a row right now, so up/down steps a whole row instead of one tile.
    int NavCols()
    {
        double w = ScrollOf(NavList)?.ViewportWidth ?? 0;
        if (w <= 0) w = NavList.ActualWidth > 0 ? NavList.ActualWidth : ActualWidth - 84;
        return Math.Max(1, (int)(w / NavTileW));
    }

    void NavMove(int d)
    {
        if (games.Count == 0) return;
        var i = Math.Clamp((NavList.SelectedIndex < 0 ? 0 : NavList.SelectedIndex) + d, 0, games.Count - 1);
        if (i != NavList.SelectedIndex) NavList.SelectedIndex = i;   // Nav_Sel scrolls it into view
    }

    void Nav_Sel(object s, SelectionChangedEventArgs e)
    {
        if (navOpen) RevealSelection();
    }

    // A click picks that cover and slides the pane away.
    void Nav_Pick(object s, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(NavList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.Content is not Game g) return;
        e.Handled = true;                       // the pane owns the press: no double-click play through
        NavList.SelectedItem = g;
        CloseNav(true);
    }

    void Nav_Wheel(object s, MouseWheelEventArgs e)
    {
        if (ScrollOf(NavList) is not { } v) return;
        gridTarget = Math.Clamp((gridTarget ?? v.VerticalOffset) - e.Delta * 1.3, 0, v.ScrollableHeight);
        e.Handled = true;
    }

    // Arrow keys move by tile / row (the ListBox's own arrows would step one item at a time), Enter picks.
    void Nav_Key(object s, KeyEventArgs e)
    {
        int step = e.Key switch
        {
            Key.Left => -1, Key.Right => 1,
            Key.Up => -NavCols(), Key.Down => NavCols(),
            Key.Home => -games.Count, Key.End => games.Count,
            _ => 0
        };
        if (step != 0) { NavMove(step); e.Handled = true; }
        else if (e.Key == Key.Enter) { CloseNav(true); e.Handled = true; }
    }

    void Nav_Close_Click(object s, MouseButtonEventArgs e) => CloseNav(false);
    void NavDone_Click(object s, RoutedEventArgs e) => CloseNav(true);

    // The toolbar button (the mouse/keyboard twin of SELECT).
    void Nav_Toolbar_Click(object s, RoutedEventArgs e) => ToggleNav();

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
        if (ScrollOf(List) is not { } v) return;
        shelfTarget = Math.Clamp((shelfTarget ?? v.HorizontalOffset) - e.Delta * 1.2, 0, v.ScrollableWidth);
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
        if (running.Remove(g.Id, out var p)) { try { p.Dispose(); } catch { } }
        if (cur == g) PlayLabel.Text = "Play";
    }

    // ---------- Add / edit / remove ----------
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
        bool renamed = n0 != cur.Name;
        if (renamed || hu0 != (cur.HltbUrl ?? "") || (manual0 && !cur.ManualHours))
        {
            cur.HltbTried = false; cur.HltbIdTried = false;
            if (renamed) cur.HltbGameId = null;
            if (!cur.DescManual) cur.DescVer = 0;
            cur.MetaVer = 0;
        }
        if (renamed) { cur.SteamId = null; if (cur.Description == desc0) { cur.Description = ""; cur.DescVer = 0; cur.DescManual = false; } }
        if (desc0 != "" && cur.Description == "") cur.Fetched = false;
        if (tu0 != (cur.TrailerUrl ?? "")) { ReleaseMedia(); Assets.DeleteTrailer(cur); cur.TrailerTried = false; }
        if (mu0 != (cur.MusicUrl ?? "")) { ReleaseMedia(); Assets.DeleteMusic(cur); cur.MusicTried = false; }
        if (hu0 != (cur.HltbUrl ?? "")) { cur.ManualHours = false; cur.Main = cur.Extra = cur.Full = 0; }
        bool artChanged = a0 != cur.GridUrl + "|" + cur.HeroUrl + "|" + cur.LogoUrl;
        if (artChanged) { cur.Grid = cur.Hero = cur.Logo = null; }
        if (artChanged || renamed)
        {
            cur.Fetched = false;
            if (renamed) { Assets.Delete(cur); Assets.DeleteArt(cur); cur.TrailerTried = cur.MusicTried = false; trailerPath = null; }
        }
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
        Store.Settings.TextScale = r.text;
        Store.Save();
        ApplyScale();
    }
}
