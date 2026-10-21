using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace Lumen;

// Shared building blocks, so every dialog looks and behaves the same.
static class Dlg
{
    // Themed colours + Mica, fixed width, height follows the content.
    public static void Setup(Window w, string title, double width)
    {
        w.Title = title;
        w.Width = width;
        w.SizeToContent = SizeToContent.Height;
        w.ResizeMode = ResizeMode.NoResize;
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.Background = (Brush)Application.Current.Resources["Bg"];
        w.Foreground = (Brush)Application.Current.Resources["Fg"];
        w.SourceInitialized += (_, _) => Win.Mica(w, !Win.Light());
    }

    public static TextBox Box(string text = "") => new() { Text = text, Padding = new Thickness(6, 5, 6, 5) };

    public static TextBlock Label(string t, double top = 12) =>
        new() { Text = t, Margin = new Thickness(0, top, 0, 5), Opacity = .7, TextWrapping = TextWrapping.Wrap };

    public static TextBlock Head(string t) =>
        new() { Text = t, Margin = new Thickness(0, 24, 0, 0), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    public static Button Btn(string text, double width = 90, bool isDefault = false, bool isCancel = false) =>
        new() { Content = text, Width = width, Padding = new Thickness(0, 6, 0, 6), IsDefault = isDefault, IsCancel = isCancel };

    // Right-aligned button row (the last button is the primary one).
    public static StackPanel Buttons(double top, params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, top, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i < buttons.Length - 1) buttons[i].Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(buttons[i]);
        }
        return row;
    }
}

public class GameDialog : Window
{
    public TextBox NameBox = Dlg.Box(), PathBox = Dlg.Box(), ArgsBox = Dlg.Box(),
                   GridBox = Dlg.Box(), HeroBox = Dlg.Box(), LogoBox = Dlg.Box(),
                   MainBox = Dlg.Box(), ExtraBox = Dlg.Box(), FullBox = Dlg.Box(), DescBox = Dlg.Box(),
                   TrailerLinkBox = Dlg.Box(), MusicLinkBox = Dlg.Box(), HltbLinkBox = Dlg.Box();

    public static double Num(string s) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    static string Hrs(double? v) => v is > 0 ? v.Value.ToString("0.#", CultureInfo.InvariantCulture) : "";

    public GameDialog(Game? g)
    {
        Dlg.Setup(this, g is null ? "Add game" : "Edit game", 560);
        MaxHeight = SystemParameters.WorkArea.Height - 40;

        NameBox.Text = g?.Name ?? "";
        PathBox.Text = g?.Path ?? "";
        ArgsBox.Text = g?.Args ?? "";
        GridBox.Text = g?.GridUrl ?? "";
        HeroBox.Text = g?.HeroUrl ?? "";
        LogoBox.Text = g?.LogoUrl ?? "";
        MainBox.Text = Hrs(g?.Main);
        ExtraBox.Text = Hrs(g?.Extra);
        FullBox.Text = Hrs(g?.Full);
        TrailerLinkBox.Text = g?.TrailerUrl ?? "";
        MusicLinkBox.Text = g?.MusicUrl ?? "";
        HltbLinkBox.Text = g?.HltbUrl ?? "";
        DescBox.Text = g?.Description ?? "";
        DescBox.AcceptsReturn = true;
        DescBox.TextWrapping = TextWrapping.Wrap;
        DescBox.MinHeight = 72;
        DescBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var browse = new Button { Content = "Browse…", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = "Games & launchers|*.exe;*.lnk;*.bat;*.url|All files|*.*" };
            if (d.ShowDialog() != true) return;
            PathBox.Text = d.FileName;
            if (NameBox.Text.Trim() == "")
                NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(d.FileName).Replace('_', ' ').Replace('-', ' ');
        };

        var ok = Dlg.Btn("Save", isDefault: true);
        ok.Click += (_, _) =>
        {
            if (NameBox.Text.Trim() == "" || PathBox.Text.Trim() == "") return;
            DialogResult = true;
        };
        var cancel = Dlg.Btn("Cancel", isCancel: true);

        var pathRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(PathBox);

        var hours = new UniformGrid { Columns = 3 };
        hours.Children.Add(Small("Main story (h)", MainBox));
        hours.Children.Add(Small("Main + extras (h)", ExtraBox));
        hours.Children.Add(Small("Completionist (h)", FullBox));

        var root = new StackPanel { Margin = new Thickness(24, 12, 24, 24) };
        void Add(params UIElement[] items) { foreach (var i in items) root.Children.Add(i); }

        Add(Dlg.Label("Name"), NameBox,
            Dlg.Label("Launcher path (exe, shortcut, or a URL like steam://rungameid/12345)"), pathRow,
            Dlg.Label("Launch arguments (optional)"), ArgsBox,
            Dlg.Head("Custom artwork — image link or file path (PNG/JPG). Empty = find automatically"),
            Dlg.Label("Cover (portrait, ~600×900)"), GridBox,
            Dlg.Label("Banner (wide hero image)"), HeroBox,
            Dlg.Label("Logo (transparent PNG) — shown as the title over the banner"), LogoBox,
            Dlg.Head("Trailer & music — paste a YouTube link (or video/audio link or file). Empty = find automatically"),
            Dlg.Label("Trailer link (saved as a small mp4)"), TrailerLinkBox,
            Dlg.Label("Theme music link (YouTube → saved as m4a audio)"), MusicLinkBox,
            Dlg.Head("Description — filled from HowLongToBeat when available; type your own to override"), DescBox,
            Dlg.Head("Play time — filled from HowLongToBeat; type values here to override"), hours,
            Dlg.Label("HowLongToBeat game page link (optional), e.g. howlongtobeat.com/game/68151"), HltbLinkBox,
            Dlg.Buttons(22, cancel, ok));
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    static StackPanel Small(string label, TextBox box)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        p.Children.Add(Dlg.Label(label));
        p.Children.Add(box);
        return p;
    }
}

public static class Dialogs
{
    static Window Shell(Window owner, string title, double width)
    {
        var w = new Window { Owner = owner };
        Dlg.Setup(w, title, width);
        return w;
    }

    public static string? Ask(Window owner, string title, string text, string initial)
    {
        var w = Shell(owner, title, 520);
        var tb = Dlg.Box(initial);
        tb.Margin = new Thickness(0, 12, 0, 18);
        string? result = null;
        var ok = Dlg.Btn("OK", isDefault: true);
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.Click += (_, _) => { result = tb.Text.Trim(); w.DialogResult = true; };

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(tb);
        root.Children.Add(ok);
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    // SteamGridDB key + UI scale + text size. `preview` (optional) shows both scales live while a slider moves.
    public static (string key, double scale, double text)? AppSettings(Window owner, Action<double, double>? preview = null)
    {
        var w = Shell(owner, "Settings", 520);
        var key = Dlg.Box(Store.Settings.SgdbKey);
        var start = Store.Settings.UiScale <= 0 ? 1.0 : Store.Settings.UiScale;
        var startText = Store.Settings.TextScale <= 0 ? 1.0 : Store.Settings.TextScale;

        Slider? scaleSl = null, textSl = null;
        void Changed() { if (scaleSl != null && textSl != null) preview?.Invoke(scaleSl.Value, textSl.Value); }

        (DockPanel row, Slider slider) ScaleRow(double value, double min, double max)
        {
            var pct = new TextBlock
            {
                Text = $"{value:0%}", VerticalAlignment = VerticalAlignment.Center, MinWidth = 52,
                HorizontalAlignment = HorizontalAlignment.Right, Opacity = .8
            };
            var s = new Slider
            {
                Minimum = min, Maximum = max, Value = value, TickFrequency = .05, IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0)
            };
            s.ValueChanged += (_, _) => { pct.Text = $"{s.Value:0%}"; Changed(); };
            var row = new DockPanel();
            DockPanel.SetDock(pct, Dock.Right);
            row.Children.Add(pct);
            row.Children.Add(s);
            return (row, s);
        }

        var (scaleRow, sl1) = ScaleRow(start, .6, 1.6);
        var (textRow, sl2) = ScaleRow(startText, .7, 1.6);
        scaleSl = sl1; textSl = sl2;

        (string, double, double)? result = null;
        var ok = Dlg.Btn("Save", isDefault: true);
        ok.Click += (_, _) => { result = (key.Text.Trim(), sl1.Value, sl2.Value); w.DialogResult = true; };
        var cancel = Dlg.Btn("Cancel", isCancel: true);
        w.Closed += (_, _) => { if (result is null) preview?.Invoke(start, startText); };   // put the old sizes back

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(Dlg.Label("SteamGridDB API key (free: steamgriddb.com → Preferences → API). Used for covers, banners and logos.", 16));
        root.Children.Add(key);
        root.Children.Add(Dlg.Label("Interface scale — makes everything bigger or smaller; useful on small screens.", 16));
        root.Children.Add(scaleRow);
        root.Children.Add(Dlg.Label("Text size — play times, genres, developer and description, on top of the interface scale. The same for every game.", 16));
        root.Children.Add(textRow);
        root.Children.Add(Dlg.Buttons(22, cancel, ok));
        w.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        w.ShowDialog();
        return result;
    }

    // Paste links (or file paths) for cover / banner / logo. The caller downloads them automatically.
    public static (string cover, string banner, string logo)? ArtLinks(Window owner, string cover, string banner, string logo)
    {
        var w = Shell(owner, "Artwork links", 560);
        var coverBox = Dlg.Box(cover);
        var bannerBox = Dlg.Box(banner);
        var logoBox = Dlg.Box(logo);

        (string, string, string)? result = null;
        var ok = Dlg.Btn("Download", 110, isDefault: true);
        ok.Click += (_, _) => { result = (coverBox.Text.Trim(), bannerBox.Text.Trim(), logoBox.Text.Trim()); w.DialogResult = true; };
        var cancel = Dlg.Btn("Cancel", isCancel: true);

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock
        {
            Text = "Paste an image link (or a file path) — it is downloaded automatically. Leave a box empty to keep the artwork the app found.",
            TextWrapping = TextWrapping.Wrap, Opacity = .8
        });
        root.Children.Add(Dlg.Label("Cover (portrait, ~600×900)"));
        root.Children.Add(coverBox);
        root.Children.Add(Dlg.Label("Banner (wide hero image)"));
        root.Children.Add(bannerBox);
        root.Children.Add(Dlg.Label("Logo (transparent PNG) — shown as the title over the banner"));
        root.Children.Add(logoBox);
        root.Children.Add(Dlg.Buttons(20, cancel, ok));
        w.Content = root;
        w.ShowDialog();
        return result;
    }
}
